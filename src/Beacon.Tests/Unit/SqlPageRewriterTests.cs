using Beacon.Core.Helpers;
using Beacon.Core.Services.Validation;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using SqlParser;
using SqlParser.Dialects;

namespace Beacon.Tests.Unit;

/// <summary>
/// Paging, sorting and counting SQL around a user's query. SQLite plans are executed against real data;
/// the other dialects are checked for the exact clauses and for re-parsing in their own dialect.
/// </summary>
[TestFixture]
public class SqlPageRewriterTests
{
    private static readonly SortCriterion ByNameDesc = new("name", SortDirection.Descending);

    [Test]
    public void PostgreSql_Unordered_AppendsLimitOffset()
    {
        var plan = SqlPageRewriter.Plan("SELECT id, name FROM customers", "PostgreSQL", 40, 20, null);

        plan.PageSql.Should().Be("SELECT id, name FROM customers LIMIT 20 OFFSET 40");
        plan.CountSql.Should().Be("SELECT COUNT(*) FROM (SELECT id, name FROM customers) AS beacon_count");
        plan.SortApplied.Should().BeFalse();
        ShouldParse(plan, new PostgreSqlDialect());
    }

    [Test]
    public void PostgreSql_WithSort_OrdersByQuotedColumn()
    {
        var plan = SqlPageRewriter.Plan("SELECT id, name FROM customers;", "PostgreSQL", 0, 20, ByNameDesc);

        plan.PageSql.Should().Be("SELECT id, name FROM customers ORDER BY \"name\" DESC LIMIT 20 OFFSET 0");
        plan.SortApplied.Should().BeTrue();
    }

    [Test]
    public void PostgreSql_OwnOrderAndRequestedSort_WrapsAsDerivedTable()
    {
        var plan = SqlPageRewriter.Plan("SELECT id, name FROM customers ORDER BY id", "PostgreSQL", 0, 20, ByNameDesc);

        plan.PageSql.Should().Be(
            "SELECT * FROM (SELECT id, name FROM customers ORDER BY id) AS beacon_page ORDER BY \"name\" DESC LIMIT 20 OFFSET 0");
        ShouldParse(plan, new PostgreSqlDialect());
    }

    [Test]
    public void PostgreSql_UserLimit_IsPagedWithinIt()
    {
        var plan = SqlPageRewriter.Plan("SELECT id FROM customers LIMIT 100", "PostgreSQL", 20, 20, null);

        plan.PageSql.Should().Be("SELECT * FROM (SELECT id FROM customers LIMIT 100) AS beacon_page LIMIT 20 OFFSET 20");
        plan.CountSql.Should().Contain("LIMIT 100");
    }

    [Test]
    public void TSql_Unordered_UsesNullOrderOffsetFetch()
    {
        var plan = SqlPageRewriter.Plan("SELECT id, name FROM dbo.Customers", "MSSQL", 40, 20, null);

        plan.PageSql.Should().Be("SELECT id, name FROM dbo.Customers ORDER BY (SELECT NULL) OFFSET 40 ROWS FETCH NEXT 20 ROWS ONLY");
        plan.CountSql.Should().Be("SELECT COUNT(*) FROM (SELECT id, name FROM dbo.Customers) AS beacon_count");
        ShouldParse(plan, new MsSqlDialect());
    }

    [Test]
    public void TSql_Ordered_KeepsItsOrderAndAddsOffsetFetch()
    {
        var plan = SqlPageRewriter.Plan("SELECT id FROM dbo.Customers ORDER BY id DESC", "MSSQL", 0, 20, null);

        plan.PageSql.Should().Be("SELECT id FROM dbo.Customers ORDER BY id DESC OFFSET 0 ROWS FETCH NEXT 20 ROWS ONLY");
        // An ORDER BY inside a T-SQL derived table needs TOP.
        plan.CountSql.Should().Be("SELECT COUNT(*) FROM (SELECT TOP 100 PERCENT id FROM dbo.Customers ORDER BY id DESC) AS beacon_count");
        ShouldParse(plan, new MsSqlDialect());
    }

