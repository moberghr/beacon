using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// Service-level integration for the read-only AST gate added in B1. Proves that
/// <see cref="QueryService.AddQueryStep"/> / <see cref="QueryService.UpdateQueryStep"/>
/// actually route <c>SqlValue</c> through <see cref="SqlReadOnlyAstValidator"/> and throw
/// on a non-read-only statement — i.e. the wiring is intact, not just the validator's own
/// logic (which is covered by <see cref="SqlReadOnlyAstValidatorTests"/>). The saved
/// <c>FinalQuery</c> passes the same gate (SQLite dialect, after <c>@resultN</c> translation)
/// on <see cref="QueryService.CreateQuery"/> / <see cref="QueryService.UpdateQuery"/>, before
/// anything is persisted — after a nested-block-comment check, since the gate's parser and SQLite
/// end such a comment in different places.
///
/// The context's <c>DataSources</c> and <c>Queries</c> sets are backed by in-memory async
/// sequences (no DB connection, no forbidden UseInMemoryDatabase — §4.7), so the dialect lookup
/// resolves to null and the gate is reached before any real query executes; <c>SaveChangesAsync</c>
/// is counted instead of reaching a database.
/// </summary>
[TestFixture]
public class QueryServiceReadOnlyGateTests
{
    private const string WriteSql = "DELETE FROM foo";

    // The reproduced attack: ATTACH a host file, write into it, then return something harmless.
    private const string AttachFinalQuery = "ATTACH DATABASE 'beacon-pwned.db' AS x; CREATE TABLE x.loot AS SELECT * FROM @result1; SELECT * FROM @result1";

    private const string ReadOnlyFinalQuery = "SELECT * FROM @result1";

    // A nested block comment, which the gate's parser and SQLite end in different places; refused before saving.
    private const string NestedCommentFinalQuery = "SELECT * FROM @result1 /* /* */ ; PRAGMA user_version=7; -- */";

    private static QueryService BuildService(ContextSpy? spy = null, IQueryVersionService? versionService = null)
    {
        var contextSpy = spy ?? new ContextSpy();
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new SpyContext(contextSpy));

