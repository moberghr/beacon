using Beacon.Core.Data.Enums;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Grounding;

/// <summary>
/// The check SQL passes before Beacon stores it as grounding: comments are removed in the data source's dialect, SQL whose
/// comments cannot be removed is refused (comment characters inside a string literal the tokenizer reads as one are kept),
/// SQL holding characters that do not show is refused, a data source without a catalog refuses everything, and the
/// comment-free SQL passes the read-only gate and the catalog check. In-memory sets, no database (§4.7); nothing runs.
/// </summary>
[TestFixture]
public class GroundingSqlCheckTests
{
    private const int DataSourceId = TestGroundingSqlChecks.DataSourceId;

    [TestCase("-- Orders of the day\nSELECT id FROM orders", "SELECT id FROM orders")]
    [TestCase("SELECT id FROM orders -- a remark for the reader", "SELECT id FROM orders")]
    [TestCase("SELECT id /* the ids */ FROM orders", "SELECT id   FROM orders")]
    [TestCase("SELECT id /* outer /* nested */ still outer */ FROM orders", "SELECT id   FROM orders")]
    [TestCase("SELECT o.id, o.status FROM public.orders o WHERE o.total > 10", "SELECT o.id, o.status FROM public.orders o WHERE o.total > 10")]
    public async Task SqlThatMayBeStored_ComesBackWithoutComments(string sql, string expected)
    {
        var result = await TestGroundingSqlChecks.Real().CheckAsync(DataSourceId, sql, CancellationToken.None);

        result.IsAllowed.Should().BeTrue(result.Refusal);
        result.Sql.Should().Be(expected);
    }

    // Comment characters inside a string literal are text the statement compares, not a comment: kept as written, comments
    // elsewhere in the same SQL still removed.
    [TestCase(DatabaseEngineType.PostgreSQL, "SELECT id FROM orders WHERE status = 'a--b'", "SELECT id FROM orders WHERE status = 'a--b'")]
    [TestCase(DatabaseEngineType.PostgreSQL, "SELECT id FROM orders WHERE status = '/* not a comment */'", "SELECT id FROM orders WHERE status = '/* not a comment */'")]
    [TestCase(DatabaseEngineType.PostgreSQL, "SELECT id FROM orders WHERE status = 'it''s -- fine' -- a remark", "SELECT id FROM orders WHERE status = 'it''s -- fine'")]
    [TestCase(DatabaseEngineType.MySQL, "SELECT id FROM orders WHERE status = '#1'", "SELECT id FROM orders WHERE status = '#1'")]
    public async Task CommentCharactersInsideAStringLiteral_AreKept(DatabaseEngineType engine, string sql, string expected)
    {
        var result = await TestGroundingSqlChecks.Real(engine).CheckAsync(DataSourceId, sql, CancellationToken.None);

        result.IsAllowed.Should().BeTrue(result.Refusal);
        result.Sql.Should().Be(expected);
    }

    // A comment the stripper cannot remove — an executable comment, a comment it cannot read — refuses the SQL, and so does a
    // literal the tokenizer cannot read as the engine does (MySQL reads -- without a space differently): what is stored as
    // grounding never carries a comment. The refusal says that comment characters inside a string literal are allowed.
    [TestCase(DatabaseEngineType.MySQL, "SELECT id /*!50000 , status */ FROM orders")]
    [TestCase(DatabaseEngineType.PostgreSQL, "SELECT id /* never closed FROM orders")]
    [TestCase(DatabaseEngineType.PostgreSQL, "SELECT id FROM orders WHERE status = 'a--b")]
    [TestCase(DatabaseEngineType.MySQL, "SELECT id FROM orders WHERE status = 'a--b'")]
    public async Task SqlWhoseCommentsCannotBeRemoved_IsRefused(DatabaseEngineType engine, string sql)
    {
        var result = await TestGroundingSqlChecks.Real(engine).CheckAsync(DataSourceId, sql, CancellationToken.None);

        result.IsAllowed.Should().BeFalse();
        result.Sql.Should().BeNull();
        result.Refusal.Should().Be(GroundingSqlCheck.CommentRefusal);
        result.Refusal.Should().Contain("string literal").And.NotContain(sql);
    }

