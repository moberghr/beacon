using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Embeddings;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Services.Metadata;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// Descriptions and documentation in the <c>ask</c> context: the description items of the tables and columns the prompt
/// renders (SC4), the project's documentation chunks behind <c>EnableDocChunksInAsk</c> (SC5) and the glossary matched by
/// keyword without semantic retrieval (SC8). Runs the real <see cref="KnowledgeGraphService"/> over a mocked
/// <see cref="BeaconContext"/> backed by the async-queryable doubles (§4.7), with the in-memory cosine search and
/// <see cref="FakeEmbeddingService"/>.
/// </summary>
[TestFixture]
[SetCulture("")]
public class KnowledgeDocumentationContextTests
{
    private const int DataSourceId = 7;
    private const int OtherDataSourceId = 8;
    private const int ProjectId = 3;
    private const int OtherProjectId = 99;
    private const string Question = "How many orders were refunded in 2024?";
    private const string Dialect = "PostgreSQL";
    private const string DocumentationFence = "<untrusted_data label=\"documentation\">";
    private const string DocumentationHeading = "\n## Project Documentation";

    // Makes the documentation block longer than the budget's reservation for the line naming left-out tables.
    private static readonly string Filler = string.Join(' ', Enumerable.Repeat("detail", 150));

    // ---------- SC4: description items ----------

    [Test]
    public async Task AskContext_RecordsTheDescriptionItemsOfTheRenderedTablesAndColumns_InOrderOfAppearance()
    {
        var world = new World();
        world.Tables.Add(Table(1, "customers", "Customers", ("id", null), ("name", "Customer name")));
        world.Tables.Add(Table(2, "orders", "Orders placed", ("id", "Order id"), ("status", "Order status"), ("total", null)));
        world.DescriptionItems.AddRange(
        [
            Item(24, "orders", "total"),
            Item(23, "orders", "status"),
            Item(21, "orders", ""),
            Item(22, "orders", "id"),
            Item(25, "orders", "gone_column"),
            Item(26, "customers", "", OtherDataSourceId)
        ]);

        var result = await world.Service().GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: true, CancellationToken.None);

