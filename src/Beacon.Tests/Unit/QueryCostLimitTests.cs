using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using FluentAssertions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// MaxExplainCost: on a PostgreSQL data source with a limit set, the statement as it runs is planned (never run) and refused
/// when the planner's estimated total cost exceeds the limit. Without a limit, or on any other engine, nothing is estimated.
/// An estimate that fails is never a pass.
/// </summary>
[TestFixture]
public class QueryCostLimitTests
{
    private const string FinalSql = "SELECT id FROM orders WHERE status = @p0 LIMIT 101";

    [Test]
    public async Task WithoutALimit_NothingIsEstimated()
    {
        var provider = new EstimatingProvider(1_000_000m);

        var check = await Limit(provider).CheckAsync(Source(DatabaseEngineType.PostgreSQL), FinalSql, new Dictionary<string, object?>(), null, CancellationToken.None);

        check.Should().BeSameAs(QueryCostCheck.Pass);
        provider.Estimates.Should().BeEmpty();
    }

    [TestCase(DatabaseEngineType.MySQL)]
    [TestCase(DatabaseEngineType.MSSQL)]
    [TestCase(DatabaseEngineType.Snowflake)]
    [TestCase(DatabaseEngineType.AzureSynapse)]
    public async Task OnAnotherEngine_TheSettingDoesNotApply_AndNothingIsEstimated(DatabaseEngineType engine)
    {
        var provider = new EstimatingProvider(1_000_000m);

        var check = await Limit(provider).CheckAsync(Source(engine), FinalSql, new Dictionary<string, object?>(), 10m, CancellationToken.None);

        check.Passed.Should().BeTrue();
        provider.Estimates.Should().BeEmpty();
    }

    [Test]
    public async Task OnANonDatabaseSource_NothingIsEstimated()
    {
        var provider = new EstimatingProvider(1_000_000m);
        var source = Source(null);
        source.DataSourceType = DataSourceType.Api;

        var check = await Limit(provider).CheckAsync(source, FinalSql, new Dictionary<string, object?>(), 10m, CancellationToken.None);

        check.Passed.Should().BeTrue();
        provider.Estimates.Should().BeEmpty();
    }

    [Test]
    public async Task ACostAboveTheLimit_IsRefused_WithTheEstimateAndTheLimit()
    {
        var provider = new EstimatingProvider(1234.5m);

        var check = await Limit(provider).CheckAsync(Source(DatabaseEngineType.PostgreSQL), FinalSql, new Dictionary<string, object?>(), 1000m, CancellationToken.None);

        check.Passed.Should().BeFalse();
        check.EstimateError.Should().BeNull();
        check.Refusal.Should().Be("The query's estimated cost (1234.5) exceeds this project's limit (1000). Narrow the query, add filters, or ask an administrator to raise MaxExplainCost.");
    }

    [TestCase(1000)]
    [TestCase(999.99)]
    [TestCase(0)]
    public async Task ACostAtOrBelowTheLimit_Passes(decimal cost)
    {
        var provider = new EstimatingProvider(cost);

        var check = await Limit(provider).CheckAsync(Source(DatabaseEngineType.PostgreSQL), FinalSql, new Dictionary<string, object?>(), 1000m, CancellationToken.None);

        check.Should().BeSameAs(QueryCostCheck.Pass);
        provider.Estimates.Should().ContainSingle();
    }

    [Test]
    public async Task TheEstimate_PlansTheStatementAsItRuns_WithItsParameters()
    {
        var provider = new EstimatingProvider(5m);
        var parameters = new Dictionary<string, object?> { ["p0"] = "open" };

        await Limit(provider).CheckAsync(Source(DatabaseEngineType.PostgreSQL), FinalSql, parameters, 10m, CancellationToken.None);

        var (sql, received) = provider.Estimates.Should().ContainSingle().Subject;
        sql.Should().Be(FinalSql);
        received.Should().Equal(parameters);
    }

