using System.Collections;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.Api;
using Beacon.Api.Endpoints;
using Beacon.Core.Authentication;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.DataQuality;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.HostData;
using Beacon.Core.Mcp;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Validation;
using Beacon.Core.Worker.Services;
using Beacon.Tests.Common;
using Beacon.Tests.Unit.HostData;

namespace Beacon.Tests.Unit;

/// <summary>
/// Data-quality rule SQL is read-only (§1.5). Every rule's SQL passes the read-only gate on every evaluation and runs
/// through the provider's read-only path capped at one row; the create, update, delete and evaluate endpoints need the
/// Execute scope; on a host-managed source CustomSql is refused, the host policy applies and a failure returns the
/// generic host error. A failed rule never stores or returns the server's error text, and a caller's cancellation is
/// never recorded as a failed rule. Saving a contract is covered by <see cref="DataContractCustomSqlSaveTests"/>.
/// </summary>
[TestFixture]
[NonParallelizable] // DbConnectionFactory is a process-wide registry.
public class DataQualityCustomSqlReadOnlyTests
{
    private const string WriteBatch = "UPDATE accounts SET balance = 0; SELECT 'IBAN123' AS actual_value, 1 AS passed";
    private const string HostKey = "efcore:Netgiro";
    private const string RowValue = "IBAN123";
    private const string MaskedValue = "0101302989";

    private static readonly DateTime PriorEvaluatedAt = new(2026, 10, 1, 6, 0, 0, DateTimeKind.Utc);

    private static readonly DatabaseEngineType[] RegisteredEngines = [DatabaseEngineType.PostgreSQL, DatabaseEngineType.MySQL, DatabaseEngineType.MSSQL];

    private static readonly DatabaseEngineType[] GateEngines =
    [
        DatabaseEngineType.PostgreSQL,
        DatabaseEngineType.MSSQL,
        DatabaseEngineType.MySQL,
        DatabaseEngineType.AzureSynapse,
        DatabaseEngineType.Snowflake
    ];

    private readonly Dictionary<DatabaseEngineType, Func<string, DbConnection>> _previousFactories = [];

    [SetUp]
    public void SetUp()
    {
        var factories = GetFactoryRegistry();
        foreach (var engine in RegisteredEngines)
        {
            if (factories.TryGetValue(engine, out var previous))
            {
                _previousFactories[engine] = previous;
            }
        }
    }

    [TearDown]
    public void TearDown()
    {
        var factories = GetFactoryRegistry();
        foreach (var engine in RegisteredEngines)
        {
            if (_previousFactories.TryGetValue(engine, out var previous))
            {
                factories[engine] = previous;
                continue;
            }

            factories.TryRemove(engine, out _);
        }

        _previousFactories.Clear();
    }

    // --- the read-only gate every rule's SQL passes ---------------------------------------------------------------

    [TestCaseSource(nameof(NonReadOnlyStatements))]
    public void RejectionOf_NonReadOnlySql_IsARejection(string sql, DatabaseEngineType engine)
    {
        var report = TestSqlGate.Create().Evaluate(DataQualityRuleGuard.GateRequest(sql, engine, hostManagedKey: null));

        DataQualityRuleGuard.RejectionOf(report).Should().StartWith("Rule SQL was rejected:", $"'{sql}' is not a single read-only SELECT on {engine}");
    }

    [TestCase(DatabaseEngineType.PostgreSQL, "SELECT 1 AS passed FROM accounts LIMIT 5")]
    [TestCase(DatabaseEngineType.MySQL, "SELECT 1 AS passed FROM accounts LIMIT 1")]
    [TestCase(DatabaseEngineType.MSSQL, "SELECT TOP 5 1 AS passed FROM accounts")]
    [TestCase(DatabaseEngineType.MSSQL, "SELECT 1 AS passed FROM accounts ORDER BY id OFFSET 0 ROWS FETCH NEXT 5 ROWS ONLY")]
    public void RejectionOf_SqlWithItsOwnRowLimit_IsARejection(DatabaseEngineType engine, string sql)
    {
        var report = TestSqlGate.Create().Evaluate(DataQualityRuleGuard.GateRequest(sql, engine, hostManagedKey: null));

        DataQualityRuleGuard.RejectionOf(report).Should().Be(DataQualityRuleGuard.OwnRowLimitMessage, "the provider buffers the whole result");
    }

