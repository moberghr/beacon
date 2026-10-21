using System.Text.Json;
using Beacon.Connector.MySql;
using Beacon.Connector.PostgreSql;
using Beacon.Connector.SqlServer;
using Beacon.Core;
using Beacon.Core.Adapters;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Entities.DataMigration;
using Beacon.Core.Data.Enums;
using Beacon.Core.DataMigration;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.DataMigration;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// A migration run writes through its destination engine's <see cref="IMigrationBulkWriter"/>, on the connection and inside
/// the one transaction the run opens: Core validates every name first, reads the primary key inside the transaction for
/// Upsert, hands the rows over in chunks and commits once; any failure rolls back and fails the run. Recording ADO.NET
/// doubles and a recording writer, no database (§4.7).
/// </summary>
[TestFixture]
[NonParallelizable] // DbConnectionFactory is a process-wide registry.
public class MigrationServiceWriterTests
{
    private const int JobId = 1;
    private const int ReadSource = 10;
    private const int WriteTarget = 20;
    private const string PrimaryKeyLookup = "pg_index";

    private ProjectAccessWorld _world = null!;
    private Mock<IQueryExecutionPreviewService> _preview = null!;
    private RecordingMigrationWriter _writer = null!;
    private Mock<IEncryptionService> _encryption = null!;
    private RecordingDbConnection _destination = null!;
    private int _connectionsOpened;

    [SetUp]
    public void SetUp()
    {
        _world = new ProjectAccessWorld();
        _world.Source(ReadSource);
        _world.Source(WriteTarget).AllowMigrationWrites = true;
        _world.Serve(new List<MigrationExecutionHistory>());

        _preview = new Mock<IQueryExecutionPreviewService>();
        SourceRows(Row(1, "a"), Row(2, "b"));

        _writer = new RecordingMigrationWriter(DatabaseEngineType.PostgreSQL);
        _encryption = new Mock<IEncryptionService>();
        _encryption.Setup(x => x.Decrypt(It.IsAny<string>())).Returns("Host=unused;Database=unused");
        Destination(new RecordingDbConnection { Scalar = OnSearchPath });
    }

    [TearDown]
    public void RestoreConnectionFactories()
    {
        new BeaconBuilder(new ServiceCollection(), new ConfigurationBuilder().Build())
            .AddPostgreSqlConnector()
            .AddSqlServerConnector()
            .AddMySqlConnector();
    }

