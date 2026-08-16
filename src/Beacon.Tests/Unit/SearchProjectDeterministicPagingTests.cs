using FluentAssertions;
using NUnit.Framework;
using Beacon.AI.Services.Knowledge;

namespace Beacon.Tests.Unit;

/// <summary>
/// The final in-memory sort of <see cref="KnowledgeGraphService.OrderForDeterministicPaging"/>:
/// items with EQUAL relevance must come out in one deterministic order regardless of input order,
/// because paging over reshuffling ties would show a caller different "pages" of the same results
/// between calls.
/// </summary>
[TestFixture]
public class SearchProjectDeterministicPagingTests
{
    [Test]
    public void EqualRelevance_ShuffledInputs_ProduceIdenticalOrder()
    {
        var items = EqualRelevanceItems();
        var shuffledOneWay = new List<SearchResult> { items[3], items[0], items[4], items[2], items[1], items[5] };
        var shuffledAnotherWay = new List<SearchResult> { items[5], items[2], items[0], items[4], items[1], items[3] };

        var firstPass = KnowledgeGraphService.OrderForDeterministicPaging(shuffledOneWay, items.Count);
        var secondPass = KnowledgeGraphService.OrderForDeterministicPaging(shuffledAnotherWay, items.Count);

        firstPass.Should().HaveCount(items.Count);
        firstPass.Select(Key).Should().Equal(secondPass.Select(Key));
    }

    [Test]
    public void EqualRelevance_TieBreakFollowsSourceSchemaTableColumnOrder()
    {
        var items = EqualRelevanceItems();
        var shuffled = new List<SearchResult> { items[4], items[1], items[5], items[0], items[3], items[2] };

        var sorted = KnowledgeGraphService.OrderForDeterministicPaging(shuffled, items.Count);

        sorted.Select(Key).Should().Equal(
            "src-a|public|orders|",
            "src-a|public|orders|amount",
            "src-a|public|orders|id",
            "src-a|public|users|",
            "src-a|sales|orders|",
            "src-b|public|orders|");
    }

    [Test]
    public void EqualRelevance_PagingWindowIsStableAcrossPasses()
    {
        var items = EqualRelevanceItems();
        var shuffledOneWay = new List<SearchResult> { items[2], items[5], items[1], items[4], items[0], items[3] };
        var shuffledAnotherWay = new List<SearchResult> { items[1], items[3], items[5], items[0], items[2], items[4] };

        var firstPage = KnowledgeGraphService.OrderForDeterministicPaging(shuffledOneWay, 3);
        var samePageAgain = KnowledgeGraphService.OrderForDeterministicPaging(shuffledAnotherWay, 3);

        firstPage.Should().HaveCount(3);
        firstPage.Select(Key).Should().Equal(samePageAgain.Select(Key));
    }

    [Test]
    public void HigherRelevance_AlwaysSortsBeforeTiedBand()
    {
        var items = EqualRelevanceItems();
        var exactMatch = MakeResult("src-z", "zeta", "zzz_last_by_every_tiebreak", "zzz", relevance: 1.0);
        var shuffled = new List<SearchResult> { items[1], exactMatch, items[0] };

        var sorted = KnowledgeGraphService.OrderForDeterministicPaging(shuffled, shuffled.Count);

        sorted[0].Should().BeSameAs(exactMatch, "relevance outranks every alphabetical tie-break key");
    }

    private static string Key(SearchResult result) =>
        $"{result.DataSourceName}|{result.SchemaName}|{result.TableName}|{result.ColumnName}";

    private static List<SearchResult> EqualRelevanceItems() =>
    [
        MakeResult("src-a", "public", "orders", null),
        MakeResult("src-a", "public", "orders", "amount"),
        MakeResult("src-a", "public", "orders", "id"),
        MakeResult("src-a", "public", "users", null),
        MakeResult("src-a", "sales", "orders", null),
        MakeResult("src-b", "public", "orders", null)
    ];

    private static SearchResult MakeResult(string dataSourceName, string schemaName, string tableName, string? columnName, double relevance = 0.8) =>
        new()
        {
            Type = columnName == null ? "table" : "column",
            DataSourceName = dataSourceName,
            SchemaName = schemaName,
            TableName = tableName,
            ColumnName = columnName,
            Relevance = relevance
        };
}
