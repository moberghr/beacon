using Beacon.Connector.MySql;
using Beacon.Connector.PostgreSql;
using Beacon.Connector.SqlServer;
using Beacon.Core;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// Every way Beacon runs a stored or drafted query step — previews, the AI actor's check, a subscription run — goes through
/// the shared execution gate (regex guardrail, read-only AST, the source's own database, a row cap placed in the SQL) and
/// the provider's read-only path, which on PostgreSQL opens a READ ONLY session and transaction. A subscription's rows are
/// PII-masked before they reach a recipient; a migration's read is capped at its own row budget. Recording ADO.NET doubles,
/// no database (§4.7).
/// </summary>
[TestFixture]
[NonParallelizable] // DbConnectionFactory is a process-wide registry.
public class StoredQueryExecutionGateTests
{
    private const int QueryId = 7;
    private const int SubscriptionId = 11;
    private const int DataSourceId = 3;
    private const string ReadOnlySession = "SET default_transaction_read_only = on";
    private const string ReadOnlyTransaction = "SET TRANSACTION READ ONLY";

    [TearDown]
    public void RestoreConnectionFactories()
    {
        new BeaconBuilder(new ServiceCollection(), new ConfigurationBuilder().Build())
            .AddPostgreSqlConnector()
            .AddSqlServerConnector()
            .AddMySqlConnector();
    }

    [TestCase("step preview")]
    [TestCase("paged step preview")]
    [TestCase("paged query preview")]
    [TestCase("query check")]
    [TestCase("subscription")]
    public async Task EveryEntryPoint_RunsTheStepInsideAReadOnlyTransaction(string entryPoint)
    {
        // A user-defined function the parser cannot see into: only the database's read-only mode stops it writing.
        const string sql = "SELECT ledger.post_adjustment(42) AS posted";
        var connection = Connect(new RecordingDbConnection { Columns = [("posted", x => 1)], RowCount = 1 });
        var service = BuildService(sql);

        await Run(service, entryPoint);

        var stepCommands = connection.Executions
            .Where(x => x.CommandText.Contains("post_adjustment"))
            .ToList();
        stepCommands.Should().NotBeEmpty();
        stepCommands.Should().OnlyContain(x => x.Transaction != null, "the step runs only inside the read-only transaction");
        connection.ExecutedCommands.Should().Contain(ReadOnlySession);
        connection.ExecutedCommands.Should().Contain(ReadOnlyTransaction);
        var commands = connection.ExecutedCommands.ToList();
        commands.IndexOf(ReadOnlyTransaction).Should().BeLessThan(commands.FindIndex(x => x.Contains("post_adjustment")));
    }

    [TestCase("step preview")]
    [TestCase("paged step preview")]
    [TestCase("subscription")]
    public async Task AWriteAfterAComment_IsRefusedByTheGuardrail_BeforeAnythingRuns(string entryPoint)
    {
        var connection = Connect(new RecordingDbConnection { RowCount = 1 });
        var service = BuildService("/* refresh */ DELETE FROM accounts");

        var act = () => Run(service, entryPoint);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not allowed*");
        connection.Executions.Should().BeEmpty();
    }

    // A saved query that explains itself in comments — a heading line, or a remark that names a write it does not make —
    // is judged by its statement: comments are removed before the regex guardrail, the AST stage parses the text as saved.
    [TestCase("step preview", "-- Revenue by account\nSELECT id FROM accounts")]
    [TestCase("paged step preview", "/* delete old rows before the month closes */ SELECT id FROM accounts")]
    [TestCase("subscription", "SELECT id -- update weekly\nFROM accounts")]
    public async Task ACommentedSavedQuery_Runs(string entryPoint, string sql)
    {
        var connection = Connect(new RecordingDbConnection { RowCount = 1 });
        var service = BuildService(sql);

        await Run(service, entryPoint);

        connection.ExecutedCommands.Should().Contain(x => x.Contains("FROM accounts"));
    }

