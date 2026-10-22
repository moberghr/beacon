using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Embeddings;
using Beacon.AI.Services.Knowledge;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Services.Metadata;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// The schema context an <c>ask</c> sends to the LLM stays within <c>Beacon:Mcp:Ceilings:AskSchemaContextChars</c>: under
/// the budget it is the context as rendered before the budget existed, byte for byte; over it whole table blocks are kept
/// in relevance order and one line names the tables left out. Runs the real <see cref="KnowledgeGraphService"/> over a
/// mocked <see cref="BeaconContext"/> backed by the async-queryable doubles (§4.7).
/// </summary>
[TestFixture]
public class AskSchemaContextBudgetTests
{
    private const int DataSourceId = 7;
    private const int ProjectId = 3;

    // ---------- the budget itself ----------

    [Test]
    public void Assemble_UnderTheBudget_RendersEveryBlockAndPartWithoutATailLine()
    {
        var sections = new[] { new SchemaContextSection("## Tables\n", [Block("a", 10), Block("b", 10)], HeadingWhenEmpty: true) };

        var result = AskSchemaContextBudget.Assemble("head\n", sections, [Part(SchemaContextPartKind.Coverage, "cover\n"), Part(SchemaContextPartKind.Glossary, "tail\n")], [], budgetChars: 1_000);

        result.Text.Should().Be("head\n## Tables\n" + Block("a", 10).Text + Block("b", 10).Text + "cover\ntail\n");
        result.TablesShown.Should().Be(2);
        result.TablesNotShown.Should().BeEmpty();
        result.PartsLeftOut.Should().BeEmpty();
    }

    [Test]
    public void Assemble_OverTheBudget_KeepsWholeBlocksInRankOrder_SkippingABlockThatDoesNotFit()
    {
        var blocks = new[] { Block("a", 1_000), Block("b", 3_000), Block("c", 1_000), Block("d", 300) };
        var sections = new[] { new SchemaContextSection("", blocks, HeadingWhenEmpty: false) };
        var budget = 10 + AskSchemaContextBudget.OmittedTablesLineMaxChars(blocks.Length) + 2_300;

        // "c" ranks first, then the existing order: c, a, b, d. c + a fit in the 2300 left after the head, the part and the
        // reserved tail line; b does not and is skipped, and d still fits after it.
        var result = AskSchemaContextBudget.Assemble("head\n", sections, [Part(SchemaContextPartKind.Glossary, "tail\n")], ["public.c"], budget);

        var line = AskSchemaContextBudget.OmittedTablesLine(["public.b"]);
        result.Text.Should().Be("head\n" + blocks[0].Text + blocks[2].Text + blocks[3].Text + line + Environment.NewLine + "tail\n");
        result.TablesShown.Should().Be(3);
        result.TablesNotShown.Should().Equal("public.b");
        result.Text.Length.Should().BeLessThanOrEqualTo(budget, "the tail line is counted inside the budget");
        line.Should().Be("-- 1 more tables not shown: public.b");
    }

    [Test]
    public void Assemble_OverTheBudget_LeavesOutTheHeadingOfASectionWithNoBlockShown()
    {
        var sections = new[]
        {
            new SchemaContextSection("## Relevant\n", [Block("a", 20)], HeadingWhenEmpty: true),
            new SchemaContextSection("## Other\n", [Block("b", 3_000)], HeadingWhenEmpty: false)
        };

        var result = AskSchemaContextBudget.Assemble("", sections, [], [], budgetChars: AskSchemaContextBudget.OmittedTablesLineMaxChars(2) + 21 + 25);

        result.Text.Should().NotContain("## Other");
        result.Text.Should().StartWith("## Relevant\n" + Block("a", 20).Text);
        result.TablesNotShown.Should().Equal("public.b");
    }

