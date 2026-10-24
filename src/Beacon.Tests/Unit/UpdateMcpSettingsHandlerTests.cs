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
        return new UpdateMcpSettingsHandler(factory.Object, provider.Object, Options.Create(options ?? new McpDeploymentOptions()), TestPermissions.Admin, TestProjectAccess.Actors(), new RecordingSecurityAudit(), NullLogger<UpdateMcpSettingsHandler>.Instance);
    }

    [TestCase("(unclosed")]
    [TestCase("a{2,1}")]
    [TestCase("*invalid")]
    public async Task Handle_InvalidCustomPiiPattern_Throws(string badPattern)
    {
        var (handler, saves) = CreateHandlerWithRow(new McpSettings(), Unlocked());
        var command = new UpdateMcpSettingsCommand(new McpSettingsData
        {
            CustomPiiPatterns = [badPattern],
        });

        var act = async () => await handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*valid regular expression*");
        saves().Should().Be(0);
    }

    // A new pattern must run on the linear-time engine: lookarounds, backreferences and atomic groups are refused.
    [TestCase("(?=x)secret")]
    [TestCase("(?<!x)secret")]
    [TestCase(@"(a)\1")]
    [TestCase("(?>a+)b")]
    public async Task Handle_ANewCustomPiiPatternTheLinearEngineCannotRun_IsRefused(string pattern)
    {
        var (handler, saves) = CreateHandlerWithRow(new McpSettings(), Unlocked());

        var act = () => handler.Handle(new UpdateMcpSettingsCommand(new McpSettingsData { CustomPiiPatterns = ["iban", pattern] }), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Custom PII patterns cannot use lookarounds, backreferences or atomic groups.");
        saves().Should().Be(0);
    }

    [Test]
    public async Task Handle_ANewCustomPiiPatternOver500Characters_IsRefused_And500AreWritten()
    {
        var (handler, saves) = CreateHandlerWithRow(new McpSettings(), Unlocked());

        var tooLong = () => handler.Handle(new UpdateMcpSettingsCommand(new McpSettingsData { CustomPiiPatterns = [new string('a', 501)] }), CancellationToken.None);

        await tooLong.Should().ThrowAsync<InvalidOperationException>().WithMessage("Custom PII patterns must be at most 500 characters.");
        saves().Should().Be(0);

        await handler.Handle(new UpdateMcpSettingsCommand(new McpSettingsData { CustomPiiPatterns = [new string('a', 500)] }), CancellationToken.None);

        saves().Should().Be(1);
    }

    // The page re-sends the whole list: a pattern stored before the checks existed is not checked again, so it does not
    // block a save that changes something else.
    [Test]
    public async Task Handle_AStoredLookaroundPattern_DoesNotBlockAnUnrelatedSave()
    {
        var stored = new McpSettings { MaxRowLimit = 1000, CustomPiiPatterns = "[\"(?=x)secret\"]" };
        var (handler, saves) = CreateHandlerWithRow(stored, Unlocked());

        await handler.Handle(
            new UpdateMcpSettingsCommand(new McpSettingsData { MaxRowLimit = 200, CustomPiiPatterns = ["(?=x)secret"] }),
            CancellationToken.None);

        saves().Should().Be(1);
        stored.MaxRowLimit.Should().Be(200);
        stored.CustomPiiPatterns.Should().Be("[\"(?=x)secret\"]");
    }

    // Every numeric setting of the global settings and a project's overrides has a range (McpSettingsRanges) or is listed
    // here with the reason it needs none.
    [Test]
    public void EveryNumericSetting_HasARange_OrIsExemptWithAReason()
    {
        var exempt = new Dictionary<string, string>(StringComparer.Ordinal);
        var numeric = new[] { typeof(McpSettingsData), typeof(McpProjectSettingsData) }
            .SelectMany(x => x.GetProperties())
            .Where(x => IsNumeric(x.PropertyType))
            .Select(x => x.Name)
            .Distinct()
            .ToList();

        numeric.Should().NotBeEmpty();
        numeric.Except(exempt.Keys).Should().BeEquivalentTo(McpSettingsRanges.Fields, "a new numeric setting needs a range, or an exemption with its reason");

        static bool IsNumeric(Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? type;

            return underlying == typeof(int)
                || underlying == typeof(long)
                || underlying == typeof(short)
                || underlying == typeof(double)
                || underlying == typeof(float)
                || underlying == typeof(decimal);
        }
    }

    // A row cap below one row is refused before any DB access (the strict factory throws on one).
    [TestCase(0)]
    [TestCase(-1)]
    public async Task Handle_MaxRowLimitBelowOne_Throws_AndWritesNothing(int maxRowLimit)
    {
        var act = async () => await CreateHandler().Handle(
            new UpdateMcpSettingsCommand(new McpSettingsData { MaxRowLimit = maxRowLimit }),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("MaxRowLimit must be between 1 and 10000000.");
    }

    // A retention window below one day is refused before any DB access (the strict factory throws on one).
    [TestCase(0)]
    [TestCase(-1)]
    public async Task Handle_LearningSignalRetentionDaysBelowOne_Throws_AndWritesNothing(int retentionDays)
    {
        var act = async () => await CreateHandler().Handle(
            new UpdateMcpSettingsCommand(new McpSettingsData { LearningSignalRetentionDays = retentionDays }),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("LearningSignalRetentionDays must be between 1 and 36500.");
    }

    // Every numeric setting outside its range is refused before any DB access (the strict factory throws on one), with a
    // message naming the field and the range.
    [TestCaseSource(nameof(OutOfRangeGlobalValues))]
    public async Task Handle_ANumericSettingOutsideItsRange_IsRefused_NamingTheFieldAndRange(string field, Action<McpSettingsData> set, string range)
    {
        var data = new McpSettingsData();
        set(data);

        var act = async () => await CreateHandler(Unlocked()).Handle(new UpdateMcpSettingsCommand(data), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage($"{field} must be {range}.");
    }

    [Test]
    public async Task Handle_EveryNumericSettingAtTheEdgeOfItsRange_IsWritten()
    {
        var stored = new McpSettings();
        var (handler, saves) = CreateHandlerWithRow(stored, Unlocked());

        await handler.Handle(
            new UpdateMcpSettingsCommand(new McpSettingsData
            {
                MaxRowLimit = 10_000_000,
                LearningAutoApproveThreshold = 1,
                LearningInjectionBudgetChars = 0,
                LearningSignalRetentionDays = 36_500,
                SelfConsistencyCandidateCount = 1,
                ExemplarTopK = 1,
                LearningReplayMinFlips = 1,
                DocChunkWindowSentences = 1,
                DocChunkOverlapSentences = 0,
                GlossaryTopK = 1,
                DocChunkTopK = 1,
                GoldenExemplarTopK = 1,
                GoldenExemplarBudgetChars = 0,
                ValueGroundingMaxProbes = 0,
                SelfConsistencyMinTables = 1,
                StatementTimeoutSeconds = 3600,
                MaxResultBytes = 1,
                MaxExplainCost = 0,
                MaxConcurrentQueriesPerKey = 1
            }),
            CancellationToken.None);

        saves().Should().Be(1);
        stored.StatementTimeoutSeconds.Should().Be(3600);
        stored.MaxConcurrentQueriesPerKey.Should().Be(1);
        stored.ValueGroundingMaxProbes.Should().Be(0);
        stored.MaxExplainCost.Should().Be(0);
    }

    [Test]
    public async Task Handle_OneRowAndOneDay_AreWritten()
    {
        var stored = new McpSettings { MaxRowLimit = 1000, LearningSignalRetentionDays = 90 };
        var (handler, saves) = CreateHandlerWithRow(stored, Unlocked());

        await handler.Handle(
            new UpdateMcpSettingsCommand(new McpSettingsData { MaxRowLimit = 1, LearningSignalRetentionDays = 1 }),
            CancellationToken.None);

        stored.MaxRowLimit.Should().Be(1);
        stored.LearningSignalRetentionDays.Should().Be(1);
        saves().Should().Be(1);
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

    // The global GET hands the page the STORED values next to the resolved ones, so every value a save sends is written as
    // sent: one above a deployment ceiling is kept (it applies again when the ceiling is raised), and an edit to exactly the
    // ceiling is written too.
    [Test]
    public async Task Handle_ValuesAboveACeiling_AreStoredAsSent_AndAnEditToTheCeilingIsWritten()
    {
        var stored = new McpSettings { MaxRowLimit = 9000, StatementTimeoutSeconds = 120, MaxExplainCost = null };
        var (handler, saves) = CreateHandlerWithRow(
            stored,
            new McpDeploymentOptions
            {
                Ceilings = new McpCeilingOptions { MaxRowLimit = 1000, StatementTimeoutSeconds = 60, MaxExplainCost = 50m }
            });

        // The page showed the stored 9000 / 120 / no limit; the admin keeps the row cap and sets the others to the ceiling.
        await handler.Handle(
            new UpdateMcpSettingsCommand(new McpSettingsData { MaxRowLimit = 9000, StatementTimeoutSeconds = 60, MaxExplainCost = 50m }),
            CancellationToken.None);

        stored.MaxRowLimit.Should().Be(9000, "a stored value above the ceiling is kept as sent");
        stored.StatementTimeoutSeconds.Should().Be(60, "an edit to exactly the ceiling is an edit");
        stored.MaxExplainCost.Should().Be(50m);
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

    internal static IEnumerable<TestCaseData> OutOfRangeGlobalValues()
    {
        yield return Case("MaxRowLimit", x => x.MaxRowLimit = 10_000_001, "between 1 and 10000000");
        yield return Case("LearningAutoApproveThreshold", x => x.LearningAutoApproveThreshold = -0.1, "between 0 and 1");
        yield return Case("LearningAutoApproveThreshold", x => x.LearningAutoApproveThreshold = 1.5, "between 0 and 1");
        yield return Case("LearningAutoApproveThreshold", x => x.LearningAutoApproveThreshold = double.NaN, "between 0 and 1");
        yield return Case("LearningInjectionBudgetChars", x => x.LearningInjectionBudgetChars = -1, "at least 0");
        yield return Case("LearningSignalRetentionDays", x => x.LearningSignalRetentionDays = 36_501, "between 1 and 36500");
        yield return Case("SelfConsistencyCandidateCount", x => x.SelfConsistencyCandidateCount = 0, "at least 1");
        yield return Case("ExemplarTopK", x => x.ExemplarTopK = 0, "at least 1");
        yield return Case("LearningReplayMinFlips", x => x.LearningReplayMinFlips = 0, "at least 1");
        yield return Case("DocChunkWindowSentences", x => x.DocChunkWindowSentences = 0, "at least 1");
        yield return Case("DocChunkOverlapSentences", x => x.DocChunkOverlapSentences = -1, "at least 0");
        yield return Case("GlossaryTopK", x => x.GlossaryTopK = 0, "at least 1");
        yield return Case("DocChunkTopK", x => x.DocChunkTopK = 0, "at least 1");
        yield return Case("GoldenExemplarTopK", x => x.GoldenExemplarTopK = 0, "at least 1");
        yield return Case("GoldenExemplarBudgetChars", x => x.GoldenExemplarBudgetChars = -1, "at least 0");
        yield return Case("ValueGroundingMaxProbes", x => x.ValueGroundingMaxProbes = -1, "at least 0");
        yield return Case("SelfConsistencyMinTables", x => x.SelfConsistencyMinTables = 0, "at least 1");
        yield return Case("StatementTimeoutSeconds", x => x.StatementTimeoutSeconds = 0, "between 1 and 3600");
        yield return Case("StatementTimeoutSeconds", x => x.StatementTimeoutSeconds = 3601, "between 1 and 3600");
        yield return Case("MaxResultBytes", x => x.MaxResultBytes = 0, "at least 1");
        yield return Case("MaxExplainCost", x => x.MaxExplainCost = -1, "between 0 and 9999999999999999.99");
        yield return Case("MaxConcurrentQueriesPerKey", x => x.MaxConcurrentQueriesPerKey = 0, "at least 1");

        static TestCaseData Case(string field, Action<McpSettingsData> set, string range) =>
            new TestCaseData(field, set, range).SetArgDisplayNames(field, range);
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

        return (new UpdateMcpSettingsHandler(factory.Object, provider.Object, Options.Create(options), permissions ?? TestPermissions.Admin, TestProjectAccess.Actors(), new RecordingSecurityAudit(), NullLogger<UpdateMcpSettingsHandler>.Instance), () => saves);
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