    // Characters a reviewer cannot see — format characters (bidirectional controls, zero-width characters, the byte order
    // mark), tag characters, variation selectors — are refused wherever they stand, a literal or a comment included.
    [TestCase("SELECT id FROM orders WHERE status = 'a\u202Eb'")]
    [TestCase("SELECT id FROM orders WHERE status = 'a\u2066b\u2069'")]
    [TestCase("SELECT id\u200B FROM orders")]
    [TestCase("SELECT id FROM orders -- \u200Dnote")]
    [TestCase("\uFEFFSELECT id FROM orders")]
    [TestCase("SELECT id FROM orders WHERE status = 'a\U000E0041b'")]
    [TestCase("SELECT id FROM orders WHERE status = 'a\uFE0Fb'")]
    [TestCase("SELECT id FROM orders WHERE status = 'a\U000E0100b'")]
    public async Task SqlHoldingCharactersThatDoNotShow_IsRefused(string sql)
    {
        var reads = 0;

        var result = await TestGroundingSqlChecks.Real(onRead: () => reads++).CheckAsync(DataSourceId, sql, CancellationToken.None);

        result.Refusal.Should().Be(GroundingSqlCheck.HiddenCharacterRefusal);
        reads.Should().Be(0, "refused before anything is read");
    }

    // Without a catalog there is nothing to check the SQL against: the catalog stage would pass any column.
    [Test]
    public async Task ADataSourceWithoutACatalog_RefusesEvenSqlThatWouldRun()
    {
        var result = await TestGroundingSqlChecks.Real(withCatalog: false).CheckAsync(DataSourceId, "SELECT anything FROM anywhere", CancellationToken.None);

        result.Refusal.Should().Be(GroundingSqlCheck.NoCatalogRefusal);
        result.Refusal.Should().Contain("Extract metadata for this data source first");
    }

    [Test]
    public async Task ADataSourceWithoutACatalog_StillRefusesSqlThatCouldNotRun_ForThatReason()
    {
        var result = await TestGroundingSqlChecks.Real(withCatalog: false).CheckAsync(DataSourceId, "DELETE FROM orders", CancellationToken.None);

        result.Refusal.Should().NotBe(GroundingSqlCheck.NoCatalogRefusal);
    }

    [Test]
    public async Task ALineCommentTheDialectReads_IsRemoved()
    {
        var result = await TestGroundingSqlChecks.Real(DatabaseEngineType.MySQL)
            .CheckAsync(DataSourceId, "SELECT id FROM orders # a remark", CancellationToken.None);

        result.IsAllowed.Should().BeTrue(result.Refusal);
        result.Sql.Should().Be("SELECT id FROM orders");
    }

    [TestCase("DELETE FROM orders")]
    [TestCase("/* tidy up */ DELETE FROM orders")]
    [TestCase("SELECT id FROM orders; SELECT 2")]
    [TestCase("SELECT missing_column FROM orders")]
    public async Task SqlThatCouldNotRunReadOnly_OrNamesWhatTheSourceLacks_IsRefused(string sql)
    {
        var result = await TestGroundingSqlChecks.Real().CheckAsync(DataSourceId, sql, CancellationToken.None);

        result.IsAllowed.Should().BeFalse();
        result.Sql.Should().BeNull();
        result.Refusal.Should().NotBe(GroundingSqlCheck.CommentRefusal);
    }

    // Each dialect's comments are read by its own tokenizer and removed: T-SQL nests block comments and reads [brackets] as
    // identifiers; Snowflake ends a line at //; BigQuery at #; SQLite and Databricks at --.
    [TestCase(DataSourceType.Database, DatabaseEngineType.MSSQL, "SELECT [id] FROM [orders] -- the ids", "SELECT [id] FROM [orders]")]
    [TestCase(DataSourceType.Database, DatabaseEngineType.MSSQL, "SELECT id /* outer /* nested */ still outer */ FROM orders", "SELECT id   FROM orders")]
    [TestCase(DataSourceType.Database, DatabaseEngineType.AzureSynapse, "SELECT id /* outer /* nested */ still outer */ FROM orders -- the ids", "SELECT id   FROM orders")]
    [TestCase(DataSourceType.Database, DatabaseEngineType.SQLite, "/* all of them */ SELECT id FROM orders -- the ids", "SELECT id FROM orders")]
    [TestCase(DataSourceType.Database, DatabaseEngineType.Snowflake, "SELECT id FROM orders // the ids", "SELECT id FROM orders")]
    [TestCase(DataSourceType.BigQuery, null, "SELECT id FROM orders # the ids", "SELECT id FROM orders")]
    [TestCase(DataSourceType.Databricks, null, "SELECT id FROM orders -- the ids", "SELECT id FROM orders")]
    public async Task EachDialectsComments_AreRemoved(DataSourceType type, DatabaseEngineType? engine, string sql, string expected)
    {
        var result = await TestGroundingSqlChecks.RealFor(type, engine).CheckAsync(DataSourceId, sql, CancellationToken.None);

        result.IsAllowed.Should().BeTrue(result.Refusal);
        result.Sql.Should().Be(expected);
        result.Dialect.Should().NotBeNull();
    }