    // An author's own LIMIT above the ceiling is lowered to one row past it in the SQL, and the read itself also stops one
    // row past the ceiling (and cancels the rest of the statement) instead of fetching the whole result before refusing it.
    [Test]
    public async Task Subscription_AnOuterLimitAboveTheCeiling_IsLoweredAndStopsReadingOneRowPastIt()
    {
        var connection = Connect(new RecordingDbConnection { RowCount = 500 });
        var service = BuildService("SELECT id FROM accounts LIMIT 1000000", maxRows: 10, executionRowCeiling: 20);

        var act = () => service.ExecuteQuery(SubscriptionId, CancellationToken.None);

        (await act.Should().ThrowAsync<BeaconException>()).Which.Message.Should().Contain("more than 20 rows")
            .And.Contain("Beacon:Query:MaxExecutionRows", "the message names the setting that sets the limit");
        connection.ExecutedCommands.Should().Contain("SELECT id FROM accounts LIMIT 21");
        connection.RowsRead.Should().Be(21);
        connection.CancelCalls.Should().Be(1, "the rest of the statement is cancelled, not drained");
        connection.Commits.Should().Be(0, "a read stopped at the cap is rolled back, not committed");
    }

    [Test]
    public async Task Subscription_CapsTheStatementItself_SoNoMoreThanTheCeilingIsFetched()
    {
        var connection = Connect(new RecordingDbConnection { RowCount = 50 });
        var service = BuildService("SELECT id FROM accounts", maxRows: 10);

        await service.ExecuteQuery(SubscriptionId, CancellationToken.None);

        connection.ExecutedCommands.Should().Contain($"SELECT id FROM accounts LIMIT {Ceiling + 1}");
    }

    [Test]
    public async Task Subscription_ResultWithinTheCeiling_CountsEveryRow_AndDeliversMaxRows()
    {
        Connect(new RecordingDbConnection { RowCount = 50 });
        var service = BuildService("SELECT id FROM accounts", maxRows: 10);

        var result = await service.ExecuteQuery(SubscriptionId, CancellationToken.None);

        result.TotalRecords.Should().Be(50);
        result.AllRecords.Should().HaveCount(10);
        result.TopRecords.Should().HaveCount(10);
    }

    [Test]
    public async Task Subscription_ResultAboveTheCeiling_FailsTheRun_InsteadOfDeliveringPartOfIt()
    {
        Connect(new RecordingDbConnection { RowCount = Ceiling + 1 });
        var service = BuildService("SELECT id FROM accounts", maxRows: 10);

        var act = () => service.ExecuteQuery(SubscriptionId, CancellationToken.None);

        (await act.Should().ThrowAsync<BeaconException>())
            .Which.Message.Should().Contain($"more than {Ceiling} rows");
    }