    [Test]
    public void Assemble_GroundingSectionsLeaveNoRoomForTheTopTable_LeavesThemOutInTheFixedOrderUntilItFits()
    {
        var top = Block("top", 100);
        var sections = new[] { new SchemaContextSection("", [Block("big", 5_000), top], HeadingWhenEmpty: false) };
        SchemaContextPart[] parts =
        [
            Part(SchemaContextPartKind.JoinPaths, Text('j', 50)),
            Part(SchemaContextPartKind.ValueMatches, Text('v', 50)),
            Part(SchemaContextPartKind.GoldenExemplars, Text('g', 50)),
            Part(SchemaContextPartKind.LearnedPatterns, Text('l', 50)),
            Part(SchemaContextPartKind.Glossary, Text('o', 50))
        ];

        // 250 characters for the parts and the table: the learned patterns and then the glossary go, and the rest stay.
        var result = AskSchemaContextBudget.Assemble("", sections, parts, ["public.top"], AskSchemaContextBudget.OmittedTablesLineMaxChars(2) + 250);

        result.PartsLeftOut.Should().Equal(SchemaContextPartKind.LearnedPatterns, SchemaContextPartKind.Glossary);
        result.TopTableCut.Should().BeFalse();
        result.TablesNotShown.Should().Equal("public.big");
        result.Text.Should().Contain(top.Text).And.Contain(Text('j', 50)).And.Contain(Text('v', 50)).And.Contain(Text('g', 50));
        result.Text.Should().NotContain(Text('l', 50)).And.NotContain(Text('o', 50));
    }

    [Test]
    public void Assemble_TheTopTableOverTheBudget_IsSkipped_AndASmallerBlockThatFitsIsShown()
    {
        var top = Block("top", 5_000);
        var small = Block("other", 100);
        var sections = new[] { new SchemaContextSection("", [top, small], HeadingWhenEmpty: false) };
        SchemaContextPart[] parts =
        [
            Part(SchemaContextPartKind.JoinPaths, Text('j', 50)),
            Part(SchemaContextPartKind.ValueMatches, Text('v', 50)),
            Part(SchemaContextPartKind.GoldenExemplars, Text('g', 50)),
            Part(SchemaContextPartKind.LearnedPatterns, Text('l', 50)),
            Part(SchemaContextPartKind.Glossary, Text('o', 50))
        ];
        var budget = AskSchemaContextBudget.OmittedTablesLineMaxChars(2) + 100;

        var result = AskSchemaContextBudget.Assemble("", sections, parts, [], budget);

        result.TablesShown.Should().Be(1);
        result.TopTableCut.Should().BeFalse();
        result.Text.Should().StartWith(small.Text).And.NotContain(top.Text);
        result.PartsLeftOut.Should().Equal(AskSchemaContextBudget.DropOrder);
        result.TablesNotShown.Should().Equal("public.top");
        result.Text.Should().Contain("public.top");
        result.Text.Length.Should().BeLessThanOrEqualTo(budget);
    }

    [Test]
    public void Assemble_EveryBlockOverTheBudget_ShowsTheTopRankedOneCutAtALineBoundaryWithAMarker()
    {
        var top = LinesBlock("top", 200, 40);
        var other = LinesBlock("other", 100, 40);
        var sections = new[] { new SchemaContextSection("", [other, top], HeadingWhenEmpty: false) };
        var budget = AskSchemaContextBudget.OmittedTablesLineMaxChars(2) + 1_000;

        var result = AskSchemaContextBudget.Assemble("", sections, [], ["public.top"], budget);

        var marker = $"-- public.top: more columns not shown{Environment.NewLine}";
        result.TopTableCut.Should().BeTrue();
        result.TablesShown.Should().Be(1);
        result.TablesNotShown.Should().Equal("public.other");
        result.Text.Should().StartWith("t");
        result.Text.Should().Contain(marker);
        var cutBody = result.Text[..result.Text.IndexOf(marker, StringComparison.Ordinal)];
        top.Text.Should().StartWith(cutBody);
        cutBody.Should().EndWith("\n").And.NotBeEmpty();
        result.Text.Should().Contain(AskSchemaContextBudget.OmittedTablesLine(["public.other"]));
        result.Text.Length.Should().BeLessThanOrEqualTo(budget);
    }

