using System.Diagnostics;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using SQLitePCL;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Services;
using Beacon.Core.Services.Validation;

namespace Beacon.Tests.Unit;

/// <summary>
/// The in-memory SQLite join store must stop a running statement when its timeout or the caller's token fires.
/// Microsoft.Data.Sqlite only checks the token before a statement starts and ignores CommandTimeout for a running
/// one, so without an interrupt a runaway join holds the thread until it finishes.
/// It also runs stored final-query SQL, so it is read-only at the engine: once a query runs, a SQLite authorizer
/// lets only reads compile, SQLite's own tokenizer must find exactly one statement, and a nested block comment —
/// which SQLite and the AST gate's parser end in different places — is refused before anything is parsed. A paged
/// read gates the original statement itself — not only the page/count rewrites, which fall back to streaming the
/// original. Loading is guarded too: result-column names are caller-controlled, so they load as quoted identifiers
/// with positional parameters, one statement per command, under a load-only authorizer from construction.
/// </summary>
[TestFixture]
public class InMemoryDatabaseManagerTests
{
    // Counts to 100M one row at a time — tens of seconds of CPU, far past every deadline below.
    private const string RunawayQuery = "WITH RECURSIVE r(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM r WHERE i < 100000000) SELECT COUNT(*) FROM r";

    // A nested block comment, which SqlParserCS and SQLite end in different places. The AST gate refuses it, and the
    // store must refuse it on its own too when handed the text directly.
    private const string NestedCommentSmuggle = "SELECT * FROM [result1] /* /* */ ; CREATE TABLE smuggled(x); -- */";

    private static readonly SqlReadOnlyAstValidator Validator = new(NullLogger<SqlReadOnlyAstValidator>.Instance);

    // Test-only, for reading back state the store's own authorizers refuse to show (they deny every PRAGMA).
    private static readonly delegate_authorizer AllowEverything = (_, _, _, _, _, _) => raw.SQLITE_OK;

    [Test]
    public async Task ExecuteQueryAsync_Attach_IsRefusedByTheEngine()
    {
        using var manager = new InMemoryDatabaseManager(NullLogger<InMemoryDatabaseManager>.Instance);

        var act = () => manager.ExecuteQueryAsync("ATTACH DATABASE ':memory:' AS x");

        await act.Should().ThrowAsync<SqliteException>()
            .Where(x => x.SqliteErrorCode == raw.SQLITE_AUTH, "the read-only authorizer refuses ATTACH while it compiles");
    }

    [Test]
    public void NestedCommentSmuggle_IsRejectedByTheAstGate()
    {
        Validator.Validate(NestedCommentSmuggle, nameof(DatabaseEngineType.SQLite))
            .Should()
            .StartWith("Nested block comments are not allowed");
    }

    [Test]
    public async Task ExecuteQueryAsync_NestedCommentSmuggle_IsRejectedAndNeverRuns()
    {
        using var manager = await ManagerWithPeople();

        var act = () => manager.ExecuteQueryAsync(NestedCommentSmuggle);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Nested block comments*");
        manager.AnalyzeDatabase().Tables.Should().NotContainKey("smuggled", "the smuggled statement must never reach SQLite");
    }

