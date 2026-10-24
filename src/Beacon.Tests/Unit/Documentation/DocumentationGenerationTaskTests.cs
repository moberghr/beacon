using Beacon.AI.Services.Documentation;
using Beacon.Core.Authorization;
using Beacon.Core.BackgroundTasks;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Projects;
using Beacon.Core.Models;
using Beacon.Core.SecurityAudit;
using Beacon.Tests.Common;
using FluentAssertions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Documentation;

/// <summary>
/// A documentation generation run as a background task: the start hands the project to the task service (a second one
/// while one runs is refused), and the task re-checks, as the user who started it, that they may still curate the project
/// and query its data sources before the LLM is called, and skips the task when not.
/// </summary>
[TestFixture]
public class DocumentationGenerationTaskTests
{
    private const int ProjectId = 12;
    private const int TaskId = 31;
    private const int UserId = 4;

    [Test]
    public async Task Start_HandsTheProjectToABackgroundTask_AndAnswersWithItsTicket()
    {
        var ticket = new BackgroundTaskTicket(TaskId, "job-1", BackgroundTaskState.Queued);
        var tasks = new Mock<IBackgroundTaskService>();
        tasks
            .Setup(x => x.StartAsync(BackgroundTaskKind.DocumentationGeneration, ProjectId, It.IsAny<Func<int, SecurityAuditEntry>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ticket);

        var started = await new StartProjectDocumentationGenerationHandler(tasks.Object)
            .Handle(new StartProjectDocumentationGenerationCommand(ProjectId), CancellationToken.None);

        started.Should().Be(ticket);
    }

    [Test]
    public async Task Start_WhileAGenerationOfTheProjectRuns_IsRefusedAsRateLimited()
    {
        var tasks = new Mock<IBackgroundTaskService>();
        tasks
            .Setup(x => x.StartAsync(It.IsAny<BackgroundTaskKind>(), It.IsAny<int>(), It.IsAny<Func<int, SecurityAuditEntry>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BackgroundTaskTicket?)null);

        var start = () => new StartProjectDocumentationGenerationHandler(tasks.Object)
            .Handle(new StartProjectDocumentationGenerationCommand(ProjectId), CancellationToken.None);

        await start.Should().ThrowAsync<BeaconRateLimitedException>();
    }

    [Test]
    public async Task Start_OnBothRoutes_StagesAnIdsOnlyAuditEventWithTheTask()
    {
        var ticket = new BackgroundTaskTicket(TaskId, "job-1", BackgroundTaskState.Queued);
        var events = new List<SecurityAuditEntry>();
        var tasks = new Mock<IBackgroundTaskService>();
        tasks
            .Setup(x => x.StartAsync(BackgroundTaskKind.DocumentationGeneration, ProjectId, It.IsNotNull<Func<int, SecurityAuditEntry>?>(), It.IsAny<CancellationToken>()))
            .Callback<BackgroundTaskKind, int, Func<int, SecurityAuditEntry>?, CancellationToken>((_, _, audit, _) => events.Add(audit!(TaskId)))
            .ReturnsAsync(ticket);

        await new StartProjectDocumentationGenerationHandler(tasks.Object)
            .Handle(new StartProjectDocumentationGenerationCommand(ProjectId), CancellationToken.None);
        await new Beacon.AI.Handlers.Projects.GenerateProjectDocumentationHandler(tasks.Object)
            .Handle(new GenerateProjectDocumentationCommand(ProjectId, UserId), CancellationToken.None);

        events.Should().HaveCount(2);
        foreach (var entry in events)
        {
            entry.Action.Should().Be(SecurityAuditActions.DocumentationGenerationStarted);
            entry.Action.Should().Be("curation.documentation_generation_started");
            entry.ResourceType.Should().Be(SecurityAuditResources.Project);
            entry.ResourceId.Should().Be(ProjectId.ToString());
            entry.ProjectId.Should().Be(ProjectId);
            entry.Details.Should().NotBeNull();
        }
    }

    [Test]
    public void Start_NeedsCurate_AndTheStatusRead_NeedsRead()
    {
        var start = (IProjectAuthorizedRequest)new StartProjectDocumentationGenerationCommand(ProjectId);
        var read = (IProjectAuthorizedRequest)new GetProjectDocumentationGenerationQuery(ProjectId, TaskId);

        start.ProjectChecks.Select(x => x.Action).Should().Equal(ProjectAction.Curate);
        read.ProjectChecks.Select(x => x.Action).Should().Equal(ProjectAction.Read);
    }

    [Test]
    public async Task Status_IsReadForTheDocumentationKind()
    {
        var status = new BackgroundTaskStatus(TaskId, "job-1", BackgroundTaskState.Running, DateTime.UtcNow, DateTime.UtcNow, null, null, null);
        var tasks = new Mock<IBackgroundTaskService>();
        tasks
            .Setup(x => x.GetStatusAsync(BackgroundTaskKind.DocumentationGeneration, ProjectId, TaskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(status);

        var read = await new GetProjectDocumentationGenerationHandler(tasks.Object)
            .Handle(new GetProjectDocumentationGenerationQuery(ProjectId, TaskId), CancellationToken.None);

        read.Should().BeSameAs(status);
    }

    [Test]
    public async Task TheTask_WhenTheUserMayCurateAndQuery_GeneratesAsTheUser()
    {
        var world = new World();

        var outcome = await world.Executor().ExecuteAsync(new BackgroundTaskContext(TaskId, ProjectId, UserId), CancellationToken.None);

        outcome.SkippedCode.Should().BeNull();
        world.Documentation.Verify(x => x.GenerateDocumentationAsync(ProjectId, UserId, It.IsAny<CancellationToken>()), Times.Once);
        world.AccessCheck.Verify(
            x => x.AllowsAsync(ProjectAction.Query, It.Is<IReadOnlyCollection<int>>(y => y.SequenceEqual(new[] { 3, 8 })), It.IsAny<string>(), TaskId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task TheTask_WhenTheUserMayNoLongerCurateTheProject_IsSkipped_WithoutCallingTheLlm()
    {
        var world = new World { Scope = ProjectAccessScope.None };

        var outcome = await world.Executor().ExecuteAsync(new BackgroundTaskContext(TaskId, ProjectId, UserId), CancellationToken.None);

        outcome.SkippedCode.Should().Be(BackgroundTaskFailureCodes.Forbidden);
        world.Documentation.Verify(x => x.GenerateDocumentationAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task TheTask_WhenTheUserMayNoLongerQueryTheDataSources_IsSkipped_WithoutCallingTheLlm()
    {
        var world = new World { MayQuery = false };

        var outcome = await world.Executor().ExecuteAsync(new BackgroundTaskContext(TaskId, ProjectId, UserId), CancellationToken.None);

        outcome.SkippedCode.Should().Be(BackgroundTaskFailureCodes.Forbidden);
        world.Documentation.Verify(x => x.GenerateDocumentationAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task TheTask_ForAProjectWithoutDataSources_NeedsNoDataSourceCheck()
    {
        var world = new World { DataSourceIds = [] };

        var outcome = await world.Executor().ExecuteAsync(new BackgroundTaskContext(TaskId, ProjectId, UserId), CancellationToken.None);

        outcome.SkippedCode.Should().BeNull();
        world.AccessCheck.Verify(
            x => x.AllowsAsync(It.IsAny<ProjectAction>(), It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        world.Documentation.Verify(x => x.GenerateDocumentationAsync(ProjectId, UserId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task TheTask_AGenerationThatFails_FailsTheTask()
    {
        var world = new World();
        var executor = world.Executor();
        world.Documentation
            .Setup(x => x.GenerateDocumentationAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("provider failed"));

        var run = () => executor.ExecuteAsync(new BackgroundTaskContext(TaskId, ProjectId, UserId), CancellationToken.None);

        await run.Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public void TheTask_IsTheDocumentationKind_GivenUpAfterTwoHours()
    {
        var executor = new World().Executor();

        executor.Kind.Should().Be(BackgroundTaskKind.DocumentationGeneration);
        ((int)executor.Kind).Should().Be(5);
        executor.AbandonAfter.Should().Be(TimeSpan.FromHours(2));
    }

    private sealed class World
    {
        public Mock<IProjectDocumentationService> Documentation { get; } = new();

        public Mock<IExecutionAccessCheck> AccessCheck { get; } = new();

        public ProjectAccessScope Scope { get; init; } = ProjectAccessScope.Unrestricted;

        public bool MayQuery { get; init; } = true;

        public int[] DataSourceIds { get; init; } = [3, 8];

        public DocumentationGenerationTaskExecutor Executor()
        {
            Documentation
                .Setup(x => x.GenerateDocumentationAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProjectDocumentation());
            AccessCheck
                .Setup(x => x.AllowsAsync(It.IsAny<ProjectAction>(), It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(MayQuery);
            var projectAccess = new Mock<IProjectAccessEvaluator>();
            projectAccess
                .Setup(x => x.GetScopeAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Scope);
            var capture = new SqlCapture().ThenRows(DataSourceIds.Select(x => new object[] { x }).ToArray());

            return new DocumentationGenerationTaskExecutor(Documentation.Object, projectAccess.Object, AccessCheck.Object, capture.Factory());
        }
    }
}
