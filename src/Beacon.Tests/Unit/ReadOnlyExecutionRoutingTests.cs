using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Knowledge;
using Beacon.Connector.AzureSynapse;
using Beacon.Connector.MySql;
using Beacon.Connector.PostgreSql;
using Beacon.Connector.Snowflake;
using Beacon.Connector.SqlServer;
using Beacon.Core;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.MCP.Services;
using Beacon.MCP.Tools;
using Beacon.Tests.Common;
using GuardrailValidationResult = Beacon.Core.Services.Security.QueryValidationResult;
using ProviderValidationResult = Beacon.Core.Models.Providers.QueryValidationResult;
using Beacon.Core.Mcp;

namespace Beacon.Tests.Unit;

/// <summary>
/// §1.5 backstop routing: MCP SQL execution goes through <c>ExecuteReadOnlyQueryAsync</c> so the
/// database itself rejects writes (PostgreSQL 25006 read_only_sql_transaction) even when the
/// regex/AST parsers are bypassed. Covers the interface default forwarding, the ProjectQueryTool
/// routing split (SQL → read-only path, API → normal path), and the <see cref="DatabaseProvider"/>
/// transaction mechanics via a recording <see cref="DbConnection"/> registered in
/// <see cref="DbConnectionFactory"/> — no real database (§4.7).
/// </summary>
[TestFixture]
[NonParallelizable] // DbConnectionFactory is a process-wide registry — parallel fixtures would race the fake registrations.
public class ReadOnlyExecutionRoutingTests
{
    private const int ProjectId = 42;
    private const int SqlDataSourceId = 7;
    private const int ApiDataSourceId = 8;
    private const string ValidSql = "SELECT id, name FROM customers";

    [TearDown]
    public void RestoreConnectionFactories()
    {
        // The DatabaseProvider tests re-register the engines' connection factories with recording
        // fakes; run the REAL registration paths (AddPostgreSqlConnector / AddSqlServerConnector)
        // over a throwaway service collection so later suites — including the in-process
        // integration harness — see production wiring.
        new BeaconBuilder(new ServiceCollection(), new ConfigurationBuilder().Build())
            .AddPostgreSqlConnector()
            .AddSqlServerConnector()
            .AddMySqlConnector()
            .AddAzureSynapseConnector()
            .AddSnowflakeConnector();
    }

    // --- (a) default interface method forwarding -------------------------------------------------

    [Test]
    public async Task ExecuteReadOnlyQueryAsync_Default_ForwardsToExecuteQueryAsyncWithSameArguments()
    {
        var provider = new ForwardingOnlyProvider();
        var dataSource = new DataSource
        {
            Id = 1,
            Name = "ds",
            DataSourceType = DataSourceType.Database,
            EncryptedConnectionData = "encrypted"
        };
        var parameters = new Dictionary<string, object?> { ["p"] = 1 };
        using var cts = new CancellationTokenSource();

        IDataSourceProvider viaInterface = provider;
        var result = await viaInterface.ExecuteReadOnlyQueryAsync(dataSource, ValidSql, parameters, cts.Token);

        result.Should().BeSameAs(provider.Result, "the default implementation must return ExecuteQueryAsync's result untouched");
        provider.ReceivedDataSource.Should().BeSameAs(dataSource);
        provider.ReceivedQuery.Should().Be(ValidSql);
        provider.ReceivedParameters.Should().BeSameAs(parameters);
        provider.ReceivedCancellationToken.Should().Be(cts.Token);
    }

    [Test]
    public void SupportsDatabaseReadOnlyEnforcement_InterfaceDefault_ReportsFalse()
    {
        // The honest-capability contract: a provider that inherits the forwarding default must
        // never claim a database-level read-only guarantee.
        IDataSourceProvider viaInterface = new ForwardingOnlyProvider();

        viaInterface.SupportsDatabaseReadOnlyEnforcement(DatabaseEngineType.PostgreSQL).Should().BeFalse();
        viaInterface.SupportsDatabaseReadOnlyEnforcement(DatabaseEngineType.MSSQL).Should().BeFalse();
        viaInterface.SupportsDatabaseReadOnlyEnforcement(null).Should().BeFalse();
    }

    [Test]
    public void SupportsDatabaseReadOnlyEnforcement_DatabaseProvider_TrueForPostgreSqlAndMySql()
    {
        var provider = CreateDatabaseProvider();

        provider.SupportsDatabaseReadOnlyEnforcement(DatabaseEngineType.PostgreSQL).Should().BeTrue(
            "PostgreSQL has the session + transaction read-only backstop");
        provider.SupportsDatabaseReadOnlyEnforcement(DatabaseEngineType.MySQL).Should().BeTrue(
            "MySQL runs the statement inside START TRANSACTION READ ONLY");
        provider.SupportsDatabaseReadOnlyEnforcement(DatabaseEngineType.MSSQL).Should().BeFalse();
        provider.SupportsDatabaseReadOnlyEnforcement(DatabaseEngineType.Snowflake).Should().BeFalse();
        provider.SupportsDatabaseReadOnlyEnforcement(null).Should().BeFalse();
    }

    // --- (b) MCP routing through ProjectQueryTool -------------------------------------------------