    [TestCase(DatabaseEngineType.PostgreSQL, "SELECT CASE WHEN COUNT(*) > 0 THEN 1 ELSE 0 END AS passed FROM accounts LIMIT 1")]
    [TestCase(DatabaseEngineType.MySQL, "SELECT CASE WHEN COUNT(*) > 0 THEN 1 ELSE 0 END AS passed FROM accounts LIMIT 1")]
    [TestCase(DatabaseEngineType.MSSQL, "SELECT TOP 1 CASE WHEN COUNT(*) > 0 THEN 1 ELSE 0 END AS passed FROM accounts")]
    public void GateRequest_ReadOnlySelect_PassesCappedAtOneRow(DatabaseEngineType engine, string capped)
    {
        var report = TestSqlGate.Create().Evaluate(DataQualityRuleGuard.GateRequest(
            "SELECT CASE WHEN COUNT(*) > 0 THEN 1 ELSE 0 END AS passed FROM accounts",
            engine,
            hostManagedKey: null));

        DataQualityRuleGuard.RejectionOf(report).Should().BeNull();
        report.FinalSql.Should().Be(capped);
    }

    // --- evaluation ---------------------------------------------------------------------------------------------

    [Test]
    public async Task EvaluateContractAsync_CustomSqlWriteBatch_NeverReachesTheConnection()
    {
        var server = Register(DatabaseEngineType.MySQL, new FakeServer { Result = CustomSqlRow() });
        var saved = new List<object>();
        var service = CreateService(Contract(OrdinarySource(DatabaseEngineType.MySQL), CustomSqlRule(WriteBatch)), saved);

        var result = await service.EvaluateContractAsync(42, CancellationToken.None);

        server.Commands.Should().BeEmpty("a write batch is rejected before any connection is opened");
        var ruleResult = result.RuleResults.Should().ContainSingle().Subject;
        ruleResult.Passed.Should().BeFalse();
        ruleResult.ActualValue.Should().Be("Error");
        ruleResult.Message.Should().Be("Rule SQL was rejected: Write operations are not allowed. Only SELECT queries are permitted.");
        StoredRuleResult(saved).Message.Should().Be(ruleResult.Message);
    }

    [Test]
    public async Task EvaluateContractAsync_CustomSqlWithItsOwnRowLimit_IsNotRun()
    {
        var server = Register(DatabaseEngineType.PostgreSQL, new FakeServer { Result = CustomSqlRow() });
        var service = CreateService(Contract(OrdinarySource(DatabaseEngineType.PostgreSQL), CustomSqlRule("SELECT 1 AS passed FROM accounts LIMIT 5")), []);

        var result = await service.EvaluateContractAsync(42, CancellationToken.None);

        server.Commands.Should().BeEmpty();
        result.RuleResults.Should().ContainSingle().Which.Message.Should().Be(DataQualityRuleGuard.OwnRowLimitMessage);
    }

    [Test]
    public async Task EvaluateContractAsync_ReadOnlyCustomSql_RunsInsideAReadOnlyTransactionCappedAtOneRow()
    {
        var server = Register(DatabaseEngineType.PostgreSQL, new FakeServer { Result = CustomSqlRow() });
        var service = CreateService(
            Contract(OrdinarySource(DatabaseEngineType.PostgreSQL), CustomSqlRule("SELECT 1 AS passed, MAX(iban) AS actual_value FROM accounts")),
            []);

        var result = await service.EvaluateContractAsync(42, CancellationToken.None);

        server.Commands
            .Select(x => x.Sql)
            .Should().Equal(
                "SET default_transaction_read_only = on",
                "SET TRANSACTION READ ONLY",
                "SELECT 1 AS passed, MAX(iban) AS actual_value FROM accounts LIMIT 1");
        server.Commands[^1].Transaction.Should().NotBeNull("the rule runs inside the READ ONLY transaction");
        result.RuleResults.Should().ContainSingle()
            .Which.ActualValue.Should().Be(RowValue, "an Admin's read-only custom check still reports its value");
    }

