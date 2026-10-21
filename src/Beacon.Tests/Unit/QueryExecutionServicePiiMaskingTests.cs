using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.HostData;
using Beacon.Core.Models;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.MCP.Services;
using Beacon.Tests.Common;
using Beacon.Tests.Unit.HostData;
using Beacon.Core.Mcp;

namespace Beacon.Tests.Unit;

/// <summary>
/// Proves the PII-masking WIRING in <see cref="QueryExecutionService"/> (the MCP ask path): rows
/// returned by the provider are routed through the real <see cref="QueryGuardrailService"/> before
/// formatting, so PII column values are masked (§1.6/§1.11). <c>MaskPiiValues</c>' own logic is
/// covered by <see cref="QueryGuardrailServiceTests"/>; this asserts the seam is actually connected.
/// </summary>
[TestFixture]
public class QueryExecutionServicePiiMaskingTests
{
    private const string RawEmail = "alice@example.com";
    private const string HostKey = "efcore:Netgiro";
    private const string Ssn = "0101302989";

    [Test]
    public async Task ExecuteAsync_PiiDetectionOn_MasksPiiColumnValues()
    {
        var service = BuildService(piiDetectionOn: true);

        var result = await service.ExecuteAsync(1, "SELECT email, name FROM users", 100, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.FormattedResult.Should().NotContain(RawEmail);
        result.FormattedResult.Should().Contain("a***m");   // masked email
        result.FormattedResult.Should().Contain("Alice");   // non-PII column untouched

        // The structured channel must carry the SAME masked rows — never the raw values.
        var structuredValues = StructuredRowValues(result);
        structuredValues.Should().Contain("a***m");
        structuredValues.Should().Contain("Alice");
        structuredValues.Should().NotContain(RawEmail);
    }

    // PII detection another live project linking the data source turns on applies to this project's callers too.
    [Test]
    public async Task ExecuteAsync_AnotherProjectLinkingTheSourceDetectsPii_MasksForThisProjectToo()
    {
        var sharedSources = TestSharedSources.Linking(1, new Dictionary<int, McpSettingsData>
        {
            [1] = new() { EnablePiiDetection = false },
            [2] = new() { EnablePiiDetection = true }
        });
        var service = BuildService(piiDetectionOn: false, sharedSources: sharedSources);

        var result = await service.ExecuteAsync(1, "SELECT email, name FROM users", 100, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.FormattedResult.Should().NotContain(RawEmail).And.Contain("a***m");
    }

    // BigQuery reads `#` as a comment: the row cap goes on a new line and the alias resolves, so only the PII column is
    // masked. Parsed generically, the cap would land inside the comment and every column would be masked.
    [Test]
    public async Task ExecuteAsync_BigQuerySource_CapsAndMasksInBigQueryDialect()
    {
        var executed = new List<string>();
        var service = BuildService(
            piiDetectionOn: true,
            rows: [new Dictionary<string, object?> { ["contact"] = RawEmail, ["name"] = "Alice" }],
            dataSourceType: DataSourceType.BigQuery,
            engine: null,
            executed: executed);

        var result = await service.ExecuteAsync(1, "SELECT email AS contact, name FROM users # note", 100, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        executed.Should().Equal("SELECT email AS contact, name FROM users # note\nLIMIT 100");
        StructuredRowValues(result).Should().Contain("Alice").And.NotContain(RawEmail);
    }

    [Test]
    public async Task ExecuteAsync_DatabricksSource_RunsCappedAndMasked()
    {
        var executed = new List<string>();
        var service = BuildService(
            piiDetectionOn: true,
            rows: [new Dictionary<string, object?> { ["contact"] = RawEmail, ["name"] = "Alice" }],
            dataSourceType: DataSourceType.Databricks,
            engine: null,
            executed: executed,
            configuredDatabase: "main");

        var result = await service.ExecuteAsync(1, "SELECT email AS contact, name FROM main.crm.users", 100, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        executed.Should().Equal("SELECT email AS contact, name FROM main.crm.users LIMIT 100");
        StructuredRowValues(result).Should().Contain("Alice").And.NotContain(RawEmail);
    }

    // Each database engine caps the statement in its own dialect: MySQL reads `#` as a comment, so the cap goes on a new
    // line after it; SQL Server has no LIMIT.
    [TestCase(DatabaseEngineType.MySQL, "SELECT email AS contact, name FROM users # note", "SELECT email AS contact, name FROM users # note\nLIMIT 100")]
    [TestCase(DatabaseEngineType.MSSQL, "SELECT email AS contact, name FROM users", "SELECT TOP 100 email AS contact, name FROM users")]
    public async Task ExecuteAsync_DatabaseSource_CapsAndMasksInItsEnginesDialect(DatabaseEngineType engine, string sql, string capped)
    {
        var executed = new List<string>();
        var service = BuildService(
            piiDetectionOn: true,
            rows: [new Dictionary<string, object?> { ["contact"] = RawEmail, ["name"] = "Alice" }],
            engine: engine,
            executed: executed);

        var result = await service.ExecuteAsync(1, sql, 100, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        executed.Should().Equal(capped);
        StructuredRowValues(result).Should().Contain("Alice").And.NotContain(RawEmail);
    }

    [Test]
    public async Task ExecuteAsync_PiiDetectionOff_ReturnsRawValues()
    {
        var service = BuildService(piiDetectionOn: false);

        var result = await service.ExecuteAsync(1, "SELECT email, name FROM users", 100, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.FormattedResult.Should().Contain(RawEmail);
        StructuredRowValues(result).Should().Contain(RawEmail);
    }

    // The database's error quotes a row value: the MCP caller (the ask flow, dry_run) gets it redacted while PII detection
    // is on, as it was otherwise.
    [TestCase(true, "Database error 22P02 (data exception): a value could not be converted to integer. The database's message is withheld because it can quote row values.")]
    [TestCase(false, DatabaseErrorWithAValue)]
    public async Task ExecuteAsync_AFailureQuotingAValue_IsRedactedWhilePiiDetectionIsOn(bool piiDetectionOn, string expected)
    {
        var service = BuildService(piiDetectionOn, result: new ProviderQueryResult { Success = false, ErrorMessage = DatabaseErrorWithAValue });

        var result = await service.ExecuteAsync(1, "SELECT total FROM orders", 100, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be(expected);
    }

    [TestCase(true, "Database error 22P02 (data exception): a value could not be converted to integer. The database's message is withheld because it can quote row values.")]
    [TestCase(false, DatabaseErrorWithAValue)]
    public async Task ValidateAsync_ADryRunFailureQuotingAValue_IsRedactedWhilePiiDetectionIsOn(bool piiDetectionOn, string expected)
    {
        var service = BuildService(piiDetectionOn, validation: new Beacon.Core.Models.Providers.QueryValidationResult { IsValid = false, Errors = [DatabaseErrorWithAValue] });

        var outcome = await service.ValidateAsync(1, "SELECT total FROM orders", CancellationToken.None);

        outcome.Error.Should().Be(expected);
    }

    private const string DatabaseErrorWithAValue = "22P02: invalid input syntax for type integer: \"Ivan Horvat\"";

    private static List<string?> StructuredRowValues(QueryExecutionResult result)
    {
        result.Structured.Should().NotBeNull();
        return result.Structured!["rows"]!.AsArray()
            .SelectMany(x => x!.AsArray())
            .Select(x => x?.GetValue<string>())
            .ToList();
    }

    [Test]
    public async Task ExecuteAsync_ResolvesPiiDetectionForTheActiveProject_NotTheGlobalRow()
    {
        // T-F001 / SF-F001: the PII switch is per project now. Global says OFF, project 7 says ON, the request
        // runs under project 7 — masking must follow the project, and the provider must be asked for 7 only.
        Mock<IMcpSettingsProvider>? settingsProvider = null;
        var service = BuildService(
            piiDetectionOn: false,
            activeProjectId: 7,
            perProject: new Dictionary<int, McpSettingsData> { [7] = new McpSettingsData { EnablePiiDetection = true } },
            captureProvider: x => settingsProvider = x);

        var result = await service.ExecuteAsync(1, "SELECT email, name FROM users", 100, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.FormattedResult.Should().NotContain(RawEmail, "project 7 has PII detection on even though the global row has it off");
        settingsProvider!.Verify(x => x.GetEffectiveSettingsAsync(7, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        settingsProvider.Verify(x => x.GetEffectiveSettingsAsync(It.Is<int>(id => id != 7), It.IsAny<CancellationToken>()), Times.Never);
    }

    // The recorded MCP limits apply on the ask path: the statement timeout and the result size budget are the active
    // project's resolved settings, not constants. Global is 30 s / 256 KB; project 7 is tighter.
    [Test]
    public async Task ExecuteAsync_RunsWithinTheProjectsResolvedStatementTimeout()
    {
        var service = BuildService(
            piiDetectionOn: false,
            activeProjectId: 7,
            perProject: new Dictionary<int, McpSettingsData> { [7] = new McpSettingsData { StatementTimeoutSeconds = 1 } },
            execute: async token =>
            {
                await Task.Delay(TimeSpan.FromSeconds(20), token);
                return new ProviderQueryResult { Success = true, Rows = [] };
            });
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var act = () => service.ExecuteAsync(1, "SELECT name FROM users", 100, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "the 1 s project timeout cancelled the statement, not the 30 s default");
    }

    [Test]
    public async Task ExecuteAsync_AResultOverTheProjectsMaxResultBytes_ReturnsFewerRows_TruncatedBySize()
    {
        var rows = Enumerable.Range(1, 40)
            .Select(x => new Dictionary<string, object?> { ["id"] = x, ["note"] = new string('x', 500) })
            .ToList();
        var service = BuildService(
            piiDetectionOn: false,
            activeProjectId: 7,
            perProject: new Dictionary<int, McpSettingsData> { [7] = new McpSettingsData { MaxResultBytes = 4096 } },
            rows: rows);

        var result = await service.ExecuteAsync(1, "SELECT id, note FROM users", 100, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.FormattedResult.Should().Contain("_Further rows omitted — response size budget reached._");
        var structured = result.Structured!.AsObject();
        structured["truncated"]!.GetValue<bool>().Should().BeTrue();
        structured["rows_omitted_for_size"]!.GetValue<bool>().Should().BeTrue();
        structured["row_count"]!.GetValue<int>().Should().Be(40);
        structured["rows"]!.AsArray().Count.Should().BeInRange(1, 39);
        (System.Text.Encoding.UTF8.GetByteCount(result.FormattedResult!) + System.Text.Encoding.UTF8.GetByteCount(structured.ToJsonString()))
            .Should().BeLessThanOrEqualTo(4096, "the text and the structured payload together stay within MaxResultBytes");
    }

    [Test]
    public async Task ExecuteAsync_TheSameResultUnderTheDefaultMaxResultBytes_IsNotTruncated()
    {
        var rows = Enumerable.Range(1, 40)
            .Select(x => new Dictionary<string, object?> { ["id"] = x, ["note"] = new string('x', 500) })
            .ToList();
        var service = BuildService(piiDetectionOn: false, rows: rows);

        var result = await service.ExecuteAsync(1, "SELECT id, note FROM users", 100, CancellationToken.None);

        result.FormattedResult.Should().NotContain("response size budget");
        result.Structured!.AsObject().ContainsKey("rows_omitted_for_size").Should().BeFalse();
        result.Structured!["rows"]!.AsArray().Count.Should().Be(40);
    }

    // MaxExplainCost on the ask path: the statement as it runs is checked against the project's resolved limit first; a
    // refusal reaches the caller as it is and the statement never runs.
    [Test]
    public async Task ExecuteAsync_ACostOverTheProjectsLimit_IsRefused_WithoutRunningTheStatement()
    {
        const string refusal = "The query's estimated cost (5000) exceeds this project's limit (50). Narrow the query, add filters, or ask an administrator to raise MaxExplainCost.";
        var executed = new List<string>();
        var costLimit = new Mock<IQueryCostLimit>();
        costLimit
            .Setup(x => x.CheckAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<decimal?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryCostCheck(refusal, null));
        var service = BuildService(
            piiDetectionOn: true,
            activeProjectId: 7,
            perProject: new Dictionary<int, McpSettingsData> { [7] = new McpSettingsData { MaxExplainCost = 50m } },
            executed: executed,
            costLimit: costLimit.Object);

        var result = await service.ExecuteAsync(1, "SELECT name FROM users", 100, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be(refusal);
        executed.Should().BeEmpty();
        costLimit.Verify(
            x => x.CheckAsync(It.IsAny<DataSource>(), It.Is<string>(y => y.Contains("LIMIT 100")), It.IsAny<IReadOnlyDictionary<string, object?>>(), 50m, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task ExecuteAsync_AFailedEstimate_FailsAsTheStatementWould_WithoutRunningIt()
    {
        var executed = new List<string>();
        var costLimit = new Mock<IQueryCostLimit>();
        costLimit
            .Setup(x => x.CheckAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<decimal?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryCostCheck(null, "relation \"userz\" does not exist"));
        var service = BuildService(piiDetectionOn: false, executed: executed, costLimit: costLimit.Object);

        var result = await service.ExecuteAsync(1, "SELECT name FROM userz", 100, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("relation \"userz\" does not exist");
        executed.Should().BeEmpty();
    }

    [TestCase("SELECT * FROM users", "email")]
    [TestCase("SELECT customer_email FROM users", "customer_email")]
    [TestCase("SELECT email_address FROM users", "email_address")]
    public async Task ExecuteAsync_PiiDetectionOn_MasksPiiResultColumnsTheSqlTextDoesNotName(string sql, string resultColumn)
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { [resultColumn] = RawEmail, ["name"] = "Alice" }
        };
        var service = BuildService(piiDetectionOn: true, rows: rows);

        var result = await service.ExecuteAsync(1, sql, 100, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.FormattedResult.Should().NotContain(RawEmail);
        result.FormattedResult.Should().Contain("Alice");
        StructuredRowValues(result).Should().Contain("a***m").And.NotContain(RawEmail);
    }

    // SC7: the result key "contact" is not PII by name and the SQL-text match is "email" — only the executed SQL's
    // alias resolution masks it.
    [TestCase("SELECT email AS contact, name FROM users")]
    [TestCase("SELECT lower(u.email) AS contact, name FROM users u")]
    public async Task ExecuteAsync_PiiDetectionOn_MasksAnAliasedPiiColumn(string sql)
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["contact"] = RawEmail, ["name"] = "Alice" }
        };
        var service = BuildService(piiDetectionOn: true, rows: rows);

        var result = await service.ExecuteAsync(1, sql, 100, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.FormattedResult.Should().NotContain(RawEmail);
        result.FormattedResult.Should().Contain("Alice");
        StructuredRowValues(result).Should().Contain("a***m").And.NotContain(RawEmail);
    }

    [Test]
    public async Task ExecuteAsync_PiiDetectionOff_DoesNotMaskResultColumns()
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["customer_email"] = RawEmail, ["name"] = "Alice" }
        };
        var service = BuildService(piiDetectionOn: false, rows: rows);

        var result = await service.ExecuteAsync(1, "SELECT * FROM users", 100, CancellationToken.None);

        result.FormattedResult.Should().Contain(RawEmail);
    }

    // The host policy's masked output columns are masked in full on this path too, whatever the detection setting, even
    // when the provider returns the value as it is (this provider double does not mask).
    [TestCase(true)]
    [TestCase(false)]
    public async Task ExecuteAsync_HostMaskedColumn_IsMaskedInFull(bool piiDetectionOn)
    {
        var hostGuard = new Mock<IHostDataSourceGuard>();
        hostGuard
            .Setup(x => x.Check(It.IsAny<DataSource>(), It.IsAny<string>()))
            .Returns(new HostPolicyResult(true, null, ["ref_code"]));
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["ref_code"] = "123456789", ["name"] = "Alice" }
        };
        var service = BuildService(piiDetectionOn, rows: rows, hostGuard: hostGuard.Object);

        var result = await service.ExecuteAsync(1, "SELECT ref_code, name FROM customers", 100, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        StructuredRowValues(result).Should().Equal(HostResultMasker.MaskedValue, "Alice");
        result.FormattedResult.Should().NotContain("123456789").And.NotContain("1***9");
    }

    // A value the provider already masked in full comes out exactly as the provider masked it, not masked a second time.
    [Test]
    public async Task ExecuteAsync_ValueTheProviderAlreadyMasked_IsReturnedAsMasked()
    {
        var hostGuard = new Mock<IHostDataSourceGuard>();
        hostGuard
            .Setup(x => x.Check(It.IsAny<DataSource>(), It.IsAny<string>()))
            .Returns(new HostPolicyResult(true, null, ["customer_email"]));
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["customer_email"] = HostResultMasker.MaskedValue, ["name"] = "Alice" }
        };
        var service = BuildService(piiDetectionOn: true, rows: rows, hostGuard: hostGuard.Object);

        var result = await service.ExecuteAsync(1, "SELECT customer_email, name FROM customers", 100, CancellationToken.None);

        StructuredRowValues(result).Should().Equal(HostResultMasker.MaskedValue, "Alice");
    }

    // An ordinary source has no host-masked columns: detector-found PII keeps its partial mask.
    [Test]
    public async Task ExecuteAsync_OrdinarySource_KeepsThePartialMask()
    {
        var service = BuildService(piiDetectionOn: true);

        var result = await service.ExecuteAsync(1, "SELECT email, name FROM users", 100, CancellationToken.None);

        StructuredRowValues(result).Should().Equal("a***m", "Alice");
    }

    // The real host check derives the masked output columns: an aliased column and one named in another case are both
    // masked in full, the result key matched without regard to case.
    [TestCase(true)]
    [TestCase(false)]
    public async Task ExecuteAsync_HostCheckedSource_MasksTheAliasedAndDifferentlyCasedColumnsInFull(bool piiDetectionOn)
    {
        var snapshot = HostTestModelFactory.Read(x => x.AllowTables("Customer").MaskColumns(y => y.Name == "SSN"));
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["Id"] = "1", ["ssn"] = Ssn, ["KT"] = Ssn }
        };
        var service = BuildService(
            piiDetectionOn,
            rows: rows,
            engine: DatabaseEngineType.MSSQL,
            hostGuard: new HostDataSourceGuard(new SingleHostRegistry(snapshot)),
            hostManagedKey: HostKey);

        var result = await service.ExecuteAsync(1, "SELECT c.Id, c.ssn, c.SSN AS kt FROM dbo.Customer c", 100, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        StructuredRowValues(result).Should().Equal("1", HostResultMasker.MaskedValue, HostResultMasker.MaskedValue);
        result.FormattedResult.Should().NotContain(Ssn);
    }

    // The host policy refuses the statement: the executor's own gate refuses it before it reaches the source.
    [Test]
    public async Task ExecuteAsync_RefusedHostCheck_IsRefusedBeforeTheStatementRuns()
    {
        const string sql = "SELECT c.Id, c.SSN AS kt FROM dbo.Customer c";
        var executed = new List<string>();
        var hostGuard = new HostDataSourceGuard(new TestHostDataSources());
        var service = BuildService(
            piiDetectionOn: true,
            engine: DatabaseEngineType.MSSQL,
            executed: executed,
            hostGuard: hostGuard,
            hostManagedKey: HostKey);

        var result = await service.ExecuteAsync(1, sql, 100, CancellationToken.None);

        result.Should().BeEquivalentTo(new { IsSuccess = false, Refused = true, RowCount = 0 }, x => x.ExcludingMissingMembers());
        result.ErrorMessage.Should().Be(GateRefusal(sql, DatabaseEngineType.MSSQL, hostGuard, HostKey));
        executed.Should().BeEmpty();
    }

    // A host check of the statement as it ran that refuses names no masked column: every result column is masked in full
    // rather than none (the gate's check of the same statement let it run).
    [TestCase(true)]
    [TestCase(false)]
    public async Task ExecuteAsync_HostCheckRefusingTheStatementAsItRan_MasksEveryResultColumnInFull(bool piiDetectionOn)
    {
        var hostGuard = new Mock<IHostDataSourceGuard>();
        hostGuard
            .Setup(x => x.Check(HostKey, It.IsAny<string>()))
            .Returns(new HostPolicyResult(true, null, []));
        hostGuard
            .Setup(x => x.Check(It.IsAny<DataSource>(), It.IsAny<string>()))
            .Returns(HostPolicyResult.Reject("refused"));
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["Id"] = "1", ["kt"] = Ssn }
        };
        var service = BuildService(
            piiDetectionOn,
            rows: rows,
            engine: DatabaseEngineType.MSSQL,
            hostGuard: hostGuard.Object,
            hostManagedKey: HostKey);

        var result = await service.ExecuteAsync(1, "SELECT c.Id, c.SSN AS kt FROM dbo.Customer c", 100, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        StructuredRowValues(result).Should().Equal(HostResultMasker.MaskedValue, HostResultMasker.MaskedValue);
        result.FormattedResult.Should().NotContain(Ssn);
    }

    // The statement that reaches the source is the gate's own output: the ask's row cap is the gate's row-limit stage,
    // also lowering a larger bound the statement sets itself.
    [TestCase("SELECT name FROM users", 7, "SELECT name FROM users LIMIT 7")]
    [TestCase("SELECT name FROM users LIMIT 500", 100, "SELECT name FROM users LIMIT 100")]
    public async Task ExecuteAsync_RunsTheGatesFinalSql_CappedAtTheRequestedRows(string sql, int maxRows, string expected)
    {
        var executed = new List<string>();
        var service = BuildService(piiDetectionOn: false, executed: executed);

        var result = await service.ExecuteAsync(1, sql, maxRows, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        executed.Should().Equal(expected);
    }

    // The executor's gate refuses what the gate refuses anywhere: a write, a system catalog read, a name in another
    // database. Nothing reaches the source, and the refusal is Beacon's own message, not a database error.
    [TestCase("DELETE FROM users")]
    [TestCase("SELECT * FROM pg_catalog.pg_stats")]
    [TestCase("SELECT * FROM other_db.public.users")]
    public async Task ExecuteAsync_SqlTheGateRefuses_NeverReachesTheSource(string sql)
    {
        var executed = new List<string>();
        var service = BuildService(piiDetectionOn: true, executed: executed);

        var result = await service.ExecuteAsync(1, sql, 100, CancellationToken.None);

        result.Should().BeEquivalentTo(new { IsSuccess = false, Refused = true, RowCount = 0 }, x => x.ExcludingMissingMembers());
        result.ErrorMessage.Should().Be(GateRefusal(sql, DatabaseEngineType.PostgreSQL));
        executed.Should().BeEmpty();
    }

    // The gate's own refusal of the statement as the executor asks for it (no database qualifier configured).
    private static string? GateRefusal(string sql, DatabaseEngineType engine, IHostDataSourceGuard? hostGuard = null, string? hostManagedKey = null)
    {
        var dataSource = new DataSource { Name = "source", DataSourceType = DataSourceType.Database, DatabaseEngineType = engine, EncryptedConnectionData = "" };
        var report = TestSqlGate.Create(hostGuard: hostGuard).Evaluate(SqlGateRequest.FromSettings(sql, DataSourceSqlDialect.Of(dataSource), new McpSettingsData { EnablePiiDetection = true }, TestRowSecurity.NotApplicable) with
        {
            MaxRows = 100,
            HostManagedKey = hostManagedKey
        });

        report.Blocked.Should().BeTrue();

        return report.BlockReason;
    }

    private static QueryExecutionService BuildService(
        bool piiDetectionOn,
        int activeProjectId = 1,
        IReadOnlyDictionary<int, McpSettingsData>? perProject = null,
        Action<Mock<IMcpSettingsProvider>>? captureProvider = null,
        List<Dictionary<string, object?>>? rows = null,
        DataSourceType dataSourceType = DataSourceType.Database,
        DatabaseEngineType? engine = DatabaseEngineType.PostgreSQL,
        List<string>? executed = null,
        ISharedSourceSettings? sharedSources = null,
        ProviderQueryResult? result = null,
        Beacon.Core.Models.Providers.QueryValidationResult? validation = null,
        Func<CancellationToken, Task<ProviderQueryResult>>? execute = null,
        IQueryCostLimit? costLimit = null,
        IHostDataSourceGuard? hostGuard = null,
        string? hostManagedKey = null,
        string? configuredDatabase = null)
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new SeededDataSourceContext(dataSourceType, engine, hostManagedKey));

        var providerResult = result ?? new ProviderQueryResult
        {
            Success = true,
            Rows = rows ??
            [
                new Dictionary<string, object?> { ["email"] = RawEmail, ["name"] = "Alice" }
            ]
        };
        // QueryExecutionService routes through the database-level read-only path (§1.5 backstop).
        var provider = new Mock<IDataSourceProvider>();
        provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(
                It.IsAny<DataSource>(),
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .Callback<DataSource, string, Dictionary<string, object?>, CancellationToken>((_, sql, _, _) => executed?.Add(sql))
            .Returns<DataSource, string, Dictionary<string, object?>, CancellationToken>((_, _, _, token) => execute?.Invoke(token) ?? Task.FromResult(providerResult));
        provider
            .Setup(x => x.ValidateQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(validation ?? new Beacon.Core.Models.Providers.QueryValidationResult { IsValid = true });

        var providerFactory = new Mock<IDataSourceProviderFactory>();
        providerFactory
            .Setup(x => x.GetProvider(It.IsAny<DataSourceType>()))
            .Returns(provider.Object);

        var settingsProvider = SettingsProviderMock.Create(
            new McpSettingsData { EnablePiiDetection = piiDetectionOn },
            projectSettings: perProject);
        captureProvider?.Invoke(settingsProvider);

        var guard = hostGuard ?? new HostDataSourceGuard(new TestHostDataSources());

        return new QueryExecutionService(
            factory.Object,
            providerFactory.Object,
            new QueryGuardrailService(),
            sharedSources ?? SettingsProviderMock.Unshared(settingsProvider.Object),
            new McpProjectContext { UserId = 1, ActiveProjectId = activeProjectId, AllowedProjectIds = [activeProjectId] }, new McpStatementLog(), costLimit ?? TestQueryCostLimit.Passing,
            guard,
            TestSqlGate.Create(hostGuard: guard),
            TestConfiguredDatabases.Of(configuredDatabase), TestRowSecurity.Resolver);
    }

    private sealed class SeededDataSourceContext : BeaconContext
    {
        private static readonly DbContextOptions<SeededDataSourceContext> _options =
            new DbContextOptionsBuilder<SeededDataSourceContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly DataSourceType _dataSourceType;
        private readonly DatabaseEngineType? _engine;
        private readonly string? _hostManagedKey;

        public SeededDataSourceContext(DataSourceType dataSourceType, DatabaseEngineType? engine, string? hostManagedKey) : base(_options, "beacon")
        {
            _dataSourceType = dataSourceType;
            _engine = engine;
            _hostManagedKey = hostManagedKey;
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(DataSource))
            {
                return (DbSet<TEntity>)(object)BuildDataSourceSet(_dataSourceType, _engine, _hostManagedKey);
            }

            return base.Set<TEntity>();
        }

        private static DbSet<DataSource> BuildDataSourceSet(DataSourceType dataSourceType, DatabaseEngineType? engine, string? hostManagedKey)
        {
            var data = new List<DataSource>
            {
                new()
                {
                    Id = 1,
                    Name = "ds",
                    DataSourceType = dataSourceType,
                    EncryptedConnectionData = "x",
                    DatabaseEngineType = engine,
                    HostManagedKey = hostManagedKey
                }
            }.AsQueryable();

            var set = new Mock<DbSet<DataSource>>();
            set.As<IAsyncEnumerable<DataSource>>()
                .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
                .Returns(new TestAsyncEnumerator<DataSource>(data.GetEnumerator()));
            set.As<IQueryable<DataSource>>()
                .Setup(x => x.Provider)
                .Returns(new TestAsyncQueryProvider<DataSource>(data.Provider));
            set.As<IQueryable<DataSource>>().Setup(x => x.Expression).Returns(data.Expression);
            set.As<IQueryable<DataSource>>().Setup(x => x.ElementType).Returns(data.ElementType);
            set.As<IQueryable<DataSource>>().Setup(x => x.GetEnumerator()).Returns(data.GetEnumerator());
            return set.Object;
        }
    }

    private sealed class SingleHostRegistry(HostExposureSnapshot snapshot) : IHostDataSourceRegistry
    {
        public IReadOnlyList<HostDataSourceRegistration> Registrations => [];

        public HostExposureSnapshot? GetSnapshot(string hostManagedKey) => hostManagedKey == HostKey ? snapshot : null;
    }
}
