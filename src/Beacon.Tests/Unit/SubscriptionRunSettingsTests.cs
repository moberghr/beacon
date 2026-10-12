using System.Diagnostics;
using Beacon.Connector.MySql;
using Beacon.Connector.PostgreSql;
using Beacon.Core;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.HostData;
using Beacon.Core.Models;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Tests.Common;
using Beacon.Tests.Unit.HostData;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// What a subscription run takes from its own settings and from the data sources it reads: its timeout ends a statement —
/// the final query on the in-memory join included — before Beacon's own does; its rows are PII-masked as the strictest
/// data source asks (each source's settings are its linking projects' strictest), with that source's custom patterns, or
/// not at all when none asks; the delivered result — the final query's rows, or the last step's when there is none — is
/// masked by its column names and by the lineage of the steps' PII columns, while the rows a final query joins keep their
/// values. Two engines over recording ADO.NET doubles, no database (§4.7).
/// </summary>
[TestFixture]
[NonParallelizable] // DbConnectionFactory is a process-wide registry.
public class SubscriptionRunSettingsTests
{
    private const int QueryId = 7;
    private const int SubscriptionId = 11;
    private const int Ledger = 3;
    private const int Crm = 4;
    private const string HostKey = "efcore:Netgiro";

    [TearDown]
    public void RestoreConnectionFactories()
    {
        new BeaconBuilder(new ServiceCollection(), new ConfigurationBuilder().Build())
            .AddPostgreSqlConnector()
            .AddMySqlConnector();
    }