    [TestCase(DatabaseEngineType.PostgreSQL, "SELECT COUNT(*) AS non_matching FROM \"sales\".\"orders\" WHERE \"code\" !~ @p0 LIMIT 1")]
    [TestCase(DatabaseEngineType.MySQL, "SELECT COUNT(*) AS non_matching FROM `sales`.`orders` WHERE `code` NOT REGEXP @p0 LIMIT 1")]
    [TestCase(DatabaseEngineType.MSSQL, "SELECT TOP 1 COUNT(*) AS non_matching FROM [sales].[orders] WHERE [code] NOT LIKE @p0")]
    public async Task EvaluateContractAsync_PatternRule_BindsThePatternAsAParameter(DatabaseEngineType engine, string expectedSql)
    {
        const string pattern = @"^A\' or 'B$";
        var server = Register(engine, new FakeServer { Result = SingleValueRow("non_matching", 0L) });
        var rule = Rule(DataContractRuleType.Pattern, JsonSerializer.Serialize(new { column = "code", pattern }));
        var service = CreateService(Contract(OrdinarySource(engine), rule), []);

        var result = await service.EvaluateContractAsync(42, CancellationToken.None);

        // PostgreSQL and MySQL wrap the rule in read-only transaction statements; the rule is the one SELECT.
        var command = server.Commands.Single(x => x.Sql.StartsWith("SELECT", StringComparison.Ordinal));
        command.Sql.Should().Be(expectedSql);
        command.Parameters.Should().Equal(new Dictionary<string, object?> { ["p0"] = pattern });
        result.RuleResults.Should().ContainSingle().Which.Passed.Should().BeTrue();
    }

