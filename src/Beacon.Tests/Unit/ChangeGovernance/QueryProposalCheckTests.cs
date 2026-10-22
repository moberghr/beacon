using Beacon.Connector.AzureSynapse;
using Beacon.Connector.MySql;
using Beacon.Connector.PostgreSql;
using Beacon.Connector.Snowflake;
using Beacon.Connector.SqlServer;
using Beacon.Core;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models.Providers;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ChangeGovernance;

/// <summary>
/// A proposed query is checked without running any of it: every step passes the checks of a save, then its source's own
/// non-executing validation — a plan (EXPLAIN) or a description of the first result (sp_describe_first_result_set), never
/// the statement — and the final query passes the gate. Nothing the gate or the save check refuses reaches the source; a
/// source without such a validation is left to the gate.
/// </summary>
[TestFixture]
[NonParallelizable] // DbConnectionFactory is a process-wide registry.
public class QueryProposalCheckTests
{
    [TearDown]
    public void RestoreConnectionFactories()
    {
        // The per-engine cases register recording connections; put the production registrations back for later suites.
        new BeaconBuilder(new ServiceCollection(), new ConfigurationBuilder().Build())
            .AddPostgreSqlConnector()
            .AddSqlServerConnector()
            .AddMySqlConnector()
            .AddAzureSynapseConnector()
            .AddSnowflakeConnector();
    }

    // The source validates each step as it would run — flattened, each placeholder written as NULL (a dry run binds no
    // values) — and is asked for nothing else.
    [Test]
    public async Task AProposal_IsValidatedByItsSource_WithoutRunning()
    {
        var store = new GovernanceStore();
        var provider = Provider(Valid());
        var proposal = Proposal("SELECT id FROM orders WHERE total > {minimum}\nAND region = {region}");
        proposal.Steps[0].Parameters =
        [
            new QueryStepParameterData { Name = "minimum", Type = ParameterType.Number, Placeholder = "{minimum}" },
            new QueryStepParameterData { Name = "region", Type = ParameterType.String, Placeholder = "{region}" },
        ];

        var rejection = await Check(store, provider).RejectionAsync(proposal, CancellationToken.None);

        rejection.Should().BeNull();
        provider.Verify(
            x => x.ValidateQueryAsync(
                It.Is<DataSource>(y => y.Id == GovernanceStore.DataSourceId),
                "SELECT id FROM orders WHERE total > NULL AND region = NULL",
                It.IsAny<CancellationToken>()),
            Times.Once);
        provider.VerifyNoOtherCalls();
    }

    [TestCase("branch_id", "SELECT id FROM orders WHERE branch = branch_id", "Parameter 'branch' has an invalid placeholder*")]
    [TestCase("{a b}", "SELECT id FROM orders WHERE branch = {a b}", "Parameter 'branch' has an invalid placeholder*")]
    [TestCase("{missing}", "SELECT id FROM orders", "*missing required parameters*")]
    public async Task AProposalWithABadPlaceholder_IsRefused_BeforeTheSourceIsReached(string placeholder, string sql, string message)
    {
        var store = new GovernanceStore();
        var provider = Provider(Valid());
        var proposal = Proposal(sql);
        proposal.Steps[0].Parameters = [new QueryStepParameterData { Name = "branch", Type = ParameterType.Number, Placeholder = placeholder }];

        var rejection = await Check(store, provider).RejectionAsync(proposal, CancellationToken.None);

        rejection.Should().Match(message);
        provider.VerifyNoOtherCalls();
    }

    // What the gate or the save check refuses never reaches the source.
    [TestCase("DELETE FROM orders")]
    [TestCase("SELECT 1; SELECT 2")]
    [TestCase("SELECT id FROM other_db.public.orders")]
    public async Task AProposalTheGateRefuses_NeverReachesTheSource(string sql)
    {
        var store = new GovernanceStore();
        var provider = Provider(Valid());

        var rejection = await Check(store, provider).RejectionAsync(Proposal(sql), CancellationToken.None);

        rejection.Should().NotBeNullOrWhiteSpace();
        provider.VerifyNoOtherCalls();
    }

    // On the engines where EXPLAIN ANALYZE runs the statement it explains, a proposal that would complete the dry run's
    // EXPLAIN into one is refused before the source is reached.
    [TestCase(DatabaseEngineType.PostgreSQL, "ANALYZE SELECT id FROM orders")]
    [TestCase(DatabaseEngineType.PostgreSQL, "(ANALYZE) SELECT id FROM orders")]
    [TestCase(DatabaseEngineType.MySQL, "ANALYZE SELECT id FROM orders")]
    [TestCase(DatabaseEngineType.MySQL, "ANALYZE FORMAT=TREE SELECT id FROM orders")]
    public async Task AProposalThatWouldMakeTheDryRunExecute_NeverReachesTheSource(DatabaseEngineType engine, string sql)
    {
        var store = new GovernanceStore();
        store.Warehouse.DatabaseEngineType = engine;
        var provider = Provider(Valid());

        var rejection = await Check(store, provider).RejectionAsync(Proposal(sql), CancellationToken.None);

        rejection.Should().NotBeNullOrWhiteSpace();
        provider.VerifyNoOtherCalls();
    }