    // Comment characters inside a string literal are kept where the dialect's tokenizer reads them as part of the literal.
    [TestCase(DataSourceType.Database, DatabaseEngineType.MSSQL, "SELECT id FROM orders WHERE status = N'a--b'")]
    [TestCase(DataSourceType.Database, DatabaseEngineType.AzureSynapse, "SELECT id FROM orders WHERE status = 'a/*b'")]
    [TestCase(DataSourceType.Database, DatabaseEngineType.SQLite, "SELECT id FROM orders WHERE status = 'a--b'")]
    [TestCase(DataSourceType.Database, DatabaseEngineType.Snowflake, "SELECT id FROM orders WHERE status = 'a//b'")]
    [TestCase(DataSourceType.BigQuery, null, "SELECT id FROM orders WHERE status = 'a#b'")]
    [TestCase(DataSourceType.Databricks, null, "SELECT id FROM orders WHERE status = 'a--b'")]
    public async Task CommentCharactersInsideEachDialectsLiterals_AreKept(DataSourceType type, DatabaseEngineType? engine, string sql)
    {
        var result = await TestGroundingSqlChecks.RealFor(type, engine).CheckAsync(DataSourceId, sql, CancellationToken.None);

        result.IsAllowed.Should().BeTrue(result.Refusal);
        result.Sql.Should().Be(sql);
    }

    // What a dialect's tokenizer does not read as a removable comment, or as a literal holding the marker, is refused: a
    // block comment nested on an engine that ends it at its first */ (SQLite, MySQL), a marker inside a [bracketed]
    // identifier, MySQL's '--' without a space, and a data source of no known dialect (nothing can be read safely).
    [TestCase(DataSourceType.Database, DatabaseEngineType.SQLite, "SELECT id /* outer /* nested */ FROM orders")]
    [TestCase(DataSourceType.Database, DatabaseEngineType.MySQL, "SELECT id /* outer /* nested */ FROM orders")]
    [TestCase(DataSourceType.Database, DatabaseEngineType.MSSQL, "SELECT [a--b] FROM orders")]
    [TestCase(DataSourceType.Database, DatabaseEngineType.MySQL, "SELECT id FROM orders WHERE status = 'a--b'")]
    [TestCase(DataSourceType.Database, null, "SELECT id FROM orders -- the ids")]
    [TestCase(DataSourceType.Database, null, "SELECT id FROM orders WHERE status = 'a--b'")]
    public async Task WhatADialectCannotReadSafely_IsRefused(DataSourceType type, DatabaseEngineType? engine, string sql)
    {
        var result = await TestGroundingSqlChecks.RealFor(type, engine).CheckAsync(DataSourceId, sql, CancellationToken.None);

        result.Refusal.Should().Be(GroundingSqlCheck.CommentRefusal);
        result.Sql.Should().BeNull();
    }

    // A control character, NUL or a noncharacter (U+FFFF ends the tokenizer's input) in a comment: the comment cannot be
    // removed safely, so the SQL is refused.
    [TestCase("SELECT id FROM orders -- a\u0000b")]
    [TestCase("SELECT id FROM orders -- a\u0007b")]
    [TestCase("SELECT id FROM orders /* a\uFFFFb */")]
    [TestCase("SELECT id FROM orders -- a\uFFFF\nDELETE FROM orders")]
    public async Task AControlCharacterOrNoncharacterInAComment_IsRefused(string escaped)
    {
        var sql = System.Text.RegularExpressions.Regex.Unescape(escaped);

        var result = await TestGroundingSqlChecks.Real().CheckAsync(DataSourceId, sql, CancellationToken.None);

        result.Refusal.Should().Be(GroundingSqlCheck.CommentRefusal);
    }

