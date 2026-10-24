using Beacon.Connector.AzureSynapse;
using Beacon.Connector.MySql;
using Beacon.Connector.PostgreSql;
using Beacon.Connector.SqlServer;
using Beacon.Core;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MySql.Data.MySqlClient;
using Npgsql;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// A connection made through <see cref="DbConnectionFactory"/> waits a bounded time to open: a connection string that sets
/// no connect timeout, or 0 (wait forever), gets 15 seconds; one above 30 seconds gets 30; one within the bounds keeps its
/// own. Read back through each driver's own connection string builder. The MySQL read-only form keeps its statement
/// batches off. No database: the connections are never opened.
/// </summary>
[TestFixture]
[NonParallelizable] // DbConnectionFactory is a process-wide registry.
public class ConnectTimeoutTests
{
    [SetUp]
    public void RegisterConnectors()
    {
        new BeaconBuilder(new ServiceCollection(), new ConfigurationBuilder().Build())
            .AddPostgreSqlConnector()
            .AddSqlServerConnector()
            .AddAzureSynapseConnector()
            .AddMySqlConnector();
    }

    [TestCase(null, 15)]
    [TestCase(0, 15)]
    [TestCase(60, 30)]
    [TestCase(10, 10)]
    [TestCase(30, 30)]
    [TestCase(31, 30)]
    public void PostgreSql_ConnectTimeout_IsBounded(int? configured, int expected)
    {
        var connectionString = "Host=db.internal;Database=sales;Username=reader;Password=pw" + (configured == null ? "" : $";Timeout={configured}");

        using var connection = DbConnectionFactory.CreateConnection(DatabaseEngineType.PostgreSQL, connectionString);

        new NpgsqlConnectionStringBuilder(connection.ConnectionString).Timeout.Should().Be(expected);
    }

    [TestCase(DatabaseEngineType.MSSQL, null, 15)]
    [TestCase(DatabaseEngineType.MSSQL, 0, 15)]
    [TestCase(DatabaseEngineType.MSSQL, 60, 30)]
    [TestCase(DatabaseEngineType.MSSQL, 10, 10)]
    [TestCase(DatabaseEngineType.AzureSynapse, null, 15)]
    [TestCase(DatabaseEngineType.AzureSynapse, 0, 15)]
    [TestCase(DatabaseEngineType.AzureSynapse, 60, 30)]
    [TestCase(DatabaseEngineType.AzureSynapse, 10, 10)]
    [TestCase(DatabaseEngineType.MSSQL, 30, 30)]
    [TestCase(DatabaseEngineType.MSSQL, 31, 30)]
    [TestCase(DatabaseEngineType.AzureSynapse, 31, 30)]
    public void SqlClient_ConnectTimeout_IsBounded(DatabaseEngineType engine, int? configured, int expected)
    {
        var connectionString = "Server=db.internal;Database=sales;User Id=reader;Password=pw;Encrypt=Mandatory" + (configured == null ? "" : $";Connect Timeout={configured}");

        using var connection = DbConnectionFactory.CreateConnection(engine, connectionString);

        new SqlConnectionStringBuilder(connection.ConnectionString).ConnectTimeout.Should().Be(expected);
    }

    [TestCase(null, 15u)]
    [TestCase(0, 15u)]
    [TestCase(60, 30u)]
    [TestCase(10, 10u)]
    [TestCase(30, 30u)]
    [TestCase(31, 30u)]
    public void MySql_ConnectTimeout_IsBounded_AndTheReadOnlyFormKeepsBatchesOff(int? configured, uint expected)
    {
        var connectionString = "Server=db.internal;Database=sales;Uid=reader;Pwd=pw" + (configured == null ? "" : $";Connection Timeout={configured}");

        using var connection = DbConnectionFactory.CreateConnection(DatabaseEngineType.MySQL, connectionString);
        using var readOnly = DbConnectionFactory.CreateConnection(DatabaseEngineType.MySQL, DbConnectionFactory.WithoutStatementBatches(connectionString));

        new MySqlConnectionStringBuilder(connection.ConnectionString).ConnectionTimeout.Should().Be(expected);
        new MySqlConnectionStringBuilder(connection.ConnectionString).AllowBatch.Should().BeTrue();
        var readOnlySettings = new MySqlConnectionStringBuilder(readOnly.ConnectionString);
        readOnlySettings.ConnectionTimeout.Should().Be(expected);
        readOnlySettings.AllowBatch.Should().BeFalse();
    }

