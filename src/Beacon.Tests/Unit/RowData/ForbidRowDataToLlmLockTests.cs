using Beacon.Api;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Handlers.McpSettings;
using Beacon.Core.Models;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Services;
using Beacon.Core.Services.Metadata;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.RowData;

/// <summary>
/// The deployment lock <c>Beacon:Mcp:ForbidRowDataToLlm</c> (off by default): it pins sample-value collection, value
/// grounding and the eval judge off for the global row and every project, so the refresh collects no samples, nothing
/// shows stored ones, and no value is probed; a request that would turn one on answers 409 (SettingLockedException), a
/// project override of a locked field is refused even when it agrees, and the stored rows it overrides are named at
/// startup by id.
/// </summary>
[TestFixture]
public class ForbidRowDataToLlmLockTests
{
    private const int DataSourceId = 5;
    private const int ProjectId = 3;

    private static readonly McpDeploymentOptions Lock = new() { ForbidRowDataToLlm = true };

    [Test]
    public void TheLock_IsOffByDefault()
    {
        new McpDeploymentOptions().ForbidRowDataToLlm.Should().BeFalse();
        McpLockPolicy.ActiveRules(new McpDeploymentOptions())
            .Select(x => x.LockName)
            .Should().NotContain(nameof(McpDeploymentOptions.ForbidRowDataToLlm));
    }

    [Test]
    public void Resolution_PinsSampleValuesValueGroundingAndTheJudgeOff_OverEveryStoredValue()
    {
        var global = new McpSettingsData { EnableSampleValueCollection = true, EnableValueGrounding = true, EnableEvalJudge = true };
        var project = new McpProjectSettingsData { EnableSampleValueCollection = true, EnableValueGrounding = true, EnableEvalJudge = true };

        var result = McpSettingsProvider.Resolve(global, project, Lock);

        result.Effective.EnableSampleValueCollection.Should().BeFalse();
        result.Effective.EnableValueGrounding.Should().BeFalse();
        result.Effective.EnableEvalJudge.Should().BeFalse();
        result.Effective.EnforceReadOnly.Should().BeTrue("the other locks still apply");
        result.LockedFields.Should().Contain(["EnableSampleValueCollection", "EnableValueGrounding", "EnableEvalJudge"]);
        global.EnableSampleValueCollection.Should().BeTrue("the stored value is kept");
    }

    [Test]
    public async Task TheLocksQuery_ListsTheThreeFields()
    {
        var locks = await new GetMcpDeploymentLocksHandler(Options.Create(new McpDeploymentOptions { ForceReadOnly = false, ForcePiiDetection = false, ForbidRowDataToLlm = true }))
            .Handle(new GetMcpDeploymentLocksQuery(), CancellationToken.None);

        locks.LockedFields.Should().Equal("EnableEvalJudge", "EnableSampleValueCollection", "EnableValueGrounding");
    }

    [TestCase(nameof(McpSettingsData.EnableSampleValueCollection))]
    [TestCase(nameof(McpSettingsData.EnableValueGrounding))]
    [TestCase(nameof(McpSettingsData.EnableEvalJudge))]
    public async Task TurningRowDataOnGlobally_UnderTheLock_AnswersSettingLocked(string field)
    {
        var data = new McpSettingsData
        {
            EnableSampleValueCollection = field == nameof(McpSettingsData.EnableSampleValueCollection),
            EnableValueGrounding = field == nameof(McpSettingsData.EnableValueGrounding),
            EnableEvalJudge = field == nameof(McpSettingsData.EnableEvalJudge)
        };
        var handler = new UpdateMcpSettingsHandler(
            Mock.Of<IDbContextFactory<BeaconContext>>(MockBehavior.Strict),
            Mock.Of<IMcpSettingsProvider>(),
            Options.Create(Lock),
            TestPermissions.Admin,
            TestProjectAccess.Actors(),
            NullLogger<UpdateMcpSettingsHandler>.Instance);

        var act = () => handler.Handle(new UpdateMcpSettingsCommand(data), CancellationToken.None);

        var refused = (await act.Should().ThrowAsync<SettingLockedException>()).Which;
        refused.FieldName.Should().Be(field);
        refused.Message.Should().Contain(nameof(McpDeploymentOptions.ForbidRowDataToLlm));
    }

