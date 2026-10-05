using Beacon.Core.Helpers;
using Beacon.Core.Services;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// One page plus an exact total, keeping only the page in memory — through the rewritten SQL when the shape
/// allows it, and by streaming the original statement when it does not or when the gate rejects a rewrite.
/// </summary>
[TestFixture]
public class SqlPageExecutorTests
{
    private SqliteConnection _connection = null!;

    [SetUp]
    public async Task SetUp()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        await _connection.ExecuteAsync("CREATE TABLE people (id INTEGER, name TEXT)");
        for (var index = 1; index <= 45; index++)
        {
            await _connection.ExecuteAsync("INSERT INTO people VALUES (@id, @name)", new { id = index, name = $"n{index:D2}" });
        }
    }

    [TearDown]
    public async Task TearDown() => await _connection.DisposeAsync();

    [Test]
    public async Task RewritablePlan_ReturnsSortedPageAndTotal()
    {
        var page = await Run("SELECT id, name FROM people", new Paging { Page = 1, PageSize = 20, Sort = "-id" }, _ => true);

        page.TotalCount.Should().Be(45);
        page.SortApplied.Should().BeTrue();
        page.Rows.Select(x => Convert.ToInt64(x["id"])).Should().Equal(Enumerable.Range(6, 20).Reverse().Select(x => (long)x));
    }

    [Test]
    public async Task CommentedSql_StreamsOriginal_KeepingOnlyThePage()
    {
        var page = await Run("SELECT id, name FROM people ORDER BY id -- newest last", new Paging { Page = 2, PageSize = 20, Sort = "-id" }, _ => true);

        page.TotalCount.Should().Be(45);
        page.SortApplied.Should().BeFalse("a commented statement is never rewritten, so the sort cannot be applied");
        page.Rows.Select(x => Convert.ToInt64(x["id"])).Should().Equal(41L, 42L, 43L, 44L, 45L);
    }

    [Test]
    public async Task RejectedRewrite_FallsBackToTheApprovedOriginal()
    {
        var executed = new List<string>();

        var page = await Run(
            "SELECT id FROM people WHERE id <= 30",
            new Paging { PageSize = 10, Sort = "id" },
            x =>
            {
                executed.Add(x);
                return false;
            });

        executed.Should().NotBeEmpty("each rewritten statement is offered to the gate");
        page.TotalCount.Should().Be(30);
        page.Rows.Should().HaveCount(10);
        page.SortApplied.Should().BeFalse();
    }

    private Task<SqlResultPage> Run(string sql, ListRequest paging, Func<string, bool> isAllowed) =>
        SqlPageExecutor.ExecuteAsync(_connection, sql, null, "SQLite", paging, isAllowed, null, CancellationToken.None);

    private sealed record Paging : ListRequest;
}
