using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.Api;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Handlers.McpSettings;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.SecurityAudit;

/// <summary>
/// The deployment lock <c>Beacon:Mcp:Audit:RequireContent</c> (off by default): it pins <c>RetainQueryContent</c> on for
/// the global row and every project, so no Admin can switch the audit's content off at runtime — a write that would
/// answers 409 (and a project override of it is refused even when it agrees), the locks list names it, the stored rows it
/// overrides are named at startup, and it cannot be combined with <c>ForceNoContentRetention</c> (startup fails).
/// </summary>
[TestFixture]
public class RequireContentLockTests
{
    private static readonly McpDeploymentOptions Lock = new() { Audit = new McpAuditOptions { RequireContent = true } };

    [Test]
    public void TheLock_IsOffByDefault()
    {
        new McpAuditOptions().RequireContent.Should().BeFalse();
        McpLockPolicy.ActiveRules(new McpDeploymentOptions()).Select(x => x.LockName).Should().NotContain(McpLockPolicy.RequireContentLockName);
    }

    [Test]
    public void Resolution_PinsContentRetentionOn_OverEveryStoredValue()
    {
        var global = new McpSettingsData { RetainQueryContent = false };
        var project = new McpProjectSettingsData { RetainQueryContent = false };

        var result = McpSettingsProvider.Resolve(global, project, Lock);

        result.Effective.RetainQueryContent.Should().BeTrue();
        result.LockedFields.Should().Contain(nameof(McpSettingsData.RetainQueryContent));
        global.RetainQueryContent.Should().BeFalse("the stored value is kept, and takes effect again when the lock is lifted");
    }

    [Test]
    public async Task SwitchingContentRetentionOffGlobally_UnderTheLock_AnswersSettingLocked()
    {
        var handler = new UpdateMcpSettingsHandler(
            Mock.Of<IDbContextFactory<BeaconContext>>(MockBehavior.Strict),
            Mock.Of<IMcpSettingsProvider>(),
            Options.Create(Lock),
            TestPermissions.Admin,
            TestProjectAccess.Actors(),
            new RecordingSecurityAudit(),
            NullLogger<UpdateMcpSettingsHandler>.Instance);

        var act = () => handler.Handle(new UpdateMcpSettingsCommand(new McpSettingsData { RetainQueryContent = false }), CancellationToken.None);

        var refused = (await act.Should().ThrowAsync<SettingLockedException>()).Which;
        refused.FieldName.Should().Be(nameof(McpSettingsData.RetainQueryContent));
        refused.Message.Should().Contain("Audit:RequireContent");
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task AProjectOverrideOfContentRetention_UnderTheLock_IsRefused_EvenWhenItAgrees(bool value)
    {
        var handler = new UpdateMcpProjectSettingsHandler(
            Mock.Of<IDbContextFactory<BeaconContext>>(MockBehavior.Strict),
            Mock.Of<IMcpSettingsProvider>(),
            Options.Create(Lock),
            TestPermissions.Admin,
            TestProjectAccess.Actors(),
            new RecordingSecurityAudit(),
            NullLogger<UpdateMcpProjectSettingsHandler>.Instance);

        var act = () => handler.Handle(new UpdateMcpProjectSettingsCommand(3, new McpProjectSettingsData { RetainQueryContent = value }), CancellationToken.None);

        (await act.Should().ThrowAsync<SettingLockedException>()).Which.FieldName.Should().Be(nameof(McpSettingsData.RetainQueryContent));
    }

    [Test]
    public async Task TheLocksQuery_ListsTheFieldAndTheLock()
    {
        var locks = await new GetMcpDeploymentLocksHandler(Options.Create(new McpDeploymentOptions { ForceReadOnly = false, ForcePiiDetection = false, Audit = new McpAuditOptions { RequireContent = true } }))
            .Handle(new GetMcpDeploymentLocksQuery(), CancellationToken.None);

        locks.LockedFields.Should().Equal(nameof(McpSettingsData.RetainQueryContent));
        locks.Locks.Should().Equal(new McpDeploymentLock(nameof(McpSettingsData.RetainQueryContent), "Audit:RequireContent"));
    }

    [Test]
    public void TogetherWithForceNoContentRetention_StartupFails()
    {
        var validator = new McpDeploymentOptionsValidator();

        var both = validator.Validate(null, new McpDeploymentOptions { ForceNoContentRetention = true, Audit = new McpAuditOptions { RequireContent = true } });
        var alone = validator.Validate(null, Lock);

        both.Failed.Should().BeTrue();
        both.FailureMessage.Should().Contain("Beacon:Mcp:Audit:RequireContent").And.Contain("Beacon:Mcp:ForceNoContentRetention");
        alone.Succeeded.Should().BeTrue();
    }

    [Test]
    public async Task Startup_NamesTheStoredSettingsTheLockOverrides()
    {
        var world = new ProjectAccessWorld();
        world.Serve(new List<McpSettings> { new() { Id = 1, RetainQueryContent = false } });
        world.Serve(new List<McpProjectSettings>
        {
            new() { Id = 1, ProjectId = 7, RetainQueryContent = false },
            new() { Id = 2, ProjectId = 8, RetainQueryContent = true },
            new() { Id = 3, ProjectId = 9 }
        });
        var services = new ServiceCollection();
        services.AddSingleton(world.Factory());
        await using var provider = services.BuildServiceProvider();
        var logs = new LogRecorder();
        var startup = new ReadOnlyStartupWarnings(Options.Create(Lock), logs.For<ReadOnlyStartupWarnings>(), Options.Create(new MigrationOptions()));

        await startup.WarnAboutOverriddenSettingsAsync(provider, CancellationToken.None);

        var warning = logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning).Subject;
        warning.Message.Should().Contain("Audit:RequireContent").And.Contain("global settings rows 1").And.Contain("overrides of projects 7");
        warning.Message.Should().NotContain("8").And.NotContain("9");
    }
}