    [Test]
    public void Assemble_RandomBlockSizes_NeverExceedTheBudget()
    {
        var random = new Random(12345);
        for (var round = 0; round < 300; round++)
        {
            var blocks = Enumerable.Range(0, random.Next(1, 12))
                .Select(x => LinesBlock($"t{x}", random.Next(20, 4_000), random.Next(10, 120)))
                .ToList();
            var sections = new[] { new SchemaContextSection("## Tables\n", blocks, HeadingWhenEmpty: true) };
            SchemaContextPart[] parts =
            [
                Part(SchemaContextPartKind.JoinPaths, Text('j', random.Next(10, 3_000))),
                Part(SchemaContextPartKind.Glossary, Text('o', random.Next(10, 3_000)))
            ];
            var budget = random.Next(10_000, 20_000) / 10 + AskSchemaContextBudget.OmittedTablesLineMaxChars(blocks.Count);

            var result = AskSchemaContextBudget.Assemble("head\n", sections, parts, [], budget);

            result.Text.Length.Should().BeLessThanOrEqualTo(budget, $"round {round}");
            result.TablesShown.Should().BeGreaterThan(0);
        }
    }

    [Test]
    public void Assemble_TablesLeftOut_DropsTheCoverageNote_SoTheTailLineIsTheOneStatement()
    {
        var sections = new[] { new SchemaContextSection("", [Block("a", 40), Block("b", 3_000)], HeadingWhenEmpty: false) };
        var coverage = Part(SchemaContextPartKind.Coverage, "## Coverage\n  Detailed tables truncated at 2\n");

        var result = AskSchemaContextBudget.Assemble("", sections, [coverage], [], AskSchemaContextBudget.OmittedTablesLineMaxChars(2) + coverage.Text.Length + 40);

        result.TablesNotShown.Should().Equal("public.b");
        result.Text.Should().NotContain("## Coverage");
        result.Text.Should().Contain("-- 1 more tables not shown: public.b");
    }

    [Test]
    public void Assemble_RanksByTheNamesGiven_IgnoringNamesNoBlockHas()
    {
        var blocks = new[] { Block("a", 3_000), Block("b", 3_000), Block("c", 40) };
        var sections = new[] { new SchemaContextSection("", blocks, HeadingWhenEmpty: false) };

        var result = AskSchemaContextBudget.Assemble("", sections, [], ["public.missing", "PUBLIC.C", "public.c"], AskSchemaContextBudget.OmittedTablesLineMaxChars(3) + 40);

        result.Text.Should().StartWith(blocks[2].Text);
        result.TablesNotShown.Should().Equal("public.a", "public.b");
    }

    [Test]
    public void OmittedTablesLine_CapsTheNameListWithAnEllipsis_WithinTheReservedLength()
    {
        var names = Enumerable.Range(1, 400)
            .Select(x => $"warehouse_schema.fact_table_{x:D4}")
            .ToList();

        var line = AskSchemaContextBudget.OmittedTablesLine(names);

        line.Should().StartWith("-- 400 more tables not shown: warehouse_schema.fact_table_0001, ");
        line.Should().EndWith(", …");
        (line.Length + Environment.NewLine.Length).Should().BeLessThanOrEqualTo(AskSchemaContextBudget.OmittedTablesLineMaxChars(names.Count));
    }

    [Test]
    public void TablesNamedIn_FindsTablesTheQuestionNames_AsWholeWords()
    {
        var blocks = new[] { Block("orders", 1), Block("order_items", 1), Block("regions", 1), Block("customers", 1) };

        var named = AskSchemaContextBudget.TablesNamedIn("How many ORDERS per region?", blocks);

        named.Should().Equal("public.orders");
    }

    [Test]
    public void TablesNamedIn_AQualifiedName_NamesOnlyTheTableInThatSchema()
    {
        var blocks = new[] { Block("orders", 1), Block("orders", 1, schema: "archive"), Block("totals", 1) };

        TablesNamedInOrder("rows in archive.orders", blocks).Should().Equal("archive.orders");
        TablesNamedInOrder("sum of orders.amount", blocks).Should().Equal("public.orders", "archive.orders");
        TablesNamedInOrder("from db.archive.orders", blocks).Should().Equal("archive.orders");
    }

    [Test]
    public void TablesNamedIn_DoesNotTakeSqlKeywordsInTheQuestion_ForATableNamedLikeOne()
    {
        var blocks = new[] { Block("order", 1), Block("orders", 1) };

        AskSchemaContextBudget.TablesNamedIn("orders by date, newest first", blocks).Should().Equal("public.orders");
    }

