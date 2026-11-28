using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Beacon.AI.Services.Knowledge;
using Beacon.Core.Data;
using Beacon.Tests.Common;

namespace Beacon.Tests.Integration;

/// <summary>
/// Query-translation coverage (§4.3, §4.12) for the two reads the ask context gained: the description items of the shown
/// tables and the glossary terms matched by keyword. Each mirrors the query in <see cref="KnowledgeGraphService"/> and is
/// translated with <c>ToQueryString()</c> on both providers against a dummy connection (no DB hit, §4.7).
/// </summary>
[TestFixture]
public class KnowledgeDocumentationTranslationTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void DescriptionItemsOfShownTables_Translates(bool sqlServer)
    {
        using var context = Create(sqlServer);
        var tableNames = new List<string> { "orders", "customers" };

        // Mirrors KnowledgeGraphService.DescriptionItemsShownAsync.
        var sql = context.McpDescriptionItems
            .Where(x => x.DataSourceId == 7)
            .Where(x => tableNames.Contains(x.TableName))
            .Select(x =>
                new
                {
                    x.Id,
                    x.SchemaName,
                    x.TableName,
                    x.ColumnName
                })
            .ToQueryString();

        var table = sqlServer ? "[beacon].[McpDescriptionItems]" : "beacon.mcp_description_items";
        sql.Should().Contain(table, Exactly.Once());
        sql.Should().MatchRegex(sqlServer ? @"\[DataSourceId\] = 7" : @"data_source_id = 7");
        sql.Should().MatchRegex(sqlServer ? @"\[TableName\] IN \(" : @"table_name = ANY \(");
        sql.Should().NotContain("structure_hash").And.NotContain("StructureHash");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void KeywordGlossaryTerms_Translates(bool sqlServer)
    {
        using var context = Create(sqlServer);

        // Mirrors KnowledgeGraphService.KeywordGlossaryBlockAsync: the project's active terms, at most 500, the newest first.
        var sql = context.McpGlossaryTerms
            .Where(x => x.ProjectId == 3)
            .Where(x => x.IsActive)
            .OrderByDescending(x => x.Id)
            .Take(KnowledgeGraphService.KeywordGlossaryTerms)
            .Select(x =>
                new
                {
                    x.Id,
                    x.Term,
                    x.Definition,
                    x.TargetSchema,
                    x.TargetTable,
                    x.TargetColumn,
                    x.MetricExpression,
                    x.Synonyms
                })
            .ToQueryString();

        sql.Should().MatchRegex(sqlServer ? @"\[ProjectId\] = 3" : @"project_id = 3");
        sql.Should().MatchRegex(sqlServer ? @"\[IsActive\] = CAST\(1 AS bit\)" : @"\.is_active");
        sql.Should().MatchRegex(sqlServer ? @"ORDER BY \[m\]\.\[Id\] DESC" : @"ORDER BY m\.id DESC");
        sql.Should().MatchRegex(sqlServer ? @"TOP\(@p\)|OFFSET 0 ROWS FETCH NEXT @p ROWS ONLY|TOP\(@__p_0\)" : @"LIMIT @p");
    }

    private static BeaconContext Create(bool sqlServer)
    {
        return sqlServer ? SqlServerTestContext.Create() : NpgsqlTestContext.Create();
    }
}
