using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Handlers.McpSettings;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

[TestFixture]
public class UpdateMcpSettingsHandlerTests
{
    private static UpdateMcpSettingsHandler CreateHandler(McpDeploymentOptions? options = null)
    {
        // The invalid-regex and deployment-lock guards run before any DB access, so these doubles are never
        // invoked on the rejection path — a throwing factory proves the validation short-circuits first.
        var factory = new Mock<IDbContextFactory<BeaconContext>>(MockBehavior.Strict);
        var provider = new Mock<IMcpSettingsProvider>(MockBehavior.Strict);
        return new UpdateMcpSettingsHandler(factory.Object, provider.Object, Options.Create(options ?? new McpDeploymentOptions()), TestPermissions.Admin, TestProjectAccess.Actors(), NullLogger<UpdateMcpSettingsHandler>.Instance);
    }

    [TestCase("(unclosed")]
    [TestCase("a{2,1}")]
    [TestCase("*invalid")]
    public async Task Handle_InvalidCustomPiiPattern_Throws(string badPattern)
    {
        var handler = CreateHandler();
        var command = new UpdateMcpSettingsCommand(new McpSettingsData
        {
            CustomPiiPatterns = [badPattern],
        });

        var act = async () => await handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*valid regular expression*");
    }

    // SC4 (global half of spec mcp-project-settings): the global PUT refuses a write that contradicts a
    // deployment lock with SettingLockedException (→ 409) and touches nothing — the strict factory throws
    // on any DB access.
    [Test]
    public async Task Handle_EnforceReadOnlyFalse_UnderForceReadOnly_ThrowsSettingLocked_AndWritesNothing()
    {
        var handler = CreateHandler(new McpDeploymentOptions { ForceReadOnly = true });

        var act = () => handler.Handle(
            new UpdateMcpSettingsCommand(new McpSettingsData { EnforceReadOnly = false }),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<SettingLockedException>();
        ex.Which.FieldName.Should().Be("EnforceReadOnly");
        ex.Which.Message.Should().Contain("ForceReadOnly");
    }

    [Test]
    public async Task Handle_RetainQueryContentTrue_UnderForceNoContentRetention_ThrowsSettingLocked_AndWritesNothing()
    {
        var handler = CreateHandler(new McpDeploymentOptions { ForceNoContentRetention = true });

        var act = () => handler.Handle(
            new UpdateMcpSettingsCommand(new McpSettingsData { RetainQueryContent = true }),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<SettingLockedException>();
        ex.Which.FieldName.Should().Be("RetainQueryContent");
    }

    // Review finding F001: the global GET returns lock/ceiling-RESOLVED values and the page re-sends every
    // field, so a locked or clamped value echoed back unchanged must NOT overwrite the stored value —
    // otherwise lifting the lock/ceiling later would no longer restore the admin's configuration.
    [Test]
    public async Task Handle_ClampedValueEchoedBack_KeepsStoredValue_ButDeliberateEditIsWritten()
    {
        var stored = new McpSettings { MaxRowLimit = 9000, StatementTimeoutSeconds = 120, MaxExplainCost = null };
        var (handler, saves) = CreateHandlerWithRow(
            stored,
            new McpDeploymentOptions
            {
                Ceilings = new McpCeilingOptions { MaxRowLimit = 1000, StatementTimeoutSeconds = 60, MaxExplainCost = 50m }
            });

        // The page showed the resolved values 1000 / 60 / 50 and sends them back; the admin only edited the timeout.
        await handler.Handle(
            new UpdateMcpSettingsCommand(new McpSettingsData { MaxRowLimit = 1000, StatementTimeoutSeconds = 30, MaxExplainCost = 50m }),
            CancellationToken.None);

        stored.MaxRowLimit.Should().Be(9000, "echoed ceiling value is not an edit");
        stored.MaxExplainCost.Should().BeNull("echoed ceiling value is not an edit (null = no limit stays stored)");
        stored.StatementTimeoutSeconds.Should().Be(30, "a value other than the resolved one is a deliberate edit");
        saves().Should().Be(1);
    }

    // Under the row-data lock the page sends the three fields as the lock pins them (off); the values stored before the
    // lock survive an unrelated save, so lifting the lock restores them.
    [Test]
    public async Task AnUnrelatedGlobalSave_UnderTheLock_KeepsTheStoredRowDataFlags()
    {
        var stored = new McpSettings { EnableSampleValueCollection = true, EnableValueGrounding = true, EnableEvalJudge = true, MaxRowLimit = 1000 };
        var (handler, saves) = CreateHandlerWithRow(stored, new McpDeploymentOptions { ForbidRowDataToLlm = true });

        await handler.Handle(
            new UpdateMcpSettingsCommand(new McpSettingsData { EnableSampleValueCollection = false, EnableValueGrounding = false, EnableEvalJudge = false, MaxRowLimit = 250 }),
            CancellationToken.None);

        stored.EnableSampleValueCollection.Should().BeTrue();
        stored.EnableValueGrounding.Should().BeTrue();
        stored.EnableEvalJudge.Should().BeTrue();
        stored.MaxRowLimit.Should().Be(250, "the unrelated field is written");
        saves().Should().Be(1);
    }

    // Without the lock the same save is a deliberate edit and turns them off.
    [Test]
    public async Task AGlobalSave_WithoutTheLock_WritesTheRowDataFlags()
    {
        var stored = new McpSettings { EnableSampleValueCollection = true, EnableValueGrounding = true, EnableEvalJudge = true };
        var (handler, _) = CreateHandlerWithRow(stored, new McpDeploymentOptions());

        await handler.Handle(
            new UpdateMcpSettingsCommand(new McpSettingsData { EnableSampleValueCollection = false, EnableValueGrounding = false, EnableEvalJudge = false }),
            CancellationToken.None);

        stored.EnableSampleValueCollection.Should().BeFalse();
        stored.EnableValueGrounding.Should().BeFalse();
        stored.EnableEvalJudge.Should().BeFalse();
    }

    [Test]
    public async Task Handle_LockedFieldEchoedBack_KeepsStoredValue()
    {
        var stored = new McpSettings { EnforceReadOnly = false, RetainQueryContent = true };
        var (handler, _) = CreateHandlerWithRow(
            stored,
            new McpDeploymentOptions { ForceReadOnly = true, ForceNoContentRetention = true });

        // Under both locks the page can only send the pinned values (anything else is refused with 409).
        await handler.Handle(
            new UpdateMcpSettingsCommand(new McpSettingsData { EnforceReadOnly = true, RetainQueryContent = false, MaxRowLimit = 5 }),
            CancellationToken.None);

        stored.EnforceReadOnly.Should().BeFalse("the lock pins the effective value; the stored admin value survives it");
        stored.RetainQueryContent.Should().BeTrue();
        stored.MaxRowLimit.Should().Be(5, "unlocked fields are written normally");
    }

    [Test]
    public async Task Handle_NoCeilingOrLock_WritesEveryFieldAsSent()
    {
        var stored = new McpSettings { MaxRowLimit = 9000, EnforceReadOnly = false };
        var (handler, _) = CreateHandlerWithRow(stored, Unlocked());

        await handler.Handle(
            new UpdateMcpSettingsCommand(new McpSettingsData { MaxRowLimit = 1000, EnforceReadOnly = true }),
            CancellationToken.None);

        stored.MaxRowLimit.Should().Be(1000);
        stored.EnforceReadOnly.Should().BeTrue();
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task ASecurityAdmin_TurningReadOnlyEnforcementOrPiiDetectionOff_IsForbidden(bool readOnlyOff, bool piiOff)
    {
        var stored = new McpSettings { EnforceReadOnly = true, EnablePiiDetection = true, MaxRowLimit = 100 };
        var (handler, saves) = CreateHandlerWithRow(stored, Unlocked(), TestPermissions.Holding(BeaconPermissions.SecurityAdmin));

        var act = () => handler.Handle(
            new UpdateMcpSettingsCommand(new McpSettingsData { EnforceReadOnly = !readOnlyOff, EnablePiiDetection = !piiOff, MaxRowLimit = 5 }),
            CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(McpSafetySettings.AdminOnlyMessage);
        stored.EnforceReadOnly.Should().BeTrue();
        stored.EnablePiiDetection.Should().BeTrue();
        stored.MaxRowLimit.Should().Be(100, "nothing of a refused change is written");
        saves().Should().Be(0);
    }

    [Test]
    public async Task ASecurityAdmin_TurnsThemOn_AndChangesTheRest()
    {
        var stored = new McpSettings { EnforceReadOnly = false, EnablePiiDetection = false };
        var (handler, _) = CreateHandlerWithRow(stored, Unlocked(), TestPermissions.Holding(BeaconPermissions.SecurityAdmin));

        await handler.Handle(
            new UpdateMcpSettingsCommand(new McpSettingsData { EnforceReadOnly = true, EnablePiiDetection = true, MaxRowLimit = 5 }),
            CancellationToken.None);

        stored.EnforceReadOnly.Should().BeTrue();
        stored.EnablePiiDetection.Should().BeTrue();
        stored.MaxRowLimit.Should().Be(5);
    }

    [Test]
    public async Task AnAdmin_TurnsThemOff()
    {
        var stored = new McpSettings { EnforceReadOnly = true, EnablePiiDetection = true };
        var (handler, _) = CreateHandlerWithRow(stored, Unlocked());

        await handler.Handle(
            new UpdateMcpSettingsCommand(new McpSettingsData { EnforceReadOnly = false, EnablePiiDetection = false }),
            CancellationToken.None);

        stored.EnforceReadOnly.Should().BeFalse();
        stored.EnablePiiDetection.Should().BeFalse();
    }

    // The default deployment locks read-only enforcement and PII detection on, for an Admin too: only the host's
    // configuration lifts the lock.
    [TestCase(false, true, "EnforceReadOnly")]
    [TestCase(true, false, "EnablePiiDetection")]
    public async Task UnderTheDefaultLocks_AnAdminTurningReadOnlyEnforcementOrPiiDetectionOff_IsRefused(bool readOnly, bool pii, string field)
    {
        var stored = new McpSettings { EnforceReadOnly = true, EnablePiiDetection = true, MaxRowLimit = 100 };
        var (handler, saves) = CreateHandlerWithRow(stored, new McpDeploymentOptions(), TestPermissions.Admin);

        var act = () => handler.Handle(
            new UpdateMcpSettingsCommand(new McpSettingsData { EnforceReadOnly = readOnly, EnablePiiDetection = pii, MaxRowLimit = 5 }),
            CancellationToken.None);

        (await act.Should().ThrowAsync<SettingLockedException>()).Which.FieldName.Should().Be(field);
        stored.EnforceReadOnly.Should().BeTrue();
        stored.EnablePiiDetection.Should().BeTrue();
        stored.MaxRowLimit.Should().Be(100, "nothing of a refused change is written");
        saves().Should().Be(0);
    }

    [Test]
    public async Task ASettingsChange_IsRecordedInTheSettingsHistory_WithWhoAndBeforeAndAfter()
    {
        var stored = new McpSettings { EnforceReadOnly = true, EnablePiiDetection = true, MaxRowLimit = 100, AskSystemPrompt = "Be brief." };
        var history = new List<AppSettingHistory>();
        var (handler, saves) = CreateHandlerWithRow(stored, Unlocked(), history: history);

        await handler.Handle(
            new UpdateMcpSettingsCommand(new McpSettingsData { EnforceReadOnly = false, EnablePiiDetection = true, MaxRowLimit = 250, AskSystemPrompt = "Be brief." }),
            CancellationToken.None);

        saves().Should().Be(1);
        history.Select(x => (x.SettingKey, x.OldValue, x.NewValue)).Should().Contain(
        [
            ("Mcp.EnforceReadOnly", "true", "false"),
            ("Mcp.MaxRowLimit", "100", "250")
        ]);
        history.Should().NotContain(x => x.SettingKey == "Mcp.EnablePiiDetection" || x.SettingKey == "Mcp.AskSystemPrompt", "unchanged settings are not recorded");
        history.Should().OnlyContain(x => x.ChangedByUserId == "3" && x.ChangedAt > DateTime.UtcNow.AddMinutes(-1));
    }

    [Test]
    public async Task ASaveThatChangesNothing_RecordsNoHistory()
    {
        var stored = new McpSettings { MaxRowLimit = 100 };
        var history = new List<AppSettingHistory>();
        var (handler, _) = CreateHandlerWithRow(stored, new McpDeploymentOptions(), history: history);

        await handler.Handle(new UpdateMcpSettingsCommand(new McpSettingsData { MaxRowLimit = 100 }), CancellationToken.None);

        history.Should().BeEmpty();
    }

    private static McpDeploymentOptions Unlocked() => new() { ForceReadOnly = false, ForcePiiDetection = false };

    private static (UpdateMcpSettingsHandler Handler, Func<int> Saves) CreateHandlerWithRow(
        McpSettings row,
        McpDeploymentOptions options,
        IBeaconPermissionAccessor? permissions = null,
        List<AppSettingHistory>? history = null)
    {
        var saves = 0;
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new SettingsRowTestContext([row], () => saves++, history ?? []));

        var provider = SettingsProviderMock.Create();

        return (new UpdateMcpSettingsHandler(factory.Object, provider.Object, Options.Create(options), permissions ?? TestPermissions.Admin, TestProjectAccess.Actors(), NullLogger<UpdateMcpSettingsHandler>.Instance), () => saves);
    }

    /// <summary>The single McpSettings row over async-queryable doubles; no DB (§4.7).</summary>
    private sealed class SettingsRowTestContext : BeaconContext
    {
        private static readonly DbContextOptions<SettingsRowTestContext> Options =
            new DbContextOptionsBuilder<SettingsRowTestContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly List<McpSettings> _rows;
        private readonly Action _onSave;
        private readonly List<AppSettingHistory> _history;

        public SettingsRowTestContext(List<McpSettings> rows, Action onSave, List<AppSettingHistory> history) : base(Options, "beacon")
        {
            _rows = rows;
            _onSave = onSave;
            _history = history;
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(AppSettingHistory))
            {
                var history = new Mock<DbSet<AppSettingHistory>>();
                history
                    .Setup(x => x.Add(It.IsAny<AppSettingHistory>()))
                    .Callback<AppSettingHistory>(x => _history.Add(x));
                return (DbSet<TEntity>)(object)history.Object;
            }

            if (typeof(TEntity) == typeof(McpSettings))
            {
                var queryable = _rows.AsQueryable();
                var set = new Mock<DbSet<McpSettings>>();
                set.As<IAsyncEnumerable<McpSettings>>()
                    .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
                    .Returns(() => new TestAsyncEnumerator<McpSettings>(queryable.GetEnumerator()));
                set.As<IQueryable<McpSettings>>()
                    .Setup(x => x.Provider)
                    .Returns(new TestAsyncQueryProvider<McpSettings>(queryable.Provider));
                set.As<IQueryable<McpSettings>>().Setup(x => x.Expression).Returns(queryable.Expression);
                set.As<IQueryable<McpSettings>>().Setup(x => x.ElementType).Returns(queryable.ElementType);
                set.As<IQueryable<McpSettings>>().Setup(x => x.GetEnumerator()).Returns(() => queryable.GetEnumerator());
                return (DbSet<TEntity>)(object)set.Object;
            }

            return base.Set<TEntity>();
        }

        public override int SaveChanges()
        {
            _onSave();
            return 0;
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            _onSave();
            return Task.FromResult(0);
        }
    }
}