    [Test]
    public async Task ExecutePagedAsync_NestedCommentSmuggle_IsRejectedAndNeverRuns()
    {
        using var manager = await ManagerWithPeople();

        var act = () => manager.ExecutePagedAsync(
            NestedCommentSmuggle,
            new Paging(),
            x => Validator.Validate(x, nameof(DatabaseEngineType.SQLite)),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Nested block comments*");
        manager.AnalyzeDatabase().Tables.Should().NotContainKey("smuggled", "the smuggled statement must never reach SQLite");
    }

    [Test]
    public async Task ExecuteQueryAsync_TrailingStatement_IsRejectedBySqliteTokenizerAndNeverRuns()
    {
        using var manager = await ManagerWithPeople();

        var act = () => manager.ExecuteQueryAsync("SELECT * FROM [result1]; CREATE TABLE smuggled(x)");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*single SQL statement*");
        manager.AnalyzeDatabase().Tables.Should().NotContainKey("smuggled");
    }

    [Test]
    public async Task ExecuteQueryAsync_TwoSelects_AreRejected()
    {
        using var manager = new InMemoryDatabaseManager(NullLogger<InMemoryDatabaseManager>.Instance);

        var act = () => manager.ExecuteQueryAsync("SELECT 1; SELECT 2");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*single SQL statement*");
    }

    [Test]
    public async Task ExecutePagedAsync_TrailingStatement_IsRejectedBySqliteTokenizerAndNeverRuns()
    {
        using var manager = await ManagerWithPeople();

        // The gate is a stand-in that approves everything, so only the engine check stands between the two.
        var act = () => manager.ExecutePagedAsync(
            "SELECT * FROM [result1]; CREATE TABLE smuggled(x)",
            new Paging(),
            _ => null,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*single SQL statement*");
        manager.AnalyzeDatabase().Tables.Should().NotContainKey("smuggled");
    }

    [Test]
    public async Task ExecuteQueryAsync_TrailingSemicolonAndComment_StillRuns()
    {
        using var manager = new InMemoryDatabaseManager(NullLogger<InMemoryDatabaseManager>.Instance);

        var (results, _, _) = await manager.ExecuteQueryAsync("SELECT 41 + 1 AS answer; -- the answer\n /* done */ ;");

        results.Should().ContainSingle()
            .Which["answer"].Should().Be(42L);
    }

    [TestCase("CREATE TABLE smuggled(x)")]
    [TestCase("INSERT INTO result1 (id) VALUES (99)")]
    [TestCase("PRAGMA user_version = 7")]
    [TestCase("BEGIN TRANSACTION")]
    public async Task ExecuteQueryAsync_NonReadStatement_IsRefusedByTheAuthorizer(string sql)
    {
        using var manager = await ManagerWithPeople();

        var act = () => manager.ExecuteQueryAsync(sql);

        await act.Should().ThrowAsync<SqliteException>()
            .Where(x => x.SqliteErrorCode == raw.SQLITE_AUTH);
        manager.AnalyzeDatabase().Tables.Should().NotContainKey("smuggled");
        manager.AnalyzeDatabase().Tables["result1"].RowCount.Should().Be(3, "nothing may be written");
    }

    [Test]
    public async Task CreateTableFromResults_AfterAQueryRan_IsRefused()
    {
        using var manager = await ManagerWithPeople();
        await manager.ExecuteQueryAsync("SELECT 1");

        var act = () => manager.CreateTableFromResults("result2", People(), Project());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*read-only*");
    }

    [Test]
    public async Task ExecuteQueryAsync_JoinCteAndWindowFunction_RunUnderTheReadOnlyAuthorizer()
    {
        using var manager = await ManagerWithPeople();
        await manager.CreateTableFromResults(
            "result2",
            [
                new Dictionary<string, object?> { ["person_id"] = 1L, ["total"] = 10L },
                new Dictionary<string, object?> { ["person_id"] = 1L, ["total"] = 15L },
                new Dictionary<string, object?> { ["person_id"] = 2L, ["total"] = 40L }
            ],
            Project());

        var (results, _, _) = await manager.ExecuteQueryAsync("""
            WITH totals AS (SELECT person_id, SUM(total) AS total FROM [result2] GROUP BY person_id)
            SELECT p.id, t.total, ROW_NUMBER() OVER (ORDER BY t.total DESC) AS position
            FROM [result1] p
            JOIN totals t ON t.person_id = p.id
            ORDER BY position
            """);

        results.Select(x => (x["id"], x["total"], x["position"])).Should().Equal((2L, 40L, 1L), (1L, 25L, 2L));

        var analysis = manager.AnalyzeDatabase();
        analysis.Tables["result1"].ColumnCount.Should().Be(1, "the analysis still reads the schema once the store is read-only");
        analysis.Tables["result2"].ColumnCount.Should().Be(2);
        analysis.TotalRows.Should().Be(6);
    }

    [Test]
    public async Task ExecutePagedAsync_CteUnderTheReadOnlyAuthorizer_PagesAndCounts()
    {
        using var manager = await ManagerWithPeople();

        var page = await manager.ExecutePagedAsync(
            "WITH ranked AS (SELECT id, ROW_NUMBER() OVER (ORDER BY id DESC) AS position FROM [result1]) SELECT id, position FROM ranked",
            new Paging { PageSize = 2, Sort = "id" },
            x => Validator.Validate(x, nameof(DatabaseEngineType.SQLite)),
            CancellationToken.None);

        page.TotalCount.Should().Be(3);
        page.Rows.Select(x => Convert.ToInt64(x["id"])).Should().Equal(1L, 2L);
    }

    [TestCase("SELECT * FROM [result1] /* /* */ ; PRAGMA user_version=7; -- */", true)]
    [TestCase("SELECT 1 /* a /*/ ; SELECT 2 -- */ */", true)]
    [TestCase("SELECT 1 /* outer /* inner */", true)]
    [TestCase("SELECT 1 /* plain */ FROM [result1] /**/", false)]
    [TestCase("SELECT '/*' AS a, \"/* b\" AS b FROM [c/*] -- /* line\n/* fine */", false)]
    [TestCase("SELECT 'it''s /*' AS a /* x */", false)]
    [TestCase("SELECT `/*` FROM t /* x */", false)]
    public void HasNestedBlockComment_LexesLikeSqlite(string sql, bool expected)
    {
        InMemoryDatabaseManager.HasNestedBlockComment(sql).Should().Be(expected);
    }

    [Test]
    public async Task ExecutePagedAsync_RejectedStatement_ThrowsAndNeverRunsIt()
    {
        using var manager = new InMemoryDatabaseManager(NullLogger<InMemoryDatabaseManager>.Instance);

        var act = () => manager.ExecutePagedAsync(
            "CREATE TABLE t(x)",
            new Paging(),
            x => Validator.Validate(x, nameof(DatabaseEngineType.SQLite)),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        manager.AnalyzeDatabase().Tables.Should().NotContainKey("t", "the rejected statement must never reach SQLite");
    }

    [Test]
    public async Task ExecutePagedAsync_ApprovedSelect_GatesTheOriginalAndItsRewritesThenPages()
    {
        using var manager = new InMemoryDatabaseManager(NullLogger<InMemoryDatabaseManager>.Instance);
        await manager.CreateTableFromResults(
            "people",
            Enumerable.Range(1, 5)
                .Select(x => (IDictionary<string, object?>)new Dictionary<string, object?> { ["id"] = (long)x })
                .ToList(),
            Project());
        var validated = new List<string>();

        var page = await manager.ExecutePagedAsync(
            "SELECT id FROM people",
            new Paging { PageSize = 2, Sort = "id" },
            x =>
            {
                validated.Add(x);
                return Validator.Validate(x, nameof(DatabaseEngineType.SQLite));
            },
            CancellationToken.None);

        validated[0].Should().Be("SELECT id FROM people", "the original statement is gated before anything runs");
        validated.Should().HaveCountGreaterThan(1, "the page and count rewrites pass the same gate");
        page.TotalCount.Should().Be(5);
        page.Rows.Select(x => Convert.ToInt64(x["id"])).Should().Equal(1L, 2L);
    }

    [Test]
    public async Task ExecuteQueryAsync_RunawayQuery_TimesOutAtTheDeadline()
    {
        using var manager = new InMemoryDatabaseManager(NullLogger<InMemoryDatabaseManager>.Instance);
        var stopwatch = Stopwatch.StartNew();

        var (results, _, timedOut) = await manager.ExecuteQueryAsync(RunawayQuery, timeoutSeconds: 1);

        timedOut.Should().BeTrue();
        results.Should().BeEmpty();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task ExecuteQueryAsync_CallerCancels_ThrowsAndConnectionStaysUsable()
    {
        using var manager = new InMemoryDatabaseManager(NullLogger<InMemoryDatabaseManager>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var stopwatch = Stopwatch.StartNew();

        var act = () => manager.ExecuteQueryAsync(RunawayQuery, timeoutSeconds: 30, cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));

        var (results, _, timedOut) = await manager.ExecuteQueryAsync("SELECT 41 + 1 AS answer", timeoutSeconds: 5);

        timedOut.Should().BeFalse();
        results.Should().ContainSingle()
            .Which["answer"].Should().Be(42L);
    }

    // Result-column names are caller-controlled — the aliases of step, source or LLM-written SQL — and each of these
    // closes one SQLite quoting style and appends a statement. The reviewer's reproduction appended
    // PRAGMA hard_heap_limit, which starved every later in-memory join in the process; user_version is the harmless
    // stand-in.
    [TestCase("a]);PRAGMA user_version=7;--")]
    [TestCase("a\");PRAGMA user_version=7;--")]
    [TestCase("a`);PRAGMA user_version=7;--")]
    [TestCase("a');PRAGMA user_version=7;--")]
    public async Task CreateTableFromResults_HostileColumnName_LoadsAsAPlainColumnAndNeverRunsAsSql(string columnName)
    {
        using var manager = new InMemoryDatabaseManager(NullLogger<InMemoryDatabaseManager>.Instance);

        await manager.CreateTableFromResults(
            "result1",
            [new Dictionary<string, object?> { ["id"] = 1L, [columnName] = "loaded" }],
            Project());
        var (results, _, _) = await manager.ExecuteQueryAsync("SELECT * FROM [result1]");

        results.Should().ContainSingle();
        results[0].Keys.Should().Equal(["id", columnName], "the column keeps its exact name");
        results[0][columnName].Should().Be("loaded");
        UserVersion(manager).Should().Be(0, "no part of a column name may run as SQL");
    }

    [Test]
    public async Task CreateTableFromResults_HostileColumnName_CannotCreateAnotherTable()
    {
        using var manager = new InMemoryDatabaseManager(NullLogger<InMemoryDatabaseManager>.Instance);

        await manager.CreateTableFromResults(
            "result1",
            [new Dictionary<string, object?> { ["a]);CREATE TABLE smuggled(x);--"] = 1L }],
            Project());

        manager.AnalyzeDatabase().Tables.Keys.Should().Equal("result1");
    }

    [Test]
    public async Task CreateTableFromResults_BracketsQuotesSpacesUnicodeAndControlCharacters_RoundTripByExactName()
    {
        var row = new Dictionary<string, object?>
        {
            ["weird] \"col\" name"] = "brackets and quotes",
            ["naïve 名前 ✓"] = "unicode",
            ["tab\tand\nnewline"] = "control characters",
            ["@p1"] = "parameter look-alike",
            ["{=p0}"] = "literal-token look-alike"
        };
        using var manager = new InMemoryDatabaseManager(NullLogger<InMemoryDatabaseManager>.Instance);

        await manager.CreateTableFromResults("result1", [row], Project());

        manager.AnalyzeDatabase().Tables["result1"].ColumnCount.Should().Be(5, "the analysis reads the schema while tables load");

        // Each value differs from its column name: a quoted name SQLite failed to resolve would come back as a string
        // literal, never as the loaded value.
        var (byName, _, _) = await manager.ExecuteQueryAsync(
            "SELECT \"weird] \"\"col\"\" name\" AS a, [naïve 名前 ✓] AS b, \"tab\tand\nnewline\" AS c, \"@p1\" AS d, \"{=p0}\" AS e FROM [result1]");
        byName.Should().ContainSingle();
        byName[0].Values.Should().Equal("brackets and quotes", "unicode", "control characters", "parameter look-alike", "literal-token look-alike");

        var (all, _, _) = await manager.ExecuteQueryAsync("SELECT * FROM [result1]");
        all.Should().ContainSingle()
            .Which.Should().Equal(row);
    }

    [TestCase("a\0b", "result1")]
    [TestCase("id", "res\0ult1")]
    public async Task CreateTableFromResults_NulInAName_IsRefusedBeforeAnythingLoads(string columnName, string tableName)
    {
        using var manager = new InMemoryDatabaseManager(NullLogger<InMemoryDatabaseManager>.Instance);

        var act = () => manager.CreateTableFromResults(tableName, [new Dictionary<string, object?> { [columnName] = 1L }], Project());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*NUL*");
        manager.AnalyzeDatabase().Tables.Should().BeEmpty();
    }

    // Statements a table load never needs, compiled on the store's connection while tables load (before any query).
    [TestCase("PRAGMA user_version = 7")]
    [TestCase("PRAGMA writable_schema = ON")]
    [TestCase("ATTACH DATABASE ':memory:' AS x")]
    [TestCase("DROP TABLE result1")]
    [TestCase("DELETE FROM result1")]
    [TestCase("UPDATE result1 SET id = 0")]
    [TestCase("ALTER TABLE result1 ADD COLUMN extra TEXT")]
    [TestCase("CREATE TEMP TABLE shadow (x)")]
    [TestCase("CREATE VIEW shadow AS SELECT 1")]
    [TestCase("CREATE INDEX shadow ON result1 (id)")]
    [TestCase("CREATE TRIGGER shadow AFTER INSERT ON result1 BEGIN SELECT 1; END")]
    [TestCase("SAVEPOINT shadow")]
    public async Task LoadPhase_NonLoadStatement_IsRefusedByTheLoadAuthorizer(string sql)
    {
        using var manager = await ManagerWithPeople();

        Prepare(manager, sql).Should().Be(raw.SQLITE_AUTH);
    }

    [TestCase("CREATE TABLE result2 (id INTEGER)")]
    [TestCase("INSERT INTO result1 (id) VALUES (4)")]
    [TestCase("SELECT COUNT(*) FROM result1")]
    [TestCase("BEGIN")]
    public async Task LoadPhase_LoadStatement_StillCompiles(string sql)
    {
        using var manager = await ManagerWithPeople();

        Prepare(manager, sql).Should().Be(raw.SQLITE_OK);
    }

    [Test]
    public async Task ExecuteQueryAsync_OversizedBlob_FailsAsTooBigAndTheStoreStaysUsable()
    {
        using var manager = new InMemoryDatabaseManager(NullLogger<InMemoryDatabaseManager>.Instance);

        var act = () => manager.ExecuteQueryAsync("SELECT length(randomblob(100000000)) AS size");

        await act.Should().ThrowAsync<SqliteException>()
            .Where(x => x.SqliteErrorCode == raw.SQLITE_TOOBIG);

        var (results, _, _) = await manager.ExecuteQueryAsync("SELECT length(randomblob(1000)) AS size");

        results.Should().ContainSingle()
            .Which["size"].Should().Be(1000L);
    }

    private static async Task<InMemoryDatabaseManager> ManagerWithPeople()
    {
        var manager = new InMemoryDatabaseManager(NullLogger<InMemoryDatabaseManager>.Instance);
        await manager.CreateTableFromResults("result1", People(), Project());

        return manager;
    }

    private static List<IDictionary<string, object?>> People() =>
        Enumerable.Range(1, 3)
            .Select(x => (IDictionary<string, object?>)new Dictionary<string, object?> { ["id"] = (long)x })
            .ToList();

    private static ProjectInfo Project() =>
        new()
        {
            Name = "people",
            DatabaseEngine = nameof(DatabaseEngineType.PostgreSQL),
            DatabaseEngineType = DatabaseEngineType.PostgreSQL
        };

    /// <summary>Compiles (never runs) <paramref name="sql"/> on the store's connection and returns SQLite's code.</summary>
    private static int Prepare(InMemoryDatabaseManager manager, string sql)
    {
        var rc = raw.sqlite3_prepare_v2(manager.Handle, sql, out var statement);
        raw.sqlite3_finalize(statement);

        return rc;
    }

    /// <summary>
    /// The database's user_version — what a smuggled <c>PRAGMA user_version=7</c> would have set. Lifts the store's
    /// authorizer to read it, so call it last.
    /// </summary>
    private static int UserVersion(InMemoryDatabaseManager manager)
    {
        raw.sqlite3_set_authorizer(manager.Handle, AllowEverything, null);
        raw.sqlite3_prepare_v2(manager.Handle, "PRAGMA user_version", out var statement).Should().Be(raw.SQLITE_OK);
        try
        {
            raw.sqlite3_step(statement).Should().Be(raw.SQLITE_ROW);

            return raw.sqlite3_column_int(statement, 0);
        }
        finally
        {
            raw.sqlite3_finalize(statement);
        }
    }

    private sealed record Paging : ListRequest;
}
