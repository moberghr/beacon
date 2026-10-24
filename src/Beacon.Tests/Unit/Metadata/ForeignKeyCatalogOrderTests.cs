using System.Reflection;
using Beacon.Connector.AzureSynapse;
using Beacon.Connector.MySql;
using Beacon.Connector.PostgreSql;
using Beacon.Connector.SqlServer;
using Beacon.Core;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Metadata;

/// <summary>
/// The foreign-key catalog statement of every engine that reads foreign keys returns its rows in constraint-name order, so a
/// column in several foreign keys reads them in the same order on every refresh. PostgreSQL and MySQL are read through a
/// recording connection (no database, §4.7), Synapse too; SQL Server's statement is read from its extractor.
/// </summary>
[TestFixture]
[NonParallelizable] // DbConnectionFactory is a process-wide registry.
public class ForeignKeyCatalogOrderTests
{
    [TearDown]
    public void RestoreConnectionFactories()
    {
        new BeaconBuilder(new ServiceCollection(), new ConfigurationBuilder().Build())
            .AddPostgreSqlConnector()
            .AddSqlServerConnector()
            .AddMySqlConnector()
            .AddAzureSynapseConnector();
    }

    [Test]
    public async Task PostgreSqlForeignKeyStatementOrdersByConstraintName()
    {
        var connection = new RecordingDbConnection { RowCount = 0 };
        DbConnectionFactory.Register(DatabaseEngineType.PostgreSQL, _ => connection);

        await new PostgreSqlMetadataExtractor().ExtractMetadataAsync("Host=unused;Database=unused");

        ForeignKeyStatement(connection.ExecutedCommands).Should().EndWith("ORDER BY constraint_name");
    }

    [Test]
    public async Task MySqlForeignKeyStatementOrdersByConstraintName()
    {
        var connection = new RecordingDbConnection { RowCount = 0 };
        DbConnectionFactory.Register(DatabaseEngineType.MySQL, _ => connection);

        await new MySqlMetadataExtractor().ExtractMetadataAsync("Server=unused;Database=unused");

        ForeignKeyStatement(connection.ExecutedCommands).Should().EndWith("ORDER BY constraint_name");
    }

    [Test]
    public async Task SynapseForeignKeyStatementOrdersByConstraintName()
    {
        var connection = new RecordingDbConnection { RowCount = 0 };
        DbConnectionFactory.Register(DatabaseEngineType.AzureSynapse, _ => connection);

        await new AzureSynapseMetadataExtractor().ExtractMetadataAsync("Server=unused;Database=unused");

        ForeignKeyStatement(connection.ExecutedCommands).Should().EndWith("ORDER BY constraint_name");
    }

    [Test]
    public void SqlServerForeignKeyStatementOrdersByConstraintName()
    {
        // SQL Server opens its own SqlConnection, so its fixed statement is read from the extractor's constant.
        var statement = (string)typeof(SqlServerMetadataExtractor)
            .GetField("ForeignKeysQuery", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetRawConstantValue()!;

        statement.Trim().Should().EndWith("ORDER BY constraint_name");
    }

    private static string ForeignKeyStatement(IEnumerable<string> statements)
    {
        return statements
            .Where(x => x.Contains("foreign_table_name", StringComparison.Ordinal))
            .Single()
            .Trim();
    }
}
