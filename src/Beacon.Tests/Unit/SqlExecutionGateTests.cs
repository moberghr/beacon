using System.Collections.ObjectModel;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.Core.Configuration;
using Beacon.Core.Models;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// The shared pre-execution gate (spec <c>sql-execution-gate</c>). Every adversarial read-only case from the
/// 2026-07-03 lesson is re-attacked THROUGH the gate, not just through the validator, because the gate is
/// what the MCP tools actually call.
/// </summary>
[TestFixture]
public class SqlExecutionGateTests
{
    private SqlExecutionGate _gate = null!;

    // A deployment that lets read-only enforcement be switched off (Beacon:Mcp:ForceReadOnly = false).
    private SqlExecutionGate _unlocked = null!;

    [SetUp]
    public void SetUp()
    {
        _gate = TestSqlGate.Create();
        _unlocked = TestSqlGate.Create(deployment: TestSqlGate.Unlocked);
    }

    private static Dictionary<string, HashSet<string>> Catalog() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["orders"] = new(StringComparer.OrdinalIgnoreCase) { "id", "customer_id", "total", "created_time" },
        ["customers"] = new(StringComparer.OrdinalIgnoreCase) { "id", "name", "email" }
    };

    private static SchemaLintContext LintContext() => new([], new Dictionary<string, IReadOnlySet<string>>(), Catalog());

    private static SqlGateRequest Request(string sql, string dialect = "PostgreSQL") =>
        SqlGateRequest.FromSettings(sql, dialect, TestSqlGate.DefaultSettings());

    [TestCase("WITH x AS (INSERT INTO orders (id) VALUES (1) RETURNING id) SELECT * FROM x", "PostgreSQL")]
    [TestCase("SELECT id INTO t2 FROM orders UNION SELECT id FROM customers", "PostgreSQL")]
    [TestCase("EXPLAIN ANALYZE DELETE FROM orders", "PostgreSQL")]
    [TestCase("SELECT 1; SELECT 2", "PostgreSQL")]
    [TestCase("SELECT listagg(x, ',') WITHIN GROUP (ORDER BY !!!) FROM", "Snowflake")]
    public void Evaluate_ReadOnlySideDoors_AreBlockedAndNothingElseRuns(string sql, string dialect)
    {
        var report = _gate.Evaluate(Request(sql, dialect) with { Catalog = Catalog(), LintContext = LintContext(), MaxRows = 10 });

        report.Blocked.Should().BeTrue();
        report.BlockReason.Should().NotBeNullOrWhiteSpace();
        report.Verdicts.ReadOnly.Status.Should().Be(SqlGateStatus.Fail);
        report.Verdicts.ReadOnly.Code.Should().BeOneOf("guardrail", "ast");
        report.Verdicts.Schema.Code.Should().Be("not_evaluated");
        report.Verdicts.Lint.Code.Should().Be("not_evaluated");
        report.Verdicts.RowLimit.Code.Should().Be("not_evaluated");
        report.FinalSql.Should().Be(sql);
    }

    [Test]
    public void Evaluate_ParseFailure_FailsClosedOnAst()
    {
        var report = _gate.Evaluate(Request("SELECT listagg(x, ',') WITHIN GROUP (ORDER BY !!!) FROM", "Snowflake"));

        report.Blocked.Should().BeTrue();
        report.Verdicts.ReadOnly.Code.Should().Be("ast");
    }

    [Test]
    public void Evaluate_WhitespaceOnly_FailsWithEmptyCode()
    {
        var report = _gate.Evaluate(Request("   \n"));

        report.Blocked.Should().BeTrue();
        report.Verdicts.ReadOnly.Code.Should().Be("empty");
    }

    [Test]
    public void Evaluate_ValidSelect_AllRequested_PassesAndCaps()
    {
        var report = _gate.Evaluate(Request("SELECT id, total FROM orders") with
        {
            Catalog = Catalog(),
            LintContext = LintContext(),
            MaxRows = 50
        });

        report.Blocked.Should().BeFalse();
        report.BlockReason.Should().BeNull();
        report.Verdicts.ReadOnly.Should().Be(SqlGateVerdict.Passed);
        report.Verdicts.Schema.Should().Be(SqlGateVerdict.Passed);
        report.Verdicts.Lint.Status.Should().Be(SqlGateStatus.Pass);
        report.LintFindings.Should().BeEmpty();
        report.Verdicts.RowLimit.Status.Should().Be(SqlGateStatus.Pass);
        report.FinalSql.Should().Be("SELECT id, total FROM orders LIMIT 50");
        report.TablesUsed.Should().Equal("orders");
        report.ColumnsUsed.Should().Contain("id").And.Contain("total");
    }

    [Test]
    public void Evaluate_NoCatalog_StillReportsTablesUsed()
    {
        var sql = "WITH r AS (SELECT * FROM public.orders o) SELECT c.name FROM r JOIN customers c ON c.id = r.customer_id";

        var report = _gate.Evaluate(Request(sql));

        report.Blocked.Should().BeFalse();
        report.Verdicts.Schema.Code.Should().Be("not_requested");
        report.TablesUsed.Should().Equal("public.orders", "customers");
    }

    [Test]
    public void Evaluate_EmptyCatalog_SchemaSkippedEmptyCatalog_NeverBlocks()
    {
        var report = _gate.Evaluate(Request("SELECT bogus FROM anything") with
        {
            Catalog = new Dictionary<string, HashSet<string>>(),
            BlockOnSchemaFailure = true
        });

        report.Blocked.Should().BeFalse();
        report.Verdicts.Schema.Status.Should().Be(SqlGateStatus.Skipped);
        report.Verdicts.Schema.Code.Should().Be("empty_catalog");
        report.Verdicts.Schema.Message.Should().Contain("column check was skipped");
    }

    [Test]
    public void Evaluate_UnknownColumn_BlockOnSchemaFailure_Blocks()
    {
        var report = _gate.Evaluate(Request("SELECT bogus FROM orders") with
        {
            Catalog = Catalog(),
            BlockOnSchemaFailure = true,
            MaxRows = 10
        });

        report.Blocked.Should().BeTrue();
        report.BlockReason.Should().Contain("bogus");
        report.Verdicts.Schema.Status.Should().Be(SqlGateStatus.Fail);
        report.Verdicts.Schema.Code.Should().Be("schema");
        report.Verdicts.RowLimit.Code.Should().Be("not_evaluated");
        report.FinalSql.Should().Be("SELECT bogus FROM orders");
    }

    [Test]
    public void Evaluate_UnknownColumn_Advisory_NotBlocked_RowLimitStillApplied()
    {
        var report = _gate.Evaluate(Request("SELECT bogus FROM orders") with
        {
            Catalog = Catalog(),
            BlockOnSchemaFailure = false,
            MaxRows = 10
        });

        report.Blocked.Should().BeFalse();
        report.Verdicts.Schema.Status.Should().Be(SqlGateStatus.Fail);
        report.FinalSql.Should().EndWith("LIMIT 10");
    }

    [Test]
    public void Evaluate_LintDisabled_SkippedDisabled()
    {
        var settings = TestSqlGate.DefaultSettings();
        settings.EnableSemanticLint = false;

        var report = _gate.Evaluate(SqlGateRequest.FromSettings("SELECT id FROM orders", "PostgreSQL", settings) with
        {
            LintContext = LintContext()
        });

        report.Verdicts.Lint.Code.Should().Be("disabled");
        report.LintFindings.Should().BeEmpty();
    }

    // Read-only enforcement off lets unparseable SQL reach the lint stage, which then has no opinion rather than a pass. The
    // in-memory SQLite join reaches no other database, so its SQL skips the cross-database stage, which would block it.
    [Test]
    public void Evaluate_UnparseableSqlWithReadOnlyOff_LintIsSkippedNotPassed()
    {
        var report = _unlocked.Evaluate(Request("SELECT FROM WHERE", "SQLite") with
        {
            EnforceReadOnly = false,
            InMemoryJoin = true,
            LintContext = LintContext()
        });

        report.Verdicts.Lint.Status.Should().Be(SqlGateStatus.Skipped);
        report.Verdicts.Lint.Code.Should().Be(SqlGateCodes.ParseFailed);
        report.LintFindings.Should().BeEmpty();
    }

    [Test]
    public void Evaluate_LintNotRequested_SkippedNotRequested()
    {
        var report = _gate.Evaluate(Request("SELECT id FROM orders"));

        report.Verdicts.Lint.Code.Should().Be("not_requested");
        report.Verdicts.RowLimit.Code.Should().Be("not_requested");
    }

    [Test]
    public void Evaluate_RowLimit_AlreadyLimited()
    {
        var report = _gate.Evaluate(Request("SELECT id FROM orders LIMIT 5") with { MaxRows = 100 });

        report.Verdicts.RowLimit.Code.Should().Be("already_limited");
        report.FinalSql.Should().Be("SELECT id FROM orders LIMIT 5");
    }

    [Test]
    public void Evaluate_ReadOnlyDisabled_StillRejectsBlank()
    {
        var settings = TestSqlGate.DefaultSettings();
        settings.EnforceReadOnly = false;

        var report = _unlocked.Evaluate(SqlGateRequest.FromSettings("  ", "PostgreSQL", settings));

        report.Blocked.Should().BeTrue();
        report.Verdicts.ReadOnly.Code.Should().Be("empty");
    }

    [Test]
    public void Evaluate_ReadOnlyDisabled_StillDetectsPii()
    {
        var settings = TestSqlGate.DefaultSettings();
        settings.EnforceReadOnly = false;

        var report = _unlocked.Evaluate(SqlGateRequest.FromSettings("SELECT email FROM customers", "PostgreSQL", settings));

        report.Blocked.Should().BeFalse();
        report.Verdicts.ReadOnly.Code.Should().Be("disabled");
        report.PiiColumns.Should().Contain("email");
    }

    [Test]
    public void Evaluate_ReadOnlyDisabled_DoesNotBlockWrites()
    {
        var settings = TestSqlGate.DefaultSettings();
        settings.EnforceReadOnly = false;

        var report = _unlocked.Evaluate(SqlGateRequest.FromSettings("DELETE FROM orders", "PostgreSQL", settings) with { Catalog = Catalog() });

        // Read-only enforcement switched off is honoured only where the deployment unlocks it.
        report.Blocked.Should().BeFalse();
        report.Verdicts.ReadOnly.Code.Should().Be("disabled");
    }

    [Test]
    public void Evaluate_UnderTheDefaultReadOnlyLock_ACallerTurningReadOnlyOff_IsStillJudgedReadOnly()
    {
        var settings = TestSqlGate.DefaultSettings();
        settings.EnforceReadOnly = false;

        var report = _gate.Evaluate(SqlGateRequest.FromSettings("DELETE FROM orders", "PostgreSQL", settings) with { Catalog = Catalog() });

        report.Blocked.Should().BeTrue("Beacon:Mcp:ForceReadOnly is on unless the deployment turns it off");
        report.Verdicts.ReadOnly.Status.Should().Be(SqlGateStatus.Fail);
        report.BlockReason.Should().Contain("deployment lock Beacon:Mcp:ForceReadOnly");
    }

    [Test]
    public void Evaluate_ARefusalTheReadOnlyLockCausesOverStoredSettings_NamesTheLock_AndNoOtherRefusalDoes()
    {
        var stored = new McpSettingsData { EnforceReadOnly = false };
        var resolved = Beacon.Core.Services.McpSettingsProvider.Resolve(stored, null, new McpDeploymentOptions()).Effective;

        var write = _gate.Evaluate(SqlGateRequest.FromSettings("DELETE FROM orders", "PostgreSQL", resolved));
        var empty = _gate.Evaluate(SqlGateRequest.FromSettings("   ", "PostgreSQL", resolved));
        var enforcedAnyway = _gate.Evaluate(SqlGateRequest.FromSettings("DELETE FROM orders", "PostgreSQL", TestSqlGate.DefaultSettings()));

        resolved.EnforceReadOnly.Should().BeTrue();
        resolved.ReadOnlyPinnedByLock.Should().BeTrue();
        write.Blocked.Should().BeTrue();
        write.BlockReason.Should().Contain("deployment lock Beacon:Mcp:ForceReadOnly");
        empty.Blocked.Should().BeTrue();
        empty.BlockReason.Should().NotContain("deployment lock", "a blank statement is refused whatever read-only says");
        enforcedAnyway.Blocked.Should().BeTrue();
        enforcedAnyway.BlockReason.Should().NotContain("deployment lock", "the stored settings enforce read-only themselves");
    }

    [Test]
    public void Evaluate_UnderTheDefaultReadOnlyLock_TheInMemoryJoin_IsJudgedReadOnly()
    {
        var settings = TestSqlGate.DefaultSettings();
        settings.EnforceReadOnly = false;

        var locked = _gate.Evaluate(SqlGateRequest.ForInMemoryJoin("SELECT * FROM result1; ATTACH DATABASE '/tmp/x.db' AS x", settings));
        var unlocked = _unlocked.Evaluate(SqlGateRequest.ForInMemoryJoin("SELECT * FROM result1; ATTACH DATABASE '/tmp/x.db' AS x", settings));

        locked.Blocked.Should().BeTrue();
        locked.Verdicts.ReadOnly.Status.Should().Be(SqlGateStatus.Fail);
        unlocked.Verdicts.ReadOnly.Code.Should().Be(SqlGateCodes.Disabled, "only an unlocked deployment lets the settings switch it off");
    }

    [Test]
    public void FromSettings_CopiesFlags()
    {
        var settings = new McpSettingsData
        {
            EnforceReadOnly = false,
            EnablePiiDetection = false,
            CustomPiiPatterns = ["foo"],
            EnableSemanticLint = false
        };

        var request = SqlGateRequest.FromSettings("SELECT 1", "MSSQL", settings);

        request.EnforceReadOnly.Should().BeFalse();
        request.DetectPii.Should().BeFalse();
        request.CustomPiiPatterns.Should().Equal("foo");
        request.EnableSemanticLint.Should().BeFalse();
        request.Catalog.Should().BeNull();
        request.LintContext.Should().BeNull();
        request.MaxRows.Should().BeNull();
        request.BlockOnSchemaFailure.Should().BeFalse();
        request.Dialect.Should().Be("MSSQL");
    }

    [Test]
    public void FromSettings_EmptyCustomPatterns_BecomeNull()
    {
        var request = SqlGateRequest.FromSettings("SELECT 1", null, TestSqlGate.DefaultSettings());

        request.CustomPiiPatterns.Should().BeNull();
    }

    [Test]
    public void Evaluate_GuardrailMock_IsHonoured()
    {
        var guardrail = new Mock<IQueryGuardrailService>();
        guardrail
            .Setup(x => x.ValidateQuery(It.IsAny<string>(), It.IsAny<QueryGuardrailOptions>()))
            .Returns(new QueryValidationResult(false, "nope"));
        var gate = TestSqlGate.Create(guardrail.Object);

        var report = gate.Evaluate(Request("SELECT id FROM orders"));

        report.Blocked.Should().BeTrue();
        report.Verdicts.ReadOnly.Code.Should().Be("guardrail");
        report.Verdicts.ReadOnly.Message.Should().Be("nope");
    }

    [Test]
    public void Evaluate_TSqlDialect_CapsWithTop()
    {
        var report = _gate.Evaluate(Request("SELECT id FROM orders", "AzureSynapse") with { MaxRows = 25 });

        report.Blocked.Should().BeFalse();
        report.FinalSql.Should().Be("SELECT TOP 25 id FROM orders");
    }

    [Test]
    public void Evaluate_UnparseableSql_ReadOnlyDisabled_RowLimitTextualFallback()
    {
        var settings = TestSqlGate.DefaultSettings();
        settings.EnforceReadOnly = false;

        // The in-memory SQLite join skips the cross-database stage, which would block SQL it cannot parse.
        var report = _unlocked.Evaluate(SqlGateRequest.ForInMemoryJoin("SELECT ??? FROM", settings) with { MaxRows = 100 });

        report.Blocked.Should().BeFalse();
        report.Verdicts.RowLimit.Status.Should().Be(SqlGateStatus.Pass);
        report.Verdicts.RowLimit.Code.Should().Be(SqlGateCodes.TextualFallback);
        report.Verdicts.RowLimit.Message.Should().Contain("could not be parsed");
        report.FinalSql.Should().EndWith(" LIMIT 100");
    }

    // A requested cap of zero or below blocks the statement: the row-limit stage fails closed, never "no cap".
    [TestCase(0)]
    [TestCase(-1)]
    public void Evaluate_NonPositiveMaxRows_Blocked(int maxRows)
    {
        var report = _gate.Evaluate(Request("SELECT id FROM orders") with { MaxRows = maxRows });

        report.Blocked.Should().BeTrue();
        report.BlockReason.Should().Be(SqlRowLimitRewriter.InvalidRowCapMessage);
        report.Verdicts.RowLimit.Status.Should().Be(SqlGateStatus.Fail);
        report.Verdicts.RowLimit.Code.Should().Be(SqlGateCodes.InvalidRowCap);
    }

    [Test]
    public void Evaluate_ExplainTable_RowLimitNotApplicable()
    {
        // DESCRIBE never passes the guardrail's SELECT/WITH/EXPLAIN prefix rule; EXPLAIN <table> does, and parses
        // as ExplainTable — metadata inspection with no result set to cap.
        var report = _gate.Evaluate(Request("EXPLAIN orders", "MySQL") with { MaxRows = 100 });

        report.Blocked.Should().BeFalse();
        report.Verdicts.RowLimit.Code.Should().Be(SqlGateCodes.NotApplicable);
        report.FinalSql.Should().Be("EXPLAIN orders");
    }

    [Test]
    public void Evaluate_ReadOnlyCatalogWrapper_SchemaStillChecked()
    {
        IReadOnlyDictionary<string, HashSet<string>> wrapped = new ReadOnlyDictionary<string, HashSet<string>>(Catalog());

        var report = _gate.Evaluate(Request("SELECT bogus FROM orders") with { Catalog = wrapped, BlockOnSchemaFailure = true });

        report.Blocked.Should().BeTrue();
        report.Verdicts.Schema.Code.Should().Be(SqlGateCodes.Schema);
        report.BlockReason.Should().Contain("bogus");
    }

    [Test]
    public void Evaluate_AdvisorySchemaFailure_LintStillRuns()
    {
        var report = _gate.Evaluate(Request("SELECT bogus FROM orders") with
        {
            Catalog = Catalog(),
            LintContext = LintContext(),
            BlockOnSchemaFailure = false
        });

        report.Blocked.Should().BeFalse();
        report.Verdicts.Schema.Status.Should().Be(SqlGateStatus.Fail);
        report.Verdicts.Lint.Status.Should().Be(SqlGateStatus.Pass, "lint is evaluated on advisory schema failures so the ask repair loop can compare findings");
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Evaluate_SqlOverDefaultCap_FailsSqlTooLong_BeforeAnyRegexRuns(bool enforceReadOnly)
    {
        var guardrail = new Mock<IQueryGuardrailService>();
        var gate = TestSqlGate.Create(guardrail.Object, deployment: TestSqlGate.Unlocked);
        var settings = TestSqlGate.DefaultSettings();
        settings.EnforceReadOnly = enforceReadOnly;
        var sql = "SELECT " + new string('1', 100_001 - "SELECT ".Length);

        var report = gate.Evaluate(SqlGateRequest.FromSettings(sql, "PostgreSQL", settings) with
        {
            Catalog = Catalog(),
            LintContext = LintContext(),
            MaxRows = 10
        });

        sql.Length.Should().Be(100_001);
        report.Blocked.Should().BeTrue();
        report.Verdicts.ReadOnly.Status.Should().Be(SqlGateStatus.Fail);
        report.Verdicts.ReadOnly.Code.Should().Be(SqlGateCodes.SqlTooLong);
        report.Verdicts.Schema.Code.Should().Be(SqlGateCodes.NotEvaluated);
        report.Verdicts.Lint.Code.Should().Be(SqlGateCodes.NotEvaluated);
        report.Verdicts.RowLimit.Code.Should().Be(SqlGateCodes.NotEvaluated);
        report.BlockReason.Should().Contain("100000").And.NotContain("SELECT", "the reason carries counts, never SQL text (§1.11)");
        report.FinalSql.Should().Be(sql);
        report.TablesUsed.Should().BeEmpty();
        guardrail.Verify(x => x.ValidateQuery(It.IsAny<string>(), It.IsAny<QueryGuardrailOptions>()), Times.Never);
    }

    [Test]
    public void Evaluate_SqlAtDefaultCap_PassesTheLengthStage()
    {
        var guardrail = new Mock<IQueryGuardrailService>();
        guardrail
            .Setup(x => x.ValidateQuery(It.IsAny<string>(), It.IsAny<QueryGuardrailOptions>()))
            .Returns(new QueryValidationResult(true));
        var gate = TestSqlGate.Create(guardrail.Object);
        var sql = "SELECT 1" + new string(' ', 100_000 - "SELECT 1".Length);

        var report = gate.Evaluate(Request(sql));

        sql.Length.Should().Be(100_000);
        report.Blocked.Should().BeFalse();
        report.Verdicts.ReadOnly.Status.Should().Be(SqlGateStatus.Pass);
        guardrail.Verify(x => x.ValidateQuery(sql, It.IsAny<QueryGuardrailOptions>()), Times.Once);
    }

    [Test]
    public void Evaluate_ConfiguredMaxSqlChars_IsHonoured()
    {
        var options = Options.Create(new McpDeploymentOptions { Ceilings = new McpCeilingOptions { MaxSqlChars = 20 } });
        var gate = new SqlExecutionGate(
            new QueryGuardrailService(),
            new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance),
            new SqlSchemaValidator(),
            new SqlSemanticLinter(),
            NullLogger<SqlExecutionGate>.Instance,
            deploymentOptions: options);

        var over = gate.Evaluate(Request("SELECT id FROM orders"));
        var atCap = gate.Evaluate(Request("SELECT id FROM items"));

        over.Blocked.Should().BeTrue();
        over.Verdicts.ReadOnly.Code.Should().Be(SqlGateCodes.SqlTooLong);
        over.BlockReason.Should().Contain("20");
        atCap.Blocked.Should().BeFalse();
        atCap.Verdicts.ReadOnly.Status.Should().Be(SqlGateStatus.Pass);
    }

    // MySQL ends a block comment at its first `*/`; the parser would read the text after it as still commented.
    [TestCase("SELECT 1 /* /* */ ; DELETE FROM orders; -- */")]
    [TestCase("SELECT 1 /* /* */ ; DELETE FROM orders; # */")]
    [TestCase("SELECT 1 /* # /* */ ; DELETE FROM orders\n# */")]
    [TestCase("SELECT 1 /*! /* */ ; DELETE FROM orders; -- */")]
    [TestCase("SELECT 1 /*!50000 /* */ ; DELETE FROM orders; -- */")]
    [TestCase("SELECT 1 /*+ /* */ ; DELETE FROM orders; -- */")]
    [TestCase("SELECT 1 /* /* */ ; DELETE FROM orders; /* */")]
    [TestCase("SELECT id FROM orders /*! ; DELETE FROM orders */")]
    [TestCase("SELECT id FROM orders /*+ MAX_EXECUTION_TIME(1000) */")]
    public void Evaluate_MySqlNestedBlockComment_IsBlocked(string sql)
    {
        var report = _gate.Evaluate(Request(sql, "MySQL") with { MaxRows = 10 });

        report.Blocked.Should().BeTrue();
        report.Verdicts.ReadOnly.Status.Should().Be(SqlGateStatus.Fail);
        report.Verdicts.ReadOnly.Code.Should().BeOneOf("guardrail", "ast");
        report.FinalSql.Should().Be(sql);
    }

    [TestCase("SELECT '/* not a comment' AS note FROM orders")]
    [TestCase("SELECT id /* first */ FROM orders /* second */")]
    public void Evaluate_MySqlCommentLikeTextThatIsNotNested_Passes(string sql)
    {
        var report = _gate.Evaluate(Request(sql, "MySQL") with { MaxRows = 10 });

        report.Blocked.Should().BeFalse();
        report.Verdicts.ReadOnly.Status.Should().Be(SqlGateStatus.Pass);
    }
}
