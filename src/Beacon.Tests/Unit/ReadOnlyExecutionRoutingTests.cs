using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.Connector.PostgreSql;
using Beacon.Connector.SqlServer;
using Beacon.Core;
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
        // The DatabaseProvider tests re-register the PostgreSQL and MSSQL factories with recording
        // fakes; run the REAL registration paths (AddPostgreSqlConnector / AddSqlServerConnector)
        // over a throwaway service collection so later suites — including the in-process
        // integration harness — see production wiring.
        new BeaconBuilder(new ServiceCollection(), new ConfigurationBuilder().Build())
            .AddPostgreSqlConnector()
            .AddSqlServerConnector();
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
    public void SupportsDatabaseReadOnlyEnforcement_DatabaseProvider_TrueOnlyForPostgreSQL()
    {
        var provider = CreateDatabaseProvider();

        provider.SupportsDatabaseReadOnlyEnforcement(DatabaseEngineType.PostgreSQL).Should().BeTrue(
            "PostgreSQL has the session + transaction read-only backstop");
        provider.SupportsDatabaseReadOnlyEnforcement(DatabaseEngineType.MSSQL).Should().BeFalse();
        provider.SupportsDatabaseReadOnlyEnforcement(DatabaseEngineType.MySQL).Should().BeFalse(
            "MySQL read-only transactions are deferred — see SupportsReadOnlyTransaction");
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

        var result = await CreateTool(provider).ExecuteAsync(
            datasource_id: SqlDataSourceId, sql: ValidSql, cancellationToken: CancellationToken.None);

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

        var result = await CreateTool(provider).ExecuteAsync(
            datasource_id: ApiDataSourceId, api_query: apiQuery, cancellationToken: CancellationToken.None);

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
    public async Task DatabaseProvider_NonPostgresEngine_ExecuteReadOnlyQueryAsync_ExecutesPlainWithoutTransaction()
    {
        // MSSQL has no READ ONLY transaction mode, so the read-only path degrades to plain
        // execution — SupportsDatabaseReadOnlyEnforcement reports exactly this.
        var connection = new RecordingDbConnection();
        DbConnectionFactory.Register(DatabaseEngineType.MSSQL, x => connection);
        var dataSource = new DataSource
        {
            Id = SqlDataSourceId,
            Name = "mssql-warehouse",
            DataSourceType = DataSourceType.Database,
            EncryptedConnectionData = "encrypted",
            DatabaseEngineType = DatabaseEngineType.MSSQL
        };

        var result = await CreateDatabaseProvider().ExecuteReadOnlyQueryAsync(
            dataSource, "SELECT 1", new Dictionary<string, object?>(), CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        // No SET read-only statements exist for engines without database-level enforcement.
        connection.ExecutedCommands.Should().Equal("SELECT 1");
        connection.BegunTransaction.Should().BeNull("no read-only transaction must be opened for a non-PostgreSQL engine");
        connection.TransactionOf("SELECT 1").Should().BeNull();
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

    private static DatabaseProvider CreateDatabaseProvider()
    {
        var encryption = new Mock<IEncryptionService>();
        encryption
            .Setup(x => x.Decrypt("encrypted"))
            .Returns("Host=unused;Database=unused");

        return new DatabaseProvider(
            encryption.Object,
            new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance),
            NullLogger<DatabaseProvider>.Instance);
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
        guardrail
            .Setup(x => x.ApplyRowLimit(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>()))
            .Returns<string, int, string?>((sql, maxRows, _) => $"{sql} LIMIT {maxRows}");

        var settingsProvider = new Mock<IMcpSettingsProvider>();
        settingsProvider
            .Setup(x => x.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpSettingsData());

        var projectContext = new McpProjectContext { UserId = 1, AllowedProjectIds = [ProjectId] };

        // McpAuditService and McpSignalService swallow sink failures by design (§1.7/§9.5), so bare
        // factory mocks suffice here — the audit/signal paths run without a database (§4.7); the
        // audit rows themselves are asserted in DryRunToolTests / GetQueryContextToolTests.
        var auditService = new McpAuditService(
            new Mock<IDbContextFactory<BeaconContext>>().Object,
            NullLogger<McpAuditService>.Instance);
        var signalService = new McpSignalService(
            new Mock<IDbContextFactory<BeaconContext>>().Object,
            settingsProvider.Object,
            NullLogger<McpSignalService>.Instance);

        return new ProjectQueryTool(
            factory.Object,
            providerFactory.Object,
            guardrail.Object,
            new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance),
            settingsProvider.Object,
            projectContext,
            auditService,
            signalService,
            NullLogger<ProjectQueryTool>.Instance);
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

        public void RecordExecution(string commandText, DbTransaction? transaction)
        {
            Executions.Add((commandText, transaction));
            if (FailOnCommandText != null && FailOnCommandText(commandText))
            {
                throw new FakeDbException(
                    "25006: cannot execute UPDATE in a read-only transaction (read_only_sql_transaction)");
            }
        }

        public IEnumerable<string> ExecutedCommands => Executions.Select(x => x.CommandText);

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

        public override object? ExecuteScalar()
        {
            connection.RecordExecution(CommandText, DbTransaction);
            return null;
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
