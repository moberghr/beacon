using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Services;
using Beacon.Core.Services.Validation;

namespace Beacon.Tests.Unit;

/// <summary>
/// The query-builder / AI-actor final query (<see cref="VirtualTableManager.ExecuteFinalQueryWithInMemoryDatabase"/>
/// and the paged preview <see cref="VirtualTableManager.ExecuteFinalQueryPagedAsync"/>) runs stored or LLM-written SQL
/// against the in-memory SQLite join store, so it passes the read-only AST gate (§1.5) like the MCP join paths —
/// otherwise <c>ATTACH</c> creates files on the host — and the join store's engine-level read-only checks, which
/// catch what the gate's parser reads differently from SQLite (a nested block comment hiding a second statement).
/// </summary>
[TestFixture]
public class VirtualTableManagerTests
{
    // Regression input for the nested-block-comment lexing difference between the AST gate's parser (nests `/* */`)
    // and SQLite (ends a comment at the first `*/`); the trailing statement is harmless.
    private const string NestedCommentSmuggle = "SELECT * FROM @result1 /* /* */ ; PRAGMA user_version=7; -- */";

    // InMemoryDatabaseManager logs this once per virtual table it loads. The rejection test below reads its absence as
    // "nothing loaded"; ExecuteFinalQueryPaged_SuccessfulLoad_LogsTheTableLoadMessage keeps that absence meaningful.
    private const string TableLoadedLogPrefix = "Created table";

    private static readonly SqlReadOnlyAstValidator Validator = new(NullLogger<SqlReadOnlyAstValidator>.Instance);