    // A lone carriage return ends a line comment on some engines but not for the tokenizer (nor does a Unicode line
    // separator): where the comment ends cannot be read safely, so the SQL is refused, not stripped.
    [TestCase("SELECT id -- the ids\rFROM orders")]
    [TestCase("SELECT id FROM orders -- the ids\r")]
    [TestCase("SELECT id -- the ids\u2028FROM orders")]
    public async Task ALineCommentEndedByALoneCarriageReturn_IsRefused(string escaped)
    {
        var sql = System.Text.RegularExpressions.Regex.Unescape(escaped);

        var result = await TestGroundingSqlChecks.Real().CheckAsync(DataSourceId, sql, CancellationToken.None);

        result.Refusal.Should().Be(GroundingSqlCheck.CommentRefusal);
    }

    // A line comment ends at the end of the input or at its line break, \r\n included; what follows the line is SQL the
    // gate judges.
    [TestCase("SELECT id FROM orders --", "SELECT id FROM orders")]
    [TestCase("SELECT id FROM orders -- the ids\r\n", "SELECT id FROM orders")]
    [TestCase("SELECT id -- the ids\r\nFROM orders", "SELECT id  FROM orders")]
    public async Task ALineCommentAtTheEndOrBeforeALineBreak_IsRemoved(string escaped, string expected)
    {
        var sql = System.Text.RegularExpressions.Regex.Unescape(escaped);

        var result = await TestGroundingSqlChecks.Real().CheckAsync(DataSourceId, sql, CancellationToken.None);

        result.IsAllowed.Should().BeTrue(result.Refusal);
        result.Sql.Should().Be(expected);
    }

    // A comment followed by a statement on the next line: the comment goes, the statement is judged — and refused.
    [TestCase("SELECT id FROM orders -- the ids\nDELETE FROM orders")]
    [TestCase("SELECT id FROM orders /* the ids */\nDELETE FROM orders")]
    [TestCase("SELECT id FROM orders -- the ids\r\n; DELETE FROM orders")]
    public async Task ACommentFollowedByAStatementOnTheNextLine_IsRefusedForTheStatement(string escaped)
    {
        var sql = System.Text.RegularExpressions.Regex.Unescape(escaped);

        var result = await TestGroundingSqlChecks.Real().CheckAsync(DataSourceId, sql, CancellationToken.None);

        result.IsAllowed.Should().BeFalse();
        result.Refusal.Should().NotBe(GroundingSqlCheck.CommentRefusal);
    }

    // A refusal names the reason, never the SQL: no literal value, no comment text and no statement reaches the reason, which
    // the API answers with and handlers put in their errors.
    [TestCase(DatabaseEngineType.PostgreSQL, "SELECT id FROM orders WHERE status = 'acct-4417' -- ask finance\u202E")]
    [TestCase(DatabaseEngineType.PostgreSQL, "SELECT id FROM orders WHERE status = 'acct-4417' /* ask finance")]
    [TestCase(DatabaseEngineType.MySQL, "SELECT id FROM orders WHERE status = 'acct-4417--ask finance'")]
    [TestCase(DatabaseEngineType.PostgreSQL, "DELETE FROM orders WHERE status = 'acct-4417' -- ask finance")]
    [TestCase(DatabaseEngineType.PostgreSQL, "SELECT id FROM orders WHERE status = 'acct-4417'; SELECT 'ask finance'")]
    public async Task ARefusal_NeverEchoesTheSql(DatabaseEngineType engine, string escaped)
    {
        var sql = System.Text.RegularExpressions.Regex.Unescape(escaped);

        var result = await TestGroundingSqlChecks.Real(engine).CheckAsync(DataSourceId, sql, CancellationToken.None);

        result.IsAllowed.Should().BeFalse();
        result.Refusal.Should().NotContain("acct-4417").And.NotContain("ask finance").And.NotContain(sql);
    }

    [TestCase("")]
    [TestCase("   ")]
    public async Task EmptySql_IsRefused(string sql)
    {
        var result = await TestGroundingSqlChecks.Real().CheckAsync(DataSourceId, sql, CancellationToken.None);

        result.Refusal.Should().Be("The SQL is empty.");
    }

    [Test]
    public async Task AMissingDataSource_IsRefused()
    {
        var result = await TestGroundingSqlChecks.Real().CheckAsync(99, "SELECT id FROM orders", CancellationToken.None);

        result.Refusal.Should().Be("Data source 99 not found.");
    }

