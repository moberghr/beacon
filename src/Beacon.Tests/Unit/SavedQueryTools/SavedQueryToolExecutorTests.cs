using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.HostData;
using Beacon.Core.Models;
using Beacon.Core.Models.Providers;
using Beacon.Core.SavedQueries;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;
using Beacon.Tests.Unit.HostData;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using static Beacon.Tests.Unit.SavedQueryTools.SavedQueryTestData;

namespace Beacon.Tests.Unit.SavedQueryTools;

/// <summary>
/// The saved-query executor runs through the MCP read-only path: the real execution gate, the provider's read-only
/// variant with arguments as parameters (§1.5, §1.10), the project's row limit and PII masking.
/// </summary>
[TestFixture]
public class SavedQueryToolExecutorTests
{
    private const int ProjectId = 1;

    private SavedQueryTestData _data = null!;
    private Mock<IDataSourceProvider> _provider = null!;
    private List<(string Sql, Dictionary<string, object?> Parameters)> _executed = null!;
    private Queue<List<Dictionary<string, object?>>> _results = null!;

    [SetUp]
    public void SetUp()
    {
        _data = new SavedQueryTestData();
        _data.Project(ProjectId, "A", _data.DataSource(10, "warehouse"), _data.DataSource(11, "crm"), _data.DataSource(12, "host", hostManagedKey: "netgiro"));
        _executed = [];
        _results = new Queue<List<Dictionary<string, object?>>>();
        _provider = new Mock<IDataSourceProvider>();
        _provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DataSource _, string sql, Dictionary<string, object?> parameters, CancellationToken _) =>
            {
                _executed.Add((sql, parameters));
                var rows = _results.Count > 0 ? _results.Dequeue() : [];

                return new ProviderQueryResult { Success = true, Rows = rows, TotalRows = rows.Count };
            });
    }

    [Test]
    public async Task ASingleStep_RunsReadOnly_WithTheArgumentsAsParameters()
    {
        var hostile = "EU' OR '1'='1";
        _results.Enqueue([Row(("month", "2026-01"), ("total", 10m))]);
        var tool = Tool("loan_book", [Step(1, 10, "SELECT month, total FROM loans WHERE region = {region} AND issued >= {from}", Parameter("region", ParameterType.String), Parameter("from", ParameterType.DateTime))], [ProjectId]);

        var result = await Executor().ExecuteAsync(tool, ProjectId, Args(("region", hostile), ("from", new DateTime(2026, 1, 1))), CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        result.Rows.Should().ContainSingle().Which["total"].Should().Be(10m);
        _executed.Should().ContainSingle();
        _executed[0].Sql.Should().Contain("region = @p0").And.Contain("issued >= @p1").And.NotContain("OR '1'");
        _executed[0].Parameters.Should().BeEquivalentTo(new Dictionary<string, object?> { ["p0"] = hostile, ["p1"] = new DateTime(2026, 1, 1) });
        _provider.Verify(x => x.ExecuteQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()), Times.Never, "saved-query tools use only the read-only variant (§1.5)");
    }

    [Test]
    public async Task TheResult_IsCappedAtTheProjectsRowLimit_AndMarkedTruncated()
    {
        _results.Enqueue(Enumerable.Range(1, 6).Select(x => Row(("n", (object?)x))).ToList());
        var tool = Tool("numbers", [Step(1, 10, "SELECT n FROM numbers")], [ProjectId]);

        var result = await Executor(new McpSettingsData { MaxRowLimit = 5 }).ExecuteAsync(tool, ProjectId, Args(), CancellationToken.None);

        result.Rows.Should().HaveCount(5);
        result.Truncated.Should().BeTrue();
        result.MaxRows.Should().Be(5);
        _executed[0].Sql.Should().Contain("LIMIT 6", "the SQL asks for one row past the cap so truncation is detectable");
    }

    [Test]
    public async Task AWriteStatement_IsBlockedBeforeItReachesTheProvider()
    {
        var tool = Tool("sneaky", [Step(1, 10, "UPDATE loans SET total = 0")], [ProjectId]);

        var result = await Executor().ExecuteAsync(tool, ProjectId, Args(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("failed validation");
        _executed.Should().BeEmpty();
    }

    [Test]
    public async Task AHostManagedSource_GoesThroughTheHostPolicy()
    {
        var hostGuard = new Mock<IHostDataSourceGuard>();
        hostGuard
            .Setup(x => x.Check("netgiro", It.IsAny<string>()))
            .Returns(HostPolicyResult.Reject("Table 'secrets' is not exposed by the host."));
        var tool = Tool("host_rows", [Step(1, 12, "SELECT * FROM secrets WHERE id = {id}", Parameter("id", ParameterType.Number))], [ProjectId]);

        var result = await Executor(hostGuard: hostGuard.Object).ExecuteAsync(tool, ProjectId, Args(("id", 5L)), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not exposed by the host");
        hostGuard.Verify(x => x.Check("netgiro", It.Is<string>(y => y.Contains("@p0"))), Times.Once, "the host policy judges the SQL that would run");
        _executed.Should().BeEmpty();
    }

    // The REAL host policy (not a mocked guard) must accept the bound SQL a parameterised saved query produces, on
    // both engines — on PostgreSQL "@p0" otherwise parses as abs() of a column p0.
    [TestCase(false, "SELECT c.Name FROM dbo.Customer c WHERE c.Id = {id} AND c.Name <> {name}")]
    [TestCase(true, "SELECT c.\"Name\" FROM public.\"Customer\" c WHERE c.\"Id\" = {id} AND c.\"Name\" <> {name}")]
    public async Task AHostManagedSource_WithParameters_PassesTheRealHostPolicy(bool npgsql, string sql)
    {
        _data.DataSource(20, "netgiro-host", hostManagedKey: HostKey, engine: HostTestModelFactory.EngineOf(npgsql));
        _results.Enqueue([Row(("Name", "Anna"))]);
        var tool = Tool("customer_by_id", [Step(1, 20, sql, Parameter("id", ParameterType.Number), Parameter("name", ParameterType.String))], [ProjectId]);

        var result = await Executor(hostGuard: RealHostGuard(npgsql)).ExecuteAsync(tool, ProjectId, Args(("id", 5L), ("name", "Bo")), CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        result.Rows.Should().ContainSingle().Which["Name"].Should().Be("Anna");
        _executed.Should().ContainSingle();
        _executed[0].Sql.Should().Contain("@p0").And.Contain("@p1").And.NotContain("{id}");
        _executed[0].Parameters.Should().BeEquivalentTo(new Dictionary<string, object?> { ["p0"] = 5L, ["p1"] = "Bo" });
    }

    [TestCase(false, "SELECT c.Name FROM dbo.Customer c WHERE c.SSN = {ssn}")]
    [TestCase(true, "SELECT c.\"Name\" FROM public.\"Customer\" c WHERE c.\"SSN\" = {ssn}")]
    public async Task AHostManagedSource_MaskedColumnPredicate_IsRejectedByTheRealHostPolicy(bool npgsql, string sql)
    {
        _data.DataSource(20, "netgiro-host", hostManagedKey: HostKey, engine: HostTestModelFactory.EngineOf(npgsql));
        var tool = Tool("customer_by_ssn", [Step(1, 20, sql, Parameter("ssn", ParameterType.String))], [ProjectId]);

        var result = await Executor(hostGuard: RealHostGuard(npgsql)).ExecuteAsync(tool, ProjectId, Args(("ssn", "0101302989")), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Masked column");
        _executed.Should().BeEmpty();
    }

    [Test]
    public async Task PiiColumns_AreMaskedWhenTheProjectDetectsPii()
    {
        _results.Enqueue([Row(("email", "ada@example.com"), ("total", 3))]);
        var tool = Tool("customers", [Step(1, 10, "SELECT email, total FROM customers")], [ProjectId]);

        var result = await Executor(new McpSettingsData { EnablePiiDetection = true }).ExecuteAsync(tool, ProjectId, Args(), CancellationToken.None);

        result.Rows[0]["email"].Should().NotBe("ada@example.com");
        result.Rows[0]["total"].Should().Be(3);
    }

    [TestCase("SELECT * FROM customers", "email")]
    [TestCase("SELECT * FROM customers", "customer_email")]
    [TestCase("SELECT * FROM customers", "email_address")]
    public async Task PiiResultColumns_AreMaskedWhenTheProjectDetectsPii_EvenIfTheSqlTextDoesNotNameThem(string sql, string resultColumn)
    {
        _results.Enqueue([Row((resultColumn, "ada@example.com"), ("total", 3))]);
        var tool = Tool("customers", [Step(1, 10, sql)], [ProjectId]);

        var result = await Executor(new McpSettingsData { EnablePiiDetection = true }).ExecuteAsync(tool, ProjectId, Args(), CancellationToken.None);

        result.Rows[0][resultColumn].Should().Be("a***m");
        result.Rows[0]["total"].Should().Be(3);
    }

    // SC7: the result key "contact" is not PII by name and the SQL-text match is "email" — only the step SQL's alias
    // resolution masks it.
    [TestCase("SELECT email AS contact, total FROM customers")]
    [TestCase("SELECT lower(c.email) AS contact, c.total FROM customers c")]
    public async Task AnAliasedPiiColumn_IsMaskedWhenTheProjectDetectsPii(string sql)
    {
        _results.Enqueue([Row(("contact", "ada@example.com"), ("total", 3))]);
        var tool = Tool("customers", [Step(1, 10, sql)], [ProjectId]);

        var result = await Executor(new McpSettingsData { EnablePiiDetection = true }).ExecuteAsync(tool, ProjectId, Args(), CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        result.Rows[0]["contact"].Should().Be("a***m");
        result.Rows[0]["total"].Should().Be(3);
    }

    // F1: address_id matches the unanchored PII pattern. Masked before the join, 12345 and 10005 both become "1***5",
    // collide, and the join cross-matches into 4 rows; the keys must reach the join raw and only the output be masked.
    [Test]
    public async Task AMultiStepJoin_OnAKeyMatchingThePiiPattern_JoinsTheRawKeys_AndMasksThePiiOutput()
    {
        _results.Enqueue([Row(("address_id", 12345L), ("email", "ada@example.com")), Row(("address_id", 10005L), ("email", "grace@example.com"))]);
        _results.Enqueue([Row(("address_id", 12345L), ("city", "Zagreb")), Row(("address_id", 10005L), ("city", "Split"))]);
        var tool = Tool(
            "customer_cities",
            [Step(1, 11, "SELECT * FROM customers"), Step(2, 10, "SELECT address_id, city FROM addresses")],
            [ProjectId],
            finalQuery: "SELECT c.email, c.email AS contact, a.city FROM @result1 c JOIN @result2 a ON a.address_id = c.address_id ORDER BY a.city");

        var result = await Executor(new McpSettingsData { EnablePiiDetection = true }).ExecuteAsync(tool, ProjectId, Args(), CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        result.Rows.Should().HaveCount(2, "distinct join keys must not collide");
        result.Rows[0]["city"].Should().Be("Split");
        result.Rows[0]["email"].Should().Be("g***m");
        result.Rows[0]["contact"].Should().Be("g***m");
        result.Rows[1]["city"].Should().Be("Zagreb");
        result.Rows[1]["email"].Should().Be("a***m");
        result.Rows[1]["contact"].Should().Be("a***m");
    }

    [Test]
    public async Task AMultiStepJoin_AStepsAliasedPiiColumn_StaysMaskedThroughTheJoin()
    {
        _results.Enqueue([Row(("address_id", 12345L), ("contact", "ada@example.com")), Row(("address_id", 10005L), ("contact", "grace@example.com"))]);
        _results.Enqueue([Row(("address_id", 12345L), ("city", "Zagreb")), Row(("address_id", 10005L), ("city", "Split"))]);
        var tool = Tool(
            "customer_cities",
            [Step(1, 11, "SELECT address_id, email AS contact FROM customers"), Step(2, 10, "SELECT address_id, city FROM addresses")],
            [ProjectId],
            finalQuery: "SELECT c.contact, c.contact AS who, a.city FROM @result1 c JOIN @result2 a ON a.address_id = c.address_id ORDER BY a.city");

        var result = await Executor(new McpSettingsData { EnablePiiDetection = true }).ExecuteAsync(tool, ProjectId, Args(), CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        result.Rows.Should().HaveCount(2, "distinct join keys must not collide");
        result.Rows[0]["city"].Should().Be("Split");
        result.Rows[0]["contact"].Should().Be("g***m");
        result.Rows[0]["who"].Should().Be("g***m");
        result.Rows[1]["city"].Should().Be("Zagreb");
        result.Rows[1]["contact"].Should().Be("a***m");
        result.Rows[1]["who"].Should().Be("a***m");
    }

    [Test]
    public async Task DetectionOff_MasksOnlyTheHostMaskedColumns()
    {
        var hostGuard = new Mock<IHostDataSourceGuard>();
        hostGuard
            .Setup(x => x.Check("netgiro", It.IsAny<string>()))
            .Returns(new HostPolicyResult(true, null, ["ref_code"]));
        _results.Enqueue([Row(("customer_email", "ada@example.com"), ("ref_code", "123456789"), ("total", 3))]);
        var tool = Tool("host_rows", [Step(1, 12, "SELECT * FROM customers")], [ProjectId]);

        var result = await Executor(new McpSettingsData { EnablePiiDetection = false }, hostGuard.Object).ExecuteAsync(tool, ProjectId, Args(), CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        result.Rows[0]["ref_code"].Should().Be("1***9");
        result.Rows[0]["customer_email"].Should().Be("ada@example.com", "detection is off, so only the host-masked columns are masked");
        result.Rows[0]["total"].Should().Be(3);
    }

    // F-3: a host-masked column is masked BEFORE its rows enter the in-memory join store. With PII detection off the
    // final rows get no masking of their own, so a final query reading ref_code back under another name (x) would
    // otherwise return the raw value.
    [Test]
    public async Task AMultiStepJoin_AHostMaskedColumn_IsMaskedBeforeTheJoin_EvenUnderAnAlias()
    {
        var hostGuard = new Mock<IHostDataSourceGuard>();
        hostGuard
            .Setup(x => x.Check("netgiro", It.IsAny<string>()))
            .Returns(new HostPolicyResult(true, null, ["ref_code"]));
        _results.Enqueue([Row(("address_id", 12345L), ("ref_code", "123456789"))]);
        _results.Enqueue([Row(("address_id", 12345L), ("city", "Zagreb"))]);
        var tool = Tool(
            "host_cities",
            [Step(1, 12, "SELECT * FROM customers"), Step(2, 10, "SELECT address_id, city FROM addresses")],
            [ProjectId],
            finalQuery: "SELECT c.ref_code, c.ref_code AS x, a.city FROM @result1 c JOIN @result2 a ON a.address_id = c.address_id");

        var result = await Executor(new McpSettingsData { EnablePiiDetection = false }, hostGuard.Object).ExecuteAsync(tool, ProjectId, Args(), CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        hostGuard.Verify(x => x.Check("netgiro", It.IsAny<string>()), Times.Once, "step 1 runs against the host-managed source");
        result.Rows.Should().ContainSingle();
        result.Rows
            .SelectMany(x => x.Values)
            .Should()
            .NotContain("123456789");
        result.Rows[0]["ref_code"].Should().Be("1***9");
        result.Rows[0]["x"].Should().Be("1***9");
        result.Rows[0]["city"].Should().Be("Zagreb");
    }

    [Test]
    public async Task AMultiStepQuery_JoinsTheStepsInMemory()
    {
        _results.Enqueue([Row(("id", 1L), ("name", "Ada")), Row(("id", 2L), ("name", "Grace"))]);
        _results.Enqueue([Row(("customer_id", 1L), ("amount", 30L)), Row(("customer_id", 1L), ("amount", 12L))]);
        var tool = Tool(
            "customer_totals",
            [Step(1, 11, "SELECT id, name FROM customers"), Step(2, 10, "SELECT customer_id, amount FROM loans WHERE amount > {min}", Parameter("min", ParameterType.Number))],
            [ProjectId],
            finalQuery: "SELECT c.name, SUM(l.amount) AS total FROM @result1 c JOIN @result2 l ON l.customer_id = c.id GROUP BY c.name");

        var result = await Executor().ExecuteAsync(tool, ProjectId, Args(("min", 10L)), CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        result.Rows.Should().ContainSingle();
        result.Rows[0]["name"].Should().Be("Ada");
        Convert.ToInt64(result.Rows[0]["total"]).Should().Be(42);
        _executed.Should().HaveCount(2);
        _executed[1].Parameters["p0"].Should().Be(10L);
    }

    [Test]
    public async Task AnIntermediateStepPastTheCap_FailsTheCall()
    {
        var tool = Tool(
            "big_join",
            [Step(1, 10, "SELECT id FROM loans"), Step(2, 11, "SELECT id FROM customers")],
            [ProjectId],
            finalQuery: "SELECT * FROM @result1");
        _results.Enqueue(Enumerable.Range(0, SavedQueryToolExecutor.IntermediateRowCap + 1).Select(x => Row(("id", (object?)x))).ToList());

        var result = await Executor().ExecuteAsync(tool, ProjectId, Args(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("more than");
        _executed.Should().ContainSingle("the call stops at the oversized step");
    }

    [Test]
    public async Task AProviderFailure_IsReportedAsAFailedStep()
    {
        _provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderQueryResult { Success = false, ErrorMessage = "relation \"loans\" does not exist" });
        var tool = Tool("broken", [Step(1, 10, "SELECT * FROM loans")], [ProjectId]);

        var result = await Executor().ExecuteAsync(tool, ProjectId, Args(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Step 1 failed").And.Contain("does not exist");
        result.DataSourceId.Should().Be(10);
    }

    private const string HostKey = "efcore:Netgiro";

    private static HostDataSourceGuard RealHostGuard(bool npgsql)
    {
        var snapshot = HostTestModelFactory.Read(x => x.AllowTables("Customer").MaskColumns(y => y.Name == "SSN"), npgsql);

        return new HostDataSourceGuard(new SingleHostRegistry(snapshot));
    }

    private sealed class SingleHostRegistry(HostExposureSnapshot snapshot) : IHostDataSourceRegistry
    {
        public IReadOnlyList<HostDataSourceRegistration> Registrations => [];

        public HostExposureSnapshot? GetSnapshot(string hostManagedKey) => hostManagedKey == HostKey ? snapshot : null;
    }

    private SavedQueryToolExecutor Executor(McpSettingsData? settings = null, IHostDataSourceGuard? hostGuard = null)
    {
        var factory = new Mock<IDataSourceProviderFactory>();
        factory
            .Setup(x => x.GetProvider(DataSourceType.Database))
            .Returns(_provider.Object);

        return new SavedQueryToolExecutor(
            _data.Store.Factory().Object,
            factory.Object,
            TestSqlGate.Create(hostGuard: hostGuard),
            new QueryGuardrailService(),
            SettingsProviderMock.Create(settings ?? new McpSettingsData()).Object,
            NullLoggerFactory.Instance);
    }

    private static Dictionary<string, object?> Row(params (string Key, object? Value)[] values) =>
        values.ToDictionary(x => x.Key, x => x.Value);

    private static IReadOnlyDictionary<string, object?> Args(params (string Key, object? Value)[] values) =>
        values.ToDictionary(x => x.Key, x => x.Value);
}
