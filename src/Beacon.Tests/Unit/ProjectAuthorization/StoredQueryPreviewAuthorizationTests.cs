using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.HostData;
using Beacon.Core.Models;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// A stored-query preview runs exactly the steps whose data sources it authorized: the query is read once, the data
/// sources of that snapshot need Run right before the SQL, and that snapshot is what runs. A step moved after the
/// request's own check (an editor moving it to a source the previewing caller may not run) is refused before any step
/// runs, as a 404 or 403 and not as a failed preview; a step moved after the snapshot was checked does not reach the
/// run. No database: the rows are in-memory sets, and a step that runs stops at the connection lookup.
/// </summary>
[TestFixture]
public class StoredQueryPreviewAuthorizationTests
{
    private const int QueryId = 5;

    private const int OpenSource = 4;

    /// <summary>Linked only to project 2, which the caller does not read.</summary>
    private const int UnreadableSource = 9;

    /// <summary>Linked only to project 3, which the caller reads but does not run in.</summary>
    private const int ReadOnlySource = 30;

    [TestCase(false, UnreadableSource, typeof(BeaconNotFoundException))]
    [TestCase(true, UnreadableSource, typeof(BeaconNotFoundException))]
    [TestCase(false, ReadOnlySource, typeof(BeaconForbiddenException))]
    [TestCase(true, ReadOnlySource, typeof(BeaconForbiddenException))]
    public async Task AStepMovedAfterTheRequestsCheck_IsRefused_AndNoStepRuns(bool singleStep, int movedTo, Type refusal)
    {
        var world = new World();
        var connections = new Mock<IDataSourceConnectionResolver>(MockBehavior.Strict);
        var guard = new Mock<IHostDataSourceGuard>(MockBehavior.Strict);
        var service = world.PreviewService(connections.Object, guard.Object, ProjectResourceKind.Query, movedTo);

        var act = () => Preview(service, singleStep);

        (await act.Should().ThrowAsync<BeaconException>()).Which.Should().BeOfType(refusal);
        world.Resolved.Should().Equal(
            [$"Query({QueryId})", $"DataSources({movedTo})"],
            "the request's check passed on the original source, then the snapshot read after the move was checked");
        connections.Verify(x => x.GetConnectionString(It.IsAny<DataSource>()), Times.Never);
        guard.VerifyNoOtherCalls();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task TheAuthorizedSnapshotRuns_EvenWhenTheStepMovesAfterItWasChecked(bool singleStep)
    {
        var world = new World();
        var asked = new List<int>();
        var connections = new Mock<IDataSourceConnectionResolver>();
        connections
            .Setup(x => x.GetConnectionString(It.IsAny<DataSource>()))
            .Callback<DataSource>(x => asked.Add(x.Id))
            .Throws(new ExecutionReachedException());
        var service = world.PreviewService(connections.Object, new HostDataSourceGuard(new TestHostDataSources()), ProjectResourceKind.DataSources, UnreadableSource);

        var result = await Preview(service, singleStep);

        result!.Success.Should().BeFalse("the run stops at the connection lookup");
        result.ErrorMessage.Should().Be(ExecutionReachedException.Text);
        world.Resolved.Should().Equal(
            [$"Query({QueryId})", $"DataSources({OpenSource})"],
            "the snapshot checked is the one read before the move");
        asked.Should().Equal([OpenSource], "the step that runs is the snapshot's, not the one saved after the check");
    }

    private static Task<QueryPreviewResult?> Preview(IQueryExecutionPreviewService service, bool singleStep)
    {
        return singleStep
            ? service.ExecuteStepPreview(QueryId, 1, null, null, new Paging(), CancellationToken.None)
            : service.ExecuteQueryPreview(QueryId, null, new Paging(), CancellationToken.None);
    }

    /// <summary>
    /// Read in projects 1 and 3, run only in project 1, and the openly reachable sources (all but 9 and 30) for both;
    /// project 1 links the open source 4, project 2 links 9, project 3 links 30.
    /// </summary>
    private static ProjectAccessScope CallerScope()
    {
        var read = new ProjectSet([1, 3], true, [UnreadableSource, ReadOnlySource]);
        var run = new ProjectSet([1], true, [UnreadableSource, ReadOnlySource]);
        var sets = Enum.GetValues<ProjectAction>().ToDictionary(
            x => x,
            x => x switch
            {
                ProjectAction.Read => read,
                ProjectAction.Run => run,
                _ => ProjectSet.Empty
            });

        return new ProjectAccessScope(false, sets, new Dictionary<int, ProjectGrant>());
    }

    private sealed record Paging : ListRequest;

    private sealed class ExecutionReachedException() : Exception(Text)
    {
        public const string Text = "The step reached its connection.";
    }

    /// <summary>Query 5 with one step on the open source 4, as in-memory rows the services read through their own contexts.</summary>
    private sealed class World
    {
        private readonly List<DataSource> _sources;
        private readonly List<ProjectDataSource> _links;
        private readonly Query _query;

        public World()
        {
            _sources = new[] { OpenSource, UnreadableSource, ReadOnlySource }
                .Select(x => new DataSource
                {
                    Id = x,
                    Name = $"source {x}",
                    DataSourceType = DataSourceType.Database,
                    DatabaseEngineType = DatabaseEngineType.PostgreSQL,
                    EncryptedConnectionData = "enc"
                })
                .ToList();
            _links =
            [
                new ProjectDataSource { ProjectId = 1, DataSourceId = OpenSource },
                new ProjectDataSource { ProjectId = 2, DataSourceId = UnreadableSource },
                new ProjectDataSource { ProjectId = 3, DataSourceId = ReadOnlySource }
            ];
            _query = new Query { Id = QueryId, Name = "orders", Steps = [Step(OpenSource)] };
        }

        /// <summary>The resources the authorizer resolved, in order, as <c>Kind(ids)</c>.</summary>
        public List<string> Resolved { get; } = [];

        /// <summary>
        /// The preview service over the real query service and authorizer; once the authorizer has resolved a resource
        /// of kind <paramref name="moveAfter"/>, the query's step is saved again on <paramref name="moveTo"/>.
        /// </summary>
        public QueryExecutionPreviewService PreviewService(
            IDataSourceConnectionResolver connections,
            IHostDataSourceGuard guard,
            ProjectResourceKind moveAfter,
            int moveTo)
        {
            var factory = Factory();
            var queryService = TestQueryServices.QueryService(factory, connections, guard, new ConfiguredDatabaseResolver(connections, NullLogger<ConfiguredDatabaseResolver>.Instance));
            var resolver = new MovingResolver(this, moveAfter, () => _query.Steps = [Step(moveTo)]);

            return new QueryExecutionPreviewService(
                queryService,
                queryService,
                TestProjectAccess.Authorizer(CallerScope(), resolver, factory),
                NullLogger<QueryExecutionPreviewService>.Instance);
        }

        // A new row, as a save writes it: the step read into an earlier snapshot keeps its source.
        private QueryStep Step(int dataSourceId)
        {
            return new QueryStep
            {
                QueryId = QueryId,
                StepOrder = 1,
                DataSourceId = dataSourceId,
                DataSource = _sources.Single(x => x.Id == dataSourceId),
                SqlValue = "select id from orders"
            };
        }

        private IDbContextFactory<BeaconContext> Factory()
        {
            var saved = new List<object>();
            var factory = new Mock<IDbContextFactory<BeaconContext>>();
            factory
                .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new RecordingBeaconContext(
                    new Dictionary<Type, object>
                    {
                        [typeof(Query)] = RecordingBeaconContext.MemorySet(new List<Query> { _query }, saved),
                        [typeof(DataSource)] = RecordingBeaconContext.MemorySet(_sources, saved),
                        [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet(_links, saved)
                    },
                    saved,
                    new SqlCapture()));

            return factory.Object;
        }

        /// <summary>The real resolver, recording what it resolves and saving the move once a resource of one kind is resolved.</summary>
        private sealed class MovingResolver(World world, ProjectResourceKind moveAfter, Action move) : IProjectResourceResolver
        {
            private readonly ProjectResourceResolver _inner = new();

            public async Task<ResolvedProjectResource> ResolveAsync(BeaconContext context, ProjectResource resource, CancellationToken cancellationToken)
            {
                world.Resolved.Add($"{resource.Kind}({string.Join(",", resource.Ids)})");
                var resolved = await _inner.ResolveAsync(context, resource, cancellationToken);
                if (resource.Kind == moveAfter)
                {
                    move();
                }

                return resolved;
            }
        }
    }
}