    [Test]
    public async Task AProposalItsSourceRejects_IsRefused()
    {
        var store = new GovernanceStore();
        var provider = Provider(new QueryValidationResult { IsValid = false, Errors = ["Database error 42703 (undefined column)."] });

        var rejection = await Check(store, provider).RejectionAsync(Proposal("SELECT missing FROM orders"), CancellationToken.None);

        rejection.Should().Contain("42703");
        provider.Verify(x => x.ValidateQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        provider.VerifyNoOtherCalls();
    }

    // An engine without a non-executing validation reports it skipped: the proposal is left to the gate it passed.
    [Test]
    public async Task ASourceWithoutANonExecutingValidation_IsLeftToTheGate()
    {
        var store = new GovernanceStore();
        var provider = Provider(new QueryValidationResult { IsValid = false, Skipped = true, Errors = ["not validated"] });

        var rejection = await Check(store, provider).RejectionAsync(Proposal("SELECT id FROM orders"), CancellationToken.None);

        rejection.Should().BeNull();
    }

    [Test]
    public async Task AFinalQueryTheGateRefuses_IsRefusedBeforeAnyStepReachesTheSource()
    {
        var store = new GovernanceStore();
        var provider = Provider(Valid());
        var proposal = Proposal("SELECT id FROM orders");
        proposal.FinalQuery = "DELETE FROM @result1";

        var rejection = await Check(store, provider).RejectionAsync(proposal, CancellationToken.None);

        rejection.Should().StartWith("Final query rejected");
        provider.VerifyNoOtherCalls();
    }

    [Test]
    public async Task AProposalOnADataSourceThatDoesNotExist_IsRefused()
    {
        var store = new GovernanceStore();
        var provider = Provider(Valid());
        var proposal = Proposal("SELECT id FROM orders");
        proposal.Steps[0].DataSourceId = 42;

        var rejection = await Check(store, provider).RejectionAsync(proposal, CancellationToken.None);

        rejection.Should().Be("Data source 42 not found.");
        provider.VerifyNoOtherCalls();
    }

    // Through the real provider, per engine: the only command the source receives is the plan or the description of the
    // result, outside any transaction — the statement itself is never sent to run.
    [TestCase(DatabaseEngineType.PostgreSQL, "EXPLAIN SELECT id FROM orders")]
    [TestCase(DatabaseEngineType.MySQL, "EXPLAIN SELECT id FROM orders")]
    [TestCase(DatabaseEngineType.Snowflake, "EXPLAIN SELECT id FROM orders")]
    [TestCase(DatabaseEngineType.MSSQL, "sp_describe_first_result_set @tsql")]
    [TestCase(DatabaseEngineType.AzureSynapse, "sp_describe_first_result_set @tsql")]
    public async Task OnEachEngine_TheSourceIsAskedForAPlanOrAResultDescription_NeverToRunTheStatement(DatabaseEngineType engine, string command)
    {
        var store = new GovernanceStore();
        store.Warehouse.DatabaseEngineType = engine;
        var connection = new RecordingDbConnection();
        DbConnectionFactory.Register(engine, _ => connection);

        var rejection = await TestQueryServices.QueryService(store.Factory()).RejectionAsync(Proposal("SELECT id FROM orders"), CancellationToken.None);

        rejection.Should().BeNull();
        connection.ExecutedCommands.Concat(connection.BatchedCommands).Distinct().Should().Equal(command);
        connection.BeginTransactionCalls.Should().Be(0);
    }

    // SQLite has no such validation: the provider reports it skipped before it connects, and the gate's verdict stands.
    [Test]
    public async Task OnSqlite_NothingConnects_AndTheGateDecides()
    {
        var store = new GovernanceStore();
        store.Warehouse.DatabaseEngineType = DatabaseEngineType.SQLite;

        var rejection = await TestQueryServices.QueryService(store.Factory()).RejectionAsync(Proposal("SELECT id FROM orders"), CancellationToken.None);

        rejection.Should().BeNull("no connection factory is registered for SQLite, so a connection attempt would have failed the check");
    }

    private static QueryDraft Proposal(string sql) =>
        new()
        {
            Steps =
            [
                new QueryStepData
                {
                    StepOrder = 1,
                    Name = "Step 1",
                    SqlValue = sql,
                    DataSourceId = GovernanceStore.DataSourceId,
                },
            ],
        };

    private static QueryValidationResult Valid() => new() { IsValid = true };

    // A provider that answers its non-executing validation and nothing else: any other call fails the test.
    private static Mock<IDataSourceProvider> Provider(QueryValidationResult result)
    {
        var provider = new Mock<IDataSourceProvider>(MockBehavior.Strict);
        provider
            .Setup(x => x.ValidateQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        return provider;
    }

    private static IQueryProposalCheck Check(GovernanceStore store, Mock<IDataSourceProvider> provider) =>
        TestQueryServices.QueryService(store.Factory(), providers: TestQueryServices.Providers(provider.Object));
}
