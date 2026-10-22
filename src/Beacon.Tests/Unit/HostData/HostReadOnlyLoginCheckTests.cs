using Beacon.Connector.AzureSynapse;
using Beacon.Connector.MySql;
using Beacon.Connector.PostgreSql;
using Beacon.Connector.SqlServer;
using Beacon.Core;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.HostData;
using Beacon.Core.Models;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.HostData;

/// <summary>
/// A host data source's read-only login is checked for write access after the startup sync: a login that can write is one
/// Warning naming the source by id; a check that cannot run, or that finds none of the exposed tables, is a Warning that
/// the login could not be verified; a disabled source is not checked; a transport refusal is logged as not checked; nothing
/// ever throws. Never the connection string, the login's name or the registration name.
/// </summary>
[TestFixture]
[NonParallelizable] // DbConnectionFactory is a process-wide registry.
public class HostReadOnlyLoginCheckTests
{
    private const string ConnectionString = "Server=host-db.internal;Database=Netgiro;User Id=beacon_ro;Password=Sup3rS3cret!";

    private static readonly HostExposedTable[] Tables =
    [
        new("dbo", "Customer", new HashSet<string>(), new HashSet<string>(), new HashSet<string>()),
        new("lending", "Loan", new HashSet<string>(), new HashSet<string>(), new HashSet<string>())
    ];

    [TearDown]
    public void RestoreConnectionFactories()
    {
        new BeaconBuilder(new ServiceCollection(), new ConfigurationBuilder().Build())
            .AddPostgreSqlConnector()
            .AddSqlServerConnector()
            .AddAzureSynapseConnector()
            .AddMySqlConnector();
    }

    [TestCase(true, false, 2L, "CanWrite", "superuser")]
    [TestCase(false, true, 2L, "CanWrite", "write privilege on an exposed table")]
    [TestCase(true, null, null, "CanWrite", "superuser")]
    [TestCase(null, true, 1L, "CanWrite", "write privilege on an exposed table")]
    [TestCase(true, false, 0L, "CanWrite", "superuser")]
    [TestCase(false, false, 2L, "ReadOnly", null)]
    [TestCase(null, false, 2L, "Unverified", "the database did not answer every check")]
    [TestCase(false, null, 2L, "Unverified", "the database did not answer every check")]
    [TestCase(false, false, null, "Unverified", "the database did not answer every check")]
    [TestCase(false, false, 0L, "Unverified", "none of the exposed tables was found")]
    public void ClassifyPostgreSql(bool? isSuperuser, bool? canWrite, long? tablesFound, string verdict, string? reason)
    {
        var access = HostLoginWriteAccess.ClassifyPostgreSql(isSuperuser, canWrite, tablesFound);

        access.Verdict.ToString().Should().Be(verdict);
        access.Reason.Should().Be(reason);
    }

    [Test]
    public void PostgreSqlStatement_CountsColumnLevelInsertAndUpdateAsWriteAccess()
    {
        HostReadOnlyLoginCheck.PostgreSqlSql.Should().Contain("has_table_privilege(e.oid, 'INSERT, UPDATE, DELETE')");
        HostReadOnlyLoginCheck.PostgreSqlSql.Should().Contain("OR pg_catalog.has_any_column_privilege(e.oid, 'INSERT, UPDATE')");
    }

    [Test]
    public void SqlServerStatement_NamesTheCurrentDatabaseForEveryDatabaseProbe()
    {
        foreach (var permission in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            HostReadOnlyLoginCheck.SqlServerSql.Should().Contain($"HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', '{permission}')");
        }

        HostReadOnlyLoginCheck.SqlServerSql.Should().NotContain("HAS_PERMS_BY_NAME(NULL");
    }