        var validator = new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance);

        return new QueryService(
            factory.Object,
            Mock.Of<Beacon.Core.HostData.IDataSourceConnectionResolver>(),
            Mock.Of<IManualQueryExecutionLogger>(),
            NullLogger<QueryService>.Instance,
            NullLoggerFactory.Instance,
            versionService ?? Mock.Of<IQueryVersionService>(),
            new BeaconConfiguration(),
            Mock.Of<IBeaconUserContext>(),
            validator,
            Mock.Of<Beacon.Core.HostData.IHostDataSourceGuard>());
    }

    [Test]
    public async Task AddQueryStep_NonReadOnlySql_ThrowsInvalidOperationException()
    {
        var service = BuildService();
        var stepData = new QueryStepData
        {
            Name = "step",
            SqlValue = WriteSql,
            DataSourceId = 1
        };

        var act = () => service.AddQueryStep(1, stepData, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task UpdateQueryStep_NonReadOnlySql_ThrowsInvalidOperationException()
    {
        var service = BuildService();
        var stepData = new QueryStepData
        {
            Name = "step",
            SqlValue = WriteSql,
            DataSourceId = 1
        };

        var act = () => service.UpdateQueryStep(1, 0, stepData, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task CreateQuery_AttachFinalQuery_ThrowsAndPersistsNothing()
    {
        var spy = new ContextSpy();
        var service = BuildService(spy);

        var act = () => service.CreateQuery(NewQuery(AttachFinalQuery), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Final query rejected*");
        spy.SaveChangesCalls.Should().Be(0);
    }

    [Test]
    public async Task CreateQuery_ReadOnlyFinalQuery_PassesTheGateAndSaves()
    {
        var spy = new ContextSpy();
        var service = BuildService(spy);

        var response = await service.CreateQuery(NewQuery(ReadOnlyFinalQuery), CancellationToken.None);

        response.Success.Should().BeTrue();
        spy.SaveChangesCalls.Should().Be(1);
    }

    // A query without a final query (single step, or steps read on their own) never reaches the gate.
    [Test]
    public async Task CreateQuery_NoFinalQuery_SkipsTheGateAndSaves()
    {
        var spy = new ContextSpy();
        var service = BuildService(spy);

        var response = await service.CreateQuery(NewQuery(null), CancellationToken.None);

        response.Success.Should().BeTrue();
        spy.SaveChangesCalls.Should().Be(1);
    }

    [Test]
    public async Task UpdateQuery_AttachFinalQuery_ThrowsAndPersistsNothing()
    {
        var spy = new ContextSpy();
        spy.Queries.Add(new Query { Id = 7, Name = "orders" });
        var versionService = new Mock<IQueryVersionService>();
        var service = BuildService(spy, versionService.Object);

        var act = () => service.UpdateQuery(ExistingQuery(7, AttachFinalQuery), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Final query rejected*");
        spy.SaveChangesCalls.Should().Be(0);
        versionService.Verify(
            x => x.CreateVersionAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<QueryVersionStatus>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a rejected final query must not leave a version behind either");
    }

    [Test]
    public async Task CreateQuery_NestedCommentFinalQuery_ThrowsAndPersistsNothing()
    {
        var spy = new ContextSpy();
        var service = BuildService(spy);

        var act = () => service.CreateQuery(NewQuery(NestedCommentFinalQuery), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Nested block comments*");
        spy.SaveChangesCalls.Should().Be(0);
    }

    [Test]
    public async Task UpdateQuery_NestedCommentFinalQuery_ThrowsAndPersistsNothing()
    {
        var spy = new ContextSpy();
        spy.Queries.Add(new Query { Id = 7, Name = "orders" });
        var versionService = new Mock<IQueryVersionService>();
        var service = BuildService(spy, versionService.Object);

        var act = () => service.UpdateQuery(ExistingQuery(7, NestedCommentFinalQuery), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Nested block comments*");
        spy.SaveChangesCalls.Should().Be(0);
        versionService.Verify(
            x => x.CreateVersionAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<QueryVersionStatus>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task UpdateQuery_ReadOnlyFinalQuery_PassesTheGateAndSaves()
    {
        var spy = new ContextSpy();
        spy.Queries.Add(new Query { Id = 7, Name = "orders" });
        var service = BuildService(spy);

        var response = await service.UpdateQuery(ExistingQuery(7, ReadOnlyFinalQuery), CancellationToken.None);

        response.Success.Should().BeTrue();
        spy.SaveChangesCalls.Should().Be(1);
    }

    [TestCase(null)]
    [TestCase("")]
    public async Task UpdateQuery_ClearedFinalQuery_SkipsTheGateAndSaves(string? finalQuery)
    {
        var spy = new ContextSpy();
        spy.Queries.Add(new Query { Id = 7, Name = "orders", FinalQuery = ReadOnlyFinalQuery });
        var service = BuildService(spy);

        var response = await service.UpdateQuery(ExistingQuery(7, finalQuery), CancellationToken.None);

        response.Success.Should().BeTrue();
        spy.SaveChangesCalls.Should().Be(1);
        spy.Queries[0].FinalQuery.Should().Be(finalQuery);
    }

    private static QueryData NewQuery(string? finalQuery) =>
        new()
        {
            Name = "orders",
            FinalQuery = finalQuery,
            Steps =
            [
                new QueryStepData
                {
                    StepOrder = 1,
                    Name = "step",
                    SqlValue = "SELECT id FROM orders",
                    DataSourceId = 1
                }
            ]
        };

    private static QueryData ExistingQuery(int queryId, string? finalQuery) =>
        new()
        {
            QueryId = queryId,
            Name = "orders",
            FinalQuery = finalQuery
        };

    /// <summary>What the service did to its contexts: the stored queries it reads and the saves it attempted.</summary>
    private sealed class ContextSpy
    {
        public List<Query> Queries { get; } = [];

        public int SaveChangesCalls { get; set; }
    }

    /// <summary>
    /// A BeaconContext whose <c>DataSources</c> resolves to an empty async sequence and whose <c>Queries</c>
    /// resolves to <see cref="ContextSpy.Queries"/>, with no DB round-trip. <c>SaveChangesAsync</c> only counts.
    /// </summary>
    private sealed class SpyContext : BeaconContext
    {
        private static readonly DbContextOptions<SpyContext> _options =
            new DbContextOptionsBuilder<SpyContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly ContextSpy _spy;

        public SpyContext(ContextSpy spy) : base(_options, "beacon")
        {
            _spy = spy;
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(DataSource))
            {
                return (DbSet<TEntity>)(object)BuildSet(new List<DataSource>());
            }

            if (typeof(TEntity) == typeof(Query))
            {
                return (DbSet<TEntity>)(object)BuildSet(_spy.Queries);
            }

            return base.Set<TEntity>();
        }

        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            _spy.SaveChangesCalls++;
            return Task.FromResult(0);
        }
    }

    private static DbSet<T> BuildSet<T>(List<T> rows) where T : class
    {
        var data = rows.AsQueryable();
        var set = new Mock<DbSet<T>>();
        set.As<IAsyncEnumerable<T>>()
            .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
            .Returns(() => new TestAsyncEnumerator<T>(data.GetEnumerator()));
        set.As<IQueryable<T>>()
            .Setup(x => x.Provider)
            .Returns(new TestAsyncQueryProvider<T>(data.Provider));
        set.As<IQueryable<T>>().Setup(x => x.Expression).Returns(data.Expression);
        set.As<IQueryable<T>>().Setup(x => x.ElementType).Returns(data.ElementType);
        set.As<IQueryable<T>>().Setup(x => x.GetEnumerator()).Returns(() => data.GetEnumerator());
        return set.Object;
    }
}
