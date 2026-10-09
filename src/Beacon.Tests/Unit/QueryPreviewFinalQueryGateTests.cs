using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// The final query of a paged preview runs on the in-process SQLite join store, so it must pass the read-only gate
/// whether it is stored on the query or sent as an unsaved draft. Each case points a file-creating statement at a
/// temp path and asserts the preview is rejected and the file never appears. No DB: the context serves its sets
/// through the async-queryable doubles (§4.7).
/// </summary>
[TestFixture]
public class QueryPreviewFinalQueryGateTests
{
    private const string Marker = "final_query_gate_marker";

    private static readonly SqlReadOnlyAstValidator Validator = new(NullLogger<SqlReadOnlyAstValidator>.Instance);

    private readonly List<string> _tempFiles = new();

    [TearDown]
    public void DeleteTempFiles()
    {
        foreach (var path in _tempFiles)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        _tempFiles.Clear();
    }

    [Test]
    public async Task ExecuteFinalQueryPaged_VacuumInto_IsRejectedAndWritesNoFile()
    {
        var path = NewTempPath();
        using var manager = new VirtualTableManager(NullLogger<VirtualTableManager>.Instance);
        manager.AddVirtualTable(
            "@result1",
            [new Dictionary<string, object?> { ["a"] = Marker }],
            new ProjectInfo
            {
                Name = "orders",
                DatabaseEngine = nameof(DatabaseEngineType.PostgreSQL),
                DatabaseEngineType = DatabaseEngineType.PostgreSQL
            });

        var act = () => manager.ExecuteFinalQueryPagedAsync(
            $"VACUUM INTO '{path}'",
            Validator,
            NullLogger<InMemoryDatabaseManager>.Instance,
            new Paging { Page = 0, PageSize = 10 },
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        File.Exists(path).Should().BeFalse();
    }

    [Test]
    public async Task PreviewQuery_StoredFinalQueryThatAttaches_IsRejectedAndWritesNoFile()
    {
        var path = NewTempPath();
        var stored = new Query
        {
            Id = 7,
            Name = "orders",
            FinalQuery = AttachingFinalQuery(path),
            Steps = []
        };

        var act = () => BuildService(stored).PreviewQuery(7, null, new Paging(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        File.Exists(path).Should().BeFalse();
    }

    [Test]
    public async Task PreviewQuery_DraftFinalQueryThatAttaches_IsRejectedAndWritesNoFile()
    {
        var path = NewTempPath();
        var stored = new Query
        {
            Id = 7,
            Name = "orders",
            FinalQuery = "SELECT 1 AS x",
            Steps = []
        };
        var draft = new QueryDraft { FinalQuery = AttachingFinalQuery(path) };

        var act = () => BuildService(stored).PreviewQuery(7, draft, new Paging(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        File.Exists(path).Should().BeFalse();
    }

    private string NewTempPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"beacon-final-query-gate-{Guid.NewGuid():N}.db");
        _tempFiles.Add(path);

        return path;
    }

    private static string AttachingFinalQuery(string path) =>
        $"ATTACH DATABASE '{path}' AS w; CREATE TABLE w.t(a TEXT); INSERT INTO w.t VALUES ('{Marker}'); SELECT 1 AS x";

    private static QueryService BuildService(Query stored)
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new SingleQueryContext(stored));

        return new QueryService(
            factory.Object,
            Mock.Of<Beacon.Core.HostData.IDataSourceConnectionResolver>(),
            Mock.Of<IManualQueryExecutionLogger>(),
            NullLogger<QueryService>.Instance,
            NullLoggerFactory.Instance,
            Mock.Of<IQueryVersionService>(),
            null!,
            Mock.Of<IBeaconUserContext>(),
            Validator,
            Mock.Of<Beacon.Core.HostData.IHostDataSourceGuard>());
    }

    private sealed record Paging : ListRequest;

    /// <summary>
    /// Serves one stored <see cref="Query"/> and an empty <c>DataSources</c> set; <c>Include</c> on a non-EF provider
    /// is a passthrough, so the entity's populated <c>Steps</c> navigation is used as-is.
    /// </summary>
    private sealed class SingleQueryContext(Query query) : BeaconContext(Options, "beacon")
    {
        private static readonly DbContextOptions<SingleQueryContext> Options =
            new DbContextOptionsBuilder<SingleQueryContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        public override DbSet<TEntity> Set<TEntity>()
        {
            if (typeof(TEntity) == typeof(Query))
            {
                return (DbSet<TEntity>)(object)BuildSet(new List<Query> { query });
            }

            if (typeof(TEntity) == typeof(DataSource))
            {
                return (DbSet<TEntity>)(object)BuildSet(new List<DataSource>());
            }

            return base.Set<TEntity>();
        }

        private static DbSet<T> BuildSet<T>(List<T> data)
            where T : class
        {
            var queryable = data.AsQueryable();
            var set = new Mock<DbSet<T>>();
            set.As<IAsyncEnumerable<T>>()
                .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
                .Returns(() => new TestAsyncEnumerator<T>(queryable.GetEnumerator()));
            set.As<IQueryable<T>>()
                .Setup(x => x.Provider)
                .Returns(new TestAsyncQueryProvider<T>(queryable.Provider));
            set.As<IQueryable<T>>()
                .Setup(x => x.Expression)
                .Returns(queryable.Expression);
            set.As<IQueryable<T>>()
                .Setup(x => x.ElementType)
                .Returns(queryable.ElementType);
            set.As<IQueryable<T>>()
                .Setup(x => x.GetEnumerator())
                .Returns(() => queryable.GetEnumerator());

            return set.Object;
        }
    }
}