    [TestCase(1, 0, 0, 0, 0, 2, 0, "CanWrite", "db_owner")]
    [TestCase(0, 1, 0, 0, 0, 2, 0, "CanWrite", "db_datawriter")]
    [TestCase(0, 0, 1, 0, 0, 2, 0, "CanWrite", "a database-wide write permission")]
    [TestCase(0, 0, 0, 0, 1, 2, 0, "CanWrite", "a database-wide write permission")]
    [TestCase(null, 0, 0, 1, 0, null, null, "CanWrite", "a database-wide write permission")]
    [TestCase(0, 0, 0, 0, 0, 2, 1, "CanWrite", "write permission on an exposed table")]
    [TestCase(null, 0, 0, 0, 0, 2, 1, "CanWrite", "write permission on an exposed table")]
    [TestCase(1, 0, 0, 0, 0, 0, 0, "CanWrite", "db_owner")]
    [TestCase(0, 0, 0, 0, 0, 2, 0, "ReadOnly", null)]
    [TestCase(null, 0, 0, 0, 0, 2, 0, "Unverified", "the database did not answer every check")]
    [TestCase(0, 0, 0, null, 0, 2, 0, "Unverified", "the database did not answer every check")]
    [TestCase(0, 0, 0, 0, 0, 2, null, "Unverified", "the database did not answer every check")]
    [TestCase(0, 0, 0, 0, 0, null, 0, "Unverified", "the database did not answer every check")]
    [TestCase(0, 0, 0, 0, 0, 0, 0, "Unverified", "none of the exposed tables was found")]
    public void ClassifySqlServer(
        int? dbOwner,
        int? dataWriter,
        int? insert,
        int? update,
        int? delete,
        int? tablesFound,
        int? tableWrite,
        string verdict,
        string? reason)
    {
        var access = HostLoginWriteAccess.ClassifySqlServer(dbOwner, dataWriter, insert, update, delete, tablesFound, tableWrite);

        access.Verdict.ToString().Should().Be(verdict);
        access.Reason.Should().Be(reason);
    }

