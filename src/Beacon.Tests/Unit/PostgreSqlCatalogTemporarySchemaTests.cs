using System.Text.RegularExpressions;
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

namespace Beacon.Tests.Unit;

/// <summary>
/// The PostgreSQL catalog read leaves out the per-session temporary schemas (<c>pg_temp_N</c>, <c>pg_toast_temp_N</c>):
/// their tables belong to whichever session is connected while the catalog is read. Each of the three catalog statements
/// (columns, foreign keys, indexes) excludes them by the same pattern, read here the way PostgreSQL reads it. A recording
/// connection, no database (§4.7).
/// </summary>
[TestFixture]
[NonParallelizable] // DbConnectionFactory is a process-wide registry.
public class PostgreSqlCatalogTemporarySchemaTests
{
    [TearDown]
    public void RestoreConnectionFactories()
    {
        new BeaconBuilder(new ServiceCollection(), new ConfigurationBuilder().Build())
            .AddPostgreSqlConnector()
            .AddSqlServerConnector()
            .AddMySqlConnector();
    }

    [TestCase("pg_temp_3", true)]
    [TestCase("pg_toast_temp_12", true)]
    [TestCase("pg_temporal", false)]
    [TestCase("pg_toast", false)]
    [TestCase("public", false)]
    [TestCase("sales_pg_temp_1", false)]
    public async Task EveryCatalogStatement_LeavesOutTheTemporarySchemasAndNothingElse(string schema, bool excluded)
    {
        var connection = new RecordingDbConnection { RowCount = 0 };
        DbConnectionFactory.Register(DatabaseEngineType.PostgreSQL, _ => connection);

        var tables = await new PostgreSqlMetadataExtractor().ExtractMetadataAsync("Host=unused;Database=unused");

        tables.Should().BeEmpty();
        connection.ExecutedCommands.Should().HaveCount(3, "columns, foreign keys and indexes");
        foreach (var statement in connection.ExecutedCommands)
        {
            var exclusion = Regex.Match(statement, @"(?:nspname|schemaname) !~ '([^']*)'");
            exclusion.Success.Should().BeTrue(statement);
            Regex.IsMatch(schema, exclusion.Groups[1].Value).Should().Be(excluded);
        }
    }
}