    [Test]
    public void TSql_CteWithSort_AppendsOrderBy_ButCannotCount()
    {
        var sql = "WITH recent AS (SELECT id, name FROM dbo.Customers) SELECT id, name FROM recent";

        var plan = SqlPageRewriter.Plan(sql, "AzureSynapse", 0, 20, ByNameDesc);

        plan.PageSql.Should().Be($"{sql} ORDER BY [name] DESC OFFSET 0 ROWS FETCH NEXT 20 ROWS ONLY");
        plan.CountSql.Should().BeNull("T-SQL cannot nest a CTE in a derived table; the caller streams the count");
    }

    [Test]
    public void TSql_CteWithOwnOrderAndSort_FallsBackToStreaming()
    {
        var plan = SqlPageRewriter.Plan(
            "WITH r AS (SELECT id FROM t) SELECT id FROM r ORDER BY id", "MSSQL", 0, 20, ByNameDesc);

        plan.PageSql.Should().BeNull();
        plan.SortApplied.Should().BeFalse();
    }

    [TestCase("SELECT DISTINCT name FROM dbo.Customers")]
    [TestCase("SELECT name FROM dbo.A UNION SELECT name FROM dbo.B")]
    public void TSql_DistinctOrSetOperationWithoutSort_PagesThroughDerivedTable(string sql)
    {
        var plan = SqlPageRewriter.Plan(sql, "MSSQL", 0, 20, null);

        plan.PageSql.Should().Be($"SELECT * FROM ({sql}) AS beacon_page ORDER BY (SELECT NULL) OFFSET 0 ROWS FETCH NEXT 20 ROWS ONLY");
        ShouldParse(plan, new MsSqlDialect());
    }

    [Test]
    public void MySql_QuotesWithBackticks()
    {
        var plan = SqlPageRewriter.Plan("SELECT id, name FROM customers", "MySQL", 0, 20, ByNameDesc);

        plan.PageSql.Should().Be("SELECT id, name FROM customers ORDER BY `name` DESC LIMIT 20 OFFSET 0");
    }

    [TestCase("MSSQL", "[a]]; DROP TABLE x --]")]
    [TestCase("PostgreSQL", "\"a\"\"; DROP TABLE x --\"")]
    [TestCase("MySQL", "`a``; DROP TABLE x --`")]
    public void SortColumn_CannotEscapeItsQuotes(string dialect, string expected)
    {
        var column = dialect switch
        {
            "MSSQL" => "a]; DROP TABLE x --",
            "PostgreSQL" => "a\"; DROP TABLE x --",
            _ => "a`; DROP TABLE x --",
        };

        SqlPageRewriter.Quote(column, dialect).Should().Be(expected);
    }

    [TestCase("SELECT id FROM t -- trailing note")]
    [TestCase("SELECT /* hint */ id FROM t")]
    [TestCase("SELECT 1; SELECT 2")]
    [TestCase("UPDATE t SET a = 1")]
    [TestCase("not sql at all")]
    public void UnsafeOrUnsupportedShapes_AreNotRewritten(string sql)
    {
        var plan = SqlPageRewriter.Plan(sql, "PostgreSQL", 0, 20, null);

        plan.PageSql.Should().BeNull();
        plan.CountSql.Should().BeNull();
    }

    [Test]
    public async Task Sqlite_PlansReturnTheRightPageSortAndCount()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await connection.ExecuteAsync("CREATE TABLE people (id INTEGER, name TEXT)");
        for (var index = 1; index <= 45; index++)
        {
            await connection.ExecuteAsync("INSERT INTO people VALUES (@id, @name)", new { id = index, name = $"n{index:D2}" });
        }

        var sql = "WITH p AS (SELECT id, name FROM people WHERE id > 5) SELECT id, name FROM p";
        var plan = SqlPageRewriter.Plan(sql, "SQLite", 20, 10, ByNameDesc);

        var page = (await connection.QueryAsync<(long Id, string Name)>(plan.PageSql!)).ToList();
        var total = await connection.ExecuteScalarAsync<long>(plan.CountSql!);

        total.Should().Be(40);
        page.Select(x => x.Name).Should().Equal("n25", "n24", "n23", "n22", "n21", "n20", "n19", "n18", "n17", "n16");
    }

    private static void ShouldParse(SqlPagePlan plan, Dialect dialect)
    {
        foreach (var sql in new[] { plan.PageSql, plan.CountSql }.Where(x => x != null))
        {
            var act = () => new Parser().ParseSql(sql!, dialect);
            act.Should().NotThrow($"`{sql}` must be valid for the dialect");
        }
    }
}