    // Any override of a locked field is refused, even one that agrees today: it would take effect the day the lock lifts.
    [TestCase(true)]
    [TestCase(false)]
    public async Task AProjectOverrideOfALockedField_IsRefused_EvenWhenItAgrees(bool value)
    {
        var handler = new UpdateMcpProjectSettingsHandler(
            Mock.Of<IDbContextFactory<BeaconContext>>(MockBehavior.Strict),
            Mock.Of<IMcpSettingsProvider>(),
            Options.Create(Lock),
            TestPermissions.Admin,
            TestProjectAccess.Actors(),
            NullLogger<UpdateMcpProjectSettingsHandler>.Instance);

        var act = () => handler.Handle(
            new UpdateMcpProjectSettingsCommand(ProjectId, new McpProjectSettingsData { EnableValueGrounding = value }),
            CancellationToken.None);

        (await act.Should().ThrowAsync<SettingLockedException>()).Which.FieldName.Should().Be(nameof(McpSettingsData.EnableValueGrounding));
    }

    // After start-up the stored rows the lock overrides are named by id with the lock; a row that agrees is not.
    [Test]
    public async Task Startup_NamesTheStoredSettingsTheLockOverrides()
    {
        var world = new ProjectAccessWorld();
        world.Serve(new List<McpSettings> { new() { Id = 1, EnableValueGrounding = true } });
        world.Serve(new List<McpProjectSettings>
        {
            new() { Id = 1, ProjectId = 7, EnableSampleValueCollection = true },
            new() { Id = 2, ProjectId = 8, EnableEvalJudge = false },
            new() { Id = 3, ProjectId = 9 }
        });
        var services = new ServiceCollection();
        services.AddSingleton(world.Factory());
        await using var provider = services.BuildServiceProvider();
        var logs = new LogRecorder();
        var startup = new ReadOnlyStartupWarnings(
            Options.Create(new McpDeploymentOptions { ForbidRowDataToLlm = true }),
            logs.For<ReadOnlyStartupWarnings>(),
            Options.Create(new MigrationOptions()));

        await startup.WarnAboutOverriddenSettingsAsync(provider, CancellationToken.None);

        var warning = logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning).Subject;
        warning.Message.Should().Contain("ForbidRowDataToLlm").And.Contain("global settings rows 1").And.Contain("overrides of projects 7");
        warning.Message.Should().NotContain("8").And.NotContain("9");
    }

    // With no global settings row stored, startup says which row-data settings are in effect and where to turn them on;
    // once a row is stored, nothing.
    [TestCase(false, "No global MCP settings are stored, so the defaults apply: sample-value collection off, value grounding off, eval judge off.")]
    [TestCase(true, "No global MCP settings are stored, and Beacon:Mcp:ForbidRowDataToLlm pins sample-value collection, value grounding and the eval judge off")]
    public async Task Startup_WithoutAStoredGlobalRow_LogsTheRowDataSettingsInEffect(bool locked, string expected)
    {
        var (startup, logs, provider) = Startup(new List<McpSettings>(), locked);

        await startup.LogRowDataDefaultsAsync(provider, CancellationToken.None);

        logs.Entries.Should().ContainSingle().Which.Should().Match<RecordedLog>(x => x.Level == LogLevel.Information && x.Message.StartsWith(expected, StringComparison.Ordinal));
    }

    [Test]
    public async Task Startup_WithAStoredGlobalRow_LogsNothing()
    {
        var (startup, logs, provider) = Startup(new List<McpSettings> { new() { Id = 1 } }, locked: false);

        await startup.LogRowDataDefaultsAsync(provider, CancellationToken.None);

        logs.Entries.Should().BeEmpty();
    }

    private static (ReadOnlyStartupWarnings Startup, LogRecorder Logs, IServiceProvider Services) Startup(List<McpSettings> globalRows, bool locked)
    {
        var world = new ProjectAccessWorld();
        world.Serve(globalRows);
        var services = new ServiceCollection();
        services.AddSingleton(world.Factory());
        var logs = new LogRecorder();
        var startup = new ReadOnlyStartupWarnings(
            Options.Create(new McpDeploymentOptions { ForbidRowDataToLlm = locked }),
            logs.For<ReadOnlyStartupWarnings>(),
            Options.Create(new MigrationOptions()));

        return (startup, logs, services.BuildServiceProvider());
    }

    // Collection: under the lock the refresh never samples, and stores the extracted catalog without samples.
    [Test]
    public async Task UnderTheLock_TheRefreshCollectsNoSamples()
    {
        var world = new ProjectAccessWorld();
        world.Project(ProjectId);
        var source = world.Source(DataSourceId, ProjectId);
        source.DatabaseEngineType = Beacon.Core.Data.Enums.DatabaseEngineType.PostgreSQL;
        world.Serve(new List<McpSettings> { new() { Id = 1, EnableSampleValueCollection = true } });
        world.Serve(new List<McpProjectSettings> { new() { Id = 1, ProjectId = ProjectId, EnableSampleValueCollection = true } });
        world.Serve(new List<DatabaseMetadata>());
        world.Serve(new List<McpDocumentationPatch>());
        var settings = new McpSettingsProvider(world.Factory(), new MemoryCache(new MemoryCacheOptions()), Options.Create(Lock), NullLogger<McpSettingsProvider>.Instance);
        var shared = new SharedSourceSettings(world.Factory(), settings);
        var sampler = new Mock<IColumnValueSampler>(MockBehavior.Strict);
        var extractor = new Mock<IDatabaseMetadataExtractor>();
        extractor.SetupGet(x => x.SupportedEngineType).Returns(Beacon.Core.Data.Enums.DatabaseEngineType.PostgreSQL);
        extractor
            .Setup(x => x.ExtractMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new TableMetadataDto("public", "orders", [new ColumnMetadataDto("status", "text", true, false, false, 1, null, null, null, null, null)], [], null)]);
        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(x => x.Decrypt(It.IsAny<string>())).Returns("Host=unused");
        var service = new DatabaseMetadataService(
            world.Factory(),
            encryption.Object,
            [extractor.Object],
            sampler.Object,
            shared,
            new MemoryCache(new MemoryCacheOptions()),
            Mock.Of<ISchemaGraphService>(),
            NullLogger<DatabaseMetadataService>.Instance);

        var withoutLock = await new SharedSourceSettings(world.Factory(), new McpSettingsProvider(world.Factory(), new MemoryCache(new MemoryCacheOptions()), Options.Create(new McpDeploymentOptions()), NullLogger<McpSettingsProvider>.Instance))
            .GetDataSourceSettingsAsync(DataSourceId);
        var snapshot = await service.RefreshMetadataAsync(DataSourceId, forceRefresh: true);

        withoutLock.EnableSampleValueCollection.Should().BeTrue("without the lock the stored settings would sample");
        sampler.VerifyNoOtherCalls();
        snapshot.Tables.Single().Columns.Single().SampleValues.Should().BeNull();
    }

    // Rendering: settings resolved under the lock show no stored sample, whatever was stored.
    [Test]
    public void UnderTheLock_NoStoredSampleIsShown()
    {
        var resolved = McpSettingsProvider.Resolve(new McpSettingsData { EnableSampleValueCollection = true }, null, Lock).Effective;

        StoredSampleScreen.Screen(new QueryGuardrailService(), "status", ["PAID"], resolved).Should().BeNull();
    }
}