    [Test]
    public void NotShownTablesIn_MatchesQuotedUnqualifiedThreePartAndApiNames()
    {
        IReadOnlyList<string> notShown = ["public.regions", "sales.order", "invoices"];

        SchemaContextNotShownTables.NotShownTablesIn(["\"public\".\"regions\""], notShown).Should().Equal("public.regions");
        SchemaContextNotShownTables.NotShownTablesIn(["[sales].[order]"], notShown).Should().Equal("sales.order");
        SchemaContextNotShownTables.NotShownTablesIn(["regions"], notShown).Should().Equal("public.regions");
        SchemaContextNotShownTables.NotShownTablesIn(["warehouse.public.regions"], notShown).Should().Equal("public.regions");
        SchemaContextNotShownTables.NotShownTablesIn(["`api`.`invoices`", "INVOICES"], notShown).Should().Equal("invoices");
        SchemaContextNotShownTables.NotShownTablesIn(["other.regions", "public.sales", "order"], notShown).Should().Equal("sales.order");
    }

    // ---------- the ask context ----------

    [Test]
    public async Task SmartContext_SmallSchemaUnderTheBudget_IsTheUnbudgetedRendering()
    {
        var tables = Tables(3, descriptionChars: 20);

        var result = await Service(tables).GetSmartContextForAskAsync(DataSourceId, ProjectId, "how many rows", liveValueProbes: true, CancellationToken.None);

        var expected = new StringBuilder();
        expected.AppendLine("# Data Source: Sales DB (PostgreSQL)");
        expected.AppendLine("Tables: 3\n");
        foreach (var table in tables)
        {
            SchemaContextFormatter.AppendTableWithFullColumns(expected, table.SchemaName, table.TableName, table.TableDescription, Columns(table), isApi: false);
        }

        result.FullContext.Should().Be(expected.ToString());
        result.TablesShown.Should().Be(3);
        result.TablesNotShown.Should().BeEmpty();
    }

    [Test]
    public async Task SmartContext_LargeSchemaUnderTheBudget_IsTheUnbudgetedRendering()
    {
        var tables = Tables(45, descriptionChars: 20);
        tables.Add(Table("public", "orders", 20));

        var result = await Service(tables).GetSmartContextForAskAsync(DataSourceId, ProjectId, "orders last week", liveValueProbes: true, CancellationToken.None);

        var detailed = new HashSet<string>(result.RelevantTables, StringComparer.OrdinalIgnoreCase);
        var ordered = tables
            .OrderBy(x => x.SchemaName)
            .ThenBy(x => x.TableName)
            .ToList();
        var expected = new StringBuilder();
        expected.AppendLine("# Data Source: Sales DB (PostgreSQL)");
        expected.AppendLine($"Total tables: {tables.Count}\n");
        expected.AppendLine("## Relevant Tables (full schema)\n");
        foreach (var table in ordered.Where(x => detailed.Contains($"{x.SchemaName}.{x.TableName}")))
        {
            SchemaContextFormatter.AppendTableWithFullColumns(expected, table.SchemaName, table.TableName, table.TableDescription, Columns(table), isApi: false);
        }

        expected.AppendLine("\n## Other Tables (name and primary keys only)\n");
        foreach (var table in ordered.Where(x => !detailed.Contains($"{x.SchemaName}.{x.TableName}")))
        {
            SchemaContextFormatter.AppendTableCompact(expected, table.SchemaName, table.TableName, table.TableDescription, Columns(table), isApi: false);
        }

        detailed.Should().Contain("public.orders");
        result.FullContext.Should().Be(expected.ToString());
        result.TablesNotShown.Should().BeEmpty();
    }

