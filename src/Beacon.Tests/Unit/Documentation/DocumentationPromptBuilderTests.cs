using System.Text;
using System.Text.RegularExpressions;
using Beacon.AI.Services.Documentation;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using FluentAssertions;
using NUnit.Framework;
using Doc = Beacon.AI.Services.Documentation.ProjectDocumentationService;

namespace Beacon.Tests.Unit.Documentation;

/// <summary>
/// A project that fits the prompt budget gets the same prompt it always had, from every one of the eight builders: each
/// builder's output over one small project (all data kinds, an API source without endpoints) is pinned to a recorded file
/// (<c>PromptGolden/*.txt</c>; re-record with <c>BEACON_RECORD_PROMPT_GOLDEN=&lt;dir&gt;</c> and check the diff). A project
/// beyond the budget gets prompts cut at item boundaries with the count of what was left out. No database (§4.7).
/// </summary>
[TestFixture]
public class DocumentationPromptBuilderTests
{
    private static readonly (string Name, Func<Doc.DocumentationContext, string> Build)[] Builders =
    [
        ("overview", Doc.BuildProjectOverviewPrompt),
        ("domains", Doc.BuildBusinessDomainsPrompt),
        ("datamodel", Doc.BuildDataModelPrompt),
        ("dataflows", Doc.BuildDataFlowsPrompt),
        ("lineage", Doc.BuildCodeLineagePrompt),
        ("quality", Doc.BuildDataQualityPrompt),
        ("api", Doc.BuildApiDocumentationPrompt),
        ("glossary", x => Doc.BuildGlossaryPrompt(x, Sections()))
    ];

    [TestCaseSource(nameof(BuilderNames))]
    public void ASmallProjectsPrompt_IsThePromptItAlwaysHad(string name)
    {
        // The display name of the API source type depends on which connectors the process registered: pin the rest.
        var actual = Regex.Replace(Builders.Single(x => x.Name == name).Build(SmallProject()).Replace("\r\n", "\n"), @"^(- (?:billing|empty)-api) \(.*?\)", "$1 (Api)", RegexOptions.Multiline);

        var recordTo = Environment.GetEnvironmentVariable("BEACON_RECORD_PROMPT_GOLDEN");
        if (!string.IsNullOrEmpty(recordTo))
        {
            File.WriteAllText(Path.Combine(recordTo, name + ".txt"), actual);
        }

        var expected = File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Unit", "Documentation", "PromptGolden", name + ".txt")).Replace("\r\n", "\n");
        actual.Should().Be(expected);
        actual.Should().NotContain("not shown");
    }

    public static IEnumerable<string> BuilderNames() => Builders.Select(x => x.Name);

    private static List<ProjectDocumentationSection> Sections() =>
    [
        new() { Title = "Project Overview", Content = "An overview long enough to be excerpted into the glossary prompt of this small project." },
        new() { Title = "Short", Content = "too short" }
    ];

    [Test]
    public void ADataModelBeyondTheBudget_IsCutAtATableBoundary_WithTheCountOfTheRest()
    {
        const int total = 800;
        var tables = Enumerable.Range(0, total).Select(i => WideTable(i, 12)).ToList();

        var prompt = Doc.BuildDataModelPrompt(new Doc.DocumentationContext { ProjectName = "p", Tables = tables });

        var shown = Count(prompt, "### public.");
        AssertCutBetweenItems(prompt, shown, total, "### public.", maxItemLength: 1_500);
        prompt.Should().Contain($"table_{shown - 1:D4}").And.NotContain($"table_{shown:D4}");
    }

    [Test]
    public void DataFlowsBeyondTheBudget_AreCutAtATableBoundary_WithTheCountOfTheRest()
    {
        const int total = 700;
        var references = Enumerable.Range(0, total)
            .Select(i => new Doc.CodeRefInfo("src/File.cs", i, CodeReferenceType.DapperQuery, "public", $"table_{i:D4}", null, new string('x', 400), "Service", "Method"))
            .ToList();

        var prompt = Doc.BuildDataFlowsPrompt(new Doc.DocumentationContext { ProjectName = "p", CodeReferences = references });

        var shown = Count(prompt, "### Table: ");
        AssertCutBetweenItems(prompt, shown, total, "### Table: ", maxItemLength: 700);
    }

    [Test]
    public void AGlossaryBeyondTheBudget_IsCutAtATableBoundary_WithTheCountOfTheRest()
    {
        const int total = 100;
        var tables = Enumerable.Range(0, total).Select(i => WideTable(i, 150)).ToList();

        var prompt = Doc.BuildGlossaryPrompt(new Doc.DocumentationContext { ProjectName = "p", Tables = tables }, []);

        var shown = Count(prompt, "- public.table_");
        AssertCutBetweenItems(prompt, shown, total, "- public.table_", maxItemLength: 8_000);
    }

    private static void AssertCutBetweenItems(string prompt, int shown, int total, string itemMarker, int maxItemLength)
    {
        shown.Should().BeInRange(1, total - 1);
        prompt.TrimEnd().Should().EndWith($"+{total - shown} more not shown (prompt size limit)");
        prompt.Length.Should().BeLessThanOrEqualTo(DocumentationPromptBudget.DefaultLimit);
        prompt.Length.Should().BeGreaterThan(DocumentationPromptBudget.DefaultLimit - DocumentationPromptBudget.Reserve - maxItemLength, "the prompt is filled up to one item short of the budget");
        Count(prompt, "not shown").Should().Be(1);
        prompt.Should().Contain($"{itemMarker}", "items keep their order from the first");
    }

