using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Beacon.Core.Models;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.Core.Validators;

namespace Beacon.Tests.Unit;

/// <summary>
/// The validator's parser and the engines read comments, whitespace and string escapes differently: the parser nests
/// block comments, skips MySQL executable comments, always treats <c>--</c> as a comment, ends a line comment only at LF,
/// treats Unicode whitespace as a separator and applies its own escape rules. SQL that depends on such a difference
/// would run a statement the validator never saw, so it is refused. The scans ignore quoting, so a marker inside a
/// literal is refused too; the save-time keyword check also reads the body of a MySQL executable comment.
/// </summary>
[TestFixture]
public class SqlReadOnlyValidatorLexicalTests
{
    private const string WhitespaceRejection = "Only spaces, tabs and LF or CRLF line breaks may separate SQL";

    private const string BackslashRejection = "A backslash before a quote is not allowed";

    private const string DoubleDashRejection = "On MySQL, -- starts a comment only when a space follows it";

    private SqlReadOnlyAstValidator _validator = null!;

    [SetUp]
    public void SetUp()
    {
        _validator = new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance);
    }

    [TestCase("MySQL", "SELECT 1 /* /* */ ; /*!DELETE*/ FROM accounts ; -- */")]
    [TestCase("MariaDB", "SELECT 1 /* /* */ ; RENAME TABLE accounts TO x ; -- */")]
    [TestCase("MySQL", "SELECT 1 /* /* */ ; REPLACE INTO accounts(id,balance) VALUES(1,0) ; -- */")]
    [TestCase("Snowflake", "SELECT 1 /* /* */ ; SELECT 2 ; -- */")]
    [TestCase("bigquery", "SELECT 1 /* /* */ ; SELECT 2 ; -- */")]
    [TestCase("databricks", "SELECT 1 /* a /* b */ c */")]
    public void Validate_NestedComment_IsRejectedForEnginesThatDoNotNest(string dialect, string sql)
    {
        _validator.Validate(sql, dialect).Should().StartWith("Nested block comments are not allowed");
    }

    [TestCase("SELECT secret FROM t /*! INTO OUTFILE '/tmp/x' */")]
    [TestCase("SELECT 1 /*! ; DELETE FROM t; -- */")]
    [TestCase("SELECT 1 /*!50000 ; DROP TABLE t */")]
    [TestCase("SELECT * FROM accounts /*! FOR UPDATE */")]
    [TestCase("SELECT 1 AS passed /*!50000 , (SELECT 1) */")]
    [TestCase("SELECT 1 /*M! , 2 */")]
    [TestCase("SELECT /*+ MAX_EXECUTION_TIME(1) */ * FROM t")]
    [TestCase("SELECT '/*!' AS marker_inside_a_literal")]
    public void Validate_MySqlExecutableCommentOrHint_IsRejected(string sql)
    {
        _validator.Validate(sql, "MySQL").Should().StartWith("MySQL executable comments");
    }

    // MySQL needs an ASCII space or control byte after `--` for a comment: `1--1` is 1 - -1, so text the parser skips
    // would run. The scan ignores quoting, so `--` inside a literal counts as well.
    [TestCase("SELECT 1 --1; DELETE FROM t")]
    [TestCase("SELECT 1--1")]
    [TestCase("SELECT 1 --")]
    [TestCase("SELECT 'a--b' AS dashes_inside_a_literal")]
    [TestCase("SELECT 1 --\u0080; DELETE FROM t")]
    public void Validate_MySqlDoubleDashWithoutAsciiSpace_IsRejected(string sql)
    {
        _validator.Validate(sql, "MySQL").Should().StartWith(DoubleDashRejection);
    }

    // The scan cannot tell a comment's own text from code, so a run of dashes after `-- ` is refused as well.
    [Test]
    public void Validate_MySqlCommentFollowedByDashes_IsRejected()
    {
        _validator.Validate("SELECT 1 -- ----- section", "MySQL").Should().StartWith(DoubleDashRejection);
    }

    [TestCase("MySQL", "SELECT 'a\\'; DELETE FROM t; SELECT \\'' AS x")]
    [TestCase("MariaDB", "SELECT \"a\\\"b\" FROM t")]
    [TestCase("bigquery", "SELECT 'x\\', ';/**/CREATE/**/OR/**/REPLACE/**/TABLE ds.t AS SELECT 1; SELECT 1 --'")]
    [TestCase("bigquery", "SELECT r'a\\' AS x, ';DELETE FROM ds.t WHERE true; SELECT ' AS y")]
    [TestCase("databricks", "SELECT 'x\\', ';CREATE TABLE t AS SELECT 1; SELECT 1 --'")]
    [TestCase("MSSQL", "SELECT N'a\\' , 'b' AS x")]
    [TestCase("AzureSynapse", "SELECT X'0A\\' AS x")]
    [TestCase("SQLite", "SELECT N'a\\'' AS x")]
    [TestCase("SQLite", "SELECT 'a\\' AS x")]
    [TestCase("MSSQL", "SELECT 'a\\' AS x")]
    [TestCase("PostgreSQL", "SELECT E'it\\'s' AS x")]
    [TestCase("PostgreSQL", "SELECT 'a\\' AS x")]
    [TestCase("Snowflake", "SELECT 'a\\'b' AS x")]
    public void Validate_BackslashBeforeQuote_IsRejectedInEveryDialect(string dialect, string sql)
    {
        _validator.Validate(sql, dialect).Should().StartWith(BackslashRejection);
    }

    [TestCase("PostgreSQL", "SELECT 1 -- note\r; DELETE FROM t")]
    [TestCase("MSSQL", "SELECT 1 -- note\r; DELETE FROM t")]
    [TestCase("PostgreSQL", "SELECT 1\r")]
    [TestCase("MySQL", "SELECT 1 # note\u2028; DELETE FROM t")]
    [TestCase("Snowflake", "SELECT 1 // note\u0085; DELETE FROM t")]
    [TestCase("SQLite", "SELECT 1\v")]
    [TestCase("MSSQL", "SELECT\f1")]
    [TestCase("MySQL", "SELECT 1 --\u00A0; DELETE FROM t")]
    [TestCase("MySQL", "SELECT 1 --\u3000; DELETE FROM t")]
    [TestCase("PostgreSQL", "SELECT\u00A01")]
    [TestCase("PostgreSQL", "SELECT\u20001")]
    public void Validate_WhitespaceOtherThanSpaceTabLfOrCrLf_IsRejected(string dialect, string sql)
    {
        _validator.Validate(sql, dialect).Should().StartWith(WhitespaceRejection);
    }

    [TestCase("SELECT U&'d\\0061t' AS x")]
    [TestCase("SELECT u&\"col\" FROM t")]
    public void Validate_PostgreSqlUnicodeEscapePrefix_IsRejected(string sql)
    {
        _validator.Validate(sql, "PostgreSQL").Should().StartWith("Unicode-escape strings and identifiers");
    }

    [Test]
    public void Validate_PostgreSqlNonAsciiDollarTag_IsRejected()
    {
        _validator.Validate("SELECT $\u00E9$a$\u00E9$ AS x", "PostgreSQL").Should().StartWith("Dollar-quote tags may only use ASCII");
    }

    // MySQL reads $$a$$ as an identifier, as the parser does, so it has no case here.
    [TestCase("MSSQL")]
    [TestCase("SQLite")]
    [TestCase("bigquery")]
    [TestCase("databricks")]
    public void Validate_DollarQuotedString_IsRejectedOutsidePostgreSqlAndSnowflake(string dialect)
    {
        _validator.Validate("SELECT $$a$$ AS x", dialect).Should().StartWith("Dollar-quoted strings are only allowed");
    }

    [Test]
    public void Validate_SnowflakeTaggedDollarQuote_IsRejected()
    {
        _validator.Validate("SELECT $tag$a$tag$ AS x", "Snowflake").Should().StartWith("Dollar-quoted strings are only allowed");
    }

    // The look-alikes the lexical rules leave alone.
    [TestCase("MySQL", "SELECT a -- note\r\nFROM t")]
    [TestCase("MySQL", "SELECT a # note\nFROM t")]
    [TestCase("MySQL", "SELECT 1 -- a -- b")]
    [TestCase("MySQL", "SELECT '/*' AS x")]
    [TestCase("MySQL", "SELECT /* first */ 1 /* second */")]
    [TestCase("MySQL", "SELECT 'it''s', `a``b` FROM t")]
    [TestCase("MySQL", "SELECT 1 - -1 AS two")]
    [TestCase("MySQL", "SELECT 'C:\\\\path' AS p FROM t WHERE c LIKE '%\\_%'")]
    [TestCase("bigquery", "SELECT 'C:\\\\path' AS p, '\\d' AS r")]
    [TestCase("databricks", "SELECT 'C:\\\\path' AS p FROM t WHERE c LIKE '%\\_%'")]
    [TestCase("PostgreSQL", "SELECT 1--1")]
    [TestCase("PostgreSQL", "SELECT $$a$$ AS x, $tag$b$tag$ AS y")]
    [TestCase("PostgreSQL", "SELECT 1 /* outer /* inner */ still outer */")]
    [TestCase("PostgreSQL", "SELECT 'a\r\nb' AS x /* c\r\nd */\r\nFROM t")]
    [TestCase("MSSQL", "SELECT 'a\r\nb' AS x /* c\r\nd */\r\nFROM t")]
    [TestCase("MSSQL", "SELECT 1 /* outer /* inner */ still outer */")]
    [TestCase("Snowflake", "SELECT 'a\r\nb' AS x /* c\r\nd */\r\nFROM t")]
    [TestCase("Snowflake", "SELECT $$a$$ AS x, 'http://example.com' AS u")]
    [TestCase("bigquery", "SELECT '''multi\nline''' AS x # trailing comment")]
    [TestCase("PostgreSQL", "SELECT\t1")]
    public void Validate_EngineConsistentLexing_Passes(string dialect, string sql)
    {
        _validator.Validate(sql, dialect).Should().BeNull();
    }

    [Test]
    public void FullMcpStack_ReplaceIntoAfterNestedComment_IsBlockedByTheValidator()
    {
        const string sql = "SELECT 1 /* /* */ ; REPLACE INTO accounts(id,balance) VALUES(1,0) ; -- */";

        // The regex guardrail alone accepts it; the validator refuses the nested comment.
        new QueryGuardrailService().ValidateQuery(sql, new QueryGuardrailOptions { ReadOnly = true }).IsValid.Should().BeTrue();
        _validator.Validate(sql, "MySQL").Should().NotBeNull();
    }

    [TestCase("SELECT 1 /* /* */ ; /*!DELETE*/ FROM accounts ; -- */")]
    [TestCase("SELECT 1 /*! ; DELETE FROM t */")]
    [TestCase("SELECT 1 /*!50000 ; DROP TABLE t */")]
    [TestCase("SELECT * FROM t /*M! ; UPDATE canary SET v = v + 1 */")]
    [TestCase("SELECT 1 /*! SELECT 2 */ /*! DELETE FROM t */")]
    [TestCase("SELECT 1 /*! DELETE FROM t")]
    [TestCase("SELECT a -- note\rDELETE FROM t")]
    public void CheckForFlaggedWords_ReadsWhatTheEngineRuns(string sql)
    {
        FluentActions.Invoking(() => QueryValidator.CheckForFlaggedWords(sql))
            .Should().Throw<BeaconException>()
            .WithMessage("Query contains blocked SQL keywords*");
    }

    [TestCase("SELECT a /* DELETE */ FROM t -- DROP\nWHERE b = 1")]
    [TestCase("SELECT a /* DELETE */ FROM t -- DROP\r\nWHERE b = 1")]
    [TestCase("SELECT 'INSERT' AS word FROM t")]
    [TestCase("SELECT a FROM t /* unclosed")]
    public void CheckForFlaggedWords_PlainCommentsAndLiterals_StillIgnored(string sql)
    {
        FluentActions.Invoking(() => QueryValidator.CheckForFlaggedWords(sql)).Should().NotThrow();
    }

    // Paging appends LIMIT/OFFSET to the user's flattened text; an engine line comment would swallow it, so such
    // statements take the bounded streaming fallback instead of a rewrite.
    [TestCase("SELECT * FROM t # all rows", "MySQL")]
    [TestCase("SELECT * FROM t # all rows", "bigquery")]
    [TestCase("SELECT * FROM t // all rows", "Snowflake")]
    [TestCase("SELECT * FROM t -- all rows", "PostgreSQL")]
    public void PagePlan_EngineLineComment_FallsBackToStreaming(string sql, string dialect)
    {
        var plan = SqlPageRewriter.Plan(sql, dialect, 0, 50, null);

        plan.PageSql.Should().BeNull();
        plan.CountSql.Should().BeNull();
    }

    [TestCase("SELECT a # b AS xor FROM t", "PostgreSQL")]
    [TestCase("SELECT * FROM t WHERE url = 'https://example.com'", "MySQL")]
    public void PagePlan_MarkerThatIsNotACommentForTheEngine_StillRewrites(string sql, string dialect)
    {
        SqlPageRewriter.Plan(sql, dialect, 0, 50, null).PageSql.Should().EndWith("LIMIT 50 OFFSET 0");
    }

    // A trailing engine line comment on the last line would swallow a cap appended on that line.
    [TestCase("SELECT * FROM t # note", "MySQL")]
    [TestCase("SELECT * FROM t # note", "bigquery")]
    [TestCase("SELECT * FROM t // note", "Snowflake")]
    [TestCase("SELECT * FROM t -- note", "PostgreSQL")]
    public void RowLimit_TrailingEngineLineComment_PutsTheCapOnANewLine(string sql, string dialect)
    {
        var result = SqlRowLimitRewriter.Apply(sql, 10, dialect);

        result.Outcome.Should().Be(SqlRowLimitOutcome.Applied);
        result.Sql.Should().Be($"{sql}\nLIMIT 10");
    }
}