    [Test]
    public async Task EvaluateContractAsync_OnlyDisabledRules_IsNotEvaluatedAndKeepsThePriorScore()
    {
        // A failing contract whose only rule (Custom SQL) was switched off on upgrade.
        var server = Register(DatabaseEngineType.MySQL, new FakeServer { Result = CustomSqlRow() });
        var saved = new List<object>();
        var priorScore = PriorFailingScore();
        var service = CreateService(Contract(OrdinarySource(DatabaseEngineType.MySQL), CustomSqlRule(WriteBatch, isEnabled: false)), saved, scores: [priorScore]);

        var act = () => service.EvaluateContractAsync(42, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("This data contract has no enabled rules, so it was not evaluated.*");
        server.Commands.Should().BeEmpty();
        saved.Should().BeEmpty("no history row and no new score is recorded");
        priorScore.Score.Should().Be(40, "scoring no rules would have reported 100");
        priorScore.EvaluatedAt.Should().Be(PriorEvaluatedAt);
    }

    [Test]
    public async Task ScheduledEvaluation_OnlyDisabledRules_IsSkippedWithoutAlertOrJobFailure()
    {
        var server = Register(DatabaseEngineType.MySQL, new FakeServer { Result = CustomSqlRow() });
        var saved = new List<object>();
        var priorScore = PriorFailingScore();
        var evaluation = CreateService(Contract(OrdinarySource(DatabaseEngineType.MySQL), CustomSqlRule(WriteBatch, isEnabled: false)), saved, scores: [priorScore]);
        var notifications = new Mock<INotificationService>(MockBehavior.Strict);
        var jobService = new JobService(
            new Mock<IDbContextFactory<BeaconContext>>(MockBehavior.Strict).Object,
            Mock.Of<IQueryService>(),
            notifications.Object,
            Mock.Of<ITaskService>(),
            Mock.Of<IAnomalyDetectionService>(),
            evaluation,
            NullLogger<JobService>.Instance);

        var act = () => jobService.EvaluateDataContract(42, CancellationToken.None);

        await act.Should().NotThrowAsync("a contract with nothing to score does not fail its recurring job");
        notifications.VerifyNoOtherCalls();
        server.Commands.Should().BeEmpty();
        saved.Should().BeEmpty();
        priorScore.Score.Should().Be(40);
        priorScore.EvaluatedAt.Should().Be(PriorEvaluatedAt);
    }

    [Test]
    public async Task EvaluateContractAsync_ServerErrorQuotingAValue_ReturnsAGenericMessage()
    {
        Register(DatabaseEngineType.PostgreSQL, new FakeServer { Failure = new FakeDbException($"invalid input syntax for type integer: \"{RowValue}\"") });
        var saved = new List<object>();
        var service = CreateService(Contract(OrdinarySource(DatabaseEngineType.PostgreSQL), Rule(DataContractRuleType.Volume, "{}")), saved);

        var result = await service.EvaluateContractAsync(42, CancellationToken.None);

        var ruleResult = result.RuleResults.Should().ContainSingle().Subject;
        ruleResult.Passed.Should().BeFalse();
        ruleResult.Message.Should().Be(DataQualityEvaluationService.ExecutionFailedMessage);
        StoredRuleResult(saved).Message.Should().Be(DataQualityEvaluationService.ExecutionFailedMessage);
    }

    [TestCase("row_count", RowValue)]
    [TestCase("other_column", 5L)]
    public async Task EvaluateContractAsync_ResultOfTheWrongShape_ReturnsAFixedMessage(string column, object value)
    {
        Register(DatabaseEngineType.PostgreSQL, new FakeServer { Result = SingleValueRow(column, value) });
        var saved = new List<object>();
        var service = CreateService(Contract(OrdinarySource(DatabaseEngineType.PostgreSQL), Rule(DataContractRuleType.Volume, "{}")), saved);

        var result = await service.EvaluateContractAsync(42, CancellationToken.None);

        var ruleResult = result.RuleResults.Should().ContainSingle().Subject;
        ruleResult.Message.Should().Be(DataQualityEvaluationService.UnexpectedResultMessage, "a FormatException quotes the value it could not convert");
        ruleResult.ActualValue.Should().Be("Error");
        StoredRuleResult(saved).Message.Should().Be(DataQualityEvaluationService.UnexpectedResultMessage);
    }

    [Test]
    public async Task EvaluateContractAsync_MalformedStoredConfiguration_IsReportedAsInvalidConfiguration()
    {
        var server = Register(DatabaseEngineType.PostgreSQL, new FakeServer { Result = SingleValueRow("row_count", 5L) });
        var service = CreateService(Contract(OrdinarySource(DatabaseEngineType.PostgreSQL), Rule(DataContractRuleType.Volume, "not json")), []);

        var result = await service.EvaluateContractAsync(42, CancellationToken.None);

        server.Commands.Should().BeEmpty();
        result.RuleResults.Should().ContainSingle().Which.Message.Should().StartWith("Invalid rule configuration:");
    }

    [Test]
    public async Task EvaluateContractAsync_RuleTypeWithoutSqlForTheEngine_IsReportedAsUnsupported()
    {
        var rule = Rule(DataContractRuleType.Pattern, JsonSerializer.Serialize(new { column = "code", pattern = "^A" }));
        var service = CreateService(Contract(OrdinarySource(DatabaseEngineType.Snowflake), rule), []);

        var result = await service.EvaluateContractAsync(42, CancellationToken.None);

        result.RuleResults.Should().ContainSingle().Which.Message.Should().Be("Rule type Pattern is not supported on Snowflake.");
    }

    [Test]
    public async Task EvaluateContractAsync_NonDatabaseSource_IsRefused()
    {
        var service = CreateService(Contract(OrdinarySource(DatabaseEngineType.PostgreSQL, DataSourceType.Api), Rule(DataContractRuleType.Volume, "{}")), []);

        var act = () => service.EvaluateContractAsync(42, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconException>().WithMessage("Data contract's data source must be a database type");
    }

    [Test]
    public async Task EvaluateContractAsync_CancelledByTheCaller_PropagatesAndStoresNothing()
    {
        var server = Register(DatabaseEngineType.MySQL, new FakeServer { Result = SingleValueRow("row_count", 5L) });
        var saved = new List<object>();
        var service = CreateService(Contract(OrdinarySource(DatabaseEngineType.MySQL), Rule(DataContractRuleType.Volume, "{}")), saved);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var act = () => service.EvaluateContractAsync(42, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        server.Commands.Should().BeEmpty();
        saved.Should().BeEmpty("a cancelled evaluation is not a failed rule");
    }

    [Test]
    public async Task EvaluateContractAsync_RuleRunningPastTheTimeout_ReportsATimeout()
    {
        var server = Register(DatabaseEngineType.MySQL, new FakeServer { BlockUntilCancelled = true });
        var saved = new List<object>();
        var service = CreateService(Contract(OrdinarySource(DatabaseEngineType.MySQL), Rule(DataContractRuleType.Volume, "{}")), saved, TimeSpan.FromMilliseconds(100));

        var result = await service.EvaluateContractAsync(42, CancellationToken.None);

        // The rule started once inside the read-only transaction, which was still closed after the timeout.
        server.Commands.Select(x => x.Sql).Should().HaveCount(5)
            .And.StartWith(["SET SESSION TRANSACTION READ ONLY", "START TRANSACTION READ ONLY"])
            .And.EndWith(["ROLLBACK", "SET SESSION TRANSACTION READ WRITE"]);
        result.RuleResults.Should().ContainSingle().Which.Message.Should().Be(DataQualityEvaluationService.TimedOutMessage);
        StoredRuleResult(saved).Message.Should().Be(DataQualityEvaluationService.TimedOutMessage);
    }

    // --- host-managed data sources ------------------------------------------------------------------------------

    [Test]
    public async Task EvaluateContractAsync_HostSourceCustomSqlProjectingAMaskedColumn_IsRefusedBeforeConnecting()
    {
        var server = Register(DatabaseEngineType.PostgreSQL, new FakeServer { Result = SingleValueRow("actual_value", MaskedValue) });
        var saved = new List<object>();
        var service = CreateService(
            Contract(HostSource(), CustomSqlRule("SELECT 1 AS passed, \"SSN\" AS actual_value FROM \"Customer\"")),
            saved);

        var result = await service.EvaluateContractAsync(42, CancellationToken.None);

        server.Commands.Should().BeEmpty();
        var ruleResult = result.RuleResults.Should().ContainSingle().Subject;
        ruleResult.Message.Should().Be(DataQualityRuleGuard.HostManagedCustomSqlMessage);
        ruleResult.ActualValue.Should().NotContain(MaskedValue);
        StoredRuleResult(saved).ActualValue.Should().NotContain(MaskedValue);
    }

    [Test]
    public async Task EvaluateContractAsync_HostSourcePatternOnAnExposedColumn_RunsWithTheBoundPattern()
    {
        var server = Register(DatabaseEngineType.PostgreSQL, new FakeServer { Result = SingleValueRow("non_matching", 0L) });
        var rule = Rule(DataContractRuleType.Pattern, JsonSerializer.Serialize(new { column = "Name", pattern = "^[A-Z]" }));
        var service = CreateService(Contract(HostSource(), rule, "public", "Customer"), []);

        var result = await service.EvaluateContractAsync(42, CancellationToken.None);

        var command = server.Commands[^1];
        command.Sql.Should().Be("SELECT COUNT(*) AS non_matching FROM \"public\".\"Customer\" WHERE \"Name\" !~ @p0 LIMIT 1");
        command.Parameters.Should().Equal(new Dictionary<string, object?> { ["p0"] = "^[A-Z]" });
        result.RuleResults.Should().ContainSingle().Which.Passed.Should().BeTrue();
    }

    [Test]
    public async Task EvaluateContractAsync_HostSourcePatternOnAMaskedColumn_IsRefusedByTheHostPolicy()
    {
        var server = Register(DatabaseEngineType.PostgreSQL, new FakeServer { Result = SingleValueRow("non_matching", 0L) });
        var rule = Rule(DataContractRuleType.Pattern, JsonSerializer.Serialize(new { column = "SSN", pattern = "^0101" }));
        var service = CreateService(Contract(HostSource(), rule, "public", "Customer"), []);

        var result = await service.EvaluateContractAsync(42, CancellationToken.None);

        server.Commands.Should().BeEmpty("a masked column may not be probed by a predicate");
        result.RuleResults.Should().ContainSingle().Which.Message.Should().StartWith("Rule SQL was rejected:").And.Contain("SSN");
    }

    [Test]
    public async Task EvaluateContractAsync_HostSourceTableThatIsNotExposed_IsRefusedByTheHostPolicy()
    {
        var server = Register(DatabaseEngineType.PostgreSQL, new FakeServer { Result = SingleValueRow("row_count", 5L) });
        var service = CreateService(Contract(HostSource(), Rule(DataContractRuleType.Volume, "{}"), "public", "AuditLog"), []);

        var result = await service.EvaluateContractAsync(42, CancellationToken.None);

        server.Commands.Should().BeEmpty();
        result.RuleResults.Should().ContainSingle().Which.Message.Should().StartWith("Rule SQL was rejected:");
    }

    [Test]
    public async Task EvaluateContractAsync_HostSourceServerError_ReturnsTheGenericHostMessage()
    {
        var server = Register(DatabaseEngineType.PostgreSQL, new FakeServer { Failure = new FakeDbException($"invalid input syntax for type integer: \"{MaskedValue}\"") });
        var saved = new List<object>();
        var service = CreateService(Contract(HostSource(), Rule(DataContractRuleType.Volume, "{}"), "public", "Customer"), saved);

        var result = await service.EvaluateContractAsync(42, CancellationToken.None);

        server.Commands.Should().NotBeEmpty("the generated rule passed the host policy and ran");
        result.RuleResults.Should().ContainSingle().Which.Message.Should().Be(DatabaseProvider.HostQueryFailedMessage);
        StoredRuleResult(saved).Message.Should().Be(DatabaseProvider.HostQueryFailedMessage);
    }

    // --- endpoint wiring ----------------------------------------------------------------------------------------

    [TestCase("CreateDataContract")]
    [TestCase("UpdateDataContract")]
    [TestCase("DeleteDataContract")]
    [TestCase("EvaluateDataContract")]
    public async Task DataContractEndpoint_RequiresTheExecuteScope(string endpointName)
    {
        await using var app = MapBeaconApi();
        var (authorizeData, policy) = await EndpointPolicyAsync(app, endpointName);
        var authorization = app.Services.GetRequiredService<IAuthorizationService>();

        authorizeData
            .Select(x => x.Policy)
            .Should().Contain(BeaconApiEndpoints.ExecuteScopePolicyName);
        (await authorization.AuthorizeAsync(ApiKey("Read"), policy)).Succeeded.Should().BeFalse("a Read-scoped key cannot schedule or run rule SQL");
        (await authorization.AuthorizeAsync(ApiKey("Execute"), policy)).Succeeded.Should().BeTrue();
    }

    [TestCase("EvaluateDataContract", "Viewer")]
    [TestCase("EvaluateDataContract", "Editor")]
    [TestCase("EvaluateDataContract", "Admin")]
    [TestCase("CreateDataContract", "Editor")]
    [TestCase("DeleteDataContract", "Admin")]
    public async Task DataContractEndpoint_CookieSession_PassesTheScopePolicy(string endpointName, string role)
    {
        await using var app = MapBeaconApi();
        var (_, policy) = await EndpointPolicyAsync(app, endpointName);
        Claim[] claims = [new(ClaimTypes.NameIdentifier, "1"), new(ClaimTypes.Role, role)];
        var cookieUser = new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies"));

        var result = await app.Services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(cookieUser, policy);

        result.Succeeded.Should().BeTrue("an interactive session carries no scope; its role is enforced by the permission filter and the handlers");
    }

    // --- helpers ------------------------------------------------------------------------------------------------

    private static IEnumerable<TestCaseData> NonReadOnlyStatements()
    {
        string[] everyEngine =
        [
            WriteBatch,
            "UPDATE accounts SET balance = 0",
            "DELETE FROM accounts",
            "INSERT INTO accounts (id) VALUES (1)",
            "DROP TABLE accounts",
            "CREATE TABLE accounts_copy (id int)",
            "TRUNCATE TABLE accounts",
            "SELECT 1 AS passed; SELECT 2 AS passed"
        ];

        foreach (var engine in GateEngines)
        {
            foreach (var sql in everyEngine)
            {
                yield return new TestCaseData(sql, engine);
            }
        }

        // Data-modifying CTEs.
        yield return new TestCaseData("WITH x AS (DELETE FROM accounts RETURNING id) SELECT id AS passed FROM x", DatabaseEngineType.PostgreSQL);
        yield return new TestCaseData("WITH x AS (INSERT INTO accounts (id) VALUES (1) RETURNING id) SELECT id AS passed FROM x", DatabaseEngineType.PostgreSQL);
        // SELECT ... INTO creates a table.
        yield return new TestCaseData("SELECT * INTO accounts_copy FROM accounts", DatabaseEngineType.PostgreSQL);
        yield return new TestCaseData("SELECT * INTO accounts_copy FROM accounts", DatabaseEngineType.MSSQL);
        yield return new TestCaseData("SELECT 1 AS passed UNION ALL SELECT 2 INTO accounts_copy", DatabaseEngineType.MSSQL);
        // A second statement behind a comment.
        yield return new TestCaseData("SELECT 1 AS passed; /* note */ UPDATE accounts SET balance = 0", DatabaseEngineType.PostgreSQL);
        yield return new TestCaseData("SELECT 1 AS passed /* ; */; DROP TABLE accounts", DatabaseEngineType.MSSQL);
        yield return new TestCaseData("SELECT 1 AS passed -- note\n; DELETE FROM accounts", DatabaseEngineType.MySQL);
        // MySQL ends a block comment at its first */, unlike the parser.
        yield return new TestCaseData("SELECT 1 AS passed /* /* */ ; SELECT 2 AS passed -- */", DatabaseEngineType.MySQL);
        // A MySQL executable comment carrying a write.
        yield return new TestCaseData("/*!50000 UPDATE accounts SET balance = 0 */ SELECT 1 AS passed", DatabaseEngineType.MySQL);
        // EXPLAIN ANALYZE runs the statement it explains.
        yield return new TestCaseData("EXPLAIN ANALYZE DELETE FROM accounts", DatabaseEngineType.PostgreSQL);
    }

    private static DataQualityEvaluationService CreateService(
        DataContract contract,
        List<object> saved,
        TimeSpan? ruleTimeout = null,
        List<DataQualityScore>? scores = null)
    {
        var resolver = new Mock<IDataSourceConnectionResolver>();
        resolver
            .Setup(x => x.GetConnectionString(It.IsAny<DataSource>()))
            .Returns("Server=unused");
        var hostGuard = new HostDataSourceGuard(new SingleSnapshotRegistry());
        var provider = new DatabaseProvider(
            resolver.Object,
            new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance),
            hostGuard,
            NullLogger<DatabaseProvider>.Instance);
        var sets = new Dictionary<Type, object>
        {
            [typeof(DataContract)] = RecordingBeaconContext.MemorySet([contract], saved),
            [typeof(DataQualityEvaluation)] = RecordingBeaconContext.MemorySet(new List<DataQualityEvaluation>(), saved),
            [typeof(DataQualityScore)] = RecordingBeaconContext.MemorySet(scores ?? [], saved)
        };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(sets, saved));

        return new DataQualityEvaluationService(
            factory.Object,
            new DataQualitySqlGenerator(),
            new DataSourceProviderFactory([provider]),
            TestSqlGate.Create(hostGuard: hostGuard),
            NullLogger<DataQualityEvaluationService>.Instance)
        {
            RuleTimeout = ruleTimeout ?? TimeSpan.FromSeconds(60)
        };
    }

    private static DataQualityScore PriorFailingScore()
    {
        return new DataQualityScore
        {
            DataSourceId = 7,
            SchemaName = "sales",
            TableName = "orders",
            Score = 40,
            EvaluatedAt = PriorEvaluatedAt,
            TrendDirection = DataQualityTrendDirection.Degrading
        };
    }

    private static FakeServer Register(DatabaseEngineType engine, FakeServer server)
    {
        DbConnectionFactory.Register(engine, _ => new FakeConnection(server));

        return server;
    }

    private static DataQualityRuleResult StoredRuleResult(List<object> saved)
    {
        return saved
            .OfType<DataQualityEvaluation>()
            .Should().ContainSingle().Subject
            .RuleResults.Should().ContainSingle().Subject;
    }

    private static DataSource OrdinarySource(DatabaseEngineType engine, DataSourceType type = DataSourceType.Database)
    {
        return new DataSource
        {
            Id = 7,
            Name = "warehouse",
            DataSourceType = type,
            EncryptedConnectionData = "unused",
            DatabaseEngineType = engine
        };
    }

    private static DataSource HostSource()
    {
        return new DataSource
        {
            Id = 7,
            Name = "Netgiro",
            DataSourceType = DataSourceType.Database,
            EncryptedConnectionData = "reference",
            DatabaseEngineType = DatabaseEngineType.PostgreSQL,
            HostManagedKey = HostKey
        };
    }

    private static DataContract Contract(DataSource dataSource, DataContractRule rule, string schemaName = "sales", string tableName = "orders")
    {
        return new DataContract
        {
            Id = 42,
            DataSourceId = dataSource.Id,
            DataSource = dataSource,
            SchemaName = schemaName,
            TableName = tableName,
            Name = "contract",
            CronExpression = "0 0 * * *",
            IsEnabled = false,
            Rules = [rule]
        };
    }

    private static DataContractRule CustomSqlRule(string sql, bool isEnabled = true)
    {
        return Rule(DataContractRuleType.CustomSql, JsonSerializer.Serialize(new { sql }), isEnabled);
    }

    private static DataContractRule Rule(DataContractRuleType ruleType, string configuration, bool isEnabled = true)
    {
        return new DataContractRule
        {
            Id = 11,
            DataContractId = 42,
            Name = "rule",
            RuleType = ruleType,
            Configuration = configuration,
            IsEnabled = isEnabled
        };
    }

    private static ClaimsPrincipal ApiKey(string scope)
    {
        Claim[] claims =
        [
            new(ClaimTypes.NameIdentifier, "1"),
            new(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.ApiKeyAuthMethod),
            new(McpCallerClaimTypes.Scope, scope)
        ];

        return new ClaimsPrincipal(new ClaimsIdentity(claims, McpCallerClaimTypes.ApiKeyAuthenticationType));
    }

    private static DataTable CustomSqlRow()
    {
        var table = new DataTable();
        table.Columns.Add("passed", typeof(int));
        table.Columns.Add("actual_value", typeof(string));
        table.Rows.Add(1, RowValue);

        return table;
    }

    private static DataTable SingleValueRow(string column, object value)
    {
        var table = new DataTable();
        table.Columns.Add(column, value.GetType());
        table.Rows.Add(value);

        return table;
    }

    private static async Task<(IReadOnlyList<IAuthorizeData> AuthorizeData, AuthorizationPolicy Policy)> EndpointPolicyAsync(WebApplication app, string endpointName)
    {
        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(x => x.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == endpointName)
            .FirstOrDefault();
        endpoint.Should().NotBeNull();

        var authorizeData = endpoint!.Metadata.GetOrderedMetadata<IAuthorizeData>();
        var policy = await AuthorizationPolicy.CombineAsync(app.Services.GetRequiredService<IAuthorizationPolicyProvider>(), authorizeData);

        return (authorizeData, policy!);
    }

    private static WebApplication MapBeaconApi()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddBeaconApiAuthorization();
        builder.Services.AddBeaconApiServices(x => x.Realtime = false);
        builder.Services.AddAntiforgery();
        // Minimal-API metadata inference only asks "is this a service?": register stand-ins for every service type an
        // endpoint lambda takes so the /beacon/api group materialises.
        builder.Services.AddSingleton(Mock.Of<IMediator>());
        builder.Services.AddSingleton(Mock.Of<IDbContextFactory<BeaconContext>>());
        builder.Services.AddSingleton(Mock.Of<IActorUserResolver>());
        builder.Services.AddSingleton(Mock.Of<IBeaconAuthenticationProvider>());
        builder.Services.AddSingleton(Mock.Of<IUserManagementService>());
        builder.Services.AddSingleton(Mock.Of<IRoleService>());
        builder.Services.AddSingleton(Mock.Of<IBeaconAuthorizationProvider>());
        builder.Services.AddSingleton(Mock.Of<IBeaconUserContext>());

        var app = builder.Build();
        app.MapBeaconApi();

        return app;
    }