    private static int Count(string text, string marker) => text.Split(marker).Length - 1;

    private static Doc.TableInfo WideTable(int index, int columns) =>
        new()
        {
            DataSourceId = 1,
            DataSourceName = "core",
            SchemaName = "public",
            TableName = $"table_{index:D4}",
            Columns = Enumerable.Range(0, columns).Select(c => new Doc.ColumnDetail($"column_{c}_of_table_{index:D4}_with_a_long_name", "text", true, false, null, null, null, null)).ToList()
        };

    [Test]
    public void TheBudget_SkipsAnOversizedItem_ButStillAddsALaterOneThatFits()
    {
        var sb = new StringBuilder();
        var budget = new DocumentationPromptBudget(sb, limit: 3_000);

        budget.TryAdd(new string('a', 5_000)).Should().BeFalse();
        budget.TryAdd("small one\n").Should().BeTrue();
        budget.AppendNote();

        sb.ToString().Should().Be("small one\n+1 more not shown (prompt size limit)" + Environment.NewLine);
        budget.Skipped.Should().Be(1);
    }

    [Test]
    public void TheBudget_WritesNoNote_WhenNothingWasLeftOut()
    {
        var sb = new StringBuilder();
        var budget = new DocumentationPromptBudget(sb);

        budget.TryAdd("item\n");
        budget.AppendNote();

        sb.ToString().Should().Be("item\n");
        budget.Skipped.Should().Be(0);
    }

    [Test]
    public void TheBudget_WritesAGroupsHeadingWithItsFirstItemThatFits_AndItsTrailerAfterThem()
    {
        var sb = new StringBuilder();
        var budget = new DocumentationPromptBudget(sb, limit: 3_000);

        budget.Heading("## group\n");
        budget.TryAdd(new string('a', 5_000));
        budget.TryAdd("fits\n");
        budget.TryAdd("fits too\n");
        budget.EndGroup("\n");

        sb.ToString().Should().Be("## group\nfits\nfits too\n\n");
    }

    [Test]
    public void TheBudget_LeavesOutAGroupWhoseItemsAllDidNotFit_HeadingAndTrailerIncluded()
    {
        var sb = new StringBuilder();
        var budget = new DocumentationPromptBudget(sb, limit: 3_000);

        budget.Heading("## group\n");
        budget.TryAdd(new string('a', 5_000));
        budget.EndGroup("\n");

        sb.ToString().Should().BeEmpty();
        budget.Skipped.Should().Be(1);
    }

    [Test]
    public void TheBudget_WritesTheHeadingAndTrailerOfAGroupWithNoItems()
    {
        var sb = new StringBuilder();
        var budget = new DocumentationPromptBudget(sb);

        budget.Heading("## empty\n");
        budget.EndGroup("\n");

        sb.ToString().Should().Be("## empty\n\n");
    }

    private static Doc.DocumentationContext SmallProject()
    {
        var orders = new Doc.TableInfo
        {
            DataSourceId = 1,
            DataSourceName = "core",
            SchemaName = "public",
            TableName = "orders",
            Description = "Customer orders",
            Columns =
            [
                new("id", "integer", false, true, null, null, "Primary key", null),
                new("customer_id", "integer", false, false, "public.customers", "id", null, null),
                new("status", "text", true, false, null, null, null, 20)
            ],
            Indexes = [new("ix_orders_customer", "customer_id", false), new("ux_orders_ref", "ref", true)]
        };
        var customers = new Doc.TableInfo
        {
            DataSourceId = 1,
            DataSourceName = "core",
            SchemaName = "public",
            TableName = "customers",
            Columns = [new("id", "integer", false, true, null, null, null, null)]
        };
        var endpoint = new Doc.TableInfo
        {
            DataSourceId = 2,
            DataSourceName = "billing-api",
            SchemaName = "api",
            TableName = "GET /invoices",
            Description = "Invoices",
            Columns = [new("number", "string", false, false, null, null, "Invoice number", null)]
        };

        return new Doc.DocumentationContext
        {
            ProjectName = "Shop",
            ProjectDescription = "A small shop",
            DataSources =
            [
                new(1, "core", DataSourceType.Database, DatabaseEngineType.PostgreSQL),
                new(2, "billing-api", DataSourceType.Api, null),
                new(3, "empty-api", DataSourceType.Api, null)
            ],
            Tables = [orders, customers, endpoint],
            Repositories = [new("https://example.test/shop.git", "main", 120, 14)],
            CodeReferences =
            [
                new("src/Orders.cs", 12, CodeReferenceType.DapperQuery, "public", "orders", "status", "INSERT INTO orders", "OrderService", "Create"),
                new("src/Orders.cs", 40, CodeReferenceType.EntityModel, "public", "orders", null, null, "OrderService", "Get"),
                new("src/Customers.cs", 7, CodeReferenceType.ApiEndpoint, "public", "customers", null, "GET /customers", "CustomerApi", "List"),
                new("src/Misc.cs", 3, CodeReferenceType.RawSql, null, null, null, null, null, null)
            ],
            QualityScores =
            [
                new(1, "public", "orders", 91.5, default),
                new(1, "public", "customers", 40, default)
            ],
            Contracts = [new(1, "public", "orders", [new(default, "status", "{\"allowed\":[\"a\"]}"), new(default, null, null)])],
            EnumValues = [new(1, "public", "orders", "status", [new("open", 10), new("closed", 2500)])]
        };
    }
}
