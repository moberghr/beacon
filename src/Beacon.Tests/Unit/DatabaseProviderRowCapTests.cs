using Beacon.Connector.AzureSynapse;
using Beacon.Connector.MySql;
using Beacon.Connector.PostgreSql;
using Beacon.Connector.SqlServer;
using Beacon.Core;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// The database provider's read stopped at a row cap: it reads at most the cap, cancels the rest of the statement, and a
/// driver that reports that cancellation while the reader closes does not fail a read that reached the cap. A read that
/// fails before the cap is a failure, also when only closing the reader fails. Whatever the engine, a capped read is never
/// committed: PostgreSQL's READ ONLY transaction is rolled back, MySQL's read-only transaction ends with a ROLLBACK and the
/// session returns to read-write, SQL Server's transaction is rolled back to the savepoint and then rolled back. Recording
/// ADO.NET doubles, no database (§4.7).
/// </summary>
[TestFixture]
[NonParallelizable] // DbConnectionFactory is a process-wide registry.
public class DatabaseProviderRowCapTests
{
    private const string Statement = "SELECT id FROM accounts LIMIT 11";
    private const int Cap = 11;

    [TearDown]
    public void RestoreConnectionFactories()
    {
        new BeaconBuilder(new ServiceCollection(), new ConfigurationBuilder().Build())
            .AddPostgreSqlConnector()
            .AddSqlServerConnector()
            .AddMySqlConnector()
            .AddAzureSynapseConnector();
    }

    [TestCase(DatabaseEngineType.PostgreSQL)]
    [TestCase(DatabaseEngineType.MySQL)]
    [TestCase(DatabaseEngineType.MSSQL)]
    public async Task AReadStoppedAtTheCap_WhoseCancellationIsReportedOnClose_SucceedsWithTheCapsRows(DatabaseEngineType engine)
    {
        var connection = Connect(engine, new RecordingDbConnection
        {
            RowCount = 50,
            FailClosingReaderWith = new OperationCanceledException("The statement was cancelled.")
        });

        var result = await ReadCapped(engine);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.Rows.Should().HaveCount(Cap);
        connection.RowsRead.Should().Be(Cap, "the read stops at the cap");
        connection.CancelCalls.Should().BeGreaterThan(0, "the rest of the statement is cancelled, not drained");
        connection.Commits.Should().Be(0, "a read stopped at the cap is never committed");
    }

    [TestCase(DatabaseEngineType.PostgreSQL)]
    [TestCase(DatabaseEngineType.MySQL)]
    [TestCase(DatabaseEngineType.MSSQL)]
    public async Task AReadThatFailsBeforeTheCap_IsAFailure_AndNotCommitted(DatabaseEngineType engine)
    {
        var connection = Connect(engine, new RecordingDbConnection { RowCount = 50, FailReadingAtRow = 4 });

        var result = await ReadCapped(engine);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("The connection to the server was lost.");
        result.Rows.Should().BeEmpty();
        connection.RowsRead.Should().Be(4);
        connection.Commits.Should().Be(0);
    }

    // A reader that ended before the cap read every row the statement returned; a failure closing it then is a failure of
    // the read, which the cap does not explain.
    [TestCase(DatabaseEngineType.PostgreSQL)]
    [TestCase(DatabaseEngineType.MySQL)]
    [TestCase(DatabaseEngineType.MSSQL)]
    public async Task AReaderThatFailsToCloseBeforeTheCap_IsAFailure(DatabaseEngineType engine)
    {
        var connection = Connect(engine, new RecordingDbConnection
        {
            RowCount = 5,
            FailClosingReaderWith = new InvalidOperationException("The server closed the result with an error.")
        });

        var result = await ReadCapped(engine);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("The server closed the result with an error.");
        result.Rows.Should().BeEmpty();
        connection.Commits.Should().Be(0);
    }

    [Test]
    public async Task PostgreSql_AReadStoppedAtTheCap_IsRolledBack_NotCommitted()
    {
        var connection = Connect(DatabaseEngineType.PostgreSQL, new RecordingDbConnection { RowCount = 50 });

        var result = await ReadCapped(DatabaseEngineType.PostgreSQL);

        result.Rows.Should().HaveCount(Cap);
        connection.BatchedCommands.Should().Equal(Statement);
        connection.Commits.Should().Be(0);
    }