    [Test]
    public async Task ASubscriptionsOwnTimeout_EndsAStatementBeforeBeaconsOwnTimeout()
    {
        var provider = new Mock<IDataSourceProvider>();
        provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .Returns(async (DataSource _, string _, Dictionary<string, object?> _, CancellationToken token) =>
            {
                await Task.Delay(Timeout.Infinite, token);

                return new ProviderQueryResult { Success = true };
            });
        var service = Service(
            [(Ledger, "SELECT id FROM accounts")],
            timeoutSeconds: 1,
            providers: TestQueryServices.Providers(provider.Object),
            statementTimeout: TimeSpan.FromSeconds(30));
        var elapsed = Stopwatch.StartNew();

        var act = () => service.ExecuteQuery(SubscriptionId, CancellationToken.None);

        (await act.Should().ThrowAsync<BeaconException>()).Which.Message.Should().Contain("timed out");
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20), "the subscription's one-second timeout ended the statement, not Beacon's thirty");
    }

    [Test]
    public async Task ASubscriptionsOwnTimeout_EndsTheFinalQueryBeforeTheDeploymentsTimeout()
    {
        ConnectLedger([("id", x => x + 1)]);
        var service = Service(
            [(Ledger, "SELECT id FROM accounts")],
            "WITH RECURSIVE r(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM r WHERE i < 1000000000) SELECT COUNT(*) AS n FROM r",
            timeoutSeconds: 1,
            finalQueryTimeoutSeconds: 30);
        var elapsed = Stopwatch.StartNew();

        var act = () => service.ExecuteQuery(SubscriptionId, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconException>().WithMessage(QueryService.FinalQueryTimedOutMessage);
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "the subscription's one-second timeout ended the final query, not the deployment's thirty");
    }

    // One source asks for no PII detection, the other does: the run masks as the stricter one, whichever comes first.
    [TestCase(false, true)]
    [TestCase(true, false)]
    public async Task TheRowsAreMasked_AsTheStrictestDataSourceTheRunReadsAsks(bool ledgerDetects, bool crmDetects)
    {
        ConnectLedger([("id", x => x + 1), ("email", x => $"customer{x + 1}@bank.example")]);
        ConnectCrm([("id", x => x + 1), ("city", x => $"city{x + 1}")]);
        var service = Service(
            [(Ledger, "SELECT id, email FROM customers"), (Crm, "SELECT id, city FROM addresses")],
            "SELECT a.email, b.city FROM @result1 a JOIN @result2 b ON a.id = b.id",
            new Dictionary<int, McpSettingsData>
            {
                [Ledger] = new() { EnablePiiDetection = ledgerDetects },
                [Crm] = new() { EnablePiiDetection = crmDetects }
            });

        var result = await service.ExecuteQuery(SubscriptionId, CancellationToken.None);

        result.AllRecords.Should().HaveCount(3);
        result.AllRecords.Should().OnlyContain(x => x["email"]!.ToString()!.Contains("***"));
        result.AllRecords.Select(x => x["city"]).Should().Equal("city1", "city2", "city3");
        result.QueryResults.Should().NotContain("@bank.example");
    }

    [TestCase(null)]
    [TestCase("SELECT email AS contact FROM @result1")]
    public async Task WithPiiDetectionOff_TheRowsAreDeliveredAsRead(string? finalQuery)
    {
        ConnectLedger([("id", x => x + 1), ("email", x => $"customer{x + 1}@bank.example")]);
        var service = Service(
            [(Ledger, "SELECT id, email FROM customers")],
            finalQuery,
            new Dictionary<int, McpSettingsData> { [Ledger] = new() { EnablePiiDetection = false } });

        var result = await service.ExecuteQuery(SubscriptionId, CancellationToken.None);

        var column = finalQuery == null ? "email" : "contact";
        result.AllRecords.Select(x => x[column]).Should().Equal("customer1@bank.example", "customer2@bank.example", "customer3@bank.example");
    }

    // A column only a project's custom pattern calls PII — named in neither the SQL nor the built-in patterns.
    [TestCase(null, "iban")]
    [TestCase("SELECT iban AS account FROM @result1", "account")]
    public async Task ACustomPiiPattern_MasksItsColumn_OnTheStepAndThroughTheJoin(string? finalQuery, string column)
    {
        ConnectLedger([("id", x => x + 1), ("iban", x => $"HR12100100518630001{x + 1}")]);
        var service = Service(
            [(Ledger, "SELECT * FROM accounts")],
            finalQuery,
            new Dictionary<int, McpSettingsData> { [Ledger] = new() { EnablePiiDetection = true, CustomPiiPatterns = ["iban"] } });

        var result = await service.ExecuteQuery(SubscriptionId, CancellationToken.None);

        result.AllRecords.Should().HaveCount(3);
        result.AllRecords.Select(x => x[column]).Should().Equal("H***1", "H***2", "H***3");
        result.QueryResults.Should().NotContain("HR121001005186");
    }

    [Test]
    public async Task SeveralStepsWithoutAFinalQuery_DeliverTheLastStep_MaskedByItsColumnNames()
    {
        ConnectLedger([("id", x => x + 1)]);
        ConnectCrm([("id", x => x + 10), ("email", x => $"customer{x + 1}@bank.example")]);
        var service = Service(
            [(Ledger, "SELECT id FROM accounts"), (Crm, "SELECT * FROM customers")],
            settings: new Dictionary<int, McpSettingsData> { [Ledger] = new(), [Crm] = new() });

        var result = await service.ExecuteQuery(SubscriptionId, CancellationToken.None);

        result.AllRecords.Select(x => x["id"]).Should().Equal(10, 11, 12);
        result.AllRecords.Should().OnlyContain(x => x["email"]!.ToString()!.Contains("***"), "the SQL names no PII column; the result's column name is PII");
        result.QueryResults.Should().NotContain("@bank.example");
    }

    // address_id is a PII-like name: masked in the rows a recipient gets, but the steps' rows keep their values so the join
    // pairs each customer with their own address.
    [Test]
    public async Task AJoinKeyNamedLikePii_JoinsOnItsValues_AndIsMaskedOnlyInTheDeliveredRows()
    {
        ConnectLedger([("address_id", x => x + 1), ("name", x => $"name{x + 1}")]);
        ConnectCrm([("address_id", x => x + 1), ("city", x => $"city{x + 1}")]);
        var service = Service(
            [(Ledger, "SELECT address_id, name FROM customers"), (Crm, "SELECT address_id, city FROM addresses")],
            "SELECT a.name, b.city, a.address_id FROM @result1 a JOIN @result2 b ON a.address_id = b.address_id",
            new Dictionary<int, McpSettingsData> { [Ledger] = new(), [Crm] = new() });

        var result = await service.ExecuteQuery(SubscriptionId, CancellationToken.None);

        result.AllRecords.Select(x => $"{x["name"]}:{x["city"]}").Should().Equal("name1:city1", "name2:city2", "name3:city3");
        result.AllRecords.Should().OnlyContain(x => (string)x["address_id"]! == "***");
    }

    // Three different e-mail addresses that all mask to c***e: the steps' rows join on the addresses as read, each customer
    // to their own city, and only the delivered e-mail column is masked.
    [Test]
    public async Task DistinctKeysThatMaskAlike_JoinOneToOne_AndOnlyTheDeliveredColumnIsMasked()
    {
        ConnectLedger([("id", x => x + 1), ("email", x => $"customer{x + 1}@bank.example")]);
        ConnectCrm([("email", x => $"customer{x + 1}@bank.example"), ("city", x => $"city{x + 1}")]);
        var service = Service(
            [(Ledger, "SELECT id, email FROM customers"), (Crm, "SELECT email, city FROM contacts")],
            "SELECT a.id, a.email, b.city FROM @result1 a JOIN @result2 b ON a.email = b.email ORDER BY a.id",
            new Dictionary<int, McpSettingsData> { [Ledger] = new(), [Crm] = new() });

        var result = await service.ExecuteQuery(SubscriptionId, CancellationToken.None);

        result.AllRecords.Select(x => $"{x["id"]}:{x["city"]}").Should().Equal("1:city1", "2:city2", "3:city3");
        result.AllRecords.Should().OnlyContain(x => (string)x["email"]! == "c***e");
        result.QueryResults.Should().NotContain("@bank.example");
    }

    // The final query's own name for a step's PII column, or for an expression over it, is masked by the column's lineage
    // through the select list; "reach" is PII only by the step's aliasing, and "x" by no name at all.
    [TestCase("email", "SELECT id, email FROM customers", "SELECT UPPER(a.email) AS x, b.city FROM @result1 a JOIN @result2 b ON a.id = b.id")]
    [TestCase("reach", "SELECT id, email AS reach FROM customers", "SELECT UPPER(a.reach) AS x, b.city FROM @result1 a JOIN @result2 b ON a.id = b.id")]
    [TestCase("reach", "SELECT id, email AS reach FROM customers", "SELECT d.x, d.city FROM (SELECT a.reach AS x, b.city FROM @result1 a JOIN @result2 b ON a.id = b.id) d")]
    public async Task AFinalQueryColumnFilledFromAStepsPiiColumn_IsMasked_AndOtherColumnsAreNot(string ledgerColumn, string ledgerSql, string finalQuery)
    {
        ConnectLedger([("id", x => x + 1), (ledgerColumn, x => $"customer{x + 1}@bank.example")]);
        ConnectCrm([("id", x => x + 1), ("city", x => $"city{x + 1}")]);
        var service = Service(
            [(Ledger, ledgerSql), (Crm, "SELECT id, city FROM addresses")],
            finalQuery,
            new Dictionary<int, McpSettingsData> { [Ledger] = new(), [Crm] = new() });

        var result = await service.ExecuteQuery(SubscriptionId, CancellationToken.None);

        result.AllRecords.Should().HaveCount(3);
        result.AllRecords.Should().OnlyContain(x => x["x"]!.ToString()!.Contains("***"));
        result.AllRecords.Select(x => x["city"]).Should().BeEquivalentTo(new[] { "city1", "city2", "city3" });
        result.QueryResults.Should().NotContain("@bank.example");
    }

    // The provider masks a host-masked column before Beacon reads it; through the join it stays masked, under an alias
    // too, whatever the detection setting.
    [TestCase(true)]
    [TestCase(false)]
    public async Task AHostMaskedColumn_StaysMaskedThroughTheJoin(bool detectPii)
    {
        ConnectLedger([("Id", x => x + 1), ("SSN", x => $"01013029{x + 1}9")]);
        ConnectCrm([("id", x => x + 1), ("city", x => $"city{x + 1}")]);
        var snapshot = HostTestModelFactory.Read(x => x.AllowTables("Customer").MaskColumns(y => y.Name == "SSN"), npgsql: true);
        var service = Service(
            [(Ledger, "SELECT c.\"Id\", c.\"SSN\" FROM public.\"Customer\" c"), (Crm, "SELECT id, city FROM addresses")],
            "SELECT a.SSN AS kt, b.city FROM @result1 a JOIN @result2 b ON a.Id = b.id",
            new Dictionary<int, McpSettingsData>
            {
                [Ledger] = new() { EnablePiiDetection = detectPii },
                [Crm] = new() { EnablePiiDetection = detectPii }
            },
            hostSnapshot: snapshot);

        var result = await service.ExecuteQuery(SubscriptionId, CancellationToken.None);

        result.AllRecords.Should().HaveCount(3);
        result.AllRecords.Should().OnlyContain(x => x["kt"]!.ToString()!.All(y => y == '*'));
        result.AllRecords.Select(x => x["city"]).Should().BeEquivalentTo(new[] { "city1", "city2", "city3" });
        result.QueryResults.Should().NotContain("0101302");
    }

    private static void ConnectLedger(IReadOnlyList<(string Name, Func<int, object?> Value)> columns)
    {
        DbConnectionFactory.Register(DatabaseEngineType.PostgreSQL, _ => new RecordingDbConnection { Columns = columns, RowCount = 3 });
    }

    private static void ConnectCrm(IReadOnlyList<(string Name, Func<int, object?> Value)> columns)
    {
        DbConnectionFactory.Register(DatabaseEngineType.MySQL, _ => new RecordingDbConnection { Columns = columns, RowCount = 3 });
    }

    /// <param name="finalQueryTimeoutSeconds">The deployment's statement timeout on the in-memory join in place of the default.</param>
    /// <param name="hostSnapshot">Makes the ledger a host data source exposed by this snapshot.</param>
    private static IQueryService Service(
        IReadOnlyList<(int DataSourceId, string Sql)> steps,
        string? finalQuery = null,
        Dictionary<int, McpSettingsData>? settings = null,
        int? timeoutSeconds = null,
        IDataSourceProviderFactory? providers = null,
        TimeSpan? statementTimeout = null,
        int? finalQueryTimeoutSeconds = null,
        HostExposureSnapshot? hostSnapshot = null)
    {
        var dataSources = new List<DataSource>
        {
            new() { Id = Ledger, Name = "ledger", DataSourceType = DataSourceType.Database, DatabaseEngineType = DatabaseEngineType.PostgreSQL, EncryptedConnectionData = "encrypted", HostManagedKey = hostSnapshot == null ? null : HostKey },
            new() { Id = Crm, Name = "crm", DataSourceType = DataSourceType.Database, DatabaseEngineType = DatabaseEngineType.MySQL, EncryptedConnectionData = "encrypted" }
        };
        var query = new Query { Id = QueryId, Name = "customers", FinalQuery = finalQuery };
        var querySteps = steps
            .Select((x, i) =>
                new QueryStep
                {
                    Id = i + 1,
                    QueryId = QueryId,
                    Query = query,
                    StepOrder = i + 1,
                    DataSourceId = x.DataSourceId,
                    DataSource = dataSources.First(y => y.Id == x.DataSourceId),
                    SqlValue = x.Sql
                })
            .ToList();
        foreach (var step in querySteps)
        {
            query.Steps.Add(step);
        }

        var subscription = new Subscription
        {
            Id = SubscriptionId,
            QueryId = QueryId,
            Query = query,
            CronExpression = "0 * * * *",
            MaxRows = 50,
            TimeoutSeconds = timeoutSeconds
        };
        var saved = new List<object>();
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(
                new Dictionary<Type, object>
                {
                    [typeof(DataSource)] = RecordingBeaconContext.MemorySet(dataSources, saved),
                    [typeof(Query)] = RecordingBeaconContext.MemorySet([query], saved),
                    [typeof(QueryStep)] = RecordingBeaconContext.MemorySet(querySteps, saved),
                    [typeof(Subscription)] = RecordingBeaconContext.MemorySet([subscription], saved)
                },
                saved));
        var shared = new Mock<ISharedSourceSettings>();
        shared
            .Setup(x => x.GetDataSourceSettingsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int dataSourceId, CancellationToken _) => (settings?.GetValueOrDefault(dataSourceId) ?? new McpSettingsData()).Clone());

        return TestQueryServices.QueryService(
            factory.Object,
            hostGuard: hostSnapshot == null ? null : new HostDataSourceGuard(new SingleHostRegistry(hostSnapshot)),
            sharedSourceSettings: shared.Object,
            providers: providers,
            finalQueryTimeoutSeconds: finalQueryTimeoutSeconds,
            statementTimeout: statementTimeout);
    }

    private sealed class SingleHostRegistry(HostExposureSnapshot snapshot) : IHostDataSourceRegistry
    {
        public IReadOnlyList<HostDataSourceRegistration> Registrations => [];

        public HostExposureSnapshot? GetSnapshot(string hostManagedKey) => hostManagedKey == HostKey ? snapshot : null;
    }
}
