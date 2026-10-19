using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.DataMigration;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.DataMigration;
using Beacon.Core.Handlers.Home;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.DataMigration;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// A MigrationOperator migrates through project access with no bypass: it needs Query on the declared source and on the
/// data source of every step the job runs, and Write on the destination, and the destination may not be reachable by more
/// people than any of those sources (every live project linking the destination links each source; an openly reachable
/// destination needs openly reachable sources). An Admin is not checked. A refused job is neither created, run nor
/// deleted, a failed run's error is shown only to callers who read every source, and a temporary preview runs no step on a
/// source its caller may not query. Migrations are on here and each job that passes writes into a migration write target
/// (see <see cref="MigrationWritePolicyTests"/> for those rules).
/// </summary>
[TestFixture]
public class MigrationJobAccessTests
{
    private const int OpenProject = 1;
    private const int Restricted = 2;
    private const int OtherRestricted = 3;

    private const int OpenSource = 10;
    private const int RestrictedSource = 20;
    private const int SharedSource = 30;
    private const int ViewerOnlySource = 40;
    private const int ClosedUnlinkedSource = 50;
    private const int OpenUnlinkedSource = 60;
    private const int HiddenProject = 4;
    private const int HiddenSource = 70;

    private ProjectAccessWorld _world = null!;
    private Mock<IMigrationService> _migrations = null!;