    [Test]
    public async Task Truncate_ClearsThenInserts_OnTheRunsConnectionAndTransaction_AndCommitsOnce()
    {
        Job(MigrationMode.Truncate);

        var result = await Service().ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Completed, result.ErrorMessage);
        result.DestinationRowsWritten.Should().Be(2);
        _writer.Operations.Should().Equal("clear", "insert");
        var transaction = _writer.Calls[0].Target.Transaction;
        _writer.Calls.Should().AllSatisfy(x =>
        {
            x.Target.Connection.Should().BeSameAs(_destination);
            x.Target.Transaction.Should().BeSameAs(transaction);
            x.Target.Schema.Should().Be("public", "a PostgreSQL table without a schema is resolved on the search_path");
            x.Target.Table.Should().Be("target");
            x.Target.CommandTimeoutSeconds.Should().Be(Constants.Migration.BulkCopyTimeoutSeconds);
        });
        transaction.Connection.Should().BeSameAs(_destination);
        _destination.Executions.Should().ContainSingle(x => x.CommandText == "TRUNCATE TABLE target")
            .Which.Transaction.Should().BeSameAs(transaction);
        _writer.Calls[1].Rows!.Columns.Should().Equal("id", "name");
        _destination.Commits.Should().Be(1);
        _destination.Rollbacks.Should().Be(0);
    }

    [Test]
    public async Task Insert_InsertsWithoutClearing()
    {
        Job(MigrationMode.Insert);

        var result = await Service().ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Completed, result.ErrorMessage);
        _writer.Operations.Should().Equal("insert");
        _destination.Commits.Should().Be(1);
    }

    [Test]
    public async Task ASchemaQualifiedTable_ReachesTheWriterAsSchemaAndTable()
    {
        Job(MigrationMode.Insert, "sales.orders");

        var result = await Service().ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Completed, result.ErrorMessage);
        _writer.Calls.Single().Target.Should().Match<MigrationWriteTarget>(x => x.Schema == "sales" && x.Table == "orders");
    }

    // A column only some rows carry is still written; the writer puts NULL where a row lacks it.
    [Test]
    public async Task EveryRowsColumns_ReachTheWriter_InTheOrderTheyFirstAppear()
    {
        Job(MigrationMode.Insert);
        SourceRows(
            new Dictionary<string, object?> { ["id"] = 1 },
            new Dictionary<string, object?> { ["id"] = 2, ["name"] = "b" });

        await Service().ExecuteMigrationJob(Run(), CancellationToken.None);

        _writer.Calls.Single().Rows!.Columns.Should().Equal("id", "name");
    }

    [Test]
    public async Task AWriterThatThrows_RollsTheRunBack_AndTheRunFails()
    {
        Job(MigrationMode.Truncate);
        _writer.FailWritingWith = new InvalidOperationException("The copy failed.");

        var result = await Service().ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Failed);
        result.ErrorMessage.Should().Contain("The copy failed.");
        _writer.Operations.Should().Equal("clear", "insert");
        _destination.ExecutedCommands.Should().NotContain(x => x.Contains("INSERT", StringComparison.OrdinalIgnoreCase), "a failed write never falls back to row-by-row inserts");
        _destination.Commits.Should().Be(0);
        _destination.Rollbacks.Should().Be(1);
    }

    [Test]
    public async Task WithoutAWriterForTheEngine_TheRunFails_BeforeTheDestinationsCredentialsAreDecrypted()
    {
        Job(MigrationMode.Truncate);

        var result = await Service(MigrationTestWriters.Of()).ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Failed);
        result.ErrorMessage.Should().Contain("Moberg.Beacon.Connector.PostgreSql").And.Contain("AddPostgreSqlConnector()");
        _encryption.Verify(x => x.Decrypt(It.IsAny<string>()), Times.Never);
        _connectionsOpened.Should().Be(0);
    }

    [Test]
    public async Task Upsert_ReadsThePrimaryKeyInsideTheRunsTransaction_AndUpsertsByIt()
    {
        Job(MigrationMode.Upsert);
        Destination(WithPrimaryKey("id"));

        var result = await Service().ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Completed, result.ErrorMessage);
        var upsert = _writer.Calls.Single();
        upsert.Operation.Should().Be("upsert");
        upsert.KeyColumns.Should().Equal("id");
        var lookup = _destination.Executions.Single(x => x.CommandText.Contains(PrimaryKeyLookup, StringComparison.Ordinal));
        lookup.Transaction.Should().NotBeNull().And.BeSameAs(upsert.Target.Transaction);
        lookup.Parameters.Should().ContainSingle(x => x.ParameterName == "tableName")
            .Which.Value.Should().Be("\"public\".\"target\"", "the table resolves by its quoted name, exactly as entered, in the schema the search_path finds it in");
        _destination.Commits.Should().Be(1);
    }

    // SQL Server reads the key by schema and table name (parameters), inside the run's transaction, and writes through its
    // own engine's writer.
    [Test]
    public async Task ASqlServerUpsert_ReadsThePrimaryKeyInsideTheRunsTransaction_AndUpsertsThroughTheSqlServerWriter()
    {
        _world.DataSources.First(x => x.Id == WriteTarget).DatabaseEngineType = DatabaseEngineType.MSSQL;
        Job(MigrationMode.Upsert, "sales.orders");
        var sqlServerWriter = new RecordingMigrationWriter(DatabaseEngineType.MSSQL);
        var destination = new RecordingDbConnection
        {
            Scalar = _ => 1,
            RowCount = 1,
            ColumnsFor = x => x.Contains("sys.indexes", StringComparison.Ordinal) ? [("name", _ => "id")] : null
        };
        DbConnectionFactory.Register(DatabaseEngineType.MSSQL, _ => destination);

        var result = await Service(MigrationTestWriters.Of(_writer, sqlServerWriter)).ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Completed, result.ErrorMessage);
        _writer.Calls.Should().BeEmpty();
        var upsert = sqlServerWriter.Calls.Single();
        upsert.Operation.Should().Be("upsert");
        upsert.KeyColumns.Should().Equal("id");
        upsert.Target.Should().Match<MigrationWriteTarget>(x => x.Schema == "sales" && x.Table == "orders" && x.Connection == destination);
        var lookup = destination.Executions.Single(x => x.CommandText.Contains("sys.indexes", StringComparison.Ordinal));
        lookup.Transaction.Should().NotBeNull().And.BeSameAs(upsert.Target.Transaction);
        lookup.CommandText.Should().Contain("OBJECT_ID(@tableName)");
        lookup.Parameters.Should().ContainSingle(x => x.ParameterName == "tableName")
            .Which.Value.Should().Be("[sales].[orders]", "the key is read from the table the writer names");
        destination.Commits.Should().Be(1);
    }

    // SQL Server would resolve an unqualified name through the login's default schema: the run names dbo throughout, so the
    // existence check, the key lookup and the writer agree on the table.
    [Test]
    public async Task ASqlServerTableWithoutASchema_IsTheDboTable_ForTheExistenceCheck_TheKeyLookup_AndTheWriter()
    {
        _world.DataSources.First(x => x.Id == WriteTarget).DatabaseEngineType = DatabaseEngineType.MSSQL;
        Job(MigrationMode.Upsert, "orders");
        var sqlServerWriter = new RecordingMigrationWriter(DatabaseEngineType.MSSQL);
        var destination = new RecordingDbConnection
        {
            Scalar = _ => 1,
            RowCount = 1,
            ColumnsFor = x => x.Contains("sys.indexes", StringComparison.Ordinal) ? [("name", _ => "id")] : null
        };
        DbConnectionFactory.Register(DatabaseEngineType.MSSQL, _ => destination);

        var result = await Service(MigrationTestWriters.Of(sqlServerWriter)).ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Completed, result.ErrorMessage);
        sqlServerWriter.Calls.Single().Target.Should().Match<MigrationWriteTarget>(x => x.Schema == "dbo" && x.Table == "orders");
        destination.ExecutedCommands.Should().Contain(x => x.Contains("TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'orders'", StringComparison.Ordinal));
        destination.Executions.Single(x => x.CommandText.Contains("sys.indexes", StringComparison.Ordinal))
            .Parameters.Should().ContainSingle(x => x.ParameterName == "tableName")
            .Which.Value.Should().Be("[dbo].[orders]");
    }

    [Test]
    public async Task AnUpsertWithoutRows_ReadsNoKey_AndWritesNothing_EvenIntoATableWithoutAPrimaryKey()
    {
        Job(MigrationMode.Upsert);
        SourceRows();
        Destination(new RecordingDbConnection { Scalar = OnSearchPath, RowCount = 0 });

        var result = await Service().ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Completed, result.ErrorMessage);
        result.DestinationRowsWritten.Should().Be(0);
        _destination.ExecutedCommands.Should().NotContain(x => x.Contains(PrimaryKeyLookup, StringComparison.Ordinal));
        _writer.Calls.Should().BeEmpty();
        _destination.Commits.Should().Be(1);
    }

    // MySQL reads the key by schema and table name (parameters), inside the run's transaction, and writes through its own
    // engine's writer.
    [Test]
    public async Task AMySqlUpsert_ReadsThePrimaryKeyInsideTheRunsTransaction_AndUpsertsThroughTheMySqlWriter()
    {
        var mySqlWriter = new RecordingMigrationWriter(DatabaseEngineType.MySQL);
        var destination = MySqlDestination(new RecordingDbConnection
        {
            Scalar = _ => 1,
            RowCount = 1,
            ColumnsFor = x => x.Contains("KEY_COLUMN_USAGE", StringComparison.Ordinal) ? [("COLUMN_NAME", _ => "id")] : null
        });
        Job(MigrationMode.Upsert, "sales.orders");

        var result = await Service(MigrationTestWriters.Of(_writer, mySqlWriter)).ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Completed, result.ErrorMessage);
        result.DestinationRowsWritten.Should().Be(2);
        _writer.Calls.Should().BeEmpty();
        var upsert = mySqlWriter.Calls.Single();
        upsert.Operation.Should().Be("upsert");
        upsert.KeyColumns.Should().Equal("id");
        upsert.Target.Should().Match<MigrationWriteTarget>(x => x.Schema == "sales" && x.Table == "orders" && x.Connection == destination);
        var lookup = destination.Executions.Single(x => x.CommandText.Contains("KEY_COLUMN_USAGE", StringComparison.Ordinal));
        lookup.Transaction.Should().NotBeNull().And.BeSameAs(upsert.Target.Transaction);
        lookup.Parameters.Select(x => (x.ParameterName, x.Value)).Should().Contain([("schema", (object?)"sales"), ("table", "orders")]);
        destination.Commits.Should().Be(1);
    }

    [Test]
    public async Task AMySqlTruncate_ClearsThenInserts_ThroughTheMySqlWriter_AndCommitsOnce()
    {
        var mySqlWriter = new RecordingMigrationWriter(DatabaseEngineType.MySQL);
        var destination = MySqlDestination(new RecordingDbConnection { Scalar = _ => 1 });
        Job(MigrationMode.Truncate);

        var result = await Service(MigrationTestWriters.Of(mySqlWriter)).ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Completed, result.ErrorMessage);
        mySqlWriter.Operations.Should().Equal("clear", "insert");
        mySqlWriter.Calls.Should().OnlyContain(x => x.Target.Connection == destination && x.Target.Transaction != null);
        destination.Commits.Should().Be(1);
        destination.Rollbacks.Should().Be(0);
    }

    [Test]
    public async Task AMySqlWriterThatThrows_RollsTheRunBack_AndNothingIsCommitted()
    {
        var mySqlWriter = new RecordingMigrationWriter(DatabaseEngineType.MySQL) { FailWritingWith = new InvalidOperationException("row 2 failed") };
        var destination = MySqlDestination(new RecordingDbConnection { Scalar = _ => 1 });
        Job(MigrationMode.Truncate);

        var result = await Service(MigrationTestWriters.Of(mySqlWriter)).ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Failed);
        mySqlWriter.Operations.Should().Equal("clear", "insert");
        destination.Commits.Should().Be(0);
        destination.Rollbacks.Should().Be(1);
    }

    [Test]
    public async Task WithoutAMySqlWriter_TheRunFails_BeforeTheDestinationsCredentialsAreDecrypted()
    {
        var destination = MySqlDestination(new RecordingDbConnection { Scalar = _ => 1 });
        Job(MigrationMode.Insert);

        var result = await Service(MigrationTestWriters.Of(_writer)).ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Failed);
        result.ErrorMessage.Should().Contain("Moberg.Beacon.Connector.MySql").And.Contain("AddMySqlConnector()");
        _encryption.Verify(x => x.Decrypt(It.IsAny<string>()), Times.Never);
        destination.Executions.Should().BeEmpty();
    }

    // MySQL upserts keep the same key rules as every other engine.
    [Test]
    public async Task AMySqlUpsertIntoATableWithoutAPrimaryKey_FailsTheRun()
    {
        var destination = MySqlDestination(new RecordingDbConnection { Scalar = _ => 1, RowCount = 0 });
        Job(MigrationMode.Upsert);

        var result = await Service(MigrationTestWriters.All()).ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Failed);
        result.ErrorMessage.Should().Contain("Table 'target' has no primary key");
        destination.Executions.Single(x => x.CommandText.Contains("KEY_COLUMN_USAGE", StringComparison.Ordinal))
            .Transaction.Should().NotBeNull();
        destination.Commits.Should().Be(0);
        destination.Rollbacks.Should().Be(1);
    }

    [Test]
    public async Task AMySqlPrimaryKeyLookupThatFails_FailsTheRun()
    {
        var destination = MySqlDestination(new RecordingDbConnection
        {
            Scalar = _ => 1,
            FailOnCommandText = x => x.Contains("KEY_COLUMN_USAGE", StringComparison.Ordinal)
        });
        Job(MigrationMode.Upsert);

        var result = await Service(MigrationTestWriters.All()).ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Failed);
        result.ErrorMessage.Should().Contain("Failed to read the primary key of table 'target'");
        destination.Commits.Should().Be(0);
        destination.Rollbacks.Should().Be(1);
    }

    [Test]
    public async Task AMySqlUpsertRowLackingAKeyColumn_FailsTheRun()
    {
        var destination = MySqlDestination(new RecordingDbConnection
        {
            Scalar = _ => 1,
            RowCount = 1,
            ColumnsFor = x => x.Contains("KEY_COLUMN_USAGE", StringComparison.Ordinal) ? [("COLUMN_NAME", _ => "id")] : null
        });
        Job(MigrationMode.Upsert);
        SourceRows(Row(1, "a"), new Dictionary<string, object?> { ["name"] = "b" });

        var result = await Service(MigrationTestWriters.All()).ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Failed);
        result.ErrorMessage.Should().Contain("missing primary key columns: id");
        destination.Commits.Should().Be(0);
    }

    [Test]
    public async Task APrimaryKeyLookupThatFails_FailsTheRun_InsteadOfInserting()
    {
        Job(MigrationMode.Upsert);
        Destination(new RecordingDbConnection
        {
            Scalar = OnSearchPath,
            FailOnCommandText = x => x.Contains(PrimaryKeyLookup, StringComparison.Ordinal)
        });

        var result = await Service().ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Failed);
        result.ErrorMessage.Should().Contain("Failed to read the primary key of table 'public.target'");
        _writer.Calls.Should().BeEmpty();
        _destination.Commits.Should().Be(0);
        _destination.Rollbacks.Should().Be(1);
    }

    [Test]
    public async Task UpsertIntoATableWithoutAPrimaryKey_FailsTheRun()
    {
        Job(MigrationMode.Upsert);
        Destination(new RecordingDbConnection { Scalar = OnSearchPath, RowCount = 0 });

        var result = await Service().ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Failed);
        result.ErrorMessage.Should().Contain("Table 'public.target' has no primary key");
        _writer.Calls.Should().BeEmpty();
        _destination.Commits.Should().Be(0);
    }

    [Test]
    public async Task UpsertRowsLackingAKeyColumn_FailTheRun()
    {
        Job(MigrationMode.Upsert);
        Destination(WithPrimaryKey("id"));
        SourceRows(Row(1, "a"), new Dictionary<string, object?> { ["name"] = "b" });

        var result = await Service().ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Failed);
        result.ErrorMessage.Should().Contain("missing primary key columns: id");
        _writer.Calls.Should().BeEmpty();
    }

    [TestCase(0, new int[0])]
    [TestCase(Constants.Migration.BulkInsertBatchSize, new[] { Constants.Migration.BulkInsertBatchSize })]
    [TestCase(2 * Constants.Migration.BulkInsertBatchSize + 1, new[] { Constants.Migration.BulkInsertBatchSize, Constants.Migration.BulkInsertBatchSize, 1 })]
    public async Task Rows_ReachTheWriterInChunksOfTheBatchSize_NeverAnEmptyOne(int rowCount, int[] chunkSizes)
    {
        Job(MigrationMode.Insert);
        SourceRows(Enumerable.Range(1, rowCount)
            .Select(x => Row(x, "n"))
            .ToArray());

        var result = await Service().ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Completed, result.ErrorMessage);
        result.DestinationRowsWritten.Should().Be(rowCount);
        _writer.Calls.Select(x => x.Rows!.Rows.Count).Should().Equal(chunkSizes);
        _writer.Calls
            .Select(x => x.Target.Transaction)
            .Distinct()
            .Should().HaveCountLessThanOrEqualTo(1, "every chunk is written inside the one transaction");
        _destination.Commits.Should().Be(1);
    }

    [TestCase("bad name")]
    [TestCase("name\"; --")]
    public async Task AColumnThatIsNotAPlainIdentifier_FailsTheRun_BeforeAnyCommand(string column)
    {
        Job(MigrationMode.Truncate);
        SourceRows(new Dictionary<string, object?> { ["id"] = 1, [column] = "x" });

        var result = await Service().ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Failed);
        result.ErrorMessage.Should().Contain("Invalid SQL column");
        _connectionsOpened.Should().Be(0);
        _destination.ExecutedCommands.Should().BeEmpty();
        _writer.Calls.Should().BeEmpty();
    }

    [Test]
    public async Task ATableNameThatIsNotAPlainIdentifier_FailsTheRun_BeforeAnyCommand()
    {
        Job(MigrationMode.Insert, "sales.orders x");

        var result = await Service().ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Failed);
        result.ErrorMessage.Should().Contain("Invalid SQL table");
        _connectionsOpened.Should().Be(0);
        _writer.Calls.Should().BeEmpty();
    }

    // PostgreSQL finds a table named without a schema through the connection's search_path: the run resolves the schema
    // there once (the first schema of current_schemas that holds the table), and the existence check, the key lookup and
    // the writer all name that table.
    [Test]
    public async Task APostgreSqlTableWithoutASchema_IsResolvedOnTheSearchPath_ForTheCheck_TheKeyLookup_AndTheWriter()
    {
        var destination = new RecordingDbConnection
        {
            Scalar = x => x.Contains("current_schemas", StringComparison.Ordinal) ? "sales" : true,
            RowCount = 1,
            ColumnsFor = x => x.Contains(PrimaryKeyLookup, StringComparison.Ordinal) ? [("attname", _ => "id")] : null
        };
        Destination(destination);
        Job(MigrationMode.Upsert, "orders");

        var result = await Service().ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Completed, result.ErrorMessage);
        var resolution = destination.Executions.Single(x => x.CommandText.Contains("current_schemas(false)", StringComparison.Ordinal));
        resolution.Parameters.Should().ContainSingle().Which.Value.Should().Be("orders");
        var writeTransaction = _writer.Calls.Single().Target.Transaction;
        resolution.Transaction.Should().NotBeNull().And.BeSameAs(writeTransaction, "the name is resolved inside the run's transaction");
        var existence = destination.Executions.Single(x => x.CommandText.Contains("information_schema.tables", StringComparison.Ordinal) && !x.CommandText.Contains("current_schemas", StringComparison.Ordinal));
        existence.CommandText.Should().Contain("table_schema = 'sales'", "the existence check names the resolved schema, not any schema");
        existence.Transaction.Should().BeSameAs(writeTransaction);
        destination.Executions.Single(x => x.CommandText.Contains(PrimaryKeyLookup, StringComparison.Ordinal))
            .Parameters.Should().ContainSingle(x => x.ParameterName == "tableName")
            .Which.Value.Should().Be("\"sales\".\"orders\"");
        _writer.Calls.Single().Target.Should().Match<MigrationWriteTarget>(x => x.Schema == "sales" && x.Table == "orders");
    }

    [Test]
    public async Task APostgreSqlTableWithoutASchema_NotOnTheSearchPath_FailsTheRun_BeforeAnythingIsWritten()
    {
        Destination(new RecordingDbConnection { Scalar = x => x.Contains("current_schemas", StringComparison.Ordinal) ? DBNull.Value : true });
        Job(MigrationMode.Truncate, "orders");

        var result = await Service().ExecuteMigrationJob(Run(), CancellationToken.None);

        result.Status.Should().Be(MigrationStatus.Failed);
        result.ErrorMessage.Should().Contain("does not exist");
        _writer.Calls.Should().BeEmpty();
        _destination.Commits.Should().Be(0);
    }

    // The schema PostgreSQL resolves a name to, for a table that is on the search_path, and true for every other check.
    private static object? OnSearchPath(string sql) =>
        sql.Contains("current_schemas", StringComparison.Ordinal) ? "public" : true;

    private static RecordingDbConnection WithPrimaryKey(string column)
    {
        return new RecordingDbConnection
        {
            Scalar = OnSearchPath,
            RowCount = 1,
            ColumnsFor = x => x.Contains(PrimaryKeyLookup, StringComparison.Ordinal) ? [("attname", _ => column)] : null
        };
    }

    private RecordingDbConnection MySqlDestination(RecordingDbConnection connection)
    {
        _world.DataSources.First(x => x.Id == WriteTarget).DatabaseEngineType = DatabaseEngineType.MySQL;
        DbConnectionFactory.Register(DatabaseEngineType.MySQL, _ => connection);

        return connection;
    }

    private void Destination(RecordingDbConnection connection)
    {
        _destination = connection;
        _connectionsOpened = 0;
        DbConnectionFactory.Register(DatabaseEngineType.PostgreSQL, _ =>
        {
            _connectionsOpened++;
            return _destination;
        });
    }

    private void SourceRows(params Dictionary<string, object?>[] rows)
    {
        var result = new QueryResult
        {
            QueryResults = "",
            TotalRecords = rows.Length,
            DataSourceName = "source",
            SqlQuery = "SELECT id, name FROM accounts",
            SubscriptionName = "",
            SubscriptionId = null,
            AllRecords = rows
                .Select(x => (IDictionary<string, object?>)x)
                .ToList()
        };
        _preview
            .Setup(x => x.ExecuteTemporaryQueryPreview(It.IsAny<QueryData>(), It.IsAny<List<ParameterValue>?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryExecutionResult { Success = true, FinalResult = result });
    }

    private static Dictionary<string, object?> Row(int id, string name) => new() { ["id"] = id, ["name"] = name };

    private MigrationService Service(MigrationBulkWriters? writers = null)
    {
        return new MigrationService(
            _world.Factory(),
            TestTransport.Resolver(_encryption.Object),
            Mock.Of<IQueryService>(),
            _preview.Object,
            Mock.Of<IBeaconUserContext>(),
            writers ?? MigrationTestWriters.Of(_writer),
            NullLogger<MigrationService>.Instance,
            Options.Create(new MigrationOptions { Enabled = true }),
            TestChangeApprovals.Submitter(), TestProjectAccess.Actors(), new RecordingSecurityAudit(), TimeProvider.System);
    }

    private void Job(MigrationMode mode, string table = "target")
    {
        _world.Serve(new List<MigrationJob>
        {
            new()
            {
                Id = JobId,
                Name = "copy",
                Description = "copy rows",
                DataSourceId = ReadSource,
                DataSource = _world.DataSources.First(x => x.Id == ReadSource),
                DestinationDataSourceId = WriteTarget,
                DestinationDataSource = _world.DataSources.First(x => x.Id == WriteTarget),
                QueryText = JsonSerializer.Serialize(new[] { new { StepOrder = 1, Name = "step 1", SqlValue = "SELECT id, name FROM accounts", DataSourceId = ReadSource } }),
                DestinationTable = table,
                Mode = mode,
                IsEnabled = true
            }
        });
    }

    private static ExecuteMigrationJobRequest Run() => new(JobId, IsManualExecution: true, ExecutionContext: "test");
}
