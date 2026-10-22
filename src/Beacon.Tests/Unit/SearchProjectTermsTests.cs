using Beacon.AI.Services.Knowledge;
using Beacon.Core.Data;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// Project search (the MCP <c>search</c> tool) looks for the words of a query, not the whole query as one substring:
/// "how do I request annual leave" finds a document about annual leave.
/// </summary>
[TestFixture]
public class SearchProjectTermsTests
{
    [TestCase("how do I request annual leave", new[] { "request", "annual", "leave" })]
    [TestCase("Annual LEAVE", new[] { "annual", "leave" })]
    [TestCase("order_items", new[] { "order_items" })]
    [TestCase("dbo.orders, customer-id", new[] { "dbo", "orders", "customer", "id" })]
    [TestCase("leave leave annual", new[] { "leave", "annual" })]
    [TestCase("Godišnji odmor", new[] { "godišnji", "odmor" })]
    public void SearchTermsOf_SplitsTheQueryIntoWords(string query, string[] expected)
    {
        KnowledgeGraphService.SearchTermsOf(query).Should().Equal(expected);
    }

    [TestCase("how do I", "how do i")]
    [TestCase("  x  ", "x")]
    public void SearchTermsOf_QueryWithoutSearchableWords_SearchesTheWholeQuery(string query, string expected)
    {
        KnowledgeGraphService.SearchTermsOf(query).Should().Equal(expected);
    }

    [TestCase("")]
    [TestCase("   ")]
    public void SearchTermsOf_BlankQuery_HasNoTerms(string query)
    {
        KnowledgeGraphService.SearchTermsOf(query).Should().BeEmpty("a blank query must not become an empty term that every row contains");
    }

    [Test]
    public void SearchTermsOf_KeepsAtMostEightTerms()
    {
        var terms = KnowledgeGraphService.SearchTermsOf("one two three four five six seven eight nine ten");

        terms.Should().Equal("one", "two", "three", "four", "five", "six", "seven", "eight");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SearchProjectSubQueries_WithSeveralTerms_Translate(bool sqlServer)
    {
        using var context = sqlServer ? (BeaconContext)SqlServerTestContext.Create() : NpgsqlTestContext.Create();
        List<string> terms = ["annual", "leave"];

        var tables = KnowledgeGraphService.BuildMatchingTablesQuery(context, [1, 2], terms, 20).ToQueryString();
        var columns = KnowledgeGraphService.BuildMatchingColumnsQuery(context, [1, 2], terms, 20).ToQueryString();
        var sections = KnowledgeGraphService.BuildDocSectionsQuery(context, 1, terms, 10).ToQueryString();
        var documents = KnowledgeGraphService.BuildImportedDocsQuery(context, 1, terms, 10).ToQueryString();

        foreach (var sql in new[] { tables, columns, sections, documents })
        {
            var statement = Statement(sql);
            statement.Should().MatchRegex("(?i)count", "each row counts the terms it contains");
            statement.Should().MatchRegex(@"(?i)ORDER BY \(", "the term count is the first sort key");
            statement.Should().NotContain("annual", "the terms are parameters, never inlined");
        }

        documents.Should().Contain(sqlServer ? "[McpDocChunks]" : "mcp_doc_chunks");
    }

    // ToQueryString() prints the parameter values above the statement (Npgsql as "-- " comments, SQL Server as DECLAREs).
    private static string Statement(string sql) =>
        string.Join('\n', sql.Split('\n').Where(x => !x.StartsWith("-- ")).Where(x => !x.StartsWith("DECLARE ")));
}