        // customers renders descriptions but has no item row of this data source; total renders none; gone_column is not in
        // the catalog: none of them is recorded.
        result.UsedSmartRetrieval.Should().BeFalse();
        result.FullContext.Should().Contain("Customer name").And.Contain("Order status");
        result.KnowledgeItems.Should().Equal(
            new KnowledgeItemRef(McpKnowledgeItemKind.TableDescription, 21, 1),
            new KnowledgeItemRef(McpKnowledgeItemKind.ColumnDescription, 22, 2),
            new KnowledgeItemRef(McpKnowledgeItemKind.ColumnDescription, 23, 3));
    }

    [Test]
    public async Task AskContext_NoDescriptionItemRows_RecordsNothing()
    {
        var world = new World();
        world.Tables.Add(Table(2, "orders", "Orders placed", ("id", "Order id"), ("status", "Order status")));

        var result = await world.Service().GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: true, CancellationToken.None);

        result.FullContext.Should().Contain("Orders placed");
        result.KnowledgeItems.Should().BeEmpty();
    }

    // The retrieval path shows the question's tables in full and the others compact (name, key, column names): a compact
    // table renders its own description but no column's, so only its table item is recorded.
    [Test]
    public async Task AskContext_RetrievalPath_RecordsColumnDescriptionsOfFullTablesOnly()
    {
        var world = new World();
        var auditColumns = Enumerable.Range(1, 300)
            .Select(x => ($"audit_column_{x:D3}", (string?)$"audit value {x}"))
            .ToArray();
        world.Tables.Add(Table(1, "audit_log", "Audit trail", auditColumns));
        world.Tables.Add(Table(2, "orders", "Orders placed", ("id", "Order id"), ("status", "Order status")));
        world.DescriptionItems.AddRange(
        [
            Item(31, "audit_log", ""),
            Item(32, "audit_log", "audit_column_001"),
            Item(41, "orders", ""),
            Item(42, "orders", "status")
        ]);

        var result = await world.Service().GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: true, CancellationToken.None);

        result.UsedSmartRetrieval.Should().BeTrue("2 + 300 columns are over the fast path's 300");
        result.FullContext.Should().Contain("### public.orders").And.Contain("  - public.audit_log (");
        result.FullContext.Should().NotContain("audit value 1", "a compact table renders no column description");
        result.KnowledgeItems.Should().Equal(
            new KnowledgeItemRef(McpKnowledgeItemKind.TableDescription, 41, 1),
            new KnowledgeItemRef(McpKnowledgeItemKind.ColumnDescription, 42, 2),
            new KnowledgeItemRef(McpKnowledgeItemKind.TableDescription, 31, 3));
    }

    // Over the budget the top table is cut at a line boundary: the columns whose lines it keeps are recorded, the rest not.
    [Test]
    public async Task AskContext_TheTopTableCut_RecordsOnlyTheColumnsItsKeptLinesRender()
    {
        var world = new World();
        var columns = Enumerable.Range(1, 60)
            .Select(x => ($"c{x:D2}", (string?)$"column {x} of the orders table, kept for reporting"))
            .ToArray();
        world.Tables.Add(Table(2, "orders", "Orders placed", columns));
        world.DescriptionItems.Add(Item(50, "orders", ""));
        world.DescriptionItems.AddRange(Enumerable.Range(1, 60).Select(x => Item(50 + x, "orders", $"c{x:D2}")));
        var full = (await world.Service().GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: true, CancellationToken.None)).FullContext;

        var result = await world.Service(budgetChars: full.Length - 1).GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: true, CancellationToken.None);

        result.FullContext.Should().Contain("-- public.orders: more columns not shown");
        var keptColumns = Enumerable.Range(1, 60)
            .Where(x => result.FullContext.Contains($"    - (c{x:D2}: ", StringComparison.Ordinal))
            .ToList();
        keptColumns.Should().NotBeEmpty().And.HaveCountLessThan(60);
        var expected = new List<KnowledgeItemRef> { new(McpKnowledgeItemKind.TableDescription, 50, 1) };
        expected.AddRange(keptColumns.Select((x, y) => new KnowledgeItemRef(McpKnowledgeItemKind.ColumnDescription, 50 + x, y + 2)));
        result.KnowledgeItems.Should().Equal(expected);
    }

    // ---------- SC5: documentation chunks ----------

    [Test]
    public async Task AskContext_DocChunksOn_ShowsAtMostThreeProjectChunksFencedAfterTheGlossary_AndRecordsThem()
    {
        var world = await DocChunkWorldAsync();
        world.Settings.EnableDocChunksInAsk = true;

        var result = await world.Service().GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: true, CancellationToken.None);

        var context = result.FullContext;
        CountOf(context, DocumentationFence).Should().Be(1);
        context.IndexOf(DocumentationFence, StringComparison.Ordinal).Should().BeGreaterThan(context.IndexOf("## Business Glossary", StringComparison.Ordinal));
        context.Should().NotContain("other project's chunk", "chunks are searched within the caller's project only");

        var chunkItems = result.KnowledgeItems
            .Where(x => x.Kind == McpKnowledgeItemKind.DocChunk)
            .ToList();
        chunkItems.Select(x => x.Rank).Should().Equal(1, 2, 3);
        chunkItems[0].ItemId.Should().Be(61, "the chunk embedded from the question itself is the nearest");
        var shownChunkIds = world.Chunks
            .Where(x => x.ProjectId == ProjectId)
            .Where(x => context.Contains(x.ChunkText, StringComparison.Ordinal))
            .Select(x => x.Id)
            .ToList();
        shownChunkIds.Should().BeEquivalentTo(chunkItems.Select(x => x.ItemId), "the prompt shows exactly the recorded chunks");
        context.Should().Contain("Situating blurb for chunk 61");
        result.KnowledgeItems[^1].Kind.Should().Be(McpKnowledgeItemKind.DocChunk, "the documentation block is the last part of the context");
        result.KnowledgeItems.Should().Contain(new KnowledgeItemRef(McpKnowledgeItemKind.GlossaryTerm, 900, 1));
    }

    // Off — the setting, or semantic retrieval — the prompt is the one rendered without any indexed chunk, and no chunk is
    // read: the raw question (what chunks are searched with) is never embedded and the chunk store is never queried.
    [TestCase(false, true)]
    [TestCase(true, false)]
    public async Task AskContext_DocChunksNotOn_PromptIsUnchanged_AndNoChunkIsRead(bool docChunksInAsk, bool semanticRetrieval)
    {
        EmbeddingMaskingHelper.Mask(Question).Should().NotBe(Question, "the raw question must differ from the masked one the other blocks embed");
        var withChunks = await DocChunkWorldAsync();
        withChunks.Settings.EnableDocChunksInAsk = docChunksInAsk;
        withChunks.Settings.EnableSemanticRetrieval = semanticRetrieval;
        var withoutChunks = await DocChunkWorldAsync(indexChunks: false);
        withoutChunks.Settings.EnableDocChunksInAsk = docChunksInAsk;
        withoutChunks.Settings.EnableSemanticRetrieval = semanticRetrieval;

        var result = await withChunks.Service().GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: true, CancellationToken.None);
        var baseline = await withoutChunks.Service().GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: true, CancellationToken.None);

        result.FullContext.Should().Be(baseline.FullContext);
        result.FullContext.Should().NotContain(DocumentationFence);
        result.KnowledgeItems.Should().NotContain(x => x.Kind == McpKnowledgeItemKind.DocChunk);
        withChunks.Context.ChunkReads.Should().Be(0);
        withChunks.Embedder.Texts.Should().NotContain(Question);
    }

    [Test]
    public async Task AskContext_BudgetShort_LeavesTheDocumentationOutFirst_AndRecordsNoChunk()
    {
        var ample = await DocChunkWorldAsync();
        ample.Settings.EnableDocChunksInAsk = true;
        var full = (await ample.Service().GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: true, CancellationToken.None)).FullContext;
        var documentationStart = full.IndexOf(DocumentationHeading, StringComparison.Ordinal);
        documentationStart.Should().BeGreaterThan(0);
        full[documentationStart..].Should().EndWith($"</untrusted_data>\n", "the documentation block is the last part of the context");

        var tight = await DocChunkWorldAsync();
        tight.Settings.EnableDocChunksInAsk = true;

        // One character short: the line naming left-out tables is reserved at its longest (about 2,000 characters), so only
        // leaving the documentation out (longer than that reservation) makes the table fit whole.
        var result = await tight.Service(budgetChars: full.Length - 1).GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: true, CancellationToken.None);

        result.FullContext.Should().Be(full[..documentationStart]);
        result.KnowledgeItems.Should().NotContain(x => x.Kind == McpKnowledgeItemKind.DocChunk);
        result.KnowledgeItems.Should().Contain(new KnowledgeItemRef(McpKnowledgeItemKind.GlossaryTerm, 900, 1));
        result.RowDataCategories.Should().NotContain(LlmRowDataCategories.StoredDocumentation);
    }

    // A chunk cut from a section that can list values found in the data (data model, business domains, glossary) is row
    // data: it is shown, and the egress tagged, only while row data may reach the LLM provider.
    [TestCase(false)]
    [TestCase(true)]
    public async Task AskContext_AChunkOfAValueListingSection_IsShownAndTaggedOnlyWhileRowDataMayReachTheProvider(bool forbidRowData)
    {
        var world = await DocChunkWorldAsync();
        world.Settings.EnableDocChunksInAsk = true;

        var result = await world.Service(forbidRowData: forbidRowData).GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: true, CancellationToken.None);

        var chunkIds = result.KnowledgeItems
            .Where(x => x.Kind == McpKnowledgeItemKind.DocChunk)
            .Select(x => x.ItemId)
            .ToList();
        if (forbidRowData)
        {
            chunkIds.Should().NotContain(61);
            result.FullContext.Should().NotContain("documentation chunk 61");
            result.RowDataCategories.Should().NotContain(LlmRowDataCategories.StoredDocumentation);
            return;
        }

        chunkIds.Should().Contain(61);
        result.FullContext.Should().Contain("documentation chunk 61");
        result.RowDataCategories.Should().Contain(LlmRowDataCategories.StoredDocumentation);
    }

    // The rank boost covers golden cases, learned patterns and glossary terms only: stats that would move a description or a
    // chunk are never read, and those items keep their order of appearance and similarity.
    [Test]
    public async Task AskContext_BoostOn_LeavesDescriptionAndChunkItemsInTheirOrder_AndReadsNoStats()
    {
        var world = await DocChunkWorldAsync(withGlossaryTerm: false);
        world.Settings.EnableDocChunksInAsk = true;
        world.Settings.EnableKnowledgeItemBoost = true;
        world.Settings.EnableLearning = true;
        world.DescriptionItems.AddRange([Item(21, "orders", ""), Item(22, "orders", "id"), Item(23, "orders", "status")]);
        world.Stats.AddRange(
        [
            Stat(McpKnowledgeItemKind.TableDescription, 21, helpful: 0, harmful: 10),
            Stat(McpKnowledgeItemKind.ColumnDescription, 23, helpful: 10, harmful: 0),
            Stat(McpKnowledgeItemKind.DocChunk, 61, helpful: 0, harmful: 10),
            Stat(McpKnowledgeItemKind.DocChunk, 62, helpful: 10, harmful: 0),
            Stat(McpKnowledgeItemKind.DocChunk, 63, helpful: 10, harmful: 0)
        ]);

        var result = await world.Service().GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: true, CancellationToken.None);

        result.KnowledgeItems
            .Where(x => x.Kind != McpKnowledgeItemKind.DocChunk)
            .Should()
            .Equal(
                new KnowledgeItemRef(McpKnowledgeItemKind.TableDescription, 21, 1),
                new KnowledgeItemRef(McpKnowledgeItemKind.ColumnDescription, 22, 2),
                new KnowledgeItemRef(McpKnowledgeItemKind.ColumnDescription, 23, 3));
        var chunkItems = result.KnowledgeItems
            .Where(x => x.Kind == McpKnowledgeItemKind.DocChunk)
            .ToList();
        chunkItems[0].Should().Be(new KnowledgeItemRef(McpKnowledgeItemKind.DocChunk, 61, 1), "the nearest chunk stays first despite its harmful stats");
        world.Context.StatsReads.Should().Be(0);
    }

    // ---------- SC8: the glossary by keyword ----------

    [Test]
    public async Task KeywordGlossary_AQuestionNamingATerm_GetsThatTermInTheBlockAndItsItems()
    {
        var world = new World();
        world.Terms.AddRange([Term(1, "churn", "Customers lost in the period"), Term(2, "margin", "Revenue minus cost")]);

        var block = await world.Service(new UnavailableEmbeddingService()).BuildGlossaryBlockWithItemsAsync(ProjectId, "What was the churn last quarter?", world.Settings, Dialect, CancellationToken.None);

        block.Text.Should().Be("\n## Business Glossary\n" + Environment.NewLine + "- **churn**: Customers lost in the period" + Environment.NewLine);
        block.Items.Should().Equal(new KnowledgeItemRef(McpKnowledgeItemKind.GlossaryTerm, 1, 1));
    }

    [Test]
    public async Task KeywordGlossary_AQuestionNamingASynonym_GetsItsTerm()
    {
        var world = new World();
        world.Settings.EnableSemanticRetrieval = false;
        world.Terms.Add(Term(4, "monthly recurring revenue", "Subscription revenue per month", synonyms: "MRR; recurring revenue"));

        var block = await world.Service().BuildGlossaryBlockWithItemsAsync(ProjectId, "show mrr by region", world.Settings, Dialect, CancellationToken.None);

        block.Text.Should().Contain("- **monthly recurring revenue**: Subscription revenue per month");
        block.Items.Should().Equal(new KnowledgeItemRef(McpKnowledgeItemKind.GlossaryTerm, 4, 1));
    }

    [TestCase("What is the rate?", true)]
    [TestCase("rate", true)]
    [TestCase("(RATE) by month", true)]
    [TestCase("Which orders are accurate?", false)]
    [TestCase("Show all rates", false)]
    [TestCase("show net_rate per day", false)]
    [TestCase("ratexyz", false)]
    public void KeywordGlossary_ANameMatchesOnlyAsAWholeWord(string question, bool named)
    {
        GlossaryKeywords.NamesWholeWord(question, "rate").Should().Be(named);
    }

    [TestCase("show the P&L for May", true)]
    [TestCase("show the P&Lx for May", false)]
    [TestCase("all 100%", false)]
    public void KeywordGlossary_ANameWithPunctuation_IsBoundedOnlyAtItsWordCharacters(string question, bool named)
    {
        var name = question.Contains('%') ? "%" : "P&L";

        GlossaryKeywords.NamesWholeWord(question, name).Should().Be(named);
    }

    // Under a Turkish culture "id" and "ID" differ in case-insensitive culture comparison (dotted and dotless i); the match is
    // culture-invariant.
    [Test]
    [SetCulture("tr-TR")]
    public async Task KeywordGlossary_CaseIsComparedCultureInvariant()
    {
        var world = new World();
        world.Terms.Add(Term(5, "ID", "The customer's identifier"));

        var block = await world.Service(new UnavailableEmbeddingService()).BuildGlossaryBlockWithItemsAsync(ProjectId, "list every customer id", world.Settings, Dialect, CancellationToken.None);

        block.Items.Should().Equal(new KnowledgeItemRef(McpKnowledgeItemKind.GlossaryTerm, 5, 1));
    }

    [Test]
    public async Task KeywordGlossary_LongestMatchFirst_CappedAtGlossaryTopK()
    {
        var world = new World();
        world.Settings.GlossaryTopK = 2;
        world.Terms.AddRange(
        [
            Term(1, "revenue", "All revenue"),
            Term(2, "net revenue", "Revenue after refunds"),
            Term(3, "month", "A calendar month", synonyms: "monthly net revenue figure"),
            Term(4, "region", "A sales region")
        ]);

        var block = await world.Service(new UnavailableEmbeddingService()).BuildGlossaryBlockWithItemsAsync(
            ProjectId, "Monthly net revenue figure by region", world.Settings, Dialect, CancellationToken.None);

        block.Items.Should().Equal(
            new KnowledgeItemRef(McpKnowledgeItemKind.GlossaryTerm, 3, 1),
            new KnowledgeItemRef(McpKnowledgeItemKind.GlossaryTerm, 2, 2));
        block.Text.IndexOf("**month**", StringComparison.Ordinal).Should().BeLessThan(block.Text.IndexOf("**net revenue**", StringComparison.Ordinal));
    }

    [Test]
    public async Task KeywordGlossary_AQuestionNamingNoTerm_GetsNoBlock()
    {
        var world = new World();
        world.Terms.AddRange([Term(1, "churn", "Customers lost"), Term(2, "margin", "Revenue minus cost")]);

        var block = await world.Service(new UnavailableEmbeddingService()).BuildGlossaryBlockWithItemsAsync(ProjectId, Question, world.Settings, Dialect, CancellationToken.None);

        block.Text.Should().BeEmpty();
        block.Items.Should().BeEmpty();
    }

    // At most 500 terms are read (by id); an inactive term and another project's term are never matched.
    [Test]
    public async Task KeywordGlossary_ReadsTheFirst500ActiveTermsOfTheProjectOnly()
    {
        var world = new World();
        world.Terms.AddRange(Enumerable.Range(1, KnowledgeGraphService.KeywordGlossaryTerms + 1).Select(x => Term(x, $"word{x}", $"definition {x}")));
        world.Terms.Add(Term(1_000, "inactive", "An inactive term", isActive: false));
        world.Terms.Add(Term(1_001, "foreign", "Another project's term", projectId: OtherProjectId));

        var block = await world.Service(new UnavailableEmbeddingService()).BuildGlossaryBlockWithItemsAsync(
            ProjectId, $"word7 and word{KnowledgeGraphService.KeywordGlossaryTerms + 1}, inactive and foreign", world.Settings, Dialect, CancellationToken.None);

        block.Items.Should().Equal(new KnowledgeItemRef(McpKnowledgeItemKind.GlossaryTerm, 7, 1));
    }

    [Test]
    public async Task AskContext_SemanticRetrievalOff_TheNamedTermReachesThePromptAndTheItems()
    {
        var world = new World();
        world.Settings.EnableSemanticRetrieval = false;
        world.Tables.Add(Table(2, "orders", null, ("id", null), ("status", null)));
        world.Terms.AddRange([Term(11, "refunded", "An order paid back in full"), Term(12, "churn", "Customers lost")]);

        var result = await world.Service().GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: true, CancellationToken.None);

        result.FullContext.Should().Contain("## Business Glossary").And.Contain("- **refunded**: An order paid back in full").And.NotContain("churn");
        result.KnowledgeItems.Should().Equal(new KnowledgeItemRef(McpKnowledgeItemKind.GlossaryTerm, 11, 1));
    }

    // ---------- helpers ----------

    // The orders table, a glossary term the masked question is nearest to, and four project chunks (61 embedded from the raw
    // question itself, cut from a value-listing data model section; 62 and 63 from an overview section; 64 from an imported
    // document) plus another project's chunk with the question's own vector.
    private static async Task<World> DocChunkWorldAsync(bool indexChunks = true, bool withGlossaryTerm = true)
    {
        var world = new World();
        world.Tables.Add(Table(2, "orders", "Orders placed", ("id", "Order id"), ("status", "Order status")));
        var embedder = world.Embedder;
        if (withGlossaryTerm)
        {
            world.Terms.Add(Term(900, "refund", "Money paid back"));
            world.Embeddings.Add(Embedding(1, McpEmbeddingOwnerType.GlossaryTerm, 900, await embedder.EmbedAsync(EmbeddingMaskingHelper.Mask(Question), CancellationToken.None), ProjectId));
        }

        world.Sections.AddRange(
        [
            new ProjectDocumentationSection { Id = 501, SectionType = ProjectDocSectionType.DataModel, Title = "Data model", Content = "" },
            new ProjectDocumentationSection { Id = 502, SectionType = ProjectDocSectionType.ProjectOverview, Title = "Overview", Content = "" }
        ]);
        if (!indexChunks)
        {
            embedder.Reset();
            return world;
        }

        world.Chunks.AddRange(
        [
            Chunk(61, ProjectId, 501, $"documentation chunk 61: a refund reverses an order. {Filler}", "Situating blurb for chunk 61"),
            Chunk(62, ProjectId, 502, $"documentation chunk 62: orders are placed by customers. {Filler}"),
            Chunk(63, ProjectId, 502, $"documentation chunk 63: refunds are processed weekly. {Filler}"),
            Chunk(64, ProjectId, 0, "documentation chunk 64: the warehouse ships orders"),
            Chunk(65, OtherProjectId, 502, "other project's chunk")
        ]);
        world.Embeddings.AddRange(
        [
            Embedding(11, McpEmbeddingOwnerType.DocChunk, 61, await embedder.EmbedAsync(Question, CancellationToken.None), ProjectId),
            Embedding(12, McpEmbeddingOwnerType.DocChunk, 62, await embedder.EmbedAsync("orders placed by customers", CancellationToken.None), ProjectId),
            Embedding(13, McpEmbeddingOwnerType.DocChunk, 63, await embedder.EmbedAsync("refunds processed weekly orders", CancellationToken.None), ProjectId),
            Embedding(14, McpEmbeddingOwnerType.DocChunk, 64, await embedder.EmbedAsync("warehouse shipping", CancellationToken.None), ProjectId),
            Embedding(15, McpEmbeddingOwnerType.DocChunk, 65, await embedder.EmbedAsync(Question, CancellationToken.None), OtherProjectId)
        ]);
        embedder.Reset();

        return world;
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        var index = text.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static DatabaseMetadata Table(int id, string name, string? description, params (string Name, string? Description)[] columns)
    {
        return new DatabaseMetadata
        {
            Id = id,
            DataSourceId = DataSourceId,
            SchemaName = "public",
            TableName = name,
            TableDescription = description,
            Columns = columns
                .Select((x, y) =>
                    new ColumnMetadata
                    {
                        Id = id * 1_000 + y,
                        DatabaseMetadataId = id,
                        ColumnName = x.Name,
                        DataType = "integer",
                        IsPrimaryKey = y == 0,
                        IsNullable = y != 0,
                        Description = x.Description
                    })
                .ToList()
        };
    }

    private static McpDescriptionItem Item(int id, string tableName, string columnName, int dataSourceId = DataSourceId)
    {
        return new McpDescriptionItem
        {
            Id = id,
            DataSourceId = dataSourceId,
            SchemaName = "public",
            TableName = tableName,
            ColumnName = columnName,
            Source = McpDescriptionSource.Curated,
            WrittenAt = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
            StructureHash = "hash"
        };
    }

    private static McpGlossaryTerm Term(int id, string term, string definition, string? synonyms = null, bool isActive = true, int projectId = ProjectId)
    {
        return new McpGlossaryTerm
        {
            Id = id,
            ProjectId = projectId,
            Term = term,
            Definition = definition,
            Synonyms = synonyms,
            IsActive = isActive
        };
    }

    private static McpDocChunk Chunk(int id, int projectId, int sectionId, string text, string? blurb = null)
    {
        return new McpDocChunk
        {
            Id = id,
            ProjectId = projectId,
            SourceSectionId = sectionId,
            ChunkText = text,
            ContextualBlurb = blurb
        };
    }

    private static McpEmbedding Embedding(int id, McpEmbeddingOwnerType ownerType, int ownerId, float[] vector, int projectId)
    {
        return new McpEmbedding
        {
            Id = id,
            DataSourceId = 0,
            ProjectId = projectId,
            OwnerType = ownerType,
            OwnerId = ownerId,
            EmbeddingBytes = EmbeddingCodec.ToBytes(vector),
            Model = "bge-small-en-v1.5",
            Dimensions = 384,
            EmbeddingVersion = 1
        };
    }

    private static McpKnowledgeItemStat Stat(McpKnowledgeItemKind kind, int itemId, double helpful, double harmful)
    {
        return new McpKnowledgeItemStat
        {
            ProjectId = ProjectId,
            Kind = kind,
            ItemId = itemId,
            HelpfulScore = helpful,
            HarmfulScore = harmful,
            SignalCount = 10,
            CallerCount = 3
        };
    }

    private static Mock<DbSet<T>> BuildDbSet<T>(List<T> data) where T : class
    {
        var queryable = data.AsQueryable();
        var set = new Mock<DbSet<T>>();
        set.As<IAsyncEnumerable<T>>()
            .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
            .Returns(() => new TestAsyncEnumerator<T>(data.GetEnumerator()));
        set.As<IQueryable<T>>().Setup(x => x.Provider).Returns(new TestAsyncQueryProvider<T>(queryable.Provider));
        set.As<IQueryable<T>>().Setup(x => x.Expression).Returns(queryable.Expression);
        set.As<IQueryable<T>>().Setup(x => x.ElementType).Returns(queryable.ElementType);
        set.As<IQueryable<T>>().Setup(x => x.GetEnumerator()).Returns(() => data.GetEnumerator());
        set.Setup(x => x.AsQueryable()).Returns(() => set.Object);

        return set;
    }

    /// <summary>One ask's stored world: the data source linked to the caller's project, its catalog and every set the ask reads.</summary>
    private sealed class World
    {
        private static readonly DataSource SalesDb = new()
        {
            Id = DataSourceId,
            Name = "Sales DB",
            DataSourceType = DataSourceType.Database,
            DatabaseEngineType = DatabaseEngineType.PostgreSQL,
            EncryptedConnectionData = "unused-in-test"
        };

        private DocumentationTestContext? _context;

        public List<DatabaseMetadata> Tables { get; } = [];
        public List<McpDescriptionItem> DescriptionItems { get; } = [];
        public List<McpGlossaryTerm> Terms { get; } = [];
        public List<McpEmbedding> Embeddings { get; } = [];
        public List<McpDocChunk> Chunks { get; } = [];
        public List<ProjectDocumentationSection> Sections { get; } = [];
        public List<McpKnowledgeItemStat> Stats { get; } = [];
        public RecordingEmbedder Embedder { get; } = new();

        public McpSettingsData Settings { get; } = new()
        {
            EnableSemanticRetrieval = true,
            EnableGoldenExemplars = false,
            EnableLearning = false,
            EnableValueGrounding = false,
            GlossaryTopK = 5
        };

        public DocumentationTestContext Context => _context ?? throw new InvalidOperationException("Build the service first.");

        public KnowledgeGraphService Service(IBeaconEmbeddingService? embedder = null, int? budgetChars = null, bool forbidRowData = false)
        {
            foreach (var table in Tables)
            {
                table.DataSource = SalesDb;
                foreach (var column in table.Columns)
                {
                    column.DatabaseMetadata = table;
                }
            }

            _context = new DocumentationTestContext(this, SalesDb);
            var factory = new Mock<IDbContextFactory<BeaconContext>>();
            factory
                .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(_context);
            var settingsProvider = SettingsProviderMock.Create(Settings);
            settingsProvider
                .Setup(x => x.GetSettingsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Settings);
            var deployment = Options.Create(new McpDeploymentOptions
            {
                ForbidRowDataToLlm = forbidRowData,
                Ceilings = new McpCeilingOptions { AskSchemaContextChars = budgetChars }
            });

            return new KnowledgeGraphService(
                factory.Object,
                settingsProvider.Object,
                SettingsProviderMock.Unshared(settingsProvider.Object),
                embedder ?? Embedder,
                Mock.Of<ISchemaGraphService>(x => x.GetGraphAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())
                    == Task.FromResult(SchemaGraph.Build(Array.Empty<TableMetadataDto>(), Array.Empty<SchemaRelationshipEdge>()))),
                Mock.Of<IValueGroundingService>(),
                TestConfiguredDatabases.None,
                new QueryGuardrailService(),
                NullLogger<KnowledgeGraphService>.Instance,
                deploymentOptions: deployment);
        }

        public KnowledgeGraphService Service(int budgetChars) => Service(embedder: null, budgetChars: budgetChars);
    }

    /// <summary>The <see cref="FakeEmbeddingService"/>, recording every text it embeds.</summary>
    private sealed class RecordingEmbedder : IBeaconEmbeddingService
    {
        private readonly FakeEmbeddingService _inner = new();
        private readonly List<string> _texts = [];

        public bool IsAvailable => true;
        public int Dimensions => _inner.Dimensions;

        public IReadOnlyList<string> Texts
        {
            get
            {
                lock (_texts)
                {
                    return [.. _texts];
                }
            }
        }

        public Task<float[]> EmbedAsync(string text, CancellationToken ct)
        {
            lock (_texts)
            {
                _texts.Add(text);
            }

            return _inner.EmbedAsync(text, ct);
        }

        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct)
        {
            lock (_texts)
            {
                _texts.AddRange(texts);
            }

            return _inner.EmbedBatchAsync(texts, ct);
        }

        public void Reset()
        {
            lock (_texts)
            {
                _texts.Clear();
            }
        }
    }

    private sealed class UnavailableEmbeddingService : IBeaconEmbeddingService
    {
        public bool IsAvailable => false;
        public int Dimensions => 384;

        public Task<float[]> EmbedAsync(string text, CancellationToken ct) => throw new InvalidOperationException("No embedder.");

        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct) => throw new InvalidOperationException("No embedder.");
    }

    /// <summary>The world's sets, counting how often the chunk store and the item stats are queried.</summary>
    private sealed class DocumentationTestContext : BeaconContext
    {
        private static readonly DbContextOptions<DocumentationTestContext> ContextOptions =
            new DbContextOptionsBuilder<DocumentationTestContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(SqlCapture.NeverConnects)
                .Options;

        private readonly Dictionary<Type, object> _sets = [];
        private DatabaseFacade? _database;
        private int _chunkReads;
        private int _statsReads;

        public DocumentationTestContext(World world, DataSource dataSource) : base(ContextOptions, "beacon")
        {
            AddSet(new List<DataSource> { dataSource });
            AddSet(new List<ProjectDataSource> { new() { ProjectId = ProjectId, DataSourceId = DataSourceId } });
            AddSet(world.Tables);
            AddSet(world.Tables.SelectMany(x => x.Columns).ToList());
            AddSet(world.DescriptionItems);
            AddSet(world.Terms);
            AddSet(world.Embeddings);
            AddSet(world.Chunks);
            AddSet(world.Sections);
            AddSet(world.Stats);
            AddSet(new List<McpEvalCase>());
            AddSet(new List<McpLearnedPattern>());
        }

        public int ChunkReads => Volatile.Read(ref _chunkReads);
        public int StatsReads => Volatile.Read(ref _statsReads);

        public override DatabaseFacade Database => _database ??= new NonNpgsqlDatabaseFacade(this);

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(McpDocChunk))
            {
                Interlocked.Increment(ref _chunkReads);
            }

            if (typeof(TEntity) == typeof(McpKnowledgeItemStat))
            {
                Interlocked.Increment(ref _statsReads);
            }

            return _sets.TryGetValue(typeof(TEntity), out var set)
                ? (DbSet<TEntity>)set
                : base.Set<TEntity>();
        }

        public override int SaveChanges() => 0;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        private void AddSet<T>(List<T> rows) where T : class
        {
            _sets[typeof(T)] = BuildDbSet(rows).Object;
        }
    }

    // A provider other than Npgsql, so the nearest-neighbour searches run in memory (cosine over the stored vectors).
    private sealed class NonNpgsqlDatabaseFacade : DatabaseFacade
    {
        public NonNpgsqlDatabaseFacade(DbContext context) : base(context) { }

        public override string? ProviderName => "Microsoft.EntityFrameworkCore.SqlServer";
    }
}
