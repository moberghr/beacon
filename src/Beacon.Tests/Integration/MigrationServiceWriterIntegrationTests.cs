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
using Beacon.Core.Models;
using Beacon.Core.Models.DataMigration;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using NUnit.Framework;

namespace Beacon.Tests.Integration;

/// <summary>
/// A whole migration run, from <see cref="MigrationService"/> through the engine's real writer into a real server: the
/// existence check, the primary key lookup and the writer agree on the table — a mixed-case, schema-qualified PostgreSQL
/// table written in more than one chunk, and an unqualified SQL Server table, which is the dbo one. The source rows come
/// from a stubbed preview. Each engine's tests need its connection string
/// (<see cref="PostgreSqlHostDataSourceMigrationTests.AdminConnectionStringEnvVar"/>,
/// <see cref="SqlServerMigrationWriterIntegrationTests.ConnectionStringEnvVar"/>); without it they are inconclusive.
/// </summary>
[TestFixture]
[NonParallelizable] // DbConnectionFactory is a process-wide registry.
public class MigrationServiceWriterIntegrationTests
{
    private const int JobId = 1;
    private const int ReadSource = 10;
    private const int WriteTarget = 20;

    private ProjectAccessWorld _world = null!;
    private Mock<IQueryExecutionPreviewService> _preview = null!;

    [SetUp]
    public void SetUp()
    {
        new BeaconBuilder(new ServiceCollection(), new ConfigurationBuilder().Build())
            .AddPostgreSqlConnector()
            .AddSqlServerConnector()
            .AddMySqlConnector();

        _world = new ProjectAccessWorld();
        _world.Source(ReadSource);
        _world.Source(WriteTarget).AllowMigrationWrites = true;
        _world.Serve(new List<MigrationExecutionHistory>());
        _preview = new Mock<IQueryExecutionPreviewService>();
    }

    [Test]
    public async Task APostgreSqlUpsert_IntoAMixedCaseSchemaQualifiedTable_InSeveralChunks()
    {
        var connectionString = Required(PostgreSqlHostDataSourceMigrationTests.AdminConnectionStringEnvVar);
        var schema = $"Beacon_MW_{Guid.NewGuid():N}";
        await PostgreSqlAsync(
            connectionString,
            $"CREATE SCHEMA \"{schema}\"",
            $"CREATE TABLE \"{schema}\".\"Orders\" (\"Id\" int PRIMARY KEY, name text)",
            $"INSERT INTO \"{schema}\".\"Orders\" VALUES (1, 'old')");
        try
        {
            var rowCount = Constants.Migration.BulkInsertBatchSize + 1;
            SourceRows(Enumerable.Range(1, rowCount)
                .Select(x => new Dictionary<string, object?> { ["Id"] = x, ["name"] = $"n{x}" })
                .ToArray());
            Job(MigrationMode.Upsert, $"{schema}.Orders");

            var result = await Service(connectionString, new PostgreSqlMigrationBulkWriter(NullLogger<PostgreSqlMigrationBulkWriter>.Instance))
                .ExecuteMigrationJob(Run(), CancellationToken.None);

            result.Status.Should().Be(MigrationStatus.Completed, result.ErrorMessage);
            result.DestinationRowsWritten.Should().Be(rowCount);
            (await PostgreSqlScalarAsync(connectionString, $"SELECT count(*) FROM \"{schema}\".\"Orders\"")).Should().Be((long)rowCount);
            (await PostgreSqlScalarAsync(connectionString, $"SELECT name FROM \"{schema}\".\"Orders\" WHERE \"Id\" = 1")).Should().Be("n1");
        }
        finally
        {
            await PostgreSqlAsync(connectionString, $"DROP SCHEMA \"{schema}\" CASCADE");
        }
    }

    [Test]
    public async Task ASqlServerUpsert_IntoAnUnqualifiedTable_WritesTheDboTable()
    {
        var connectionString = Required(SqlServerMigrationWriterIntegrationTests.ConnectionStringEnvVar);
        var table = $"beacon_mw_{Guid.NewGuid():N}";
        await SqlServerAsync(
            connectionString,
            $"CREATE TABLE [dbo].[{table}] (Id int PRIMARY KEY, name nvarchar(20) NULL)",
            $"INSERT INTO [dbo].[{table}] VALUES (1, 'old')");
        _world.DataSources.First(x => x.Id == WriteTarget).DatabaseEngineType = DatabaseEngineType.MSSQL;
        try
        {
            SourceRows(
                new Dictionary<string, object?> { ["Id"] = 1, ["name"] = "updated" },
                new Dictionary<string, object?> { ["Id"] = 2, ["name"] = "added" });
            Job(MigrationMode.Upsert, table);

            var result = await Service(connectionString, new SqlServerMigrationBulkWriter(NullLogger<SqlServerMigrationBulkWriter>.Instance))
                .ExecuteMigrationJob(Run(), CancellationToken.None);

            result.Status.Should().Be(MigrationStatus.Completed, result.ErrorMessage);
            result.DestinationRowsWritten.Should().Be(2);
            (await SqlServerScalarAsync(connectionString, $"SELECT STRING_AGG(CONCAT(Id, '|', name), ',') WITHIN GROUP (ORDER BY Id) FROM [dbo].[{table}]"))
                .Should().Be("1|updated,2|added");
        }
        finally
        {
            await SqlServerAsync(connectionString, $"DROP TABLE IF EXISTS [dbo].[{table}]");
        }
    }

    private static string Required(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value))
        {
            Assert.Inconclusive($"{variable} is not set.");
        }

        return value!;
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

    private MigrationService Service(string connectionString, IMigrationBulkWriter writer)
    {
        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(x => x.Decrypt(It.IsAny<string>())).Returns(connectionString);

        return new MigrationService(
            _world.Factory(),
            TestTransport.Resolver(encryption.Object),
            Mock.Of<IQueryService>(),
            _preview.Object,
            Mock.Of<IBeaconUserContext>(),
            new MigrationBulkContextFactory([]),
            MigrationTestWriters.Of(writer),
            NullLogger<MigrationService>.Instance,
            Options.Create(new MigrationOptions { Enabled = true }),
            TestChangeApprovals.Submitter(), TestProjectAccess.Actors(), new RecordingSecurityAudit(), TimeProvider.System);
    }

    private void Job(MigrationMode mode, string table)
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

    private static async Task PostgreSqlAsync(string connectionString, params string[] statements)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var statement in statements)
        {
            await using var command = new NpgsqlCommand(statement, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<object?> PostgreSqlScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);

        return await command.ExecuteScalarAsync();
    }

    private static async Task SqlServerAsync(string connectionString, params string[] statements)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var statement in statements)
        {
            await using var command = new SqlCommand(statement, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<object?> SqlServerScalarAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);

        return await command.ExecuteScalarAsync();
    }
}