    // A migration reads within its own budget (Beacon:Migrations:MaxRows), in place of the general ceiling.
    [Test]
    public async Task MigrationRead_CapsTheStatementAtItsOwnBudget_NotTheGeneralCeiling()
    {
        var connection = Connect(new RecordingDbConnection { RowCount = 50 });
        var service = BuildService("SELECT id FROM accounts", executionRowCeiling: 20);

        var result = await service.ExecuteQueryAdvanced(QueryId, null, null, 100, CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.FinalResult!.AllRecords.Should().HaveCount(50);
        connection.ExecutedCommands.Should().Contain("SELECT id FROM accounts LIMIT 101");
    }

    [Test]
    public async Task MigrationRead_AboveItsBudget_FailsInsteadOfCopyingPartOfIt()
    {
        var connection = Connect(new RecordingDbConnection { RowCount = 50 });
        var service = BuildService("SELECT id FROM accounts", executionRowCeiling: 1000);

        var result = await service.ExecuteQueryAdvanced(QueryId, null, null, 30, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("more than 30 rows").And.Contain("Beacon:Migrations:MaxRows");
        connection.RowsRead.Should().Be(31, "the read stops one row past the budget");
    }

    // The ceiling is the most rows a statement may return: exactly that many is a complete result.
    [Test]
    public async Task Subscription_AResultOfExactlyTheCeiling_IsDeliveredWhole()
    {
        var connection = Connect(new RecordingDbConnection { RowCount = 20 });
        var service = BuildService("SELECT id FROM accounts", maxRows: 50, executionRowCeiling: 20);

        var result = await service.ExecuteQuery(SubscriptionId, CancellationToken.None);

        result.TotalRecords.Should().Be(20);
        result.AllRecords.Should().HaveCount(20);
        connection.ExecutedCommands.Should().Contain("SELECT id FROM accounts LIMIT 21");
    }

    [Test]
    public async Task MigrationRead_ExactlyItsBudget_IsReadWhole()
    {
        Connect(new RecordingDbConnection { RowCount = 30 });
        var service = BuildService("SELECT id FROM accounts", executionRowCeiling: 1000);

        var result = await service.ExecuteQueryAdvanced(QueryId, null, null, 30, CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.FinalResult!.AllRecords.Should().HaveCount(30);
    }

    // The final query joins the step results on the in-memory store, capped in its SQL one row past the run's ceiling; a
    // larger join fails the run instead of reaching a recipient or a migration's destination in part.
    [Test]
    public async Task Subscription_AFinalQueryAboveTheCeiling_FailsTheRun_AndRanCappedOneRowPastIt()
    {
        Connect(new RecordingDbConnection { RowCount = 20 });
        var gate = new RecordingSqlGate(TestSqlGate.Create());
        var service = BuildService(
            "SELECT id FROM accounts",
            maxRows: 50,
            finalQuery: "SELECT a.id FROM @result1 a CROSS JOIN @result1 b",
            executionRowCeiling: 20,
            gate: gate);

        var act = () => service.ExecuteQuery(SubscriptionId, CancellationToken.None);

        (await act.Should().ThrowAsync<BeaconException>()).Which.Message.Should()
            .StartWith("The final query returned more than 20 rows")
            .And.Contain("Beacon:Query:MaxExecutionRows");
        JoinStatements(gate).Should().ContainSingle().Which.Should().EndWith("LIMIT 21");
    }

    [Test]
    public async Task Subscription_AFinalQueryOfExactlyTheCeiling_IsDeliveredWhole()
    {
        Connect(new RecordingDbConnection { RowCount = 20 });
        var service = BuildService("SELECT id FROM accounts", maxRows: 50, finalQuery: "SELECT id FROM @result1", executionRowCeiling: 20);

        var result = await service.ExecuteQuery(SubscriptionId, CancellationToken.None);

        result.TotalRecords.Should().Be(20);
        result.AllRecords.Select(x => Convert.ToInt32(x["id"])).Should().Equal(Enumerable.Range(1, 20));
    }

    // A migration's final query is capped at the migration's own budget, below or above the general ceiling alike.
    [Test]
    public async Task MigrationRead_AFinalQueryAboveItsBudget_FailsTheRun_AndRanCappedOneRowPastIt()
    {
        Connect(new RecordingDbConnection { RowCount = 20 });
        var gate = new RecordingSqlGate(TestSqlGate.Create());
        var service = BuildService(
            "SELECT id FROM accounts",
            finalQuery: "SELECT a.id FROM @result1 a CROSS JOIN @result1 b",
            executionRowCeiling: 1000,
            gate: gate);

        var act = () => service.ExecuteQueryAdvanced(QueryId, null, null, 30, CancellationToken.None);

        (await act.Should().ThrowAsync<BeaconException>()).Which.Message.Should()
            .StartWith("The final query returned more than 30 rows")
            .And.Contain("Beacon:Migrations:MaxRows");
        JoinStatements(gate).Should().ContainSingle().Which.Should().EndWith("LIMIT 31");
    }

    [Test]
    public async Task MigrationRead_ABudgetAboveTheGeneralCeiling_ReadsTheWholeFinalQuery()
    {
        Connect(new RecordingDbConnection { RowCount = 20 });
        var gate = new RecordingSqlGate(TestSqlGate.Create());
        var service = BuildService(
            "SELECT id FROM accounts",
            finalQuery: "SELECT a.id FROM @result1 a CROSS JOIN @result1 b",
            executionRowCeiling: 20,
            gate: gate);

        var result = await service.ExecuteQueryAdvanced(QueryId, null, null, 1000, CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.FinalResult!.AllRecords.Should().HaveCount(400, "twenty rows joined with themselves, none cut at the general ceiling");
        JoinStatements(gate).Should().ContainSingle().Which.Should().EndWith("LIMIT 1001");
    }

    [Test]
    public async Task Subscription_MasksPiiColumns_BeforeTheResultReachesARecipient()
    {
        Connect(new RecordingDbConnection
        {
            Columns = [("id", x => x + 1), ("email", x => $"customer{x}@bank.example")],
            RowCount = 3
        });
        var service = BuildService("SELECT id, email FROM customers", maxRows: 10);

        var result = await service.ExecuteQuery(SubscriptionId, CancellationToken.None);

        result.AllRecords.Should().HaveCount(3);
        result.AllRecords.Should().OnlyContain(x => x["email"]!.ToString()!.Contains("***"));
        result.AllRecords.Select(x => x["id"]).Should().Equal(1, 2, 3);
        result.QueryResults.Should().NotContain("@bank.example");
    }

    [Test]
    public async Task Subscription_CancelledRequest_StopsBeforeTheStepRuns()
    {
        var connection = Connect(new RecordingDbConnection { RowCount = 1 });
        var service = BuildService("SELECT id FROM accounts");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = () => service.ExecuteQuery(SubscriptionId, cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        connection.ExecutedCommands.Should().NotContain(x => x.Contains("accounts"));
    }

    // A final query that runs out of time has no rows; it fails the run instead of reading as an empty success to a
    // subscription, a query check, the AI actor or a migration about to write.
    [TestCase("query check")]
    [TestCase("subscription")]
    public async Task AFinalQueryThatTimesOut_FailsTheRun(string entryPoint)
    {
        Connect(new RecordingDbConnection { Columns = [("id", x => x + 1)], RowCount = 2 });
        var service = BuildService(
            "SELECT id FROM accounts",
            finalQuery: "WITH RECURSIVE r(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM r WHERE i < 100000000) SELECT COUNT(*) AS n FROM r",
            finalQueryTimeoutSeconds: 1);

        Func<Task> act = entryPoint == "subscription"
            ? () => service.ExecuteQuery(SubscriptionId, CancellationToken.None)
            : () => service.ExecuteQueryAdvanced(QueryId, cancellationToken: CancellationToken.None);

        await act.Should().ThrowAsync<BeaconException>().WithMessage("The final query timed out.");
    }

    [Test]
    public async Task Subscription_WithAFinalQuery_MasksPiiReadBackThroughTheJoin()
    {
        Connect(new RecordingDbConnection
        {
            Columns = [("id", x => x + 1), ("email", x => $"customer{x}@bank.example")],
            RowCount = 2
        });
        var service = BuildService("SELECT id, email FROM customers", maxRows: 10, finalQuery: "SELECT id, email AS contact FROM @result1");

        var result = await service.ExecuteQuery(SubscriptionId, CancellationToken.None);

        result.AllRecords.Should().HaveCount(2);
        result.AllRecords.Should().OnlyContain(x => x["contact"]!.ToString()!.Contains("***"));
        result.QueryResults.Should().NotContain("@bank.example");
    }

    [Test]
    public async Task PagedStepPreview_SqlThatCannotBePaged_IsPagedInMemoryWithinTheCeiling()
    {
        var connection = Connect(new RecordingDbConnection { RowCount = 5 });
        var service = BuildService("SELECT id /* every account */ FROM accounts", executionRowCeiling: 5);

        var page = await service.PreviewQueryStepPaged(QueryId, 1, null, null, new Paging { Page = 1, PageSize = 2 }, CancellationToken.None);

        page.Success.Should().BeTrue(page.ErrorMessage);
        page.Result!.TotalCount.Should().Be(5);
        page.Result.Rows.Select(x => x["id"]).Should().Equal(3, 4);
        connection.ExecutedCommands.Should().Contain("SELECT id /* every account */ FROM accounts LIMIT 6");
    }

    [Test]
    public async Task PagedStepPreview_SqlThatCannotBePaged_AboveTheCeiling_FailsInsteadOfShowingPart()
    {
        Connect(new RecordingDbConnection { RowCount = 6 });
        var service = BuildService("SELECT id /* every account */ FROM accounts", executionRowCeiling: 5);

        var page = await service.PreviewQueryStepPaged(QueryId, 1, null, null, new Paging { Page = 0, PageSize = 2 }, CancellationToken.None);

        page.Success.Should().BeFalse();
        page.ErrorMessage.Should().Contain("more than 5 rows");
    }

    private const int Ceiling = Constants.Query.MaxExecutionRows;

    private static RecordingDbConnection Connect(RecordingDbConnection connection)
    {
        DbConnectionFactory.Register(DatabaseEngineType.PostgreSQL, _ => connection);

        return connection;
    }

    private static async Task Run(IQueryService service, string entryPoint)
    {
        switch (entryPoint)
        {
            case "step preview":
                await service.PreviewQueryStep(QueryId, 1, CancellationToken.None);
                break;
            case "paged step preview":
                var page = await service.PreviewQueryStepPaged(QueryId, 1, null, null, new Paging { Page = 0, PageSize = 20 }, CancellationToken.None);
                page.Success.Should().BeTrue(page.ErrorMessage);
                break;
            case "paged query preview":
                var preview = await service.PreviewQuery(QueryId, null, new Paging { Page = 0, PageSize = 20 }, CancellationToken.None);
                preview.Success.Should().BeTrue(preview.ErrorMessage);
                break;
            case "query check":
                var check = await service.ExecuteQueryAdvanced(QueryId, cancellationToken: CancellationToken.None);
                check.Success.Should().BeTrue(check.ErrorMessage);
                break;
            default:
                await service.ExecuteQuery(SubscriptionId, CancellationToken.None);
                break;
        }
    }

    private static IQueryService BuildService(
        string sql,
        int? maxRows = null,
        string? finalQuery = null,
        int? executionRowCeiling = null,
        int? finalQueryTimeoutSeconds = null,
        ISqlExecutionGate? gate = null)
    {
        var dataSource = new DataSource
        {
            Id = DataSourceId,
            Name = "core",
            DataSourceType = DataSourceType.Database,
            DatabaseEngineType = DatabaseEngineType.PostgreSQL,
            EncryptedConnectionData = "encrypted"
        };
        var query = new Query { Id = QueryId, Name = "accounts", FinalQuery = finalQuery };
        var step = new QueryStep
        {
            Id = 1,
            QueryId = QueryId,
            Query = query,
            StepOrder = 1,
            DataSourceId = DataSourceId,
            DataSource = dataSource,
            SqlValue = sql
        };
        query.Steps.Add(step);
        var subscription = new Subscription
        {
            Id = SubscriptionId,
            QueryId = QueryId,
            Query = query,
            CronExpression = "0 * * * *",
            MaxRows = maxRows
        };
        var saved = new List<object>();
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(
                new Dictionary<Type, object>
                {
                    [typeof(DataSource)] = RecordingBeaconContext.MemorySet([dataSource], saved),
                    [typeof(Query)] = RecordingBeaconContext.MemorySet([query], saved),
                    [typeof(QueryStep)] = RecordingBeaconContext.MemorySet([step], saved),
                    [typeof(Subscription)] = RecordingBeaconContext.MemorySet([subscription], saved)
                },
                saved));

        return TestQueryServices.QueryService(
            factory.Object,
            executionRowCeiling: executionRowCeiling,
            finalQueryTimeoutSeconds: finalQueryTimeoutSeconds,
            gate: gate);
    }

    // The statement that ran on the in-memory join store: the gate's FinalSql for the join request.
    private static List<string> JoinStatements(RecordingSqlGate gate)
    {
        return gate.Evaluations
            .Where(x => x.Request.InMemoryJoin)
            .Select(x => x.Report.FinalSql)
            .ToList();
    }

    private sealed record Paging : ListRequest;
}