    [Test]
    public async Task PostgreSql_AReadWithinTheCap_IsCommittedAsAReadOnlyTransaction()
    {
        var connection = Connect(DatabaseEngineType.PostgreSQL, new RecordingDbConnection { RowCount = 10 });

        var result = await ReadCapped(DatabaseEngineType.PostgreSQL);

        result.Rows.Should().HaveCount(10);
        connection.Commits.Should().Be(1);
        connection.CancelCalls.Should().Be(0);
    }

    [Test]
    public async Task MySql_AReadStoppedAtTheCap_EndsItsReadOnlyTransaction_AndRestoresTheSession()
    {
        var connection = Connect(DatabaseEngineType.MySQL, new RecordingDbConnection { RowCount = 50 });

        var result = await ReadCapped(DatabaseEngineType.MySQL);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.Rows.Should().HaveCount(Cap);
        connection.ExecutedCommands.Should().Equal(
            "SET SESSION TRANSACTION READ ONLY",
            "START TRANSACTION READ ONLY",
            Statement,
            "ROLLBACK",
            "SET SESSION TRANSACTION READ WRITE");
        connection.Commits.Should().Be(0);
    }

    [Test]
    public async Task SqlServer_AReadStoppedAtTheCap_IsRolledBackToItsSavepoint_AndRolledBack()
    {
        var connection = Connect(DatabaseEngineType.MSSQL, new RecordingDbConnection { RowCount = 50 });

        var result = await ReadCapped(DatabaseEngineType.MSSQL);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.Rows.Should().HaveCount(Cap);
        connection.ExecutedCommands.Should().Equal(
            "SAVE TRANSACTION @savepoint",
            Statement,
            "SELECT @@TRANCOUNT",
            "ROLLBACK TRANSACTION @savepoint");
        connection.Rollbacks.Should().Be(1);
        connection.Commits.Should().Be(0);
    }

    // Each row holds a 1,000-character text value: 2,000 bytes, plus 40 bytes of row overhead (32 + 8 for its column), so
    // 2,040 bytes against the budget; a budget of 10,000 is passed by the fifth row.
    private static readonly IReadOnlyList<(string Name, Func<int, object?> Value)> WideRows = [("body", _ => new string('x', 1000))];

    [TestCase(DatabaseEngineType.PostgreSQL)]
    [TestCase(DatabaseEngineType.MySQL)]
    [TestCase(DatabaseEngineType.MSSQL)]
    [TestCase(DatabaseEngineType.AzureSynapse)]
    public async Task AReadPastTheByteBudget_StopsAndFails_NamingTheLimit_AndIsNotCommitted(DatabaseEngineType engine)
    {
        var connection = Connect(engine, new RecordingDbConnection { RowCount = 50, Columns = WideRows });

        var result = await ReadCapped(engine, new QueryExecutionOptions { MaxResultBytes = 10_000 });

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be(
            "The result is larger than the 10000-byte limit (Beacon:Query:MaxResultBytes); select fewer columns or rows.");
        result.Rows.Should().BeEmpty();
        connection.RowsRead.Should().Be(5, "the read stops at the first row past the budget");
        connection.CancelCalls.Should().BeGreaterThan(0, "the rest of the statement is cancelled, not drained");
        connection.Commits.Should().Be(0);
    }

    // PostgreSQL counts each value as it is read: the read stops inside the row whose value passes the budget, and the
    // single-statement batch is cancelled.
    [Test]
    public async Task PostgreSql_AReadPastTheByteBudget_StopsAtTheValue_AndCancelsTheBatch()
    {
        var connection = Connect(DatabaseEngineType.PostgreSQL, new RecordingDbConnection
        {
            RowCount = 50,
            Columns = [("body", _ => new string('x', 1000)), ("tail", _ => new string('y', 1000))]
        });

        var result = await ReadCapped(DatabaseEngineType.PostgreSQL, new QueryExecutionOptions { MaxResultBytes = 1_500 });

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("1500-byte limit");
        connection.RowsRead.Should().Be(1, "the first row's first value already passes the budget");
        connection.BatchedCommands.Should().Equal(Statement);
        connection.CancelCalls.Should().BeGreaterThan(0, "the rest of the statement is cancelled, not drained");
        connection.Commits.Should().Be(0);
    }

    // A read that reaches its row cap before the byte budget is capped as before: no failure.
    [TestCase(DatabaseEngineType.PostgreSQL)]
    [TestCase(DatabaseEngineType.MySQL)]
    [TestCase(DatabaseEngineType.MSSQL)]
    public async Task AReadThatReachesTheCapBeforeTheByteBudget_IsCapped_NotFailed(DatabaseEngineType engine)
    {
        var connection = Connect(engine, new RecordingDbConnection { RowCount = 50, Columns = WideRows });

        var result = await ReadCapped(engine, new QueryExecutionOptions { MaxResultBytes = 30_000 });

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.Rows.Should().HaveCount(Cap);
        connection.RowsRead.Should().Be(Cap);
        connection.Commits.Should().Be(0);
    }