    [Test]
    public void AResult_IsEitherTheSqlOrAReason()
    {
        GroundingSqlCheckResult.Allowed("SELECT 1").Should().BeEquivalentTo(new { IsAllowed = true, Sql = "SELECT 1", Refusal = (string?)null });
        GroundingSqlCheckResult.Refused("no").Should().BeEquivalentTo(new { IsAllowed = false, Sql = (string?)null, Refusal = "no" });
    }

    [Test]
    public async Task ABatch_ReadsTheSourceAndItsCatalogOnce_AndDecidesAsEachCheckDoes()
    {
        var batchReads = 0;
        var singleReads = 0;
        var batch = TestGroundingSqlChecks.Real(onRead: () => batchReads++).StartBatch();
        var single = TestGroundingSqlChecks.Real(onRead: () => singleReads++);

        foreach (var sql in new[] { "SELECT id FROM orders -- the ids", "SELECT status FROM orders", "SELECT missing_column FROM orders" })
        {
            var fromBatch = await batch.CheckAsync(DataSourceId, sql, CancellationToken.None);
            var fromSingle = await single.CheckAsync(DataSourceId, sql, CancellationToken.None);

            fromBatch.Should().BeEquivalentTo(fromSingle, $"a batch decides {sql} as a single check does");
        }

        batchReads.Should().Be(2, "the data source and its catalog are read once for the batch");
        singleReads.Should().Be(6, "a single check reads both every time, the data source once");
        batch.StartBatch().Should().BeSameAs(batch);
    }

    [Test]
    public async Task ABatch_RemembersAMissingDataSource()
    {
        var reads = 0;
        var batch = TestGroundingSqlChecks.Real(onRead: () => reads++).StartBatch();

        await batch.CheckAsync(99, "SELECT id FROM orders", CancellationToken.None);
        var again = await batch.CheckAsync(99, "SELECT status FROM orders", CancellationToken.None);

        again.Refusal.Should().Be("Data source 99 not found.");
        reads.Should().Be(1);
    }

    // Rendering for a prompt: comments go in the data source's dialect; comment characters inside a string literal the
    // dialect's tokenizer reads as one stay; without a dialect only SQL holding no comment marker is kept; anything whose
    // comments cannot be removed, or that holds characters that do not show, is left out.
    [TestCase("SELECT id FROM orders -- the ids", "PostgreSQL", "SELECT id FROM orders")]
    [TestCase("/* all */ SELECT id FROM orders", "PostgreSQL", "SELECT id FROM orders")]
    [TestCase("SELECT id FROM orders # the ids", "MySQL", "SELECT id FROM orders")]
    [TestCase("SELECT id FROM orders WHERE note = 'a--b'", "PostgreSQL", "SELECT id FROM orders WHERE note = 'a--b'")]
    [TestCase("SELECT id FROM orders WHERE note = $$a/*b$$", "PostgreSQL", "SELECT id FROM orders WHERE note = $$a/*b$$")]
    [TestCase("SELECT id FROM orders WHERE note = N'a--b'", "SQLServer", "SELECT id FROM orders WHERE note = N'a--b'")]
    [TestCase("SELECT id FROM orders WHERE note = 'a//b' // the ids", "Snowflake", "SELECT id FROM orders WHERE note = 'a//b'")]
    [TestCase("SELECT id FROM orders WHERE note = \"a#b\"", "BigQuery", "SELECT id FROM orders WHERE note = \"a#b\"")]
    [TestCase("SELECT \"a--b\" FROM orders", "PostgreSQL", null)]
    [TestCase("SELECT id FROM orders WHERE note = 'a--b'", null, null)]
    [TestCase("SELECT id /*!50000 , status */ FROM orders", "MySQL", null)]
    [TestCase("SELECT id FROM orders -- the ids", null, null)]
    [TestCase("SELECT id FROM orders WHERE note = 'a\u202Eb'", "PostgreSQL", null)]
    [TestCase("  SELECT id FROM orders  ", null, "SELECT id FROM orders")]
    public void GroundingSqlText_ShowsSqlWithoutComments_OrNothing(string sql, string? dialect, string? expected)
    {
        GroundingSqlText.WithoutComments(sql, dialect).Should().Be(expected);
    }
}