    [Test]
    public async Task SmartContext_OverTheConfiguredBudget_KeepsTheTableTheQuestionNamesFirst_ThenTheExistingOrder()
    {
        var tables = Tables(10, descriptionChars: 200);
        tables.Add(Table("zz", "zebra_events", 200));
        var blockChars = Rendered(tables[0]).Length;

        var result = await Service(tables, budgetChars: AskSchemaContextBudget.OmittedTablesLineMaxChars(tables.Count) + 100 + 3 * blockChars)
            .GetSmartContextForAskAsync(DataSourceId, ProjectId, "count zebra_events per day", liveValueProbes: true, CancellationToken.None);

        result.FullContext.Should().Contain(Rendered(tables[^1]), "the table the question names ranks first");
        result.FullContext.Should().Contain(Rendered(tables[0]));
        result.FullContext.Should().Contain(Rendered(tables[1]));
        result.FullContext.Should().NotContain("### public.t0003");
        result.TablesShown.Should().Be(3);
        result.TablesNotShown.Should().HaveCount(8);
        result.TablesNotShown[0].Should().Be("public.t0003");
        result.FullContext.Should().Contain("-- 8 more tables not shown: public.t0003, public.t0004");
    }

    [Test]
    public async Task SmartContext_FiveHundredTables_StaysWithinTheDefaultBudget_TailLineIncluded()
    {
        var tables = Tables(498, descriptionChars: 300);
        tables.Add(Table("sales", "orders", 300));
        tables.Add(Table("sales", "carrier", 300));

        var result = await Service(tables).GetSmartContextForAskAsync(DataSourceId, ProjectId, "orders shipped per carrier", liveValueProbes: true, CancellationToken.None);

        var tailLine = result.FullContext
            .Split(Environment.NewLine)
            .Single(x => x.StartsWith("-- ", StringComparison.Ordinal) && x.Contains("more tables not shown"));
        result.FullContext.Length.Should().BeLessThanOrEqualTo(120_000);
        tailLine.Length.Should().BeLessThanOrEqualTo(AskSchemaContextBudget.OmittedNamesMaxChars + 50);
        tailLine.Should().StartWith($"-- {result.TablesNotShown.Count} more tables not shown: ");
        result.TablesShown.Should().Be(500 - result.TablesNotShown.Count);
        result.TablesNotShown.Should().NotBeEmpty();
        result.TablesNotShown.Should().NotContain(["sales.orders", "sales.carrier"], "the tables retrieval ranked for the question are kept");

        var detailed = new HashSet<string>(result.RelevantTables, StringComparer.OrdinalIgnoreCase);
        var omitted = new HashSet<string>(result.TablesNotShown, StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables.Where(x => !omitted.Contains($"{x.SchemaName}.{x.TableName}")))
        {
            var block = new StringBuilder();
            if (detailed.Contains($"{table.SchemaName}.{table.TableName}"))
            {
                SchemaContextFormatter.AppendTableWithFullColumns(block, table.SchemaName, table.TableName, table.TableDescription, Columns(table), isApi: false);
            }
            else
            {
                SchemaContextFormatter.AppendTableCompact(block, table.SchemaName, table.TableName, table.TableDescription, Columns(table), isApi: false);
            }

            result.FullContext.Should().Contain(block.ToString(), "a table is shown whole or not at all");
        }
    }

    [Test]
    public async Task SmartContext_ApiSourceOverTheBudget_KeepsTheTablesRetrievalRanksFirst()
    {
        // Retrieval ranks zz_invoice_total (two question terms) above aa_invoice (one); neither is named as a whole word, and
        // the catalogue order puts aa_invoice first. Only one of them fits.
        var tables = Enumerable.Range(1, 45)
            .Select(x => Table("api", $"t{x:D4}", 3_000, Billing))
            .ToList();
        tables.Add(Table("api", "aa_invoice", 20, Billing));
        tables.Add(Table("api", "zz_invoice_total", 20, Billing));
        var head = new StringBuilder();
        head.AppendLine("# REST API: Billing API");
        head.AppendLine($"Total tables: {tables.Count}\n");
        var headings = "## Relevant Tables (full schema)\n".Length + Environment.NewLine.Length
            + "\n## Other Tables (name and primary keys only)\n".Length + Environment.NewLine.Length;
        var budget = head.Length + headings + AskSchemaContextBudget.OmittedTablesLineMaxChars(tables.Count)
            + Rendered(tables[^1], isApi: true).Length + 10;

        var result = await Service(tables, budget, Billing).GetSmartContextForAskAsync(DataSourceId, ProjectId, "invoice total by day", liveValueProbes: true, CancellationToken.None);

        result.UsedSmartRetrieval.Should().BeTrue();
        result.FullContext.Should().Contain(Rendered(tables[^1], isApi: true));
        result.TablesNotShown.Should().Contain("aa_invoice").And.NotContain("zz_invoice_total");
        result.TablesNotShown[0].Should().Be("aa_invoice", "the other table retrieval found ranks next");
    }

