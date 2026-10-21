using System.Net;
using Beacon.Connector.BigQuery;
using Beacon.Connector.Databricks;
using Beacon.Connector.MySql;
using Beacon.Connector.PostgreSql;
using Beacon.Connector.SqlServer;
using Beacon.Core;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;
using FluentAssertions;
using Google.Apis.Bigquery.v2.Data;
using Google.Cloud.BigQuery.V2;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// The read-only path of every connector binds: a provider that reports database-level read-only enforcement for an engine
/// runs the statement inside it, and the warehouse connectors without a read-only session refuse on their own verdict —
/// the dialect-correct parser and, on BigQuery, a dry run that must report a SELECT — before anything runs. No service or
/// database is reached: recording connections, a mocked BigQuery client and a recording HTTP handler.
/// </summary>
[TestFixture]
[NonParallelizable] // DbConnectionFactory is a process-wide registry.
public class ConnectorReadOnlyBindingTests
{
    [TearDown]
    public void RestoreConnectionFactories()
    {
        new BeaconBuilder(new ServiceCollection(), new ConfigurationBuilder().Build())
            .AddPostgreSqlConnector()
            .AddSqlServerConnector()
            .AddMySqlConnector();
    }

    [TestCaseSource(nameof(Engines))]
    public async Task DatabaseProvider_AnEngineReportedReadOnly_RunsTheStatementOnlyInsideItsReadOnlyMode(DatabaseEngineType engine)
    {
        var provider = TestQueryServices.DatabaseProvider();
        var connection = new RecordingDbConnection { RowCount = 1 };
        DbConnectionFactory.Register(engine, _ => connection);
        var dataSource = new DataSource { Id = 1, Name = "s", DataSourceType = DataSourceType.Database, DatabaseEngineType = engine, EncryptedConnectionData = "e" };

        var result = await provider.ExecuteReadOnlyQueryAsync(dataSource, "SELECT id FROM t", []);

        result.Success.Should().BeTrue(result.ErrorMessage);
        var commands = connection.ExecutedCommands.ToList();
        var query = commands.IndexOf("SELECT id FROM t");
        var readOnlyStatements = commands
            .Take(query)
            .Where(x => x.Contains("READ ONLY", StringComparison.OrdinalIgnoreCase) || x.Contains("read_only = on", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (provider.SupportsDatabaseReadOnlyEnforcement(engine))
        {
            readOnlyStatements.Should().NotBeEmpty("an engine reported as read-only-enforced must run the statement in that mode");
            return;
        }

        readOnlyStatements.Should().BeEmpty("an engine without a read-only mode is reported as such, never claimed");
    }

    [Test]
    public void WarehouseConnectors_DoNotClaimDatabaseReadOnlyEnforcement()
    {
        ((IDataSourceProvider)BigQuery(Mock.Of<BigQueryClient>())).SupportsDatabaseReadOnlyEnforcement(null).Should().BeFalse();
        ((IDataSourceProvider)Databricks(new RecordingHandler())).SupportsDatabaseReadOnlyEnforcement(null).Should().BeFalse();
    }

    [TestCase("DELETE FROM sales.orders WHERE 1 = 1")]
    [TestCase("SELECT 'x\\', 1 AS y")]
    [TestCase("SELECT id FROM `acme-dev.sales.orders`")]
    public async Task BigQuery_AStatementItsOwnParserRefuses_NeverReachesBigQuery(string sql)
    {
        var client = new Mock<BigQueryClient>();

        var result = await BigQuery(client.Object).ExecuteReadOnlyQueryAsync(WarehouseSource(DataSourceType.BigQuery), sql, []);

        result.Success.Should().BeFalse();
        client.Invocations.Should().BeEmpty();
    }

    [TestCase("SCRIPT")]
    [TestCase("DELETE")]
    [TestCase("EXPORT_DATA")]
    [TestCase(null)]
    public async Task BigQuery_ADryRunThatIsNotASelect_IsRefusedBeforeTheStatementRuns(string? statementType)
    {
        var client = DryRunClient(statementType);

        var result = await BigQuery(client.Object).ExecuteReadOnlyQueryAsync(WarehouseSource(DataSourceType.BigQuery), "SELECT id FROM orders", []);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("only a SELECT runs");
        Jobs(client).Should().Equal([true], "only the dry run was asked for");
    }

    [Test]
    public async Task BigQuery_ADryRunReportingASelect_IsFollowedByTheRun()
    {
        var client = DryRunClient("SELECT");

        await BigQuery(client.Object).ExecuteReadOnlyQueryAsync(WarehouseSource(DataSourceType.BigQuery), "SELECT id FROM orders", []);

        Jobs(client).Should().Equal([true, false], "the dry run, then the statement itself");
    }

    // A statement with bound values is dry-run with the same named parameters it then runs with: a dry run without them
    // would fail on the unbound @p0, or judge a different statement.
    [Test]
    public async Task BigQuery_TheDryRun_CarriesTheStatementsNamedParameters()
    {
        var client = DryRunClient("SELECT");

        await BigQuery(client.Object).ExecuteReadOnlyQueryAsync(
            WarehouseSource(DataSourceType.BigQuery),
            "SELECT id FROM orders WHERE id = @p0 AND region = @p1",
            new Dictionary<string, object?> { ["p0"] = 7L, ["p1"] = "eu" });

        var jobs = client.Invocations
            .Where(x => x.Method.Name == nameof(BigQueryClient.CreateQueryJobAsync))
            .Select(x => (Parameters: ((IEnumerable<BigQueryParameter>?)x.Arguments[1])?.ToList(), Options: (QueryOptions?)x.Arguments[2]))
            .ToList();
        jobs.Should().HaveCount(2);
        jobs.Select(x => x.Options!.DryRun == true).Should().Equal(true, false);
        jobs.Should().OnlyContain(x => x.Options!.ParameterMode == BigQueryParameterMode.Named);
        jobs.Should().OnlyContain(x => x.Parameters!.Select(y => $"{y.Name}={y.Value}").SequenceEqual(new[] { "p0=7", "p1=eu" }));
    }

    [TestCase("DELETE FROM sales.orders")]
    [TestCase("SELECT id FROM hive_metastore.sales.orders")]
    [TestCase("SELECT * FROM read_kafka(bootstrapServers => 'b:9092', subscribe => 't')")]
    public async Task Databricks_AStatementItsOwnVerdictRefuses_IsNeverSent(string sql)
    {
        var handler = new RecordingHandler();

        var result = await Databricks(handler).ExecuteReadOnlyQueryAsync(WarehouseSource(DataSourceType.Databricks), sql, []);

        result.Success.Should().BeFalse();
        handler.Requests.Should().Be(0);
    }

    [Test]
    public async Task Databricks_ASelect_IsSent()
    {
        var handler = new RecordingHandler();

        await Databricks(handler).ExecuteReadOnlyQueryAsync(WarehouseSource(DataSourceType.Databricks), "SELECT id FROM main.sales.orders", []);

        handler.Requests.Should().Be(1);
    }

    private static IEnumerable<DatabaseEngineType> Engines() =>
        [DatabaseEngineType.PostgreSQL, DatabaseEngineType.MySQL, DatabaseEngineType.MSSQL];

    private static DataSource WarehouseSource(DataSourceType type) =>
        new() { Id = 2, Name = "warehouse", DataSourceType = type, EncryptedConnectionData = "enc" };

    private static Mock<BigQueryClient> DryRunClient(string? statementType)
    {
        var client = new Mock<BigQueryClient>();
        client
            .Setup(x => x.CreateQueryJobAsync(It.IsAny<string>(), It.IsAny<IEnumerable<BigQueryParameter>>(), It.IsAny<QueryOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new BigQueryJob(
                client.Object,
                new Job
                {
                    JobReference = new JobReference { ProjectId = "acme-prod", JobId = "job" },
                    Statistics = new JobStatistics { Query = new JobStatistics2 { StatementType = statementType } }
                }));

        return client;
    }

    // DryRun of every job the client was asked to create, in order.
    private static List<bool> Jobs(Mock<BigQueryClient> client) =>
        client.Invocations
            .Where(x => x.Method.Name == nameof(BigQueryClient.CreateQueryJobAsync))
            .Select(x => ((QueryOptions?)x.Arguments[2])?.DryRun == true)
            .ToList();

    private static BigQueryProvider BigQuery(BigQueryClient client)
    {
        var encryption = new Mock<IEncryptionService>();
        encryption
            .Setup(x => x.Decrypt(It.IsAny<string>()))
            .Returns("""{"ProjectId":"acme-prod","DatasetId":"sales"}""");

        return new FixedClientBigQueryProvider(client, encryption.Object);
    }

    private static DatabricksProvider Databricks(RecordingHandler handler)
    {
        var encryption = new Mock<IEncryptionService>();
        encryption
            .Setup(x => x.Decrypt(It.IsAny<string>()))
            .Returns("""{"Host":"h","HttpPath":"/sql/1.0/warehouses/w","Token":"t","Catalog":"main","QueryTimeoutSeconds":5}""");
        var clients = new Mock<IHttpClientFactory>();
        clients
            .Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler, disposeHandler: false));

        return new DatabricksProvider(clients.Object, TestTransport.Resolver(encryption.Object), new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance), NullLogger<DatabricksProvider>.Instance);
    }

    private sealed class FixedClientBigQueryProvider(BigQueryClient client, IEncryptionService encryption)
        : BigQueryProvider(TestTransport.Resolver(encryption), new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance), NullLogger<BigQueryProvider>.Instance, Microsoft.Extensions.Options.Options.Create(new BigQueryOptions()))
    {
        protected override BigQueryClient CreateClient(BigQueryConfiguration config) => client;
    }

    /// <summary>Counts requests and answers each with a server error, so nothing is ever read from it.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }
}