    [Test]
    public async Task AFailedEstimate_IsNeverAPass_ItCarriesTheDatabasesError()
    {
        var provider = new EstimatingProvider(null, "canceling statement due to statement timeout");

        var check = await Limit(provider).CheckAsync(Source(DatabaseEngineType.PostgreSQL), FinalSql, new Dictionary<string, object?>(), 10m, CancellationToken.None);

        check.Passed.Should().BeFalse();
        check.Refusal.Should().BeNull();
        check.EstimateError.Should().Be("canceling statement due to statement timeout");
    }

    [Test]
    public async Task AProviderThatCannotEstimate_FailsTheCall_RatherThanRunningUnchecked()
    {
        var provider = new Mock<IDataSourceProvider>();
        var factory = new Mock<IDataSourceProviderFactory>();
        factory
            .Setup(x => x.GetProvider(DataSourceType.Database))
            .Returns(provider.Object);

        var check = await new QueryCostLimit(factory.Object).CheckAsync(Source(DatabaseEngineType.PostgreSQL), FinalSql, new Dictionary<string, object?>(), 10m, CancellationToken.None);

        check.Passed.Should().BeFalse();
        check.EstimateError.Should().NotBeNullOrEmpty();
    }

    [Test]
    public void ThePlansTotalCost_IsReadFromTheTopNode()
    {
        const string json = """
            [{"Plan": {"Node Type": "Limit", "Startup Cost": 0.00, "Total Cost": 35.50, "Plans": [{"Node Type": "Seq Scan", "Total Cost": 1820.00}]}}]
            """;

        PostgreSqlPlanCost.TotalCost(json).Should().Be(35.50m);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not json")]
    [TestCase("[]")]
    [TestCase("{}")]
    [TestCase("""[{"Plan": {"Node Type": "Result"}}]""")]
    [TestCase("""[{"Plan": {"Total Cost": "35.5"}}]""")]
    [TestCase("""[{"Query": {"Total Cost": 35.5}}]""")]
    public void APlanWithoutATotalCost_ReadsAsNone(string? json)
    {
        PostgreSqlPlanCost.TotalCost(json).Should().BeNull();
    }

    private static QueryCostLimit Limit(EstimatingProvider provider)
    {
        var factory = new Mock<IDataSourceProviderFactory>();
        factory
            .Setup(x => x.GetProvider(It.IsAny<DataSourceType>()))
            .Returns(provider);

        return new QueryCostLimit(factory.Object);
    }

    private static DataSource Source(DatabaseEngineType? engine)
    {
        return new DataSource
        {
            Id = 7,
            Name = "warehouse",
            DataSourceType = DataSourceType.Database,
            DatabaseEngineType = engine,
            EncryptedConnectionData = "unused"
        };
    }

    // A database provider whose planner answers with a fixed cost, or a fixed error; it records what it was asked to plan.
    private sealed class EstimatingProvider(decimal? cost, string? error = null) : IDataSourceProvider, IReadOnlyCostEstimation
    {
        public List<(string Sql, Dictionary<string, object?> Parameters)> Estimates { get; } = [];

        public DataSourceType SupportedType => DataSourceType.Database;

        public string GetQueryLanguageName() => "SQL";

        public Task<ReadOnlyCostEstimate> EstimateReadOnlyCostAsync(
            DataSource dataSource,
            string query,
            Dictionary<string, object?> parameters,
            CancellationToken cancellationToken)
        {
            Estimates.Add((query, parameters));

            return Task.FromResult(new ReadOnlyCostEstimate(cost, error));
        }

        public Task<ProviderQueryResult> ExecuteQueryAsync(
            DataSource dataSource,
            string query,
            Dictionary<string, object?> parameters,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ConnectionTestResult> TestConnectionAsync(
            DataSource dataSource,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DataSourceMetadata> GetMetadataAsync(
            DataSource dataSource,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<QueryValidationResult> ValidateQueryAsync(
            DataSource dataSource,
            string query,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
