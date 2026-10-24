using System.Text;
using Beacon.AI.Services.Ai.AiActor;
using Beacon.AI.Services.Knowledge;
using Beacon.Core.Data.Enums;
using FluentAssertions;
using NUnit.Framework;
using Doc = Beacon.AI.Services.Documentation.ProjectDocumentationService;

namespace Beacon.Tests.Unit;

/// <summary>
/// Every place that renders tables and columns into a prompt keeps the exact text it had: the ask schema context (full and
/// compact tables), the AI actor schema context and the documentation prompts that list schema (data model, business
/// domains, API). One schema with composite keys, a composite and a cross-schema foreign key, descriptions, a column
/// whose sample values were withheld as PII, complete and partial sample values, enum values, a table without a primary
/// key and a long table is rendered by each and pinned to a recorded file (<c>SchemaPromptGolden/*.txt</c>; re-record
/// only on purpose with <c>BEACON_RECORD_SCHEMA_PROMPT_GOLDEN=&lt;golden dir&gt;</c> and check the diff).
/// </summary>
[TestFixture]
[SetCulture("")]
public class SchemaPromptGoldenTests
{
    [Test]
    public void AskFullTables_MatchTheGolden()
    {
        var sb = new StringBuilder();
        foreach (var table in Schema())
        {
            SchemaContextFormatter.AppendTableWithFullColumns(sb, table.Schema, table.Name, table.Description, AskColumns(table), table.IsApi);
        }

        MatchesGolden("ask-full.txt", sb.ToString());
    }

    [Test]
    public void AskCompactTables_MatchTheGolden()
    {
        var sb = new StringBuilder();
        foreach (var table in Schema())
        {
            SchemaContextFormatter.AppendTableCompact(sb, table.Schema, table.Name, table.Description, AskColumns(table), table.IsApi);
        }

        MatchesGolden("ask-compact.txt", sb.ToString());
    }

    [Test]
    public void AiActorSchemaContext_MatchesTheGolden()
    {
        MatchesGolden("ai-actor.txt", AiActorPrompts.FormatSchemaContext(AiActorTables()));
    }

    [Test]
    public void AiActorSchemaContext_OverItsTableLimit_MatchesTheGolden()
    {
        MatchesGolden("ai-actor-limited.txt", AiActorPrompts.FormatSchemaContext(AiActorTables(), maxTables: 2));
    }

    [Test]
    public void DocumentationDataModel_MatchesTheGolden()
    {
        MatchesGolden("docs-datamodel.txt", Doc.BuildDataModelPrompt(DocumentationContext()));
    }

    [Test]
    public void DocumentationBusinessDomains_MatchesTheGolden()
    {
        MatchesGolden("docs-domains.txt", Doc.BuildBusinessDomainsPrompt(DocumentationContext()));
    }

    [Test]
    public void DocumentationApi_MatchesTheGolden()
    {
        MatchesGolden("docs-api.txt", Doc.BuildApiDocumentationPrompt(DocumentationContext()));
    }

    [Test]
    public void TheDocumentationPrompts_RenderNoSampleValues_EvenWhenAColumnCarriesThem()
    {
        var ctx = DocumentationContext(withSamples: true);
        var prompts = new[] { Doc.BuildDataModelPrompt(ctx), Doc.BuildBusinessDomainsPrompt(ctx), Doc.BuildApiDocumentationPrompt(ctx) };

        ctx.Tables[0].Columns.Should().Contain(x => x.SampleValuesJson != null, "the columns carry sample values");
        foreach (var prompt in prompts)
        {
            prompt.Should().NotContain("@example.test").And.NotContain("A-1").And.NotContain("INV-");
        }

        prompts[0].Should().Be(Doc.BuildDataModelPrompt(DocumentationContext()));
        prompts[1].Should().Be(Doc.BuildBusinessDomainsPrompt(DocumentationContext()));
        prompts[2].Should().Be(Doc.BuildApiDocumentationPrompt(DocumentationContext()));
    }

    private static void MatchesGolden(string name, string actual)
    {
        actual = actual.Replace("\r\n", "\n");
        var recordTo = Environment.GetEnvironmentVariable("BEACON_RECORD_SCHEMA_PROMPT_GOLDEN");
        if (!string.IsNullOrEmpty(recordTo))
        {
            File.WriteAllText(Path.Combine(recordTo, name), actual);
        }

        var expected = File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Unit", "SchemaPromptGolden", name)).Replace("\r\n", "\n");
        actual.Should().Be(expected);
    }

    private sealed record TestColumn(
        string Name,
        string DataType,
        bool IsPrimaryKey = false,
        bool IsNullable = true,
        string? ForeignKeySchema = null,
        string? ForeignKeyTable = null,
        string? ForeignKeyColumn = null,
        string? Description = null,
        int? MaxLength = null,
        string? SampleValuesJson = null,
        bool SampleValuesComplete = false,
        bool IsForeignKeyFlag = false);

    private sealed record TestTable(string Schema, string Name, string? Description, bool IsApi, List<TestColumn> Columns);