    // ---------- the ceiling ----------

    [Test]
    public void AskSchemaContextChars_DefaultsTo120000_AndHonoursAConfiguredValue()
    {
        new McpCeilingOptions().EffectiveAskSchemaContextChars.Should().Be(120_000);
        new McpCeilingOptions { AskSchemaContextChars = 50_000 }.EffectiveAskSchemaContextChars.Should().Be(50_000);
    }

    [TestCase(0, false)]
    [TestCase(-1, false)]
    [TestCase(9_999, false)]
    [TestCase(10_000, true)]
    [TestCase(500_000, true)]
    public void AskSchemaContextChars_ValidatorRejectsACeilingBelow10000(int value, bool valid)
    {
        var result = new McpDeploymentOptionsValidator().Validate(null, new McpDeploymentOptions { Ceilings = new McpCeilingOptions { AskSchemaContextChars = value } });

        result.Succeeded.Should().Be(valid);
        if (!valid)
        {
            result.FailureMessage.Should().Contain("Beacon:Mcp:Ceilings:AskSchemaContextChars");
        }
    }

    private static SchemaContextTableBlock Block(string table, int chars, string schema = "public")
    {
        return new SchemaContextTableBlock($"{schema}.{table}", schema, table, Text(table[0], chars));
    }

    private static SchemaContextTableBlock LinesBlock(string table, int lines, int lineChars)
    {
        var text = string.Concat(Enumerable.Repeat(new string(table[0], lineChars - 1) + "\n", lines));

        return new SchemaContextTableBlock($"public.{table}", "public", table, text);
    }

    private static SchemaContextPart Part(SchemaContextPartKind kind, string text)
    {
        return new SchemaContextPart(kind, text);
    }

    private static string Text(char fill, int chars)
    {
        return new string(fill, chars - 1) + "\n";
    }

    private static IReadOnlyList<string> TablesNamedInOrder(string question, IReadOnlyList<SchemaContextTableBlock> blocks)
    {
        return AskSchemaContextBudget.TablesNamedIn(question, blocks);
    }

    private static string Rendered(DatabaseMetadata table, bool isApi = false)
    {
        var sb = new StringBuilder();
        SchemaContextFormatter.AppendTableWithFullColumns(sb, table.SchemaName, table.TableName, table.TableDescription, Columns(table), isApi);
        return sb.ToString();
    }

    private static List<SchemaColumn> Columns(DatabaseMetadata table)
    {
        return table.Columns
            .Select(x => new SchemaColumn(x.ColumnName, x.DataType, x.IsPrimaryKey, x.IsNullable, x.ForeignKeyTable, x.ForeignKeyColumn, x.Description))
            .ToList();
    }

    private static readonly DataSource SalesDb = new()
    {
        Id = DataSourceId,
        Name = "Sales DB",
        DataSourceType = DataSourceType.Database,
        DatabaseEngineType = DatabaseEngineType.PostgreSQL,
        EncryptedConnectionData = "unused-in-test"
    };

    private static readonly DataSource Billing = new()
    {
        Id = DataSourceId,
        Name = "Billing API",
        DataSourceType = DataSourceType.Api,
        DatabaseEngineType = DatabaseEngineType.PostgreSQL,
        EncryptedConnectionData = "unused-in-test"
    };

    private static List<DatabaseMetadata> Tables(int count, int descriptionChars)
    {
        return Enumerable.Range(1, count)
            .Select(x => Table("public", $"t{x:D4}", descriptionChars))
            .ToList();
    }