    [Test]
    public async Task ExecuteFinalQuery_NestedCommentSmuggle_IsRejected()
    {
        using var manager = new VirtualTableManager(NullLogger<VirtualTableManager>.Instance);
        manager.AddVirtualTable("@result1", [new Dictionary<string, object?> { ["id"] = 1L }], Project("orders"));

        var act = () => manager.ExecuteFinalQueryWithInMemoryDatabase(
            NestedCommentSmuggle,
            Validator,
            NullLogger<InMemoryDatabaseManager>.Instance,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task ExecuteFinalQueryPaged_NestedCommentSmuggle_IsRejected()
    {
        using var manager = new VirtualTableManager(NullLogger<VirtualTableManager>.Instance);
        manager.AddVirtualTable("@result1", [new Dictionary<string, object?> { ["id"] = 1L }], Project("orders"));

        var act = () => manager.ExecuteFinalQueryPagedAsync(
            NestedCommentSmuggle,
            Validator,
            NullLogger<InMemoryDatabaseManager>.Instance,
            new Paging(),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task ExecuteFinalQuery_CteJoinAndWindowFunction_StillRunUnderTheReadOnlyAuthorizer()
    {
        using var manager = new VirtualTableManager(NullLogger<VirtualTableManager>.Instance);
        manager.AddVirtualTable("@result1", [new Dictionary<string, object?> { ["id"] = 1L, ["name"] = "Ana" }], Project("customers"));
        manager.AddVirtualTable(
            "@result2",
            [
                new Dictionary<string, object?> { ["customer_id"] = 1L, ["total"] = 250L },
                new Dictionary<string, object?> { ["customer_id"] = 1L, ["total"] = 50L }
            ],
            Project("orders"));

        // The non-paged path also analyses the store after the query, once the authorizer is engaged.
        var result = await manager.ExecuteFinalQueryWithInMemoryDatabase(
            "WITH totals AS (SELECT customer_id, SUM(total) AS total FROM @result2 GROUP BY customer_id) "
            + "SELECT c.name, t.total, RANK() OVER (ORDER BY t.total DESC) AS position FROM @result1 c JOIN totals t ON t.customer_id = c.id",
            Validator,
            NullLogger<InMemoryDatabaseManager>.Instance,
            CancellationToken.None);

        result.AllRecords.Should().ContainSingle();
        result.AllRecords[0]["name"].Should().Be("Ana");
        result.AllRecords[0]["total"].Should().Be(300L);
        result.AllRecords[0]["position"].Should().Be(1L);
    }

    [Test]
    public async Task ExecuteFinalQuery_AttachStatement_IsRejectedBeforeTouchingTheHost()
    {
        var attachPath = Path.Combine(Path.GetTempPath(), $"beacon-attach-{Guid.NewGuid():N}.db");
        using var manager = new VirtualTableManager(NullLogger<VirtualTableManager>.Instance);
        manager.AddVirtualTable("@result1", [new Dictionary<string, object?> { ["id"] = 1 }], Project("orders"));

        var act = () => manager.ExecuteFinalQueryWithInMemoryDatabase(
            $"ATTACH '{attachPath}' AS x",
            Validator,
            NullLogger<InMemoryDatabaseManager>.Instance,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        File.Exists(attachPath).Should().BeFalse();
    }

    [Test]
    public async Task ExecuteFinalQuery_ReadOnlyJoin_PassesTheGateAndJoinsVirtualTables()
    {
        using var manager = new VirtualTableManager(NullLogger<VirtualTableManager>.Instance);
        manager.AddVirtualTable("@result1", [new Dictionary<string, object?> { ["id"] = 1L, ["name"] = "Ana" }], Project("customers"));
        manager.AddVirtualTable("@result2", [new Dictionary<string, object?> { ["customer_id"] = 1L, ["total"] = 250L }], Project("orders"));

        var result = await manager.ExecuteFinalQueryWithInMemoryDatabase(
            "SELECT c.name, o.total FROM @result1 c JOIN @result2 o ON o.customer_id = c.id",
            Validator,
            NullLogger<InMemoryDatabaseManager>.Instance,
            CancellationToken.None);

        result.TimedOut.Should().BeFalse();
        result.AllRecords.Should().ContainSingle();
        result.AllRecords[0]["name"].Should().Be("Ana");
        result.AllRecords[0]["total"].Should().Be(250L);
    }

    /// <summary>
    /// The paged preview path used to stream any final query unchecked (the page executor's callback only screens
    /// its own rewrites): <c>ATTACH</c> wrote a file on the host. The gate now runs before a single table loads.
    /// </summary>
    [Test]
    public async Task ExecuteFinalQueryPaged_AttachChain_IsRejectedBeforeAnyVirtualTableLoads()
    {
        var attachPath = Path.Combine(Path.GetTempPath(), $"beacon-attach-{Guid.NewGuid():N}.db");
        var inMemoryLogger = new RecordingLogger<InMemoryDatabaseManager>();
        using var manager = new VirtualTableManager(NullLogger<VirtualTableManager>.Instance);
        manager.AddVirtualTable("@result1", [new Dictionary<string, object?> { ["id"] = 1L }], Project("orders"));

        var act = () => manager.ExecuteFinalQueryPagedAsync(
            $"ATTACH '{attachPath}' AS x; CREATE TABLE x.loot AS SELECT * FROM @result1; SELECT * FROM @result1",
            Validator,
            inMemoryLogger,
            new Paging(),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        File.Exists(attachPath).Should().BeFalse();
        inMemoryLogger.Messages.Should().NotContain(x => x.StartsWith(TableLoadedLogPrefix), "no virtual table may load for a rejected final query");
    }

    /// <summary>
    /// Positive control for the absence check above: if the load message is reworded, that check would pass even when a
    /// table loads — this test fails instead.
    /// </summary>
    [Test]
    public async Task ExecuteFinalQueryPaged_SuccessfulLoad_LogsTheTableLoadMessage()
    {
        var inMemoryLogger = new RecordingLogger<InMemoryDatabaseManager>();
        using var manager = new VirtualTableManager(NullLogger<VirtualTableManager>.Instance);
        manager.AddVirtualTable("@result1", [new Dictionary<string, object?> { ["id"] = 1L }], Project("orders"));

        await manager.ExecuteFinalQueryPagedAsync(
            "SELECT * FROM @result1",
            Validator,
            inMemoryLogger,
            new Paging(),
            CancellationToken.None);

        inMemoryLogger.Messages.Should().ContainSingle(x => x.StartsWith(TableLoadedLogPrefix));
    }

    [Test]
    public async Task ExecuteFinalQueryPaged_AttachChainWithoutSteps_IsRejected()
    {
        var attachPath = Path.Combine(Path.GetTempPath(), $"beacon-attach-{Guid.NewGuid():N}.db");
        using var manager = new VirtualTableManager(NullLogger<VirtualTableManager>.Instance);

        var act = () => manager.ExecuteFinalQueryPagedAsync(
            $"ATTACH '{attachPath}' AS x; CREATE TABLE x.loot (id INTEGER); SELECT 1",
            Validator,
            NullLogger<InMemoryDatabaseManager>.Instance,
            new Paging(),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        File.Exists(attachPath).Should().BeFalse();
    }

    [Test]
    public async Task ExecuteFinalQueryPaged_ReadOnlySelect_StillPages()
    {
        using var manager = new VirtualTableManager(NullLogger<VirtualTableManager>.Instance);
        manager.AddVirtualTable(
            "@result1",
            Enumerable.Range(1, 5)
                .Select(x => (IDictionary<string, object?>)new Dictionary<string, object?> { ["id"] = (long)x })
                .ToList(),
            Project("orders"));

        var page = await manager.ExecuteFinalQueryPagedAsync(
            "SELECT * FROM @result1",
            Validator,
            NullLogger<InMemoryDatabaseManager>.Instance,
            new Paging { Page = 1, PageSize = 2, Sort = "id" },
            CancellationToken.None);

        page.TotalCount.Should().Be(5);
        page.Rows.Select(x => Convert.ToInt64(x["id"])).Should().Equal(3L, 4L);
    }

    /// <summary>
    /// A step's result-column alias is caller-controlled SQL text — <c>SELECT 1 AS "a]);PRAGMA …;--"</c> passes the
    /// gates — and the join store used to paste it into its load SQL, running the appended PRAGMA. It must load as a
    /// plain column the final query reads back by its exact name, on both final-query paths.
    /// </summary>
    [TestCase("a]);PRAGMA user_version=7;--")]
    [TestCase("a\");PRAGMA user_version=7;--")]
    public async Task ExecuteFinalQuery_HostileStepColumnName_LoadsAsAPlainColumn(string columnName)
    {
        using var manager = new VirtualTableManager(NullLogger<VirtualTableManager>.Instance);
        manager.AddVirtualTable("@result1", [new Dictionary<string, object?> { ["id"] = 1L, [columnName] = "loaded" }], Project("orders"));

        var result = await manager.ExecuteFinalQueryWithInMemoryDatabase(
            "SELECT * FROM @result1",
            Validator,
            NullLogger<InMemoryDatabaseManager>.Instance,
            CancellationToken.None);

        result.AllRecords.Should().ContainSingle();
        result.AllRecords[0].Keys.Should().Equal("id", columnName);
        result.AllRecords[0][columnName].Should().Be("loaded");
    }

    [TestCase("a]);PRAGMA user_version=7;--")]
    [TestCase("a\");PRAGMA user_version=7;--")]
    public async Task ExecuteFinalQueryPaged_HostileStepColumnName_LoadsAsAPlainColumn(string columnName)
    {
        using var manager = new VirtualTableManager(NullLogger<VirtualTableManager>.Instance);
        manager.AddVirtualTable("@result1", [new Dictionary<string, object?> { ["id"] = 1L, [columnName] = "loaded" }], Project("orders"));

        var page = await manager.ExecuteFinalQueryPagedAsync(
            "SELECT * FROM @result1",
            Validator,
            NullLogger<InMemoryDatabaseManager>.Instance,
            new Paging(),
            CancellationToken.None);

        page.TotalCount.Should().Be(1);
        page.Rows.Should().ContainSingle();
        page.Rows[0][columnName].Should().Be("loaded");
    }

    [Test]
    public async Task ExecuteFinalQuery_HostileStepColumnName_CannotCreateAnotherTable()
    {
        using var manager = new VirtualTableManager(NullLogger<VirtualTableManager>.Instance);
        manager.AddVirtualTable("@result1", [new Dictionary<string, object?> { ["a]);CREATE TABLE smuggled(x);--"] = 1L }], Project("orders"));

        var result = await manager.ExecuteFinalQueryWithInMemoryDatabase(
            "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name",
            Validator,
            NullLogger<InMemoryDatabaseManager>.Instance,
            CancellationToken.None);

        result.AllRecords.Select(x => x["name"]).Should().Equal("result1");
    }

    private static ProjectInfo Project(string name) =>
        new()
        {
            Name = name,
            DatabaseEngine = nameof(DatabaseEngineType.PostgreSQL),
            DatabaseEngineType = DatabaseEngineType.PostgreSQL
        };

    private sealed record Paging : ListRequest;

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