    [Test]
    public async Task ALoginThatCanWrite_IsOneWarning_WithTheSourceIdOnly()
    {
        var connection = SqlServerConnection(databaseWide: [0, 1, 1, 1, 1], tablesFound: 2, tableWrite: 0);
        DbConnectionFactory.Register(DatabaseEngineType.MSSQL, _ => connection);
        var logs = new LogRecorder();

        await Check(logs).WarnIfWritableAsync(Source(DatabaseEngineType.MSSQL), Tables, CancellationToken.None);

        connection.ExecutedCommands.Should().Equal(HostReadOnlyLoginCheck.SqlServerSql, HostReadOnlyLoginCheck.SqlServerTablesSql(2));
        var warning = logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning).Subject;
        warning.Message.Should().Contain("41").And.Contain("db_datawriter");
        warning.FullText.Should().NotContainAny("Netgiro", "host-db.internal", "beacon_ro", "Sup3rS3cret");
    }

    [Test]
    public async Task OnSqlServer_AWriteGrantOnAnExposedTable_IsAWarning_AndTheNamesArePassedAsParameters()
    {
        var connection = SqlServerConnection(databaseWide: [0, 0, 0, 0, 0], tablesFound: 2, tableWrite: 1);
        DbConnectionFactory.Register(DatabaseEngineType.MSSQL, _ => connection);
        var logs = new LogRecorder();

        await Check(logs).WarnIfWritableAsync(Source(DatabaseEngineType.MSSQL), Tables, CancellationToken.None);

        var tables = connection.Executions[1];
        tables.CommandText.Should().Contain("HAS_PERMS_BY_NAME(QUOTENAME(t.schema_name) + '.' + QUOTENAME(t.table_name), 'OBJECT', 'INSERT')")
            .And.NotContainAny("Customer", "Loan", "lending");
        tables.Parameters.Select(x => (x.ParameterName, x.Value)).Should().Equal(
            ("s0", (object?)"dbo"),
            ("n0", "Customer"),
            ("s1", "lending"),
            ("n1", "Loan"));
        logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning)
            .Which.Message.Should().Contain("write permission on an exposed table");
    }

    [Test]
    public async Task OnSqlServer_WhenNoExposedTableIsFound_TheLoginIsNotVerified()
    {
        var connection = SqlServerConnection(databaseWide: [0, 0, 0, 0, 0], tablesFound: 0, tableWrite: 0);
        DbConnectionFactory.Register(DatabaseEngineType.MSSQL, _ => connection);
        var logs = new LogRecorder();

        await Check(logs).WarnIfWritableAsync(Source(DatabaseEngineType.MSSQL), Tables, CancellationToken.None);

        logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning)
            .Which.Message.Should().Contain("could not verify").And.Contain("none of the exposed tables was found");
    }

    [Test]
    public async Task OnSqlServer_ManyExposedTables_AreAskedInChunks()
    {
        var connection = SqlServerConnection(databaseWide: [0, 0, 0, 0, 0], tablesFound: 1, tableWrite: 0);
        DbConnectionFactory.Register(DatabaseEngineType.MSSQL, _ => connection);
        var tables = Enumerable.Range(0, HostReadOnlyLoginCheck.SqlServerTablesPerStatement + 1)
            .Select(x => new HostExposedTable("dbo", $"T{x}", new HashSet<string>(), new HashSet<string>(), new HashSet<string>()))
            .ToList();
        var logs = new LogRecorder();

        await Check(logs).WarnIfWritableAsync(Source(DatabaseEngineType.MSSQL), tables, CancellationToken.None);

        connection.Executions.Should().HaveCount(3);
        connection.Executions[1].Parameters.Should().HaveCount(2 * HostReadOnlyLoginCheck.SqlServerTablesPerStatement);
        connection.Executions[2].Parameters.Should().HaveCount(2);
        logs.Entries.Should().NotContain(x => x.Level >= LogLevel.Warning);
    }

    [Test]
    public async Task OnAzureSynapse_TheSqlServerChecksRun_OnTheSynapseConnection()
    {
        var connection = SqlServerConnection(databaseWide: [1, 0, 0, 0, 0], tablesFound: 2, tableWrite: 0);
        DbConnectionFactory.Register(DatabaseEngineType.AzureSynapse, _ => connection);
        var logs = new LogRecorder();

        await Check(logs).WarnIfWritableAsync(Source(DatabaseEngineType.AzureSynapse), Tables, CancellationToken.None);

        connection.ExecutedCommands.Should().StartWith(HostReadOnlyLoginCheck.SqlServerSql);
        logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning)
            .Which.Message.Should().Contain("db_owner");
    }

    [Test]
    public async Task OnPostgreSql_TheExposedTablesArePassedAsParameters()
    {
        var connection = PostgreSqlConnection(isSuperuser: false, canWrite: false, tablesFound: 2L);
        DbConnectionFactory.Register(DatabaseEngineType.PostgreSQL, _ => connection);
        var logs = new LogRecorder();

        await Check(logs).WarnIfWritableAsync(Source(DatabaseEngineType.PostgreSQL), Tables, CancellationToken.None);

        var execution = connection.Executions.Should().ContainSingle().Subject;
        execution.CommandText.Should().Be(HostReadOnlyLoginCheck.PostgreSqlSql);
        execution.Parameters.Select(x => x.Value).Should().BeEquivalentTo(new object[] { new[] { "dbo", "lending" }, new[] { "Customer", "Loan" } });
        logs.Entries.Should().NotContain(x => x.Level >= LogLevel.Warning, "a login without write access is not warned about");
    }

    [Test]
    public async Task OnPostgreSql_ALoginThatCanWriteAnExposedTable_IsAWarning()
    {
        DbConnectionFactory.Register(DatabaseEngineType.PostgreSQL, _ => PostgreSqlConnection(isSuperuser: false, canWrite: true, tablesFound: 2L));
        var logs = new LogRecorder();

        await Check(logs).WarnIfWritableAsync(Source(DatabaseEngineType.PostgreSQL), Tables, CancellationToken.None);

        var warning = logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning).Subject;
        warning.Message.Should().Contain("41").And.Contain("can write").And.Contain("write privilege on an exposed table");
        warning.FullText.Should().NotContainAny("Netgiro", "host-db.internal", "beacon_ro", "Sup3rS3cret");
    }

    [Test]
    public async Task OnPostgreSql_WhenNoExposedTableIsFound_TheLoginIsNotVerified()
    {
        DbConnectionFactory.Register(DatabaseEngineType.PostgreSQL, _ => PostgreSqlConnection(isSuperuser: false, canWrite: false, tablesFound: 0L));
        var logs = new LogRecorder();

        await Check(logs).WarnIfWritableAsync(Source(DatabaseEngineType.PostgreSQL), Tables, CancellationToken.None);

        logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning)
            .Which.Message.Should().Contain("could not verify").And.Contain("none of the exposed tables was found");
    }

    [Test]
    public async Task OnPostgreSql_WithNoExposedTables_TheLoginIsNotVerified()
    {
        DbConnectionFactory.Register(DatabaseEngineType.PostgreSQL, _ => PostgreSqlConnection(isSuperuser: false, canWrite: false, tablesFound: 0L));
        var logs = new LogRecorder();

        await Check(logs).WarnIfWritableAsync(Source(DatabaseEngineType.PostgreSQL), [], CancellationToken.None);

        logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning)
            .Which.Message.Should().Contain("could not verify");
    }

    [Test]
    public async Task OnPostgreSql_ANullAnswer_OrNoRow_IsNotVerified()
    {
        DbConnectionFactory.Register(DatabaseEngineType.PostgreSQL, _ => PostgreSqlConnection(isSuperuser: null, canWrite: false, tablesFound: 2L));
        var nullAnswer = new LogRecorder();
        await Check(nullAnswer).WarnIfWritableAsync(Source(DatabaseEngineType.PostgreSQL), Tables, CancellationToken.None);

        DbConnectionFactory.Register(DatabaseEngineType.PostgreSQL, _ => new RecordingDbConnection { RowCount = 0 });
        var noRow = new LogRecorder();
        await Check(noRow).WarnIfWritableAsync(Source(DatabaseEngineType.PostgreSQL), Tables, CancellationToken.None);

        nullAnswer.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning)
            .Which.Message.Should().Contain("could not verify").And.Contain("did not answer every check");
        noRow.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning)
            .Which.Message.Should().Contain("could not verify").And.Contain("did not answer every check");
    }

    [Test]
    public async Task WhenTheCheckCannotRun_ItWarnsThatTheLoginWasNotVerified_AndNeverThrows()
    {
        var connection = new RecordingDbConnection { FailOnCommandText = _ => true };
        DbConnectionFactory.Register(DatabaseEngineType.PostgreSQL, _ => connection);
        var logs = new LogRecorder();

        var act = () => Check(logs).WarnIfWritableAsync(Source(DatabaseEngineType.PostgreSQL), Tables, CancellationToken.None);

        await act.Should().NotThrowAsync();
        logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning)
            .Which.Message.Should().Contain("could not verify");
    }

    [Test]
    public async Task WhenTheConnectionCannotBeResolved_ItWarnsThatTheLoginWasNotVerified()
    {
        var resolver = new Mock<IDataSourceConnectionResolver>();
        resolver
            .Setup(x => x.GetConnectionString(It.IsAny<DataSource>()))
            .Throws(new InvalidOperationException("ConnectionStrings:BeaconReadOnly is missing"));
        var logs = new LogRecorder();

        var act = () => new HostReadOnlyLoginCheck(resolver.Object, logs.For<HostReadOnlyLoginCheck>())
            .WarnIfWritableAsync(Source(DatabaseEngineType.MSSQL), Tables, CancellationToken.None);

        await act.Should().NotThrowAsync();
        logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning)
            .Which.Message.Should().Contain("could not verify");
    }

    [Test]
    public async Task WhenTheTransportPolicyRefusesTheConnection_ItIsLoggedAsNotChecked()
    {
        var resolver = new Mock<IDataSourceConnectionResolver>();
        resolver
            .Setup(x => x.GetConnectionString(It.IsAny<DataSource>()))
            .Throws(new DataSourceTransportException("Set Encrypt=Strict."));
        var logs = new LogRecorder();

        await new HostReadOnlyLoginCheck(resolver.Object, logs.For<HostReadOnlyLoginCheck>())
            .WarnIfWritableAsync(Source(DatabaseEngineType.MSSQL), Tables, CancellationToken.None);

        var warning = logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning).Subject;
        warning.Message.Should().Contain("41").And.Contain("refused by the transport policy").And.Contain(nameof(DataSourceTransportException))
            .And.NotContain("could not verify").And.NotContain("Encrypt=Strict");
    }

    [Test]
    public async Task ADisabledSource_IsNotChecked_AndConnectsToNothing()
    {
        var resolver = new Mock<IDataSourceConnectionResolver>(MockBehavior.Strict);
        var logs = new LogRecorder();
        var source = Source(DatabaseEngineType.PostgreSQL);
        source.DisabledTime = DateTime.UtcNow;

        await new HostReadOnlyLoginCheck(resolver.Object, logs.For<HostReadOnlyLoginCheck>())
            .WarnIfWritableAsync(source, Tables, CancellationToken.None);

        logs.Entries.Should().BeEmpty();
    }

    [Test]
    public async Task AnEngineWithoutACheck_ConnectsToNothing()
    {
        var resolver = new Mock<IDataSourceConnectionResolver>(MockBehavior.Strict);
        var logs = new LogRecorder();

        await new HostReadOnlyLoginCheck(resolver.Object, logs.For<HostReadOnlyLoginCheck>())
            .WarnIfWritableAsync(Source(DatabaseEngineType.MySQL), Tables, CancellationToken.None);

        logs.Entries.Should().BeEmpty();
    }

    private static RecordingDbConnection SqlServerConnection(int[] databaseWide, int tablesFound, int tableWrite)
    {
        return new RecordingDbConnection
        {
            RowCount = 1,
            ColumnsFor = x => x == HostReadOnlyLoginCheck.SqlServerSql
                ? databaseWide.Select((y, i) => ($"c{i}", (Func<int, object?>)(_ => y))).ToList()
                : [("tables_found", _ => tablesFound), ("can_write", _ => tableWrite)]
        };
    }

    private static RecordingDbConnection PostgreSqlConnection(bool? isSuperuser, bool? canWrite, long? tablesFound)
    {
        return new RecordingDbConnection
        {
            RowCount = 1,
            Columns = [("is_superuser", _ => isSuperuser), ("can_write", _ => canWrite), ("tables_found", _ => tablesFound)]
        };
    }

    private static HostReadOnlyLoginCheck Check(LogRecorder logs)
    {
        var resolver = new Mock<IDataSourceConnectionResolver>();
        resolver
            .Setup(x => x.GetConnectionString(It.IsAny<DataSource>()))
            .Returns(ConnectionString);

        return new HostReadOnlyLoginCheck(resolver.Object, logs.For<HostReadOnlyLoginCheck>());
    }

    private static DataSource Source(DatabaseEngineType engine)
    {
        return new DataSource
        {
            Id = 41,
            Name = "Netgiro",
            DataSourceType = DataSourceType.Database,
            DatabaseEngineType = engine,
            EncryptedConnectionData = "reference",
            HostManagedKey = "efcore:Netgiro",
            IsReadOnly = true
        };
    }
}
