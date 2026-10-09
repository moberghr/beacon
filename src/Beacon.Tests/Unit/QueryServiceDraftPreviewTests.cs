using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
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

namespace Beacon.Tests.Unit;

/// <summary>
/// Run in the query editor previews the unsaved draft instead of saving it first. The draft must pass the
/// same checks a save applies (blocked keywords) and the same execution gate a saved step meets (read-only
/// AST), and it must never read or write the stored query. The context only serves <c>DataSources</c>;
/// touching any other set fails the test with an <see cref="AssertionException"/> (§4.7: no in-memory DB).
/// </summary>
[TestFixture]
public class QueryServiceDraftPreviewTests
{
    private const int DataSourceId = 3;

    private static readonly ListRequest FirstPage = new Paging { Page = 0, PageSize = 20 };

    [Test]
    public async Task PreviewQuery_DraftWithBlockedKeyword_IsRejectedLikeASave()
    {
        var act = () => BuildService().PreviewQuery(7, Draft("DELETE FROM orders"), FirstPage, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconException>().WithMessage("*blocked SQL keywords*DELETE*");
    }

    [Test]
    public async Task PreviewQuery_DraftWriteWithoutBlockedKeyword_StopsAtReadOnlyGate()
    {
        // SELECT ... INTO creates a table but carries no blocked keyword, so only the AST gate catches it.
        var act = () => BuildService().PreviewQuery(7, Draft("SELECT * INTO evil FROM orders"), FirstPage, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("SELECT ... INTO is not allowed*");
    }

    [Test]
    public async Task PreviewQueryStepPaged_DraftWithoutThatStep_Throws()
    {
        var act = () => BuildService().PreviewQueryStepPaged(7, 2, null, Draft("SELECT 1"), FirstPage, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*has no step 2*");
    }

    [Test]
    public async Task PreviewQueryStepPaged_DraftOnUnknownDataSource_Throws()
    {
        var draft = Draft("SELECT 1");
        draft.Steps[0].DataSourceId = 42;

        var act = () => BuildService().PreviewQueryStepPaged(7, 1, null, draft, FirstPage, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Data source 42 not found.");
    }

    private static QueryDraft Draft(string sql) =>
        new()
        {
            Steps =
            [
                new QueryStepData
                {
                    StepOrder = 1,
                    Name = "Step 1",
                    SqlValue = sql,
                    DataSourceId = DataSourceId,
                },
            ],
        };

    private static QueryService BuildService()
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new DataSourcesOnlyContext());

        return new QueryService(
            factory.Object,
            Mock.Of<Beacon.Core.HostData.IDataSourceConnectionResolver>(),
            Mock.Of<IManualQueryExecutionLogger>(),
            NullLogger<QueryService>.Instance,
            NullLoggerFactory.Instance,
            Mock.Of<IQueryVersionService>(),
            null!,
            Mock.Of<IBeaconUserContext>(),
            new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance),
            Mock.Of<Beacon.Core.HostData.IHostDataSourceGuard>());
    }

    private sealed class DataSourcesOnlyContext : BeaconContext
    {
        private static readonly DbContextOptions<DataSourcesOnlyContext> _options =
            new DbContextOptionsBuilder<DataSourcesOnlyContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        public DataSourcesOnlyContext() : base(_options, "beacon") { }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(DataSource))
            {
                return (DbSet<TEntity>)(object)BuildDataSourceSet();
            }

            throw new AssertionException($"A draft preview must not touch {typeof(TEntity).Name}.");
        }
    }

    private static DbSet<DataSource> BuildDataSourceSet()
    {
        var data = new List<DataSource>
        {
            new()
            {
                Id = DataSourceId,
                Name = "warehouse",
                DataSourceType = DataSourceType.Database,
                DatabaseEngineType = DatabaseEngineType.PostgreSQL,
                EncryptedConnectionData = "unused",
            },
        }.AsQueryable();

        var set = new Mock<DbSet<DataSource>>();
        set.As<IAsyncEnumerable<DataSource>>()
            .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
            .Returns(() => new TestAsyncEnumerator<DataSource>(data.GetEnumerator()));
        set.As<IQueryable<DataSource>>()
            .Setup(x => x.Provider)
            .Returns(new TestAsyncQueryProvider<DataSource>(data.Provider));
        set.As<IQueryable<DataSource>>().Setup(x => x.Expression).Returns(data.Expression);
        set.As<IQueryable<DataSource>>().Setup(x => x.ElementType).Returns(data.ElementType);
        set.As<IQueryable<DataSource>>().Setup(x => x.GetEnumerator()).Returns(() => data.GetEnumerator());
        return set.Object;
    }

    private sealed record Paging : ListRequest;
}
