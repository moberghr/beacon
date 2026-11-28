using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Eval;
using Beacon.AI.Services.LlmProviders;
using Beacon.AI.Services.Mcp;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.McpEval;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.Core.Worker.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// SC12 — the nightly scheduled eval (<see cref="JobService.RunScheduledEvals"/>). It starts one <see cref="McpEvalRunTrigger.Scheduled"/>
/// run, without a requester, for each project whose effective <c>EnableScheduledEval</c> is on and that has an active golden
/// case, one project after another in id order. With the setting off everywhere (the default) the eval service is never
/// called, so no LLM call is made. A project's failure is logged by ids and failure code only and the next project still
/// runs; the job's own cancellation ends the job. The selection reads the database through async doubles or a SQL capture,
/// never a database (§4.7).
/// </summary>
[TestFixture]
public class ScheduledEvalTests
{
    [Test]
    public async Task RunScheduledEvals_WithTheSettingOffEverywhere_StartsNoRun_AndNeverCallsTheEvalService()
    {
        var eval = new Mock<IMcpEvalService>(MockBehavior.Strict);
        var settings = SettingsProviderMock.Create(new McpSettingsData());
        using var log = new LogRecorder();

        await CreateJobService(MemoryFactory([ActiveCase(1), ActiveCase(2)], [1, 2]), eval.Object, settings.Object, log)
            .RunScheduledEvals(CancellationToken.None);

        new McpSettingsData().EnableScheduledEval.Should().BeFalse("the scheduled eval spends LLM calls, so it ships off");
        settings.Verify(x => x.GetEffectiveSettingsAsync(1, It.IsAny<CancellationToken>()), Times.Once);
        settings.Verify(x => x.GetEffectiveSettingsAsync(2, It.IsAny<CancellationToken>()), Times.Once);
        eval.VerifyNoOtherCalls();
        log.Entries.Should().NotContain(x => x.Level >= LogLevel.Warning);
    }

    [Test]
    public async Task RunScheduledEvals_OnForOneProjectOfTwo_StartsAndRunsExactlyOneScheduledRun_ForThatProject()
    {
        using var cancellation = new CancellationTokenSource();
        var eval = new Mock<IMcpEvalService>(MockBehavior.Strict);
        eval
            .Setup(x => x.StartRunAsync(2, null, McpEvalRunTrigger.Scheduled, cancellation.Token))
            .ReturnsAsync(40);
        eval
            .Setup(x => x.RunAsync(40, cancellation.Token))
            .Returns(Task.CompletedTask);
        var settings = SettingsProviderMock.Create(
            new McpSettingsData(),
            projectSettings: new Dictionary<int, McpSettingsData>
            {
                [1] = new() { EnableScheduledEval = false },
                [2] = new() { EnableScheduledEval = true }
            });
        using var log = new LogRecorder();

        await CreateJobService(MemoryFactory([ActiveCase(1), ActiveCase(2), ActiveCase(2)], [1, 2]), eval.Object, settings.Object, log)
            .RunScheduledEvals(cancellation.Token);

        eval.Verify(x => x.StartRunAsync(2, null, McpEvalRunTrigger.Scheduled, cancellation.Token), Times.Once);
        eval.Verify(x => x.RunAsync(40, cancellation.Token), Times.Once);
        eval.VerifyNoOtherCalls();
        log.Entries.Should().NotContain(x => x.Level >= LogLevel.Warning);
    }