    // The bounds themselves, as every connector applies them; a negative value (no driver accepts one) is unset.
    [TestCase(-1, 15)]
    [TestCase(0, 15)]
    [TestCase(1, 1)]
    [TestCase(30, 30)]
    [TestCase(31, 30)]
    [TestCase(long.MaxValue, 30)]
    public void ConnectTimeoutSeconds_BoundsTheConfiguredValue(long configured, int expected)
    {
        DbConnectionFactory.ConnectTimeoutSeconds(configured).Should().Be(expected);
    }

    // Each driver's other spelling of its connect-timeout key is read by its own builder, so it is bounded too (Npgsql has
    // no synonym for Timeout: its key in another case).
    [TestCase(DatabaseEngineType.PostgreSQL, "Host=db.internal;Database=sales;Username=reader;Password=pw;timeout=60")]
    [TestCase(DatabaseEngineType.MSSQL, "Server=db.internal;Database=sales;User Id=reader;Password=pw;Encrypt=Mandatory;Connection Timeout=60")]
    [TestCase(DatabaseEngineType.AzureSynapse, "Server=db.internal;Database=sales;User Id=reader;Password=pw;Encrypt=Mandatory;Timeout=60")]
    [TestCase(DatabaseEngineType.MySQL, "Server=db.internal;Database=sales;Uid=reader;Pwd=pw;Connect Timeout=60")]
    public void AnAliasOfTheConnectTimeoutKey_IsBounded(DatabaseEngineType engine, string connectionString)
    {
        using var connection = DbConnectionFactory.CreateConnection(engine, connectionString);

        var timeout = engine switch
        {
            DatabaseEngineType.PostgreSQL => new NpgsqlConnectionStringBuilder(connection.ConnectionString).Timeout,
            DatabaseEngineType.MySQL => (int)new MySqlConnectionStringBuilder(connection.ConnectionString).ConnectionTimeout,
            _ => new SqlConnectionStringBuilder(connection.ConnectionString).ConnectTimeout
        };
        timeout.Should().Be(30);
    }

    // A connection string already within the bounds reaches the driver exactly as stored (MySql.Data then reports it in its
    // own normalised form, as it does for any string). The PostgreSQL one already sets how it signs in (see
    // PostgreSqlHostIdentityTests for the defaults a string without them gets).
    [TestCase(DatabaseEngineType.PostgreSQL, "Host=db.internal;Database=sales;Username=reader;Password=pw;Timeout=20;Require Auth=ScramSHA256;GSS Encryption Mode=Disable")]
    [TestCase(DatabaseEngineType.MSSQL, "Server=db.internal;Database=sales;User Id=reader;Password=pw;Encrypt=Mandatory;Connect Timeout=20")]
    [TestCase(DatabaseEngineType.MySQL, "Server=db.internal;Database=sales;Uid=reader;Pwd=pw;Connection Timeout=20")]
    public void AConnectionStringWithinTheBounds_IsUsedAsItIs(DatabaseEngineType engine, string connectionString)
    {
        using var connection = DbConnectionFactory.CreateConnection(engine, connectionString);

        var asStored = engine == DatabaseEngineType.MySQL ? new MySqlConnection(connectionString).ConnectionString : connectionString;
        connection.ConnectionString.Should().Be(asStored);
    }
}
