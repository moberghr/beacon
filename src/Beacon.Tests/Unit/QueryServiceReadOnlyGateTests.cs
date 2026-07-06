using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
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
/// logic (which is covered by <see cref="SqlReadOnlyAstValidatorTests"/>).
///
/// The context's <c>DataSources</c> set is backed by an empty in-memory async sequence
/// (no DB connection, no forbidden UseInMemoryDatabase — §4.7), so the dialect lookup
/// resolves to null and the gate is reached before any real query executes.
/// </summary>
[TestFixture]
public class QueryServiceReadOnlyGateTests
{
    private const string WriteSql = "DELETE FROM foo";

    private static QueryService BuildService()
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new EmptyDataSourceContext());

        var validator = new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance);

        return new QueryService(
            factory.Object,
            Mock.Of<IEncryptionService>(),
            Mock.Of<IManualQueryExecutionLogger>(),
            NullLogger<QueryService>.Instance,
            NullLoggerFactory.Instance,
            Mock.Of<IQueryVersionService>(),
            null!,
            Mock.Of<IBeaconUserContext>(),
            validator);
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

    /// <summary>
    /// A BeaconContext whose <c>DataSources</c> resolves to an empty async sequence, so the
    /// dialect lookup in the read-only gate returns null with no DB round-trip. Only the
    /// <see cref="DataSource"/> set is used by the code path under test.
    /// </summary>
    private sealed class EmptyDataSourceContext : BeaconContext
    {
        private static readonly DbContextOptions<EmptyDataSourceContext> _options =
            new DbContextOptionsBuilder<EmptyDataSourceContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        public EmptyDataSourceContext() : base(_options, "beacon") { }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(DataSource))
            {
                return (DbSet<TEntity>)(object)BuildEmptyDataSourceSet();
            }

            return base.Set<TEntity>();
        }
    }

    private static DbSet<DataSource> BuildEmptyDataSourceSet()
    {
        var data = new List<DataSource>().AsQueryable();
        var set = new Mock<DbSet<DataSource>>();
        set.As<IAsyncEnumerable<DataSource>>()
            .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
            .Returns(new TestAsyncEnumerator<DataSource>(data.GetEnumerator()));
        set.As<IQueryable<DataSource>>()
            .Setup(x => x.Provider)
            .Returns(new TestAsyncQueryProvider<DataSource>(data.Provider));
        set.As<IQueryable<DataSource>>().Setup(x => x.Expression).Returns(data.Expression);
        set.As<IQueryable<DataSource>>().Setup(x => x.ElementType).Returns(data.ElementType);
        set.As<IQueryable<DataSource>>().Setup(x => x.GetEnumerator()).Returns(data.GetEnumerator());
        return set.Object;
    }
}
