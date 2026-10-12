using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
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
using Beacon.Core.Models;
using Beacon.Core.Models.DataMigration;
using Beacon.Core.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// What every migration demands whoever asks, an Admin included: the deployment runs migrations (409 otherwise), the
/// destination is a data source an Admin marked as a migration write target, neither host-managed nor marked read-only,
/// and no source the job reads (declared or through a step) is a write target. A refused job is neither created nor run.
/// </summary>
[TestFixture]
public class MigrationWritePolicyTests
{
    private const int ReadSource = 10;
    private const int WriteTarget = 20;
    private const int OrdinarySource = 30;

    private ProjectAccessWorld _world = null!;
    private Mock<IMigrationService> _migrations = null!;

    [SetUp]
    public void SetUp()
    {
        _world = new ProjectAccessWorld();
        _world.Source(ReadSource);
        _world.Source(WriteTarget).AllowMigrationWrites = true;
        _world.Source(OrdinarySource);
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
    public async Task AJobFromAReadSource_IntoAWriteTarget_IsCreated_AndRuns()
    {
        _world.Serve(new List<MigrationJob> { Job(1, ReadSource, WriteTarget) });

        var created = await CreateAsync(ReadSource, WriteTarget);
        var run = await RunAsync(1);

        created.MigrationJobId.Should().Be(77);
        run.ExecutionId.Should().Be(5);
    }

    [Test]
    public async Task WhileTheDeploymentDoesNotRunMigrations_NothingIsCreatedOrRun_ForAnAdminEither()
    {
        _world.Serve(new List<MigrationJob> { Job(1, ReadSource, WriteTarget) });

        var create = () => CreateAsync(ReadSource, WriteTarget, enabled: false);
        var run = () => RunAsync(1, enabled: false);

        await create.Should().ThrowAsync<BeaconConflictException>().WithMessage(MigrationWriteTargets.DisabledMessage);
        await run.Should().ThrowAsync<BeaconConflictException>().WithMessage(MigrationWriteTargets.DisabledMessage);
        _migrations.Verify(x => x.CreateMigrationJob(It.IsAny<CreateMigrationJobRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _migrations.Verify(x => x.ExecuteMigrationJob(It.IsAny<ExecuteMigrationJobRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // The routes are unmapped while migrations are off; the handlers refuse on their own too, for an in-process caller.
    [Test]
    public async Task WhileTheDeploymentDoesNotRunMigrations_NoJobIsListedShownRemovedOrItsRunsListed()
    {
        _world.Serve(new List<MigrationJob> { Job(1, ReadSource, WriteTarget) });
        await using var provider = Services(enabled: false);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var policy = services.GetRequiredService<MigrationWritePolicy>();
        var evaluator = services.GetRequiredService<IProjectAccessEvaluator>();

        var list = () => new GetMigrationJobsHandler(_world.Factory(), evaluator, policy).Handle(new GetMigrationJobsQuery(), CancellationToken.None);
        var show = () => new GetMigrationJobHandler(_world.Factory(), evaluator, policy).Handle(new GetMigrationJobQuery(1), CancellationToken.None);
        var runs = () => new GetMigrationExecutionsHandler(_migrations.Object, evaluator, services.GetRequiredService<MigrationJobAccess>(), policy)
            .Handle(new GetMigrationExecutionsQuery(), CancellationToken.None);
        var removal = () => new DeleteMigrationJobHandler(_migrations.Object, services.GetRequiredService<MigrationJobAccess>(), policy)
            .Handle(new DeleteMigrationJobCommand(1), CancellationToken.None);

        await list.Should().ThrowAsync<BeaconConflictException>().WithMessage(MigrationWriteTargets.DisabledMessage);
        await show.Should().ThrowAsync<BeaconConflictException>().WithMessage(MigrationWriteTargets.DisabledMessage);
        await runs.Should().ThrowAsync<BeaconConflictException>().WithMessage(MigrationWriteTargets.DisabledMessage);
        await removal.Should().ThrowAsync<BeaconConflictException>().WithMessage(MigrationWriteTargets.DisabledMessage);
        _migrations.Verify(x => x.GetMigrationExecutions(It.IsAny<GetMigrationExecutionsRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _migrations.Verify(x => x.DeleteMigrationJob(It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);
    }

    [Test]
    public async Task WhileTheDeploymentRunsMigrations_JobsAreListedAndShown()
    {
        _world.Serve(new List<MigrationJob> { Job(1, ReadSource, WriteTarget) });
        await using var provider = Services(enabled: true);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var policy = services.GetRequiredService<MigrationWritePolicy>();
        var evaluator = services.GetRequiredService<IProjectAccessEvaluator>();

        var list = await new GetMigrationJobsHandler(_world.Factory(), evaluator, policy).Handle(new GetMigrationJobsQuery(), CancellationToken.None);
        var shown = await new GetMigrationJobHandler(_world.Factory(), evaluator, policy).Handle(new GetMigrationJobQuery(1), CancellationToken.None);

        list.Items.Select(x => x.Id).Should().Equal(1);
        shown!.DestinationDataSourceId.Should().Be(WriteTarget);
    }

    // A source the job reads, declared or through a step, that an Admin has since marked as a write target stops the run at
    // the handler, before the migration service is asked.
    [TestCase(true, Description = "declared source")]
    [TestCase(false, Description = "a step's source")]
    public async Task AStoredJob_ThatNowReadsAWriteTarget_NeverRuns(bool declared)
    {
        var job = declared
            ? Job(1, OrdinarySource, WriteTarget)
            : Job(1, ReadSource, WriteTarget, StepsOn(ReadSource, OrdinarySource));
        _world.Serve(new List<MigrationJob> { job });
        _world.DataSources.First(x => x.Id == OrdinarySource).AllowMigrationWrites = true;

        var run = () => RunAsync(1);

        await run.Should().ThrowAsync<InvalidOperationException>().WithMessage(MigrationWriteTargets.ReadRefusedMessage);
        _migrations.Verify(x => x.ExecuteMigrationJob(It.IsAny<ExecuteMigrationJobRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ADestinationNoAdminMarked_IsRefused()
    {
        var act = () => CreateAsync(ReadSource, OrdinarySource);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*is not a migration write target*");
        _migrations.Verify(x => x.CreateMigrationJob(It.IsAny<CreateMigrationJobRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestCase(true, false, Description = "marked read-only")]
    [TestCase(false, true, Description = "host-managed")]
    public async Task AMarkedDestination_ThatIsReadOnlyOrHostManaged_IsRefused(bool isReadOnly, bool hostManaged)
    {
        var target = _world.DataSources.First(x => x.Id == WriteTarget);
        target.IsReadOnly = isReadOnly;
        target.HostManagedKey = hostManaged ? "host" : null;

        var act = () => CreateAsync(ReadSource, WriteTarget);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(isReadOnly ? "*marked read-only*" : "*host data source*");
        _migrations.Verify(x => x.CreateMigrationJob(It.IsAny<CreateMigrationJobRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task AJobReadingAWriteTarget_AsItsSourceOrThroughAStep_IsRefused()
    {
        var declared = () => CreateAsync(WriteTarget, WriteTarget);
        var throughAStep = () => CreateAsync(ReadSource, WriteTarget, StepsOn(ReadSource, WriteTarget));

        await declared.Should().ThrowAsync<InvalidOperationException>().WithMessage(MigrationWriteTargets.ReadRefusedMessage);
        await throughAStep.Should().ThrowAsync<InvalidOperationException>().WithMessage(MigrationWriteTargets.ReadRefusedMessage);
        _migrations.Verify(x => x.CreateMigrationJob(It.IsAny<CreateMigrationJobRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task AStoredJob_WhoseDestinationWasReturnedToAReadSource_NeverRuns()
    {
        _world.Serve(new List<MigrationJob> { Job(1, ReadSource, WriteTarget) });
        _world.DataSources.First(x => x.Id == WriteTarget).AllowMigrationWrites = false;

        var run = () => RunAsync(1);

        await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("*is not a migration write target*");
        _migrations.Verify(x => x.ExecuteMigrationJob(It.IsAny<ExecuteMigrationJobRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private async Task<CreateMigrationJobResult> CreateAsync(int source, int destination, string queryText = "SELECT 1", bool enabled = true)
    {
        await using var provider = Services(enabled);
        await using var scope = provider.CreateAsyncScope();
        var handler = new CreateMigrationJobHandler(
            _migrations.Object,
            scope.ServiceProvider.GetRequiredService<MigrationJobAccess>(),
            scope.ServiceProvider.GetRequiredService<MigrationWritePolicy>());

        return await handler.Handle(
            new CreateMigrationJobCommand("copy", "copy rows", source, queryText, destination, "target"),
            CancellationToken.None);
    }

    private async Task<RunMigrationJobResult> RunAsync(int jobId, bool enabled = true)
    {
        await using var provider = Services(enabled);
        await using var scope = provider.CreateAsyncScope();
        var handler = new RunMigrationJobHandler(
            _migrations.Object,
            scope.ServiceProvider.GetRequiredService<MigrationJobAccess>(),
            scope.ServiceProvider.GetRequiredService<MigrationWritePolicy>());

        return await handler.Handle(new RunMigrationJobCommand(jobId), CancellationToken.None);
    }

    // An Admin: project access does not check it, so only the write policy decides.
    private ServiceProvider Services(bool enabled)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "ext-root")], "Cookies"));
        var services = new ServiceCollection();
        _world.Register(services);
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } });
        services.AddScoped<IProjectResourceResolver, ProjectResourceResolver>();
        services.AddScoped<ProjectRequestAuthorizer>();
        services.AddScoped<MigrationJobAccess>();
        services.AddSingleton(Options.Create(new MigrationOptions { Enabled = enabled }));
        services.AddScoped<MigrationWritePolicy>();

        return services.BuildServiceProvider();
    }

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