    // The PII column's sample values never reach a formatter: the stored samples are withheld upstream, so it arrives
    // without any.
    private static List<TestTable> Schema() =>
    [
        new("sales", "order_lines", "One line per ordered item", false,
        [
            new("order_id", "integer", IsPrimaryKey: true, IsNullable: false, ForeignKeySchema: "sales", ForeignKeyTable: "sales.orders", ForeignKeyColumn: "id", Description: "Order the line belongs to"),
            new("tenant_id", "integer", IsPrimaryKey: true, IsNullable: false, ForeignKeySchema: "sales", ForeignKeyTable: "sales.orders", ForeignKeyColumn: "tenant_id"),
            new("line_no", "smallint", IsPrimaryKey: true, IsNullable: false),
            new("sku", "varchar", IsNullable: false, MaxLength: 32, SampleValuesJson: "[\"A-1\",\"B-2\"]"),
            new("status", "text", IsNullable: false, Description: "Line state", SampleValuesJson: "[\"open\",\"shipped\",\"returned\"]", SampleValuesComplete: true),
            new("amount", "numeric(12,2)", MaxLength: 12),
            new("contact_email", "varchar", MaxLength: 255, Description: "Buyer contact"),
            new("notes", "text", SampleValuesJson: "not json")
        ]),
        new("sales", "orders", null, false,
        [
            new("id", "bigint", IsPrimaryKey: true, IsNullable: false),
            new("tenant_id", "integer", IsPrimaryKey: true, IsNullable: false),
            new("customer_id", "integer", IsNullable: false, ForeignKeySchema: "crm", ForeignKeyTable: "crm.customers", ForeignKeyColumn: "id"),
            new("legacy_ref", "integer", IsForeignKeyFlag: true),
            new("placed_at", "timestamp with time zone", IsNullable: false)
        ]),
        new("audit", "events", "Append-only event log", false,
            Enumerable.Range(1, 30)
                .Select(x => new TestColumn($"c{x:D2}", x % 3 == 0 ? "text" : "integer", IsNullable: x % 2 == 0))
                .ToList()),
        new("api", "GET /invoices", "Invoices", true,
        [
            new("number", "string", IsNullable: false, Description: "Invoice number"),
            new("total", "number")
        ])
    ];

    private static List<SchemaColumn> AskColumns(TestTable table) =>
        table.Columns
            .Select(x =>
                new SchemaColumn(
                    x.Name, x.DataType, x.IsPrimaryKey, x.IsNullable, x.ForeignKeyTable, x.ForeignKeyColumn, x.Description,
                    x.MaxLength, x.SampleValuesJson, x.ForeignKeySchema, x.SampleValuesComplete))
            .ToList();

    // The AI actor builds its foreign-key reference from the stored flag and target, as AiActorService does.
    private static List<AiActorPrompts.TableSchemaInfo> AiActorTables() =>
        Schema()
            .Where(x => !x.IsApi)
            .Select(x =>
                new AiActorPrompts.TableSchemaInfo
                {
                    SchemaName = x.Schema,
                    TableName = x.Name,
                    Columns = x.Columns
                        .Select(y =>
                            new AiActorPrompts.ColumnSchemaInfo
                            {
                                Name = y.Name,
                                DataType = y.DataType,
                                IsPrimaryKey = y.IsPrimaryKey,
                                IsForeignKey = y.IsForeignKeyFlag || y.ForeignKeyTable != null,
                                IsNullable = y.IsNullable,
                                ForeignKeyReference = y.IsForeignKeyFlag || y.ForeignKeyTable != null ? $"{y.ForeignKeyTable}.{y.ForeignKeyColumn}" : null
                            })
                        .ToList()
                })
            .ToList();

    // The documentation context is loaded without sample values; withSamples gives its columns the ask's samples, plus
    // values on the PII column and the API fields, to show the documentation layouts never render them.
    private static Doc.DocumentationContext DocumentationContext(bool withSamples = false)
    {
        var tables = Schema()
            .Select(x =>
                new Doc.TableInfo
                {
                    DataSourceId = x.IsApi ? 2 : 1,
                    DataSourceName = x.IsApi ? "billing-api" : "core",
                    SchemaName = x.Schema,
                    TableName = x.Name,
                    Description = x.Description,
                    Columns = x.Columns
                        .Select(y => withSamples
                            ? new SchemaColumn(y.Name, y.DataType, y.IsPrimaryKey, y.IsNullable, y.ForeignKeyTable, y.ForeignKeyColumn, y.Description, y.MaxLength, y.SampleValuesJson ?? ExtraSamples(y.Name), y.ForeignKeySchema, y.SampleValuesComplete)
                            : new SchemaColumn(y.Name, y.DataType, y.IsPrimaryKey, y.IsNullable, y.ForeignKeyTable, y.ForeignKeyColumn, y.Description, y.MaxLength))
                        .ToList(),
                    Indexes = x.Name == "order_lines"
                        ? [new("ix_order_lines_sku", "sku", false), new("ux_order_lines_key", "order_id, tenant_id, line_no", true)]
                        : []
                })
            .ToList();

        return new Doc.DocumentationContext
        {
            ProjectName = "Shop",
            DataSources =
            [
                new(1, "core", DataSourceType.Database, DatabaseEngineType.PostgreSQL),
                new(2, "billing-api", DataSourceType.Api, null)
            ],
            Tables = tables,
            EnumValues =
            [
                new(1, "sales", "order_lines", "status", [new("open", 1_200), new("shipped", 30_000), new("returned", 7)]),
                new(1, "audit", "events", "c03", [new("a", 1)])
            ]
        };
    }

    private static string? ExtraSamples(string column) => column switch
    {
        "contact_email" => "[\"buyer@example.test\"]",
        "number" => "[\"INV-1\"]",
        _ => null
    };
}