    [Test]
    public async Task ProjectQueryTool_SqlPath_UsesReadOnlyExecution_NeverPlainExecution()
    {
        var provider = new Mock<IDataSourceProvider>();
        provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(
                It.IsAny<DataSource>(),
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderQueryResult
            {
                Success = true,
                Rows = [new Dictionary<string, object?> { ["id"] = 1, ["name"] = "Alice" }]
            });

        var result = await TestToolCalls.InvokeAsync(CreateTool(provider), x => x.ExecuteAsync(
            datasource_id: SqlDataSourceId, sql: ValidSql, cancellationToken: CancellationToken.None));

        (result.IsError ?? false).Should().BeFalse();
        provider.Verify(
            x => x.ExecuteReadOnlyQueryAsync(
                It.Is<DataSource>(y => y.Id == SqlDataSourceId),
                It.Is<string>(y => y.StartsWith(ValidSql)),
                It.IsAny<Dictionary<string, object?>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        provider.Verify(
            x => x.ExecuteQueryAsync(
                It.IsAny<DataSource>(),
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, object?>>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "SQL must never bypass the database-level read-only backstop (§1.5)");
    }

    [Test]
    public async Task ProjectQueryTool_ApiPath_KeepsPlainExecution()
    {
        const string apiQuery = """{ "method": "GET", "path": "/items" }""";
        var provider = new Mock<IDataSourceProvider>();
        provider
            .Setup(x => x.ExecuteQueryAsync(
                It.IsAny<DataSource>(),
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderQueryResult { Success = true, Rows = [] });

        var result = await TestToolCalls.InvokeAsync(CreateTool(provider), x => x.ExecuteAsync(
            datasource_id: ApiDataSourceId, api_query: apiQuery, cancellationToken: CancellationToken.None));

        (result.IsError ?? false).Should().BeFalse();
        provider.Verify(
            x => x.ExecuteQueryAsync(
                It.Is<DataSource>(y => y.Id == ApiDataSourceId),
                apiQuery,
                It.IsAny<Dictionary<string, object?>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        provider.Verify(
            x => x.ExecuteReadOnlyQueryAsync(
                It.IsAny<DataSource>(),
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, object?>>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "API sources have no SQL engine — they stay on the normal execution path");
    }

    // --- (c) DatabaseProvider transaction mechanics -----------------------------------------------

    [Test]
    public async Task DatabaseProvider_ExecuteReadOnlyQueryAsync_IssuesSetTransactionReadOnlyBeforeQuery_AndCommits()
    {
        var connection = new RecordingDbConnection();
        DbConnectionFactory.Register(DatabaseEngineType.PostgreSQL, x => connection);

        var result = await CreateDatabaseProvider().ExecuteReadOnlyQueryAsync(
            PostgresDataSource(), "SELECT 1", new Dictionary<string, object?>(), CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        connection.ExecutedCommands.Should().Equal(
            "SET default_transaction_read_only = on", "SET TRANSACTION READ ONLY", "SELECT 1");
        connection.ExecutedCommands.Should().NotContain("START TRANSACTION READ ONLY");
        connection.BegunTransaction.Should().NotBeNull("the read-only guarantee lives on the transaction");
        connection.BegunTransaction!.Committed.Should().BeTrue("reads inside a READ ONLY transaction commit fine");
        connection.TransactionOf("SET default_transaction_read_only = on").Should().BeNull(
            "the session-level backstop must run on the connection, before/outside any transaction");
        connection.TransactionOf("SET TRANSACTION READ ONLY").Should().BeSameAs(connection.BegunTransaction);
        connection.TransactionOf("SELECT 1").Should().BeSameAs(connection.BegunTransaction,
            "the user query must be enlisted in the read-only transaction");
    }

    [Test]
    public async Task DatabaseProvider_ExecuteQueryAsync_DoesNotOpenAReadOnlyTransaction()
    {
        var connection = new RecordingDbConnection();
        DbConnectionFactory.Register(DatabaseEngineType.PostgreSQL, x => connection);

        var result = await CreateDatabaseProvider().ExecuteQueryAsync(
            PostgresDataSource(), "SELECT 1", new Dictionary<string, object?>(), CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        connection.ExecutedCommands.Should().Equal("SELECT 1");
        connection.BegunTransaction.Should().BeNull();
        connection.TransactionOf("SELECT 1").Should().BeNull(
            "plain execution must not enlist the query in any transaction");
    }

    [Test]
    public async Task DatabaseProvider_ExecuteReadOnlyQueryAsync_WriteRejectedByDatabase_ReturnsFailureAndDoesNotCommit()
    {
        // Simulates the §1.5 backstop firing: a write that slipped past the parsers reaches the
        // server inside the READ ONLY transaction and is rejected (PostgreSQL 25006).
        const string smuggledWrite = "UPDATE customers SET name = 'x'";
        var connection = new RecordingDbConnection
        {
            FailOnCommandText = x => x == smuggledWrite
        };
        DbConnectionFactory.Register(DatabaseEngineType.PostgreSQL, x => connection);

        var result = await CreateDatabaseProvider().ExecuteReadOnlyQueryAsync(
            PostgresDataSource(), smuggledWrite, new Dictionary<string, object?>(), CancellationToken.None);

        result.Success.Should().BeFalse("the server-side rejection must surface as a failed result");
        result.ErrorMessage.Should().Contain("25006", "the database error must reach the caller, not be swallowed");
        result.Rows.Should().BeEmpty();
        connection.BegunTransaction.Should().NotBeNull("the write must have been attempted inside the read-only transaction");
        connection.BegunTransaction!.Committed.Should().BeFalse("a rejected write must never be committed");
        connection.ExecutedCommands.Should().Equal(
            "SET default_transaction_read_only = on", "SET TRANSACTION READ ONLY", smuggledWrite);
    }

    [Test]
    public async Task DatabaseProvider_SqlServer_ExecuteReadOnlyQueryAsync_RunsInATransactionThatIsAlwaysRolledBack()
    {
        // SQL Server has no READ ONLY transaction mode: the read-only path runs the statement inside a transaction that is
        // rolled back, never committed, so a write the parsers missed is undone. It cannot refuse the write, so
        // SupportsDatabaseReadOnlyEnforcement stays false for SQL Server.
        var connection = new RecordingDbConnection();
        DbConnectionFactory.Register(DatabaseEngineType.MSSQL, x => connection);

        var result = await CreateDatabaseProvider().ExecuteReadOnlyQueryAsync(
            EngineDataSource(DatabaseEngineType.MSSQL), "SELECT 1", new Dictionary<string, object?>(), CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        connection.ExecutedCommands.Should().Equal(
            "SAVE TRANSACTION @savepoint",
            "SELECT 1",
            "SELECT @@TRANCOUNT",
            "ROLLBACK TRANSACTION @savepoint");
        connection.BegunTransaction.Should().NotBeNull();
        connection.Executions.Should().OnlyContain(x => x.Transaction == connection.BegunTransaction);
        connection.BegunTransaction!.RolledBack.Should().BeTrue();
        connection.BegunTransaction.Committed.Should().BeFalse();
        CreateDatabaseProvider().SupportsDatabaseReadOnlyEnforcement(DatabaseEngineType.MSSQL).Should().BeFalse();
    }

    // A statement that ends the transaction it runs in (a COMMIT, or a COMMIT and a new BEGIN TRANSACTION, which drops the
    // savepoint) is not undone by the rollback: the result is discarded, the run fails, and a security event names the
    // data source.
    [TestCase(0, false)]
    [TestCase(null, true)]
    public async Task DatabaseProvider_SqlServer_AStatementThatEndsTheTransaction_FailsTheRun_AndIsLogged(int? transactionCount, bool savepointGone)
    {
        var connection = new RecordingDbConnection
        {
            TransactionCount = transactionCount,
            FailOnCommandText = x => savepointGone && x == "ROLLBACK TRANSACTION @savepoint",
            FailureMessage = "Cannot roll back the savepoint. No transaction or savepoint of that name was found."
        };
        DbConnectionFactory.Register(DatabaseEngineType.MSSQL, x => connection);
        var logs = new LogRecorder();

        var result = await CreateDatabaseProvider(logger: logs.For<DatabaseProvider>())
            .ExecuteReadOnlyQueryAsync(EngineDataSource(DatabaseEngineType.MSSQL), "SELECT 1", new Dictionary<string, object?>(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be(DatabaseProvider.SqlServerTransactionEndedMessage);
        result.Rows.Should().BeEmpty();
        connection.BegunTransaction!.Committed.Should().BeFalse();
        logs.Entries
            .Where(x => x.Level == Microsoft.Extensions.Logging.LogLevel.Warning)
            .Should().ContainSingle(x => x.Message.Contains("ended its rolled-back transaction"));
    }

    // PostgreSQL's read-only path sends the statement as a batch of exactly one command, inside the READ ONLY transaction,
    // with its parameters: the driver refuses a second statement in it instead of running each.
    [Test]
    public async Task DatabaseProvider_PostgreSql_RunsTheStatementAsABatchOfOneCommand()
    {
        var connection = new RecordingDbConnection();
        DbConnectionFactory.Register(DatabaseEngineType.PostgreSQL, x => connection);

        var result = await CreateDatabaseProvider().ExecuteReadOnlyQueryAsync(
            EngineDataSource(DatabaseEngineType.PostgreSQL), "SELECT id FROM t WHERE id = @p0", new Dictionary<string, object?> { ["p0"] = 7L }, CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        connection.BatchSizes.Should().Equal(1);
        connection.TransactionOf("SELECT id FROM t WHERE id = @p0").Should().BeSameAs(connection.BegunTransaction);
        connection.BegunTransaction!.Committed.Should().BeTrue();
    }

    // The plain path (read-only off) is unchanged: no batch.
    [Test]
    public async Task DatabaseProvider_PostgreSql_ThePlainPath_RunsNoBatch()
    {
        var connection = new RecordingDbConnection();
        DbConnectionFactory.Register(DatabaseEngineType.PostgreSQL, x => connection);

        var result = await CreateDatabaseProvider().ExecuteQueryAsync(
            EngineDataSource(DatabaseEngineType.PostgreSQL), "SELECT 1", new Dictionary<string, object?>(), CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        connection.BatchSizes.Should().BeEmpty();
    }

    // The statement that fails runs inside the transaction Beacon opened (after its savepoint), so whatever it did before
    // failing is undone with that transaction; nothing runs after it, and the server's refusal reaches the caller.
    [Test]
    public async Task DatabaseProvider_SqlServer_AWriteTheDatabaseRejects_RanInsideTheTransaction_AndNothingRunsAfterIt()
    {
        const string write = "UPDATE customers SET name = 'x'";
        var connection = new RecordingDbConnection
        {
            FailOnCommandText = x => x == write,
            FailureMessage = "The UPDATE permission was denied on the object 'customers'."
        };
        DbConnectionFactory.Register(DatabaseEngineType.MSSQL, x => connection);

        var result = await CreateDatabaseProvider().ExecuteReadOnlyQueryAsync(
            EngineDataSource(DatabaseEngineType.MSSQL), write, new Dictionary<string, object?>(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("The UPDATE permission was denied on the object 'customers'.");
        connection.ExecutedCommands.Should().Equal("SAVE TRANSACTION @savepoint", write);
        connection.TransactionOf(write).Should().BeSameAs(connection.BegunTransaction);
        connection.BegunTransaction!.Committed.Should().BeFalse();
    }

    [Test]
    public async Task DatabaseProvider_SqlServer_ExecuteQueryAsync_OpensNoTransaction()
    {
        var connection = new RecordingDbConnection();
        DbConnectionFactory.Register(DatabaseEngineType.MSSQL, x => connection);

        var result = await CreateDatabaseProvider().ExecuteQueryAsync(
            EngineDataSource(DatabaseEngineType.MSSQL), "SELECT 1", new Dictionary<string, object?>(), CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        connection.BegunTransaction.Should().BeNull();
    }

    // Synapse (dedicated and serverless pools differ in what transactions they allow) and Snowflake have no read-only
    // session: their read-only path runs the statement plainly (Snowflake inside its session statement timeout, set and
    // reset around it), and a read-only login is the only database-side check.
    [TestCase(DatabaseEngineType.AzureSynapse)]
    [TestCase(DatabaseEngineType.Snowflake)]
    public async Task DatabaseProvider_EngineWithoutReadOnlySession_ExecuteReadOnlyQueryAsync_RunsPlain(DatabaseEngineType engine)
    {
        var connection = new RecordingDbConnection();
        DbConnectionFactory.Register(engine, x => connection);

        var result = await CreateDatabaseProvider().ExecuteReadOnlyQueryAsync(
            EngineDataSource(engine), "SELECT 1", new Dictionary<string, object?>(), CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        string[] expected = engine == DatabaseEngineType.Snowflake
            ? [$"ALTER SESSION SET STATEMENT_TIMEOUT_IN_SECONDS = {TestQueryServices.QueryOptions.Value.StatementTimeoutSeconds}", "SELECT 1", "ALTER SESSION UNSET STATEMENT_TIMEOUT_IN_SECONDS"]
            : ["SELECT 1"];
        connection.ExecutedCommands.Should().Equal(expected);
        connection.BegunTransaction.Should().BeNull();
        CreateDatabaseProvider().SupportsDatabaseReadOnlyEnforcement(engine).Should().BeFalse();
    }

    [Test]
    public async Task DatabaseProvider_MySql_ExecuteReadOnlyQueryAsync_RunsReadOnlyWithoutBatches_AndRollsBack()
    {
        var connection = new RecordingDbConnection();
        string? openedWith = null;
        DbConnectionFactory.Register(DatabaseEngineType.MySQL, x =>
        {
            openedWith = x;
            return connection;
        });

        var result = await CreateDatabaseProvider("Server=unused;Allow Batch=true").ExecuteReadOnlyQueryAsync(
            MySqlDataSource(), "SELECT 1", new Dictionary<string, object?>(), CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        connection.ExecutedCommands.Should().Equal(MySqlReadOnly("SELECT 1"));
        connection.ExecutedCommands.Should().NotContain("SET default_transaction_read_only = on");
        connection.BegunTransaction.Should().BeNull(
            "MySQL fixes the access mode when a transaction starts, so it is opened with SQL, not BeginTransaction");
        openedWith.Should().NotContainEquivalentOf("allow batch=true");
        openedWith.Should().ContainEquivalentOf("allowbatch=false", "a batch could end the transaction and then write");
    }

    [Test]
    public async Task DatabaseProvider_MySql_WriteRejectedByDatabase_ReturnsFailure_AndStillRollsBack()
    {
        const string rejectedWrite = "UPDATE customers SET name = 'x'";
        var connection = new RecordingDbConnection
        {
            FailOnCommandText = x => x == rejectedWrite,
            FailureMessage = "Cannot execute statement in a READ ONLY transaction."
        };
        DbConnectionFactory.Register(DatabaseEngineType.MySQL, x => connection);

        var result = await CreateDatabaseProvider().ExecuteReadOnlyQueryAsync(
            MySqlDataSource(), rejectedWrite, new Dictionary<string, object?>(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Cannot execute statement in a READ ONLY transaction.");
        result.Rows.Should().BeEmpty();
        connection.ExecutedCommands.Should().Equal(MySqlReadOnly(rejectedWrite));
    }

    [Test]
    public async Task DatabaseProvider_MySql_StartTransactionFails_TheQueryIsNeverSent()
    {
        var connection = new RecordingDbConnection
        {
            FailOnCommandText = x => x == "START TRANSACTION READ ONLY",
            FailureMessage = "1064: syntax error"
        };
        DbConnectionFactory.Register(DatabaseEngineType.MySQL, x => connection);

        var result = await CreateDatabaseProvider().ExecuteReadOnlyQueryAsync(
            MySqlDataSource(), "SELECT 1", new Dictionary<string, object?>(), CancellationToken.None);

        result.Success.Should().BeFalse();
        connection.ExecutedCommands.Should().NotContain("SELECT 1");
        connection.ExecutedCommands.Should().EndWith(["ROLLBACK", "SET SESSION TRANSACTION READ WRITE"]);
    }

    [Test]
    public async Task DatabaseProvider_MySql_RollbackFailsAfterARead_DiscardsTheResult()
    {
        var connection = new RecordingDbConnection
        {
            FailOnCommandText = x => x == "ROLLBACK",
            FailureMessage = "2013: Lost connection to MySQL server during query"
        };
        DbConnectionFactory.Register(DatabaseEngineType.MySQL, x => connection);

        var result = await CreateDatabaseProvider().ExecuteReadOnlyQueryAsync(
            MySqlDataSource(), "SELECT 1", new Dictionary<string, object?>(), CancellationToken.None);

        result.Success.Should().BeFalse("the connection's read-only state could not be restored");
        result.ErrorMessage.Should().Be(DatabaseProvider.MySqlReadOnlyNotClosedMessage);
        result.Rows.Should().BeEmpty();
    }

    // A read stopped at the row cap cancels the rest of its statement, which can leave MySQL's transaction unable to close.
    // The result is still discarded, and the caller is told what it has to change: the result is larger than its ceiling.
    [Test]
    public async Task DatabaseProvider_MySql_RollbackFailsAfterAReadStoppedAtTheCap_ReportsTheRowCeiling()
    {
        var connection = new Beacon.Tests.Common.RecordingDbConnection
        {
            RowCount = 50,
            FailOnCommandText = x => x == "ROLLBACK"
        };
        DbConnectionFactory.Register(DatabaseEngineType.MySQL, x => connection);

        var result = await CreateDatabaseProvider().ExecuteReadOnlyQueryAsync(
            MySqlDataSource(), "SELECT id FROM accounts LIMIT 11", new Dictionary<string, object?>(), 11, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be(DatabaseProvider.MoreRowsThanMessage(10));
        result.ErrorMessage.Should().Contain("more than 10 rows");
        result.Rows.Should().BeEmpty();
    }

    [Test]
    public async Task DatabaseProvider_MySql_CallerCancelsAfterTheRead_TheRollbackStillRunsAndTheReadSucceeds()
    {
        using var cancellation = new CancellationTokenSource();
        var connection = new RecordingDbConnection
        {
            FailOnCommandText = x =>
            {
                if (x == "ROLLBACK")
                {
                    cancellation.Cancel();
                }

                return false;
            }
        };
        DbConnectionFactory.Register(DatabaseEngineType.MySQL, x => connection);

        var result = await CreateDatabaseProvider().ExecuteReadOnlyQueryAsync(
            MySqlDataSource(), "SELECT 1", new Dictionary<string, object?>(), cancellation.Token);

        result.Success.Should().BeTrue(result.ErrorMessage);
        connection.ExecutedCommands.Should().Equal(MySqlReadOnly("SELECT 1"));
    }

    [Test]
    public async Task DatabaseProvider_MySql_ExecuteQueryAsync_DoesNotOpenAReadOnlyTransaction()
    {
        var connection = new RecordingDbConnection();
        DbConnectionFactory.Register(DatabaseEngineType.MySQL, x => connection);

        var result = await CreateDatabaseProvider().ExecuteQueryAsync(
            MySqlDataSource(), "SELECT 1", new Dictionary<string, object?>(), CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        connection.ExecutedCommands.Should().Equal("SELECT 1");
    }

    [Test]
    public async Task DatabaseProvider_ValidateQueryAsync_EngineWithoutDryRunStrategy_ReportsSkippedNotValid()
    {
        // codex PR-11 R4: engines without an EXPLAIN / sp_describe_first_result_set strategy used to fall
        // through the dry-run switch as IsValid=true — a vacuous gate. They now report an explicit
        // skipped result (before any connection is opened) so callers can surface it honestly.
        var dataSource = new DataSource
        {
            Id = SqlDataSourceId,
            Name = "sqlite-cache",
            DataSourceType = DataSourceType.Database,
            EncryptedConnectionData = "encrypted",
            DatabaseEngineType = DatabaseEngineType.SQLite
        };

        var result = await CreateDatabaseProvider().ValidateQueryAsync(
            dataSource, "SELECT 1", CancellationToken.None);

        result.IsValid.Should().BeFalse("nothing was checked, so the query must not be reported valid");
        result.Skipped.Should().BeTrue();
        result.Errors.Should().ContainSingle(x => x.Contains("not supported for engine SQLite"));
    }

    [Test]
    public async Task DatabaseProvider_ValidateQueryAsync_PostgresEngine_RunsExplainAndReportsValidNotSkipped()
    {
        var connection = new RecordingDbConnection();
        DbConnectionFactory.Register(DatabaseEngineType.PostgreSQL, x => connection);

        var result = await CreateDatabaseProvider().ValidateQueryAsync(
            PostgresDataSource(), "SELECT 1", CancellationToken.None);

        result.IsValid.Should().BeTrue();
        result.Skipped.Should().BeFalse();
        connection.ExecutedCommands.Should().Equal("EXPLAIN SELECT 1");
    }

    private static DatabaseProvider CreateDatabaseProvider(
        string connectionString = "Host=unused;Database=unused",
        Microsoft.Extensions.Logging.ILogger<DatabaseProvider>? logger = null)
    {
        var encryption = new Mock<IEncryptionService>();
        encryption
            .Setup(x => x.Decrypt("encrypted"))
            .Returns(connectionString);

        var resolver = new Mock<Beacon.Core.HostData.IDataSourceConnectionResolver>();
        resolver
            .Setup(x => x.GetConnectionString(It.IsAny<DataSource>()))
            .Returns<DataSource>(x => encryption.Object.Decrypt(x.EncryptedConnectionData));

        var hostGuard = new Mock<Beacon.Core.HostData.IHostDataSourceGuard>();
        hostGuard
            .Setup(x => x.Check(It.IsAny<DataSource>(), It.IsAny<string>()))
            .Returns(new Beacon.Core.HostData.HostPolicyResult(true, null, []));

        return new DatabaseProvider(
            resolver.Object,
            new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance),
            hostGuard.Object,
            new ConfiguredDatabaseResolver(resolver.Object, NullLogger<ConfiguredDatabaseResolver>.Instance),
            logger ?? NullLogger<DatabaseProvider>.Instance,
            TestQueryServices.QueryOptions);
    }

    private static string[] MySqlReadOnly(string query) =>
    [
        "SET SESSION TRANSACTION READ ONLY",
        "START TRANSACTION READ ONLY",
        query,
        "ROLLBACK",
        "SET SESSION TRANSACTION READ WRITE"
    ];

    private static DataSource EngineDataSource(DatabaseEngineType engine)
    {
        return new DataSource
        {
            Id = SqlDataSourceId,
            Name = "warehouse",
            DataSourceType = DataSourceType.Database,
            EncryptedConnectionData = "encrypted",
            DatabaseEngineType = engine
        };
    }

    private static DataSource MySqlDataSource()
    {
        return new DataSource
        {
            Id = SqlDataSourceId,
            Name = "mysql-shop",
            DataSourceType = DataSourceType.Database,
            EncryptedConnectionData = "encrypted",
            DatabaseEngineType = DatabaseEngineType.MySQL
        };
    }

    private static DataSource PostgresDataSource()
    {
        return new DataSource
        {
            Id = SqlDataSourceId,
            Name = "warehouse",
            DataSourceType = DataSourceType.Database,
            EncryptedConnectionData = "encrypted",
            DatabaseEngineType = DatabaseEngineType.PostgreSQL
        };
    }

    private static ProjectQueryTool CreateTool(Mock<IDataSourceProvider> provider)
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RoutingTestContext());

        var providerFactory = new Mock<IDataSourceProviderFactory>();
        providerFactory
            .Setup(x => x.GetProvider(It.IsAny<DataSourceType>()))
            .Returns(provider.Object);

        var guardrail = new Mock<IQueryGuardrailService>();
        guardrail
            .Setup(x => x.ValidateQuery(It.IsAny<string>(), It.IsAny<QueryGuardrailOptions?>()))
            .Returns(new GuardrailValidationResult(true));

        var settingsProvider = SettingsProviderMock.Create();
        settingsProvider
            .Setup(x => x.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpSettingsData());

        var projectContext = new McpProjectContext { UserId = 1, AllowedProjectIds = [ProjectId], QueryProjectIds = [ProjectId] };

        // McpAuditService and McpSignalService swallow sink failures by design (§1.7/§9.5), so bare
        // factory mocks suffice here — the audit/signal paths run without a database (§4.7); the
        // audit rows themselves are asserted in DryRunToolTests / GetQueryContextToolTests.
        var auditService = new McpAuditService(
            new Mock<IDbContextFactory<BeaconContext>>().Object,
            SettingsProviderMock.Create().Object,
            new HttpContextAccessor(),
            Options.Create(new McpDeploymentOptions()),
            NullLogger<McpAuditService>.Instance, new McpAuditOutcome(), Options.Create(new BeaconTelemetryOptions()), NullLoggerFactory.Instance, TestMcpAudit.ContentHasher, new McpStatementLog());
        var signalService = new McpSignalService(
            new Mock<IDbContextFactory<BeaconContext>>().Object,
            settingsProvider.Object,
            NullLogger<McpSignalService>.Instance);

        // The catalog of a scanned source holding the table ValidSql reads (`query` refuses a source with none).
        var knowledgeGraph = new Mock<IKnowledgeGraphService>();
        knowledgeGraph
            .Setup(x => x.GetSchemaCatalogAsync(It.IsAny<int>(), ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["customers"] = new(["id", "name"], StringComparer.OrdinalIgnoreCase)
            });

        return TestToolCalls.Track(recorder => new ProjectQueryTool(
            factory.Object,
            providerFactory.Object,
            guardrail.Object,
            TestSqlGate.Create(guardrail.Object),
            TestConfiguredDatabases.None,
            TestRowSecurity.Resolver,
            knowledgeGraph.Object,
            SettingsProviderMock.Unshared(settingsProvider.Object),
            projectContext,
            recorder,
            new McpQueryConcurrencyLimiter(),
            TestQueryCostLimit.Passing,
            NullLogger<ProjectQueryTool>.Instance), () => TestToolCalls.Recorder(auditService, signalService));
    }

    /// <summary>Implements ONLY the abstract interface members — <c>ExecuteReadOnlyQueryAsync</c> is
    /// deliberately not implemented so the interface DEFAULT body is what executes.</summary>
    private sealed class ForwardingOnlyProvider : IDataSourceProvider
    {
        public ProviderQueryResult Result { get; } = new() { Success = true };
        public DataSource? ReceivedDataSource { get; private set; }
        public string? ReceivedQuery { get; private set; }
        public Dictionary<string, object?>? ReceivedParameters { get; private set; }
        public CancellationToken ReceivedCancellationToken { get; private set; }

        public DataSourceType SupportedType => DataSourceType.Database;

        public string GetQueryLanguageName() => "SQL";

        public Task<ProviderQueryResult> ExecuteQueryAsync(
            DataSource dataSource,
            string query,
            Dictionary<string, object?> parameters,
            CancellationToken cancellationToken = default)
        {
            ReceivedDataSource = dataSource;
            ReceivedQuery = query;
            ReceivedParameters = parameters;
            ReceivedCancellationToken = cancellationToken;
            return Task.FromResult(Result);
        }

        public Task<ConnectionTestResult> TestConnectionAsync(
            DataSource dataSource,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DataSourceMetadata> GetMetadataAsync(
            DataSource dataSource,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ProviderValidationResult> ValidateQueryAsync(
            DataSource dataSource,
            string query,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Serves the data-source resolution queries over async-queryable doubles — no DB, no
    /// UseInMemoryDatabase (§4.7). Mirrors DryRunToolTests.</summary>
    private sealed class RoutingTestContext : BeaconContext
    {
        private static readonly DbContextOptions<RoutingTestContext> Options =
            new DbContextOptionsBuilder<RoutingTestContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        public RoutingTestContext() : base(Options, "beacon")
        {
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(DataSource))
            {
                return (DbSet<TEntity>)(object)BuildSet(new List<DataSource>
                {
                    new()
                    {
                        Id = SqlDataSourceId,
                        Name = "warehouse",
                        DataSourceType = DataSourceType.Database,
                        EncryptedConnectionData = "encrypted",
                        DatabaseEngineType = DatabaseEngineType.PostgreSQL
                    },
                    new()
                    {
                        Id = ApiDataSourceId,
                        Name = "crm-api",
                        DataSourceType = DataSourceType.Api,
                        EncryptedConnectionData = "encrypted"
                    }
                });
            }

            if (typeof(TEntity) == typeof(ProjectDataSource))
            {
                return (DbSet<TEntity>)(object)BuildSet(new List<ProjectDataSource>
                {
                    new() { ProjectId = ProjectId, DataSourceId = SqlDataSourceId },
                    new() { ProjectId = ProjectId, DataSourceId = ApiDataSourceId }
                });
            }

            return base.Set<TEntity>();
        }

        private static DbSet<T> BuildSet<T>(List<T> data) where T : class
        {
            var queryable = data.AsQueryable();
            var set = new Mock<DbSet<T>>();
            set.As<IAsyncEnumerable<T>>()
                .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
                .Returns(() => new TestAsyncEnumerator<T>(queryable.GetEnumerator()));
            set.As<IQueryable<T>>()
                .Setup(x => x.Provider)
                .Returns(new TestAsyncQueryProvider<T>(queryable.Provider));
            set.As<IQueryable<T>>().Setup(x => x.Expression).Returns(queryable.Expression);
            set.As<IQueryable<T>>().Setup(x => x.ElementType).Returns(queryable.ElementType);
            set.As<IQueryable<T>>().Setup(x => x.GetEnumerator()).Returns(() => queryable.GetEnumerator());
            return set.Object;
        }
    }

    /// <summary>Recording ADO.NET doubles: capture every executed command text in order plus the
    /// transaction each command was enlisted in and the begun transaction, so the read-only
    /// transaction mechanics are assertable without a server. <see cref="FailOnCommandText"/> makes
    /// the fake server REJECT a matching command (after recording it) with a
    /// <see cref="DbException"/>-derived error, simulating 25006 read_only_sql_transaction.</summary>
    private sealed class RecordingDbConnection : DbConnection
    {
        private ConnectionState _state = ConnectionState.Closed;

        public List<(string CommandText, DbTransaction? Transaction)> Executions { get; } = [];
        public RecordingDbTransaction? BegunTransaction { get; private set; }
        public Func<string, bool>? FailOnCommandText { get; init; }
        public string FailureMessage { get; init; } = "25006: cannot execute UPDATE in a read-only transaction (read_only_sql_transaction)";

        public void RecordExecution(string commandText, DbTransaction? transaction)
        {
            Executions.Add((commandText, transaction));
            if (FailOnCommandText != null && FailOnCommandText(commandText))
            {
                throw new FakeDbException(FailureMessage);
            }
        }

        public IEnumerable<string> ExecutedCommands => Executions.Select(x => x.CommandText);

        /// <summary>What <c>SELECT @@TRANCOUNT</c> answers; null answers one while the begun transaction is open.</summary>
        public int? TransactionCount { get; init; }

        public int OpenTransactions => TransactionCount ?? (BegunTransaction is { Committed: false, RolledBack: false } ? 1 : 0);

        public DbTransaction? TransactionOf(string commandText) =>
            Executions.Single(x => x.CommandText == commandText).Transaction;

        [AllowNull]
        public override string ConnectionString { get; set; } = "";
        public override string Database => "unused";
        public override string DataSource => "unused";
        public override string ServerVersion => "0.0";
        public override ConnectionState State => _state;

        public override void Open() => _state = ConnectionState.Open;

        public override void Close() => _state = ConnectionState.Closed;

        public override void ChangeDatabase(string databaseName)
        {
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        {
            BegunTransaction = new RecordingDbTransaction(this);
            return BegunTransaction;
        }

        protected override DbCommand CreateDbCommand() => new RecordingDbCommand(this);

        /// <summary>The number of commands of every batch run.</summary>
        public List<int> BatchSizes { get; } = [];

        public override bool CanCreateBatch => true;

        protected override DbBatch CreateDbBatch() => new CommandBackedDbBatch(this, BatchSizes);
    }

    private sealed class RecordingDbTransaction(RecordingDbConnection connection) : DbTransaction
    {
        public bool Committed { get; private set; }
        public bool RolledBack { get; private set; }

        public override IsolationLevel IsolationLevel => IsolationLevel.Unspecified;
        protected override DbConnection DbConnection => connection;

        public override void Commit() => Committed = true;

        public override void Rollback() => RolledBack = true;
    }

    private sealed class RecordingDbCommand(RecordingDbConnection connection) : DbCommand
    {
        [AllowNull]
        public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = connection;
        protected override DbParameterCollection DbParameterCollection { get; } = new FakeParameterCollection();
        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel()
        {
        }

        public override void Prepare()
        {
        }

        public override int ExecuteNonQuery()
        {
            connection.RecordExecution(CommandText, DbTransaction);
            return 0;
        }

        // SQL Server's read-only path asks for @@TRANCOUNT after the read: one while the begun transaction is open.
        public override object? ExecuteScalar()
        {
            connection.RecordExecution(CommandText, DbTransaction);
            return CommandText == "SELECT @@TRANCOUNT" ? connection.OpenTransactions : null;
        }

        protected override DbParameter CreateDbParameter() => new FakeParameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            connection.RecordExecution(CommandText, DbTransaction);
            return new EmptyDataReader();
        }
    }

    private sealed class FakeDbException(string message) : DbException(message);

    private sealed class FakeParameterCollection : DbParameterCollection
    {
        private readonly List<object> _parameters = [];

        public override int Count => _parameters.Count;
        public override object SyncRoot => this;

        public override int Add(object value)
        {
            _parameters.Add(value);
            return _parameters.Count - 1;
        }

        public override void AddRange(Array values) => throw new NotSupportedException();

        public override void Clear() => _parameters.Clear();

        public override bool Contains(object value) => _parameters.Contains(value);

        public override bool Contains(string value) => false;

        public override void CopyTo(Array array, int index) => throw new NotSupportedException();

        public override IEnumerator GetEnumerator() => _parameters.GetEnumerator();

        public override int IndexOf(object value) => _parameters.IndexOf(value);

        public override int IndexOf(string parameterName) => -1;

        public override void Insert(int index, object value) => _parameters.Insert(index, value);

        public override void Remove(object value) => _parameters.Remove(value);

        public override void RemoveAt(int index) => _parameters.RemoveAt(index);

        public override void RemoveAt(string parameterName) => throw new NotSupportedException();

        protected override DbParameter GetParameter(int index) => (DbParameter)_parameters[index];

        protected override DbParameter GetParameter(string parameterName) => throw new NotSupportedException();

        protected override void SetParameter(int index, DbParameter value) => _parameters[index] = value;

        protected override void SetParameter(string parameterName, DbParameter value) => throw new NotSupportedException();
    }

    private sealed class FakeParameter : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; }
        public override bool IsNullable { get; set; }
        [AllowNull]
        public override string ParameterName { get; set; } = "";
        public override int Size { get; set; }
        [AllowNull]
        public override string SourceColumn { get; set; } = "";
        public override bool SourceColumnNullMapping { get; set; }
        public override object? Value { get; set; }

        public override void ResetDbType()
        {
        }
    }

    private sealed class EmptyDataReader : DbDataReader
    {
        public override int Depth => 0;
        public override int FieldCount => 0;
        public override bool HasRows => false;
        public override bool IsClosed => false;
        public override int RecordsAffected => 0;
        public override object this[int ordinal] => throw new IndexOutOfRangeException();
        public override object this[string name] => throw new IndexOutOfRangeException();

        public override bool Read() => false;

        public override bool NextResult() => false;

        public override IEnumerator GetEnumerator() => throw new NotSupportedException();

        public override bool GetBoolean(int ordinal) => throw new IndexOutOfRangeException();

        public override byte GetByte(int ordinal) => throw new IndexOutOfRangeException();

        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new IndexOutOfRangeException();

        public override char GetChar(int ordinal) => throw new IndexOutOfRangeException();

        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new IndexOutOfRangeException();

        public override string GetDataTypeName(int ordinal) => throw new IndexOutOfRangeException();

        public override DateTime GetDateTime(int ordinal) => throw new IndexOutOfRangeException();

        public override decimal GetDecimal(int ordinal) => throw new IndexOutOfRangeException();

        public override double GetDouble(int ordinal) => throw new IndexOutOfRangeException();

        public override Type GetFieldType(int ordinal) => throw new IndexOutOfRangeException();

        public override float GetFloat(int ordinal) => throw new IndexOutOfRangeException();

        public override Guid GetGuid(int ordinal) => throw new IndexOutOfRangeException();

        public override short GetInt16(int ordinal) => throw new IndexOutOfRangeException();

        public override int GetInt32(int ordinal) => throw new IndexOutOfRangeException();

        public override long GetInt64(int ordinal) => throw new IndexOutOfRangeException();

        public override string GetName(int ordinal) => throw new IndexOutOfRangeException();

        public override int GetOrdinal(string name) => throw new IndexOutOfRangeException();

        public override string GetString(int ordinal) => throw new IndexOutOfRangeException();

        public override object GetValue(int ordinal) => throw new IndexOutOfRangeException();

        public override int GetValues(object[] values) => 0;

        public override bool IsDBNull(int ordinal) => throw new IndexOutOfRangeException();
    }
}