    [SetUp]
    public void SetUp()
    {
        _world = new ProjectAccessWorld();
        _world.Project(OpenProject);
        _world.Project(Restricted, requiresMembership: true);
        _world.Project(OtherRestricted, requiresMembership: true);
        _world.Source(OpenSource, OpenProject);
        _world.Source(RestrictedSource, Restricted);
        _world.Source(SharedSource, Restricted, OtherRestricted);
        _world.Source(ViewerOnlySource, OtherRestricted);
        _world.Source(ClosedUnlinkedSource);
        _world.Source(OpenUnlinkedSource).OpenWhenUnlinked = true;
        _world.Project(HiddenProject, requiresMembership: true);
        _world.Source(HiddenSource, HiddenProject);
        _world.FunctionalUser(3, "ext-op", RoleService.RoleLevels.Editor, BeaconPermissions.MigrationOperator);
        _world.Member(Restricted, 3, ProjectRole.Editor);
        _world.Member(OtherRestricted, 3, ProjectRole.Viewer);
        _world.Users.Add(new BeaconUser { Id = 1, ExternalId = "ext-root", UserName = "root", IsEnabled = true, IsSuperAdmin = true });

        _migrations = new Mock<IMigrationService>();
        _migrations
            .Setup(x => x.CreateMigrationJob(It.IsAny<CreateMigrationJobRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateMigrationJobResponse(77, true));
        _migrations
            .Setup(x => x.ExecuteMigrationJob(It.IsAny<ExecuteMigrationJobRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecuteMigrationJobResponse(5, MigrationStatus.Completed));
    }

    [Test]
    public async Task AJob_IntoADestinationNoMoreWidelyReachable_IsCreated()
    {
        WriteTarget(RestrictedSource);

        // The destination is linked to the restricted project only; the source to it and to another one.
        var result = await CreateAsync("ext-op", SharedSource, RestrictedSource);

        result.MigrationJobId.Should().Be(77);
        _migrations.Verify(x => x.CreateMigrationJob(It.IsAny<CreateMigrationJobRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestCase(RestrictedSource, SharedSource, Description = "another restricted project links the destination")]
    [TestCase(RestrictedSource, OpenSource, Description = "an open project links the destination")]
    public async Task AJob_IntoAMoreWidelyReachableDestination_IsForbidden(int source, int destination)
    {
        var act = () => CreateAsync("ext-op", source, destination);

        await act.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(MigrationJobAccess.WiderDestinationMessage);
        _migrations.Verify(x => x.CreateMigrationJob(It.IsAny<CreateMigrationJobRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task AJob_IntoAnOpenlyReachableDestination_FromASourceThatIsNot_IsForbidden()
    {
        var act = () => CreateAsync("ext-op", RestrictedSource, OpenUnlinkedSource);

        await act.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(MigrationJobAccess.OpenDestinationMessage);
    }

    [Test]
    public async Task AJob_IntoADestinationTheOperatorMayNotWrite_IsForbidden()
    {
        // A Viewer in the only project that links the destination.
        var act = () => CreateAsync("ext-op", SharedSource, ViewerOnlySource);

        await act.Should().ThrowAsync<BeaconForbiddenException>().WithMessage("Write is not allowed*");
        _migrations.Verify(x => x.CreateMigrationJob(It.IsAny<CreateMigrationJobRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task AJob_FromASourceTheOperatorMayNotRun_IsNotFound()
    {
        var act = () => CreateAsync("ext-op", ClosedUnlinkedSource, RestrictedSource);

        await act.Should().ThrowAsync<BeaconNotFoundException>();
        _migrations.Verify(x => x.CreateMigrationJob(It.IsAny<CreateMigrationJobRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task AnAdmin_IsNotChecked()
    {
        WriteTarget(OpenSource);

        var result = await CreateAsync("ext-root", RestrictedSource, OpenSource);

        result.MigrationJobId.Should().Be(77);
    }

    [Test]
    public async Task RunningAJob_IsCheckedAgain_AndARefusedRunNeverStarts()
    {
        WriteTarget(RestrictedSource);
        _world.Serve(new List<MigrationJob>
        {
            Job(1, SharedSource, RestrictedSource),
            Job(2, RestrictedSource, OpenSource)
        });

        var allowed = await RunAsync("ext-op", 1);
        var refused = () => RunAsync("ext-op", 2);

        allowed.ExecutionId.Should().Be(5);
        await refused.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(MigrationJobAccess.WiderDestinationMessage);
        _migrations.Verify(x => x.ExecuteMigrationJob(It.Is<ExecuteMigrationJobRequest>(y => y.MigrationJobId == 2), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task TheCatalogue_ShowsAnOperatorOnlyTheJobsWhoseDataSourcesItReads()
    {
        _world.Serve(new List<MigrationJob>
        {
            Job(1, SharedSource, RestrictedSource),
            Job(2, RestrictedSource, ClosedUnlinkedSource),
            Job(3, OpenSource, OpenSource)
        });

        var operatorList = await SendAsync("ext-op", sp => Jobs(sp).Handle(new GetMigrationJobsQuery(), CancellationToken.None));
        var adminList = await SendAsync("ext-root", sp => Jobs(sp).Handle(new GetMigrationJobsQuery(), CancellationToken.None));
        var hidden = await SendAsync("ext-op", sp => JobDetail(sp).Handle(new GetMigrationJobQuery(2), CancellationToken.None));
        var delete = () => SendAsync("ext-op", sp => Removal(sp).Handle(new DeleteMigrationJobCommand(2), CancellationToken.None));

        operatorList.Items.Select(x => x.Id).Should().BeEquivalentTo([1, 3]);
        adminList.Items.Select(x => x.Id).Should().BeEquivalentTo([1, 2, 3]);
        hidden.Should().BeNull("a job whose destination the operator cannot read is not found");
        await delete.Should().ThrowAsync<BeaconNotFoundException>();
        _migrations.Verify(x => x.DeleteMigrationJob(It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);
    }

    [Test]
    public async Task AJobWhoseStepReadsARestrictedSource_DeclaringAnotherSourceAsBothEnds_IsRefusedAtCreate_AndAtRun()
    {
        // The job declares a source the operator may write as its source and destination; its step reads a restricted
        // project's source the operator has no access to.
        var steps = StepsOn(HiddenSource);
        _world.Serve(new List<MigrationJob> { Job(9, RestrictedSource, RestrictedSource, steps) });

        var create = () => CreateAsync("ext-op", RestrictedSource, RestrictedSource, steps);
        var run = () => RunAsync("ext-op", 9);

        await create.Should().ThrowAsync<BeaconNotFoundException>();
        await run.Should().ThrowAsync<BeaconNotFoundException>();
        _migrations.Verify(x => x.CreateMigrationJob(It.IsAny<CreateMigrationJobRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _migrations.Verify(x => x.ExecuteMigrationJob(It.IsAny<ExecuteMigrationJobRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task EveryStepSource_NeedsQuery_NotOnlyRun()
    {
        // A Viewer runs saved SQL; a migration step is SQL of its own.
        var act = () => CreateAsync("ext-op", RestrictedSource, RestrictedSource, StepsOn(ViewerOnlySource));

        await act.Should().ThrowAsync<BeaconForbiddenException>().WithMessage("Query is not allowed*");
        _migrations.Verify(x => x.CreateMigrationJob(It.IsAny<CreateMigrationJobRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ADestinationMoreWidelyReachableThanAStepSource_IsForbidden_EvenWhenTheDeclaredSourceIsTheDestination()
    {
        // Another restricted project links the destination (and the declared source) but not the step's source.
        WriteTarget(RestrictedSource);
        var act = () => CreateAsync("ext-op", SharedSource, SharedSource, StepsOn(RestrictedSource));
        var allowed = await CreateAsync("ext-op", SharedSource, RestrictedSource, StepsOn(SharedSource));

        await act.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(MigrationJobAccess.WiderDestinationMessage);
        allowed.MigrationJobId.Should().Be(77, "the destination is linked only where every step source is");
    }

    [Test]
    public async Task DeletingAJob_NeedsWhatRunningItNeeds()
    {
        _world.Serve(new List<MigrationJob>
        {
            Job(1, SharedSource, RestrictedSource),
            Job(2, RestrictedSource, OpenSource)
        });
        _migrations
            .Setup(x => x.DeleteMigrationJob(It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new BaseResponse { Success = true });

        await SendAsync("ext-op", sp => Removal(sp).Handle(new DeleteMigrationJobCommand(1), CancellationToken.None));
        var refused = () => SendAsync("ext-op", sp => Removal(sp).Handle(new DeleteMigrationJobCommand(2), CancellationToken.None));

        await refused.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(MigrationJobAccess.WiderDestinationMessage);
        _migrations.Verify(x => x.DeleteMigrationJob(1, It.IsAny<CancellationToken>(), false), Times.Once);
        _migrations.Verify(x => x.DeleteMigrationJob(2, It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);
    }

    [Test]
    public async Task AFailedRunsError_IsShownOnlyToCallersWhoReadEverySourceTheJobReads()
    {
        _world.Serve(new List<MigrationJob>
        {
            Job(1, RestrictedSource, RestrictedSource),
            Job(3, RestrictedSource, RestrictedSource, StepsOn(HiddenSource))
        });
        _migrations
            .Setup(x => x.GetMigrationExecutions(It.IsAny<GetMigrationExecutionsRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => PagedList<MigrationExecutionDto>.Create([Run(11, 1, "timeout"), Run(13, 3, "relation secret.salaries: value 9000"), Run(14, 3, null)], 3, 20));

        var operatorRuns = await SendAsync("ext-op", sp => Executions(sp).Handle(new GetMigrationExecutionsQuery(), CancellationToken.None));
        var adminRuns = await SendAsync("ext-root", sp => Executions(sp).Handle(new GetMigrationExecutionsQuery(), CancellationToken.None));

        operatorRuns.Items.Select(x => x.ErrorMessage).Should().Equal("timeout", MigrationJobAccess.HiddenErrorMessage, null);
        adminRuns.Items.Select(x => x.ErrorMessage).Should().Equal("timeout", "relation secret.salaries: value 9000", null);
    }

    [Test]
    public async Task TheRunHistory_ReachesTheServiceFilteredByTheProjectsTheCallerReads()
    {
        var sent = new List<GetMigrationExecutionsRequest>();
        _migrations
            .Setup(x => x.GetMigrationExecutions(It.IsAny<GetMigrationExecutionsRequest>(), It.IsAny<CancellationToken>()))
            .Callback<GetMigrationExecutionsRequest, CancellationToken>((x, _) => sent.Add(x))
            .ReturnsAsync(() => PagedList<MigrationExecutionDto>.Create([], 0, 20));

        var readable = await SendAsync("ext-op", async sp =>
        {
            await Executions(sp).Handle(new GetMigrationExecutionsQuery { MigrationJobId = 4 }, CancellationToken.None);

            return (await sp.GetRequiredService<IProjectAccessEvaluator>().GetScopeAsync(CancellationToken.None)).For(ProjectAction.Read);
        });
        await SendAsync("ext-root", sp => Executions(sp).Handle(new GetMigrationExecutionsQuery(), CancellationToken.None));

        sent.Should().HaveCount(2);
        sent[0].MigrationJobId.Should().Be(4);
        sent[0].Readable.Should().NotBeNull().And.BeEquivalentTo(readable);
        sent[0].Readable!.Ids.Should().BeEquivalentTo([OpenProject, Restricted, OtherRestricted], "the operator reads the open project and those it is a member of, not the hidden one");
        sent[1].Readable!.Ids.Should().BeNull("an Admin reads every run");
    }

    [Test]
    public async Task TheHomeSummary_CountsAnOperatorOnlyTheJobsItsListShows()
    {
        var jobs = new List<MigrationJob>
        {
            Job(1, SharedSource, RestrictedSource),
            Job(2, RestrictedSource, ClosedUnlinkedSource),
            Job(3, OpenSource, OpenSource)
        };
        var runs = new List<MigrationExecutionHistory>
        {
            Execution(jobs[0], MigrationStatus.Completed),
            Execution(jobs[1], MigrationStatus.Failed),
            Execution(jobs[2], MigrationStatus.Failed)
        };
        _world.Serve(jobs);
        _world.Serve(runs);

        var operatorSummary = await SendAsync("ext-op", sp => Summary(sp).Handle(new GetHomeMigrationSummaryQuery(), CancellationToken.None));
        var adminSummary = await SendAsync("ext-root", sp => Summary(sp).Handle(new GetHomeMigrationSummaryQuery(), CancellationToken.None));

        operatorSummary.Should().Be(new GetHomeMigrationSummaryResult(2, 1, 2, 1));
        adminSummary.Should().Be(new GetHomeMigrationSummaryResult(3, 1, 3, 2));
    }

    [Test]
    public async Task ATemporaryPreview_ForACallerOutsideSystemWork_NeedsQueryOnEachStepsSource()
    {
        var queries = new Mock<IQueryService>();
        var steps = new QueryData
        {
            Name = "copy",
            Steps =
            [
                new QueryStepData { StepOrder = 1, DataSourceId = RestrictedSource, SqlValue = "SELECT 1" },
                new QueryStepData { StepOrder = 2, DataSourceId = HiddenSource, SqlValue = "SELECT 2" }
            ]
        };

        var stored = new RecordingStoredQueryPreview();

        var preview = () => SendAsync("ext-op", sp => Preview(sp, queries.Object, stored).ExecuteTemporaryQueryPreview(steps, CancellationToken.None));

        await preview.Should().ThrowAsync<BeaconNotFoundException>();
        stored.TemporaryQueries.Should().BeEmpty("nothing runs on a source the caller may not query");
        queries.Verify(x => x.CreateQuery(It.IsAny<QueryData>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestCase(false, true, true)]
    [TestCase(true, false, false)]
    [TestCase(true, true, false)]
    public void AnOpenlyReachableDestination_NeedsAnOpenlyReachableSource(bool sourceOpen, bool destinationOpen, bool refused)
    {
        // Both unlinked, so only openness decides.
        var openWhenUnlinked = new List<int>();
        if (sourceOpen)
        {
            openWhenUnlinked.Add(1);
        }

        if (destinationOpen)
        {
            openWhenUnlinked.Add(2);
        }

        var refusal = MigrationReach.Refusal(1, 2, [], openWhenUnlinked, requireMembershipEverywhere: false);
        var everyProjectRestricted = MigrationReach.Refusal(1, 2, [], openWhenUnlinked, requireMembershipEverywhere: true);

        (refusal != null).Should().Be(refused);
        everyProjectRestricted.Should().BeNull("nothing is openly reachable when every project is restricted");
    }

    [Test]
    public void AnArchivedProjectLinkingTheDestination_WidensNothing()
    {
        var links = new List<MigrationReach.Link>
        {
            new(1, Restricted, true, false),
            new(2, Restricted, true, false),
            new(2, OtherRestricted, false, false)
        };

        MigrationReach.Refusal(1, 2, links, [], requireMembershipEverywhere: false).Should().BeNull();
    }

    private async Task<CreateMigrationJobResult> CreateAsync(string externalId, int source, int destination, string queryText = "SELECT 1")
    {
        await using var provider = Services(externalId);
        await using var scope = provider.CreateAsyncScope();
        var handler = new CreateMigrationJobHandler(
            _migrations.Object,
            scope.ServiceProvider.GetRequiredService<MigrationJobAccess>(),
            scope.ServiceProvider.GetRequiredService<MigrationWritePolicy>());

        return await handler.Handle(
            new CreateMigrationJobCommand("copy", "copy rows", source, queryText, destination, "target"),
            CancellationToken.None);
    }

    // A job's QueryText as the migration editor stores steps: a JSON array, each step naming its data source.
    private static string StepsOn(params int[] dataSourceIds)
    {
        return JsonSerializer.Serialize(dataSourceIds
            .Select((x, i) =>
                new
                {
                    StepOrder = i + 1,
                    Name = $"step {i + 1}",
                    SqlValue = "SELECT 1",
                    DataSourceId = x
                }));
    }

    private static MigrationExecutionDto Run(int id, int jobId, string? error)
    {
        return new MigrationExecutionDto(id, jobId, $"job {jobId}", DateTime.UtcNow, null, MigrationStatus.Failed, 0, 0, 0, 0, TimeSpan.Zero, 0, error, 0, false);
    }

    private GetMigrationExecutionsHandler Executions(IServiceProvider services)
    {
        return new GetMigrationExecutionsHandler(
            _migrations.Object,
            services.GetRequiredService<IProjectAccessEvaluator>(),
            services.GetRequiredService<MigrationJobAccess>(),
            services.GetRequiredService<MigrationWritePolicy>());
    }

    private GetMigrationJobsHandler Jobs(IServiceProvider services)
    {
        return new GetMigrationJobsHandler(_world.Factory(), services.GetRequiredService<IProjectAccessEvaluator>(), services.GetRequiredService<MigrationWritePolicy>(), TestChangeApprovals.MigrationJobs());
    }

    private GetMigrationJobHandler JobDetail(IServiceProvider services)
    {
        return new GetMigrationJobHandler(_world.Factory(), services.GetRequiredService<IProjectAccessEvaluator>(), services.GetRequiredService<MigrationWritePolicy>(), TestChangeApprovals.MigrationJobs());
    }

    private DeleteMigrationJobHandler Removal(IServiceProvider services)
    {
        return new DeleteMigrationJobHandler(_migrations.Object, services.GetRequiredService<MigrationJobAccess>(), services.GetRequiredService<MigrationWritePolicy>());
    }

    private GetHomeMigrationSummaryHandler Summary(IServiceProvider services)
    {
        return new GetHomeMigrationSummaryHandler(
            _world.Factory(),
            services.GetRequiredService<IProjectAccessEvaluator>(),
            services.GetRequiredService<IBeaconPermissionAccessor>());
    }

    private static MigrationExecutionHistory Execution(MigrationJob job, MigrationStatus status)
    {
        var execution = new MigrationExecutionHistory
        {
            Id = job.Id * 10,
            MigrationJobId = job.Id,
            MigrationJob = job,
            Status = status,
            StartedAt = DateTime.UtcNow,
            ExecutedQuery = job.QueryText
        };
        job.Executions.Add(execution);

        return execution;
    }

    private static QueryExecutionPreviewService Preview(IServiceProvider services, IQueryService queries, RecordingStoredQueryPreview? stored = null)
    {
        return new QueryExecutionPreviewService(
            queries,
            stored ?? new RecordingStoredQueryPreview(),
            services.GetRequiredService<ProjectRequestAuthorizer>(),
            NullLogger<QueryExecutionPreviewService>.Instance);
    }

    private async Task<RunMigrationJobResult> RunAsync(string externalId, int jobId)
    {
        await using var provider = Services(externalId);
        await using var scope = provider.CreateAsyncScope();
        var handler = new RunMigrationJobHandler(
            _migrations.Object,
            scope.ServiceProvider.GetRequiredService<MigrationJobAccess>(),
            scope.ServiceProvider.GetRequiredService<MigrationWritePolicy>());

        return await handler.Handle(new RunMigrationJobCommand(jobId), CancellationToken.None);
    }

    private async Task<T> SendAsync<T>(string externalId, Func<IServiceProvider, Task<T>> send)
    {
        await using var provider = Services(externalId);
        await using var scope = provider.CreateAsyncScope();

        return await send(scope.ServiceProvider);
    }

    private ServiceProvider Services(string externalId)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, externalId)], "Cookies"));
        var services = new ServiceCollection();
        _world.Register(services);
        services.AddSingleton<IBeaconRequestPrincipal>(TestRequestPrincipal.For(principal));
        services.AddScoped<IProjectResourceResolver, ProjectResourceResolver>();
        services.AddScoped<ProjectRequestAuthorizer>();
        services.AddScoped<MigrationJobAccess>();
        services.AddSingleton(Options.Create(new MigrationOptions { Enabled = true }));
        services.AddScoped<MigrationWritePolicy>();

        return services.BuildServiceProvider();
    }

    private void WriteTarget(int dataSourceId)
    {
        _world.DataSources.First(x => x.Id == dataSourceId).AllowMigrationWrites = true;
    }

    private MigrationJob Job(int id, int source, int destination, string queryText = "SELECT 1")
    {
        return new MigrationJob
        {
            Id = id,
            Name = $"job {id}",
            Description = "copy",
            DataSourceId = source,
            DataSource = _world.DataSources.First(x => x.Id == source),
            DestinationDataSourceId = destination,
            DestinationDataSource = _world.DataSources.First(x => x.Id == destination),
            QueryText = queryText,
            DestinationTable = "target"
        };
    }
}
