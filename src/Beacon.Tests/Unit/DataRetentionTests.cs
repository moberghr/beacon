using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.Services.Retention;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// The data retention job (<c>Beacon:Retention</c>): each target's purge SQL on both providers (no database, §4.7), how
/// the job resolves windows and isolates a failing target, the content expiry, the options validation and the lock.
/// </summary>
[TestFixture]
public class DataRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 3, 50, 0, TimeSpan.Zero);
    private static readonly DateTime Cutoff = new(2026, 7, 26, 3, 50, 0, DateTimeKind.Utc);

    [TestCase(false, "notifications", "query_execution_history", "created_time <")]
    [TestCase(true, "[Notifications]", "[QueryExecutionHistory]", "[CreatedTime] <")]
    public async Task SubscriptionRuns_DeleteTheirNotificationsFirst_InBatches_Translates(bool sqlServer, string notifications, string runs, string expiry)
    {
        var capture = Capture(sqlServer)
            .ThenRows([5], [6])
            .ThenRowsAffected(3)
            .ThenRowsAffected(2)
            .ThenNoRows();
        await using var context = await capture.Factory().CreateDbContextAsync();

        var deleted = await new SubscriptionRunsRetentionTarget().DeleteExpiredAsync(context, Cutoff, CancellationToken.None);

        deleted.Should().Be(2, "the target counts the runs it deleted");
        capture.Commands.Should().HaveCount(4);
        capture.Commands[0].Should().StartWith("SELECT").And.Contain(expiry);
        capture.Commands[1].Should().Contain("DELETE").And.Contain(notifications);
        capture.Commands[2].Should().Contain("DELETE").And.Contain(runs).And.Contain(expiry);
        capture.Commands[3].Should().StartWith("SELECT", "a full batch is followed by another read until none is left");
        capture.CommandParameters[0].Values.Should().Contain(Cutoff);
    }

    [TestCase(false, "query_step_change_history", "ai_actor_conversations")]
    [TestCase(true, "[QueryStepChangeHistory]", "[AiActorConversations]")]
    public void AiActorExecutions_StillReferencedAreKept_Translates(bool sqlServer, string changeHistory, string turns)
    {
        using BeaconContext context = sqlServer ? SqlServerTestContext.Create() : NpgsqlTestContext.Create();

        var sql = AiActorExecutionsRetentionTarget.ExpiredExecutions(context, context.AiActorExecutions, Cutoff).ToQueryString();

        sql.Should().Contain("NOT EXISTS").And.Contain(changeHistory).And.Contain(turns);
    }

    [TestCase(false, "parent_execution_id", "status")]
    [TestCase(true, "[ParentExecutionId]", "[Status]")]
    public void MigrationRuns_InFlightRunsAndParentsOfRetriesAreKept_Translates(bool sqlServer, string parent, string status)
    {
        using BeaconContext context = sqlServer ? SqlServerTestContext.Create() : NpgsqlTestContext.Create();

        var sql = MigrationRunsRetentionTarget.Expired(context, context.MigrationExecutions, Cutoff).ToQueryString();

        sql.Should().Contain("NOT EXISTS").And.Contain(parent).And.Contain(status);
    }

    [TestCase(false, "mcp_eval_results", "mcp_eval_runs")]
    [TestCase(true, "[McpEvalResults]", "[McpEvalRuns]")]
    public async Task McpEvalRuns_DeleteTheirResultsFirst_AndKeepRunningRuns_Translates(bool sqlServer, string results, string runs)
    {
        var capture = Capture(sqlServer)
            .ThenRows([9])
            .ThenRowsAffected(4)
            .ThenRowsAffected(1)
            .ThenNoRows();
        await using var context = await capture.Factory().CreateDbContextAsync();

        var deleted = await new McpEvalRunsRetentionTarget().DeleteExpiredAsync(context, Cutoff, CancellationToken.None);

        deleted.Should().Be(1);
        capture.Commands[1].Should().Contain("DELETE").And.Contain(results);
        capture.Commands[2].Should().Contain("DELETE").And.Contain(runs);
        capture.Commands[0].Should().Contain($"'{McpEvalRunsRetentionTarget.RunningStatus}'", "a running eval keeps its run");
    }

    [Test]
    public async Task AnomalyBaselines_KeepTheLongestLookBackBeyondTheWindow()
    {
        var capture = new SqlCapture()
            .ThenScalar(45)
            .ThenNoRows();
        await using var context = await capture.Factory().CreateDbContextAsync();

        await new AnomalyBaselinesRetentionTarget().DeleteExpiredAsync(context, Cutoff, CancellationToken.None);

        capture.Commands[0].Should().Contain("max(").And.Contain("lookback_days");
        capture.CommandParameters[1].Values.Should().Contain(Cutoff.AddDays(-45));
    }

    [TestCase(false, "compiled_sql", "results", "comment")]
    [TestCase(true, "[CompiledSql]", "[Results]", "[Comment]")]
    public async Task ContentExpiry_ClearsContentColumns_AndKeepsStructure_Translates(bool sqlServer, string sql, string results, string comment)
    {
        var capture = Capture(sqlServer)
            .ThenRows([7])
            .ThenRowsAffected(1)
            .ThenNoRows()
            .ThenNoRows()
            .ThenNoRows()
            .ThenNoRows()
            .ThenNoRows()
            .ThenNoRows();
        await using var context = await capture.Factory().CreateDbContextAsync();

        var cleared = await ContentExpiry.ClearAsync(context, Cutoff, CancellationToken.None);

        cleared[nameof(Beacon.Core.Data.Entities.QueryExecutionHistory)].Should().Be(1);
        capture.Commands.Should().HaveCount(8, "one batch on the run history, then one empty read per store");
        capture.Commands[0].Should().Contain(sql).And.Contain(results);
        var update = capture.Commands[1];
        update.Should().StartWith("UPDATE").And.Contain(sql).And.Contain(results);
        update.Should().NotContain(comment, "the comment holds only generic reasons and stays");
    }

    [Test]
    public void ContentExpiry_CoversExactlyTheRegisteredContentStores()
    {
        ContentExpiry.StoreTypes.Should().BeEquivalentTo(McpRetentionDenyList.ContentStores);

        foreach (var store in McpRetentionDenyList.ContentStores)
        {
            ContentExpiry.ContentProperties(store).Should().NotBeEmpty("{0} is a content store", store.Name);
        }
    }

    [Test]
    public async Task Run_UsesEachTargetsWindow_AndSkipsAKeptForeverTarget()
    {
        var defaulted = Target("defaulted");
        var configured = Target("configured");
        var keptForever = Target("kept-forever");
        var options = new RetentionOptions
        {
            Days = RetentionOptions.RecommendedDays,
            Targets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["configured"] = 7, ["kept-forever"] = 0 }
        };

        var run = await Service(options, Unlocked(), defaulted.Object, configured.Object, keptForever.Object).RunAsync(CancellationToken.None);

        defaulted.Verify(x => x.DeleteExpiredAsync(It.IsAny<BeaconContext>(), Now.UtcDateTime.AddDays(-RetentionOptions.RecommendedDays), It.IsAny<CancellationToken>()), Times.Once);
        configured.Verify(x => x.DeleteExpiredAsync(It.IsAny<BeaconContext>(), Now.UtcDateTime.AddDays(-7), It.IsAny<CancellationToken>()), Times.Once);
        keptForever.Verify(x => x.DeleteExpiredAsync(It.IsAny<BeaconContext>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        run.Deleted.Should().BeEquivalentTo(new Dictionary<string, int> { ["defaulted"] = 1, ["configured"] = 1 });
        run.ContentCleared.Should().Be(0);
    }

    [Test]
    public async Task Run_AFailingTargetDoesNotStopTheOthers_AndTheRunFails()
    {
        var failing = Target("failing");
        failing
            .Setup(x => x.DeleteExpiredAsync(It.IsAny<BeaconContext>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var after = Target("after");

        var act = () => Service(new RetentionOptions { Days = 30 }, Unlocked(), failing.Object, after.Object).RunAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("failing").And.NotContain("boom");
        after.Verify(x => x.DeleteExpiredAsync(It.IsAny<BeaconContext>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Run_UnderTheContentLock_ClearsResultSnapshotsOfEveryAge()
    {
        var capture = new SqlCapture()
            .ThenRows([3])
            .ThenRowsAffected(1)
            .ThenNoRows()
            .ThenNoRows();
        var policy = new Mock<IContentRetentionPolicy>();
        policy
            .Setup(x => x.ResolveAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentRetentionDecision(false, false));
        var service = new DataRetentionService(
            capture.Factory(), [], Options.Create(new RetentionOptions()), policy.Object, new FakeTimeProvider(Now), NullLoggerFactory.Instance);

        var run = await service.RunAsync(CancellationToken.None);

        run.ContentCleared.Should().Be(1);
        capture.Commands[0].Should().Contain("results").And.NotContain("compiled_sql", "only the result snapshot goes whatever its age");
        capture.CommandParameters[0].Values.Should().Contain(Now.UtcDateTime);
        capture.Commands[1].Should().StartWith("UPDATE").And.Contain("query_execution_history");
        capture.Commands[3].Should().Contain("notifications");
    }

    [Test]
    public async Task Run_WithoutTheContentLockOrAContentWindow_ClearsNoContent()
    {
        var capture = new SqlCapture();
        var service = new DataRetentionService(
            capture.Factory(), [], Options.Create(new RetentionOptions()), Unlocked(), new FakeTimeProvider(Now), NullLoggerFactory.Instance);

        await service.RunAsync(CancellationToken.None);

        capture.Commands.Should().BeEmpty();
    }

    [Test]
    public void Defaults_DeleteNothing()
    {
        var options = new RetentionOptions();

        options.Days.Should().Be(0, "an upgrade must not delete existing data");
        options.ContentDays.Should().Be(0);
        options.Locked.Should().BeFalse();
        options.DaysFor(RetentionTargetNames.SubscriptionRuns).Should().Be(0);
        new McpAuditOptions().RetentionDays.Should().BeNull("the MCP audit is kept forever unless configured");
        Validate(options).Succeeded.Should().BeTrue();
    }

    [Test]
    public async Task Run_WithTheDefaults_CallsNoTarget()
    {
        var target = Target("any");

        await Service(new RetentionOptions(), Unlocked(), target.Object).RunAsync(CancellationToken.None);

        target.Verify(x => x.DeleteExpiredAsync(It.IsAny<BeaconContext>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public void StartupNote_FiresOnlyWhileNoRetentionIsConfigured()
    {
        Beacon.Api.RetentionStartupNote.IsUnconfigured(new RetentionOptions(), new McpDeploymentOptions()).Should().BeTrue();
        Beacon.Api.RetentionStartupNote.IsUnconfigured(new RetentionOptions { Days = 90 }, new McpDeploymentOptions()).Should().BeFalse();
        Beacon.Api.RetentionStartupNote.IsUnconfigured(new RetentionOptions(), new McpDeploymentOptions { Audit = new McpAuditOptions { RetentionDays = 365 } }).Should().BeFalse();
    }

    [Test]
    public void Validation_RefusesAnUnknownTargetAndANegativeWindow()
    {
        var options = new RetentionOptions
        {
            Days = -1,
            Targets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["subscription-run"] = 30 }
        };

        var result = Validate(options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(x => x.Contains("Targets:subscription-run names no retention target"));
        result.Failures.Should().Contain(x => x.Contains("Beacon:Retention:Days must be between 0"));
    }

    [TestCase(0, 30, 365, null, "Beacon:Retention:Days")]
    [TestCase(90, 0, 365, null, "Beacon:Retention:ContentDays")]
    [TestCase(90, 30, 0, null, "Beacon:Mcp:Audit:RetentionDays")]
    [TestCase(90, 30, 365, 0, "Beacon:Retention:Targets:notifications")]
    public void Validation_UnderTheLock_RefusesAWindowThatKeepsDataForever(int days, int contentDays, int mcpAuditDays, int? notificationDays, string setting)
    {
        var options = new RetentionOptions { Days = days, ContentDays = contentDays, Locked = true };
        if (notificationDays is { } value)
        {
            options.Targets[RetentionTargetNames.Notifications] = value;
        }

        var result = Validate(options, new McpDeploymentOptions { Audit = new McpAuditOptions { RetentionDays = mcpAuditDays } });

        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainSingle()
            .Which.Should().Contain(setting).And.Contain("Locked is on");
    }

    [Test]
    public void Validation_UnderTheLock_AcceptsFiniteWindows()
    {
        var options = new RetentionOptions { Days = RetentionOptions.RecommendedDays, ContentDays = RetentionOptions.RecommendedRegulatedContentDays, Locked = true };
        var mcp = new McpDeploymentOptions { Audit = new McpAuditOptions { RetentionDays = McpAuditOptions.RecommendedRetentionDays } };

        Validate(options, mcp).Succeeded.Should().BeTrue();
    }

    [Test]
    public void Lock_CapsTheLearningSignalCeilingAtTheRetentionWindow()
    {
        var unlocked = new McpDeploymentOptions();
        var locked = new McpDeploymentOptions();
        var lowerAlready = new McpDeploymentOptions { Ceilings = new McpCeilingOptions { LearningSignalRetentionDays = 30 } };

        RetentionLock.CapLearningSignals(unlocked, new RetentionOptions());
        RetentionLock.CapLearningSignals(locked, new RetentionOptions { Days = 90, Locked = true });
        RetentionLock.CapLearningSignals(lowerAlready, new RetentionOptions { Days = 90, Locked = true });

        unlocked.Ceilings.LearningSignalRetentionDays.Should().BeNull();
        locked.Ceilings.LearningSignalRetentionDays.Should().Be(90);
        lowerAlready.Ceilings.LearningSignalRetentionDays.Should().Be(30);
    }

    [Test]
    public void LearningSignalCeiling_ClampsTheGlobalAndTheProjectWindow()
    {
        var options = new McpDeploymentOptions { Ceilings = new McpCeilingOptions { LearningSignalRetentionDays = 60 } };
        var global = new McpSettingsData { LearningSignalRetentionDays = 365 };

        var resolvedGlobal = McpSettingsProvider.Resolve(global, null, options);
        var resolvedProject = McpSettingsProvider.Resolve(new McpSettingsData { LearningSignalRetentionDays = 30 }, new McpProjectSettingsData { LearningSignalRetentionDays = 400 }, options);

        resolvedGlobal.Effective.LearningSignalRetentionDays.Should().Be(60);
        resolvedProject.Effective.LearningSignalRetentionDays.Should().Be(60);
    }

    [Test]
    public void McpDeploymentValidation_AcceptsZeroAsKeepForever_AndRefusesANegativeWindow()
    {
        var validator = new McpDeploymentOptionsValidator();

        validator.Validate(null, new McpDeploymentOptions { Audit = new McpAuditOptions { RetentionDays = 0 } }).Succeeded.Should().BeTrue();
        validator.Validate(null, new McpDeploymentOptions { Audit = new McpAuditOptions { RetentionDays = -1 } }).Failed.Should().BeTrue();
    }

    [Test]
    public async Task McpAuditRetention_ZeroDays_IsANoOpAndNeverOpensAContext()
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        var options = Options.Create(new McpDeploymentOptions { Audit = new McpAuditOptions { RetentionDays = 0 } });
        var service = new McpAuditRetentionService(factory.Object, options, TimeProvider.System, NullLoggerFactory.Instance);

        var deleted = await service.PurgeExpiredAsync(CancellationToken.None);

        deleted.Should().Be(0);
        factory.Verify(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    private static SqlCapture Capture(bool sqlServer) => sqlServer ? SqlCapture.OnSqlServer() : new SqlCapture();

    private static Mock<IRetentionTarget> Target(string name)
    {
        var target = new Mock<IRetentionTarget>();
        target.SetupGet(x => x.Name).Returns(name);
        target
            .Setup(x => x.DeleteExpiredAsync(It.IsAny<BeaconContext>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        return target;
    }

    private static IContentRetentionPolicy Unlocked()
    {
        var policy = new Mock<IContentRetentionPolicy>();
        policy
            .Setup(x => x.ResolveAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentRetentionDecision(true, true));

        return policy.Object;
    }

    private static DataRetentionService Service(RetentionOptions options, IContentRetentionPolicy policy, params IRetentionTarget[] targets) =>
        new(new SqlCapture().Factory(), targets, Options.Create(options), policy, new FakeTimeProvider(Now), NullLoggerFactory.Instance);

    private static ValidateOptionsResult Validate(RetentionOptions options, McpDeploymentOptions? mcp = null)
    {
        IRetentionTarget[] targets =
        [
            new SubscriptionRunsRetentionTarget(),
            new NotificationsRetentionTarget(),
            new AnomalyBaselinesRetentionTarget(),
            new AiActorExecutionsRetentionTarget(),
            new MigrationRunsRetentionTarget(),
            new McpEvalRunsRetentionTarget()
        ];

        return new RetentionOptionsValidator(targets, Options.Create(mcp ?? new McpDeploymentOptions())).Validate(null, options);
    }
}