    [Test]
    public async Task RunScheduledEvals_RunsTheProjects_OneAfterAnother_InIdOrder()
    {
        var calls = new List<string>();
        var eval = new Mock<IMcpEvalService>(MockBehavior.Strict);
        eval
            .Setup(x => x.StartRunAsync(It.IsAny<int?>(), null, McpEvalRunTrigger.Scheduled, It.IsAny<CancellationToken>()))
            .Returns((int? projectId, int? _, McpEvalRunTrigger _, CancellationToken _) =>
            {
                calls.Add($"start {projectId}");

                return Task.FromResult(projectId!.Value * 10);
            });
        eval
            .Setup(x => x.RunAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((int runId, CancellationToken _) =>
            {
                calls.Add($"run {runId}");

                return Task.CompletedTask;
            });
        var settings = SettingsProviderMock.Create(new McpSettingsData { EnableScheduledEval = true });
        using var log = new LogRecorder();

        await CreateJobService(MemoryFactory([ActiveCase(3), ActiveCase(1), ActiveCase(2)], [1, 2, 3]), eval.Object, settings.Object, log)
            .RunScheduledEvals(CancellationToken.None);

        calls.Should().Equal("start 1", "run 10", "start 2", "run 20", "start 3", "run 30");
        log.Entries.Should().NotContain(x => x.Level >= LogLevel.Warning);
    }

    [Test]
    public async Task RunScheduledEvals_AProjectWithoutAnActiveGoldenCase_GetsNoRun_EvenWithTheSettingOn()
    {
        // Project 1 has only an inactive case, project 3 has none, project 4's case belongs to a project that is gone.
        var eval = new Mock<IMcpEvalService>(MockBehavior.Strict);
        eval
            .Setup(x => x.StartRunAsync(2, null, McpEvalRunTrigger.Scheduled, It.IsAny<CancellationToken>()))
            .ReturnsAsync(7);
        eval
            .Setup(x => x.RunAsync(7, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var settings = SettingsProviderMock.Create(new McpSettingsData { EnableScheduledEval = true });
        using var log = new LogRecorder();
        var inactive = ActiveCase(1);
        inactive.IsActive = false;

        await CreateJobService(MemoryFactory([inactive, ActiveCase(2), ActiveCase(4)], [1, 2, 3]), eval.Object, settings.Object, log)
            .RunScheduledEvals(CancellationToken.None);

        eval.Verify(x => x.StartRunAsync(2, null, McpEvalRunTrigger.Scheduled, It.IsAny<CancellationToken>()), Times.Once);
        eval.Verify(x => x.RunAsync(7, It.IsAny<CancellationToken>()), Times.Once);
        eval.VerifyNoOtherCalls();
        settings.Verify(x => x.GetEffectiveSettingsAsync(It.IsNotIn(2), It.IsAny<CancellationToken>()), Times.Never);
        log.Entries.Should().NotContain(x => x.Level >= LogLevel.Warning);
    }

    [Test]
    public async Task RunScheduledEvals_AFailingProject_DoesNotStopTheNext_AndItsLogCarriesIdsAndFailureCodesOnly()
    {
        // Project 1's run fails with a message naming a table and SQL; project 2 cannot start, on a timeout that is not the
        // job's cancellation; project 3 still runs.
        var eval = new Mock<IMcpEvalService>(MockBehavior.Strict);
        eval
            .Setup(x => x.StartRunAsync(1, null, McpEvalRunTrigger.Scheduled, It.IsAny<CancellationToken>()))
            .ReturnsAsync(11);
        eval
            .Setup(x => x.RunAsync(11, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("relation public.salaries failed: SELECT ssn FROM salaries"));
        eval
            .Setup(x => x.StartRunAsync(2, null, McpEvalRunTrigger.Scheduled, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("provider timeout for secret-endpoint"));
        eval
            .Setup(x => x.StartRunAsync(3, null, McpEvalRunTrigger.Scheduled, It.IsAny<CancellationToken>()))
            .ReturnsAsync(13);
        eval
            .Setup(x => x.RunAsync(13, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var settings = SettingsProviderMock.Create(new McpSettingsData { EnableScheduledEval = true });
        using var log = new LogRecorder();

        var act = () => CreateJobService(MemoryFactory([ActiveCase(1), ActiveCase(2), ActiveCase(3)], [1, 2, 3]), eval.Object, settings.Object, log)
            .RunScheduledEvals(CancellationToken.None);

        await act.Should().NotThrowAsync();
        eval.Verify(x => x.RunAsync(13, It.IsAny<CancellationToken>()), Times.Once);
        var errors = log.Entries
            .Where(x => x.Level == LogLevel.Error)
            .ToList();
        errors.Should().HaveCount(2);
        errors[0].Message.Should().Contain("project 1").And.Contain("run 11").And.Contain("InvalidOperationException (error code");
        errors[1].Message.Should().Contain("project 2").And.Contain("TaskCanceledException");
        foreach (var error in errors)
        {
            error.Exception.Should().BeNull();
            error.FullText.Should().NotContain("salaries").And.NotContain("ssn").And.NotContain("secret-endpoint");
        }
    }

    [Test]
    public async Task RunScheduledEvals_TheJobsCancellation_EndsTheJob_AndStartsNoFurtherProject()
    {
        using var cancellation = new CancellationTokenSource();
        var eval = new Mock<IMcpEvalService>(MockBehavior.Strict);
        eval
            .Setup(x => x.StartRunAsync(1, null, McpEvalRunTrigger.Scheduled, cancellation.Token))
            .ReturnsAsync(11);
        eval
            .Setup(x => x.RunAsync(11, cancellation.Token))
            .Callback(cancellation.Cancel)
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        var settings = SettingsProviderMock.Create(new McpSettingsData { EnableScheduledEval = true });
        using var log = new LogRecorder();

        var act = () => CreateJobService(MemoryFactory([ActiveCase(1), ActiveCase(2)], [1, 2]), eval.Object, settings.Object, log)
            .RunScheduledEvals(cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        eval.Verify(x => x.StartRunAsync(2, It.IsAny<int?>(), It.IsAny<McpEvalRunTrigger>(), It.IsAny<CancellationToken>()), Times.Never);
        log.Entries.Should().NotContain(x => x.Level == LogLevel.Error);
    }

    [Test]
    public async Task RunScheduledEvals_SelectsTheProjects_InSql_OnPostgreSql()
    {
        var capture = new SqlCapture().ThenRows([4], [9]);
        var settings = SettingsProviderMock.Create(new McpSettingsData());
        var eval = new Mock<IMcpEvalService>(MockBehavior.Strict);
        using var log = new LogRecorder();

        await CreateJobService(capture.Factory(), eval.Object, settings.Object, log).RunScheduledEvals(CancellationToken.None);

        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().Contain("SELECT DISTINCT m.project_id")
            .And.Contain("FROM beacon.mcp_eval_cases AS m")
            .And.Contain("WHERE m.is_active")
            .And.Contain("FROM beacon.projects AS p")
            .And.Contain("p.archived_time IS NULL")
            .And.Contain("p.id = m.project_id")
            .And.Contain("ORDER BY m0.project_id");
        settings.Verify(x => x.GetEffectiveSettingsAsync(4, It.IsAny<CancellationToken>()), Times.Once);
        settings.Verify(x => x.GetEffectiveSettingsAsync(9, It.IsAny<CancellationToken>()), Times.Once);
        eval.VerifyNoOtherCalls();
    }

    [Test]
    public async Task RunScheduledEvals_SelectsTheProjects_InSql_OnSqlServer()
    {
        var capture = SqlCapture.OnSqlServer().ThenRows([4]);
        var settings = SettingsProviderMock.Create(new McpSettingsData());
        var eval = new Mock<IMcpEvalService>(MockBehavior.Strict);
        using var log = new LogRecorder();

        await CreateJobService(capture.Factory(), eval.Object, settings.Object, log).RunScheduledEvals(CancellationToken.None);

        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().Contain("SELECT DISTINCT [m].[ProjectId]")
            .And.Contain("FROM [beacon].[McpEvalCases] AS [m]")
            .And.Contain("WHERE [m].[IsActive] = CAST(1 AS bit)")
            .And.Contain("FROM [beacon].[Projects] AS [p]")
            .And.Contain("[p].[ArchivedTime] IS NULL")
            .And.Contain("[p].[Id] = [m].[ProjectId]")
            .And.Contain("ORDER BY [m0].[ProjectId]");
        settings.Verify(x => x.GetEffectiveSettingsAsync(4, It.IsAny<CancellationToken>()), Times.Once);
        eval.VerifyNoOtherCalls();
    }

    [TestCase(McpEvalRunTrigger.Manual, 3)]
    [TestCase(McpEvalRunTrigger.Scheduled, null)]
    public async Task StartRunAsync_StoresTheTrigger_AndTheRequester_OnTheRun(McpEvalRunTrigger trigger, int? userId)
    {
        var saved = new List<object>();
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(new Dictionary<Type, object>(), saved));
        var service = new McpEvalService(
            factory.Object,
            Mock.Of<IAskSqlPipeline>(),
            Mock.Of<IDataSourceProviderFactory>(),
            Mock.Of<IQueryGuardrailService>(),
            Mock.Of<ISqlExecutionGate>(),
            TestConfiguredDatabases.None,
            TestRowSecurity.Resolver,
            SettingsProviderMock.Create().Object,
            Mock.Of<ILlmProvider>(MockBehavior.Strict),
            TestRowDataEgress.Discarding(),
            NullLogger<McpEvalService>.Instance);

        await service.StartRunAsync(5, userId, trigger, CancellationToken.None);

        var run = saved.OfType<McpEvalRun>().Should().ContainSingle().Subject;
        run.ProjectId.Should().Be(5);
        run.TriggeredByUserId.Should().Be(userId);
        run.Trigger.Should().Be(trigger);
        run.Status.Should().Be("Running");
    }

    // The run list carries each run's trigger, so a scheduled run can be told from one a person started.
    [Test]
    public async Task GetEvalRuns_ReturnsEachRunsTrigger()
    {
        var runs = new List<McpEvalRun>
        {
            new() { Id = 1, ProjectId = 5, TriggeredByUserId = 3, Trigger = McpEvalRunTrigger.Manual, CreatedTime = DateTime.UtcNow.AddHours(-2) },
            new() { Id = 2, ProjectId = 5, Trigger = McpEvalRunTrigger.Scheduled, CreatedTime = DateTime.UtcNow.AddHours(-1) }
        };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(
                new Dictionary<Type, object> { [typeof(McpEvalRun)] = RecordingBeaconContext.MemorySet(runs, []) },
                []));

        var items = await new GetEvalRunsHandler(factory.Object).Handle(new GetEvalRunsQuery { ProjectId = 5 }, CancellationToken.None);

        items.Select(x => (x.Id, x.Trigger, x.TriggeredByUserId))
            .Should()
            .Equal((2, McpEvalRunTrigger.Scheduled, (int?)null), (1, McpEvalRunTrigger.Manual, (int?)3));
    }

    private static McpEvalCase ActiveCase(int projectId)
    {
        return new McpEvalCase
        {
            ProjectId = projectId,
            DataSourceId = 1,
            Question = "how many orders?",
            GoldSql = "SELECT 1",
            IsActive = true
        };
    }

    private static IDbContextFactory<BeaconContext> MemoryFactory(List<McpEvalCase> cases, int[] projectIds)
    {
        var projects = projectIds
            .Select(x =>
                new Project
                {
                    Id = x,
                    Name = $"project {x}"
                })
            .ToList();
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(
                new Dictionary<Type, object>
                {
                    [typeof(McpEvalCase)] = RecordingBeaconContext.MemorySet(cases, []),
                    [typeof(Project)] = RecordingBeaconContext.MemorySet(projects, [])
                },
                []));

        return factory.Object;
    }

    private static JobService CreateJobService(
        IDbContextFactory<BeaconContext> factory,
        IMcpEvalService eval,
        IMcpSettingsProvider settings,
        LogRecorder log)
    {
        return new JobService(
            factory,
            Mock.Of<IQueryService>(),
            Mock.Of<INotificationService>(),
            Mock.Of<ITaskService>(),
            Mock.Of<IAnomalyDetectionService>(),
            Mock.Of<IDataQualityEvaluationService>(),
            log.For<JobService>(),
            new RunAsScope(TestRequestPrincipal.OffRequest()),
            TestChangeApprovals.Deliveries(),
            mcpEvalService: eval,
            mcpSettingsProvider: settings,
            requestPrincipal: TestRequestPrincipal.OffRequest());
    }
}