    private static ConcurrentDictionary<DatabaseEngineType, Func<string, DbConnection>> GetFactoryRegistry()
    {
        return (ConcurrentDictionary<DatabaseEngineType, Func<string, DbConnection>>)typeof(DbConnectionFactory)
            .GetField("_factories", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;
    }

    /// <summary>Customer is exposed with SSN masked; AuditLog is not exposed; Npgsql dialect.</summary>
    private sealed class SingleSnapshotRegistry : IHostDataSourceRegistry
    {
        private readonly HostExposureSnapshot _snapshot = HostTestModelFactory.Read(
            x => x
                .AllowTables("Customer")
                .MaskColumns(y => y.Name == "SSN"),
            npgsql: true);

        public IReadOnlyList<HostDataSourceRegistration> Registrations => [];

        public HostExposureSnapshot? GetSnapshot(string hostManagedKey) => hostManagedKey == HostKey ? _snapshot : null;
    }

    /// <summary>What the fake database saw and how it answers: every command is recorded; a query returns
    /// <see cref="Result"/>, throws <see cref="Failure"/>, or waits until it is cancelled.</summary>
    private sealed class FakeServer
    {
        public List<(string Sql, DbTransaction? Transaction, Dictionary<string, object?> Parameters)> Commands { get; } = [];
        public DataTable? Result { get; init; }
        public Exception? Failure { get; init; }
        public bool BlockUntilCancelled { get; init; }
    }

    private sealed class FakeDbException(string message) : DbException(message);

    private sealed class FakeConnection(FakeServer server) : DbConnection
    {
        private ConnectionState _state = ConnectionState.Closed;

        public FakeServer Server => server;

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

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new FakeTransaction(this);

        protected override DbCommand CreateDbCommand() => new FakeCommand(this);
    }

    private sealed class FakeTransaction(FakeConnection connection) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.Unspecified;
        protected override DbConnection DbConnection => connection;

        public override void Commit()
        {
        }

        public override void Rollback()
        {
        }
    }

    private sealed class FakeCommand(FakeConnection connection) : DbCommand
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
            Record();
            return 0;
        }

        public override object? ExecuteScalar()
        {
            Record();
            return null;
        }

        protected override DbParameter CreateDbParameter() => new FakeParameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            Record();
            if (connection.Server.Failure != null)
            {
                throw connection.Server.Failure;
            }

            return (connection.Server.Result ?? new DataTable()).CreateDataReader();
        }

        protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
        {
            if (!connection.Server.BlockUntilCancelled)
            {
                return ExecuteDbDataReader(behavior);
            }

            Record();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable: the delay only ends by cancellation.");
        }

        private void Record()
        {
            var parameters = DbParameterCollection
                .Cast<DbParameter>()
                .ToDictionary(x => x.ParameterName, x => x.Value);
            connection.Server.Commands.Add((CommandText, DbTransaction, parameters));
        }
    }

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
}