    [TestCase(DatabaseEngineType.PostgreSQL)]
    [TestCase(DatabaseEngineType.MySQL)]
    [TestCase(DatabaseEngineType.MSSQL)]
    [TestCase(DatabaseEngineType.AzureSynapse)]
    public async Task AReadWithinTheByteBudget_Succeeds_WithEveryValueWhole(DatabaseEngineType engine)
    {
        Connect(engine, new RecordingDbConnection { RowCount = 5, Columns = WideRows });

        var result = await ReadCapped(engine, new QueryExecutionOptions { MaxResultBytes = 10_200 });

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.Rows.Should().HaveCount(5);
        result.Rows.Should().OnlyContain(x => ((string)x["body"]!).Length == 1000);
    }

    // A record value arrives as nested arrays: the text it holds counts however deeply it nests (2,000 bytes a row), so a
    // budget of 10,000 is passed by the fifth row as with plain text.
    [TestCase(DatabaseEngineType.PostgreSQL)]
    [TestCase(DatabaseEngineType.MySQL)]
    [TestCase(DatabaseEngineType.MSSQL)]
    public async Task AReadOfNestedRecordValues_PastTheByteBudget_StopsAndFails(DatabaseEngineType engine)
    {
        var connection = Connect(engine, new RecordingDbConnection
        {
            RowCount = 50,
            Columns = [("record", _ => ResultByteBudgetTests.Nested(new object[] { 1, new string('x', 1000) }, 10))]
        });

        var result = await ReadCapped(engine, new QueryExecutionOptions { MaxResultBytes = 10_000 });

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("10000-byte limit");
        connection.RowsRead.Should().Be(5, "the read stops at the first row past the budget");
        connection.Commits.Should().Be(0);
    }

    // A value of a type the estimate does not know counts by its text, like a 1,000-character search query object.
    [TestCase(DatabaseEngineType.PostgreSQL)]
    [TestCase(DatabaseEngineType.MSSQL)]
    public async Task AReadOfValuesCountedByTheirText_PastTheByteBudget_StopsAndFails(DatabaseEngineType engine)
    {
        var connection = Connect(engine, new RecordingDbConnection
        {
            RowCount = 50,
            Columns = [("query", _ => new TextValue(new string('x', 1000)))]
        });

        var result = await ReadCapped(engine, new QueryExecutionOptions { MaxResultBytes = 10_000 });

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("10000-byte limit");
        connection.RowsRead.Should().Be(5);
    }

    // A read without a row cap (an ad-hoc read on the plain path) is held to the same budget as it arrives.
    [Test]
    public async Task AnUncappedRead_PastTheByteBudget_StopsAndFails()
    {
        var connection = Connect(DatabaseEngineType.MSSQL, new RecordingDbConnection { RowCount = 50, Columns = WideRows });

        var result = await DatabaseProvider(new QueryExecutionOptions { MaxResultBytes = 10_000 })
            .ExecuteQueryAsync(DataSourceOf(DatabaseEngineType.MSSQL), Statement, new Dictionary<string, object?>(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("10000-byte limit");
        connection.RowsRead.Should().Be(5);
    }

    private static RecordingDbConnection Connect(DatabaseEngineType engine, RecordingDbConnection connection)
    {
        DbConnectionFactory.Register(engine, _ => connection);

        return connection;
    }

    private static Task<Beacon.Core.Models.Providers.ProviderQueryResult> ReadCapped(DatabaseEngineType engine, QueryExecutionOptions? options = null)
    {
        return DatabaseProvider(options)
            .ExecuteReadOnlyQueryAsync(DataSourceOf(engine), Statement, new Dictionary<string, object?>(), Cap, CancellationToken.None);
    }

    private static Beacon.Core.Services.Providers.DatabaseProvider DatabaseProvider(QueryExecutionOptions? options) =>
        TestQueryServices.DatabaseProvider(queryOptions: options);

    private static DataSource DataSourceOf(DatabaseEngineType engine) => new()
    {
        Id = 3,
        Name = "core",
        DataSourceType = DataSourceType.Database,
        DatabaseEngineType = engine,
        EncryptedConnectionData = "encrypted"
    };

    private sealed class TextValue(string text)
    {
        public override string ToString() => text;
    }
}