    private static DatabaseMetadata Table(string schema, string name, int descriptionChars, DataSource? dataSource = null)
    {
        var id = Math.Abs(StringComparer.Ordinal.GetHashCode(schema + "." + name)) % 1_000_000;
        return new DatabaseMetadata
        {
            Id = id,
            DataSourceId = DataSourceId,
            DataSource = dataSource ?? SalesDb,
            SchemaName = schema,
            TableName = name,
            TableDescription = new string('d', descriptionChars),
            Columns = Enumerable.Range(1, 6)
                .Select(x => new ColumnMetadata
                {
                    Id = id * 10 + x,
                    ColumnName = x == 1 ? "id" : $"col_{x}",
                    DataType = "integer",
                    IsPrimaryKey = x == 1,
                    IsNullable = x != 1
                })
                .ToList()
        };
    }

    private static KnowledgeGraphService Service(List<DatabaseMetadata> tables, int? budgetChars = null, DataSource? dataSource = null)
    {
        var settings = new McpSettingsData
        {
            EnableLearning = false,
            EnableSemanticRetrieval = false,
            EnableGoldenExemplars = false,
            EnableValueGrounding = false
        };
        var context = new BudgetTestContext(dataSource ?? SalesDb, tables);
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);
        var settingsProvider = SettingsProviderMock.Create(settings);
        var deployment = budgetChars == null
            ? null
            : Options.Create(new McpDeploymentOptions { Ceilings = new McpCeilingOptions { AskSchemaContextChars = budgetChars } });

        return new KnowledgeGraphService(
            factory.Object,
            settingsProvider.Object,
            SettingsProviderMock.Unshared(settingsProvider.Object),
            Mock.Of<IBeaconEmbeddingService>(x => x.IsAvailable == false),
            Mock.Of<ISchemaGraphService>(x => x.GetGraphAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())
                == Task.FromResult(SchemaGraph.Build(Array.Empty<TableMetadataDto>(), Array.Empty<SchemaRelationshipEdge>()))),
            Mock.Of<IValueGroundingService>(),
            TestConfiguredDatabases.None,
            new QueryGuardrailService(),
            NullLogger<KnowledgeGraphService>.Instance,
            deploymentOptions: deployment);
    }

    private static Mock<DbSet<T>> BuildDbSet<T>(IEnumerable<T> data) where T : class
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

    /// <summary>Data source linked to the caller's project, its metadata, and empty grounding sets.</summary>
    private sealed class BudgetTestContext : BeaconContext
    {
        private static readonly DbContextOptions<BudgetTestContext> ContextOptions =
            new DbContextOptionsBuilder<BudgetTestContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly Dictionary<Type, object> _sets;
        private DatabaseFacade? _database;

        public BudgetTestContext(DataSource dataSource, List<DatabaseMetadata> metadata) : base(ContextOptions, "beacon")
        {
            _sets = new Dictionary<Type, object>
            {
                [typeof(DataSource)] = BuildDbSet(new List<DataSource> { dataSource }).Object,
                [typeof(DatabaseMetadata)] = BuildDbSet(metadata).Object,
                [typeof(ProjectDataSource)] = BuildDbSet(new List<ProjectDataSource> { new() { ProjectId = ProjectId, DataSourceId = dataSource.Id } }).Object,
                [typeof(McpLearnedPattern)] = BuildDbSet(new List<McpLearnedPattern>()).Object,
                [typeof(McpEvalCase)] = BuildDbSet(new List<McpEvalCase>()).Object,
                [typeof(McpEmbedding)] = BuildDbSet(new List<McpEmbedding>()).Object,
                [typeof(McpGlossaryTerm)] = BuildDbSet(new List<McpGlossaryTerm>()).Object,
                [typeof(ColumnMetadata)] = BuildDbSet(new List<ColumnMetadata>()).Object,
                [typeof(ProjectDocumentationSection)] = BuildDbSet(new List<ProjectDocumentationSection>()).Object
            };
        }

        public override DatabaseFacade Database => _database ??= new NonNpgsqlDatabaseFacade(this);

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            return _sets.TryGetValue(typeof(TEntity), out var set)
                ? (DbSet<TEntity>)set
                : base.Set<TEntity>();
        }

        public override int SaveChanges() => 0;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    private sealed class NonNpgsqlDatabaseFacade : DatabaseFacade
    {
        public NonNpgsqlDatabaseFacade(DbContext context) : base(context) { }

        public override string? ProviderName => "Microsoft.EntityFrameworkCore.SqlServer";
    }
}
