using Beacon.AI.Services.Knowledge;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Projects;
using Beacon.Tests.Common;
using Beacon.Tests.Unit.Documentation;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// SC10: a documentation section whose generation failed holds a fixed text (<see cref="FailedDocumentationSection"/>), not
/// documentation. It is left out of the Markdown export (which get_documentation and knowledge answers serve), keyword
/// search (filtered in SQL), the table documentation read (nothing when the newest Data Model failed, never an older
/// one) and the doc-chunk index — the chunk-index case, with the pruning
/// of the chunks the section had before, is in <see cref="DocChunkIndexingServiceTests"/>, whose fixture builds the
/// indexer. The page read still shows it, so a person sees what to regenerate. No database (§4.7); the SQL is checked with
/// <c>ToQueryString()</c> on both providers.
/// </summary>
[TestFixture]
public class FailedSectionTests
{
    private const string Failed = FailedDocumentationSection.Content;

    private static readonly DateTime Earlier = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = new(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task TheExport_ThatGetDocumentationServes_LeavesOutAFailedSection()
    {
        var store = new DocumentationStore();
        store.AddDocumentation(
            30,
            Later,
            (ProjectDocSectionType.ProjectOverview, "Project Overview", "The shop sells books."),
            (ProjectDocSectionType.DataModel, "Data Model", Failed),
            (ProjectDocSectionType.DataFlows, "Data Flows", "Orders flow to invoices."));

        var markdown = await store.Service().ExportLatestToMarkdownAsync(DocumentationStore.ProjectId, CancellationToken.None);

        markdown.Should().Contain("## Project Overview").And.Contain("The shop sells books.");
        markdown.Should().Contain("## Data Flows").And.Contain("Orders flow to invoices.");
        markdown.Should().NotContain("## Data Model").And.NotContain("Generation failed");
    }

    [Test]
    public async Task TheExport_KnowledgeAnswersRead_LeavesOutAFailedSection()
    {
        var store = new DocumentationStore();
        store.AddDocumentation(
            30,
            Later,
            (ProjectDocSectionType.ProjectOverview, "Project Overview", Failed),
            (ProjectDocSectionType.DataFlows, "Data Flows", "Orders flow to invoices."));

        var markdown = await store.Service().ExportLatestToMarkdownAsync(DocumentationStore.ProjectId, ProjectDocSectionTypes.ListingValues, CancellationToken.None);

        markdown.Should().Contain("## Data Flows").And.NotContain("## Project Overview").And.NotContain("Generation failed");
    }

    [Test]
    public async Task TheSearch_NeverReturnsAFailedSection()
    {
        var store = new DocumentationStore();
        store.AddDocumentation(
            30,
            Later,
            (ProjectDocSectionType.ProjectOverview, "Project Overview", "Orders are placed online."),
            (ProjectDocSectionType.DataModel, "Data Model", Failed));
        await using var context = store.Context();

        // "generation" matches the fixed text: without the filter the failed section would be a hit.
        var rows = await KnowledgeGraphService.BuildDocSectionsQuery(context, DocumentationStore.ProjectId, ["generation", "orders"], 10)
            .ToListAsync();

        rows.Select(x => x.Title).Should().Equal("Project Overview");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TheSearch_LeavesOutFailedSectionsInSql_Translates(bool sqlServer)
    {
        using var context = sqlServer ? (BeaconContext)SqlServerTestContext.Create() : NpgsqlTestContext.Create();

        var sql = KnowledgeGraphService.BuildDocSectionsQuery(context, DocumentationStore.ProjectId, ["orders"], 10).ToQueryString();

        sql.Should().MatchRegex(@"<> N?'\*Generation failed for this section\. Please regenerate\.\*'");
    }

    // The newest generation's Data Model failed: nothing is read, never the older generation's section in its place.
    [Test]
    public async Task TheTableDocumentationRead_WhenTheNewestDataModelFailed_ReadsNothing_NotAnOlderOne()
    {
        var store = new DocumentationStore();
        store.AddDocumentation(30, Earlier, (ProjectDocSectionType.DataModel, "Data Model", "Orders belong to customers."));
        store.AddDocumentation(31, Later, (ProjectDocSectionType.DataModel, "Data Model", Failed));
        await using var context = store.Context();

        var content = await KnowledgeGraphService.BuildTableDocumentationQuery(context, DocumentationStore.ProjectId, DocumentationStore.DataSourceId)
            .FirstOrDefaultAsync();

        content.Should().BeNull();
    }

    [Test]
    public async Task TheTableDocumentationRead_ReadsTheNewestDataModel()
    {
        var store = new DocumentationStore();
        store.AddDocumentation(30, Earlier, (ProjectDocSectionType.DataModel, "Data Model", Failed));
        store.AddDocumentation(31, Later, (ProjectDocSectionType.DataModel, "Data Model", "Orders belong to customers."));
        await using var context = store.Context();

        var content = await KnowledgeGraphService.BuildTableDocumentationQuery(context, DocumentationStore.ProjectId, DocumentationStore.DataSourceId)
            .FirstOrDefaultAsync();

        content.Should().Be("Orders belong to customers.");
    }

    [Test]
    public async Task TheTableDocumentationRead_WithOnlyAFailedDataModel_ReadsNothing()
    {
        var store = new DocumentationStore();
        store.AddDocumentation(31, Later, (ProjectDocSectionType.DataModel, "Data Model", Failed));
        await using var context = store.Context();

        var content = await KnowledgeGraphService.BuildTableDocumentationQuery(context, DocumentationStore.ProjectId, DocumentationStore.DataSourceId)
            .FirstOrDefaultAsync();

        content.Should().BeNull();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TheTableDocumentationRead_LeavesOutFailedSectionsInSql_Translates(bool sqlServer)
    {
        using var context = sqlServer ? (BeaconContext)SqlServerTestContext.Create() : NpgsqlTestContext.Create();

        var sql = KnowledgeGraphService.BuildTableDocumentationQuery(context, DocumentationStore.ProjectId, DocumentationStore.DataSourceId).ToQueryString();

        // The newest section is picked first (LIMIT / TOP inside), the failed text is filtered on that one row outside.
        sql.Should().MatchRegex(@"(?s)(TOP\(@p\)|LIMIT @p).*\) AS \[?s\]?\s+WHERE .*<> N?'\*Generation failed for this section\. Please regenerate\.\*'");
        sql.Should().MatchRegex("(?i)ORDER BY .*generated_?at.* DESC");
    }

    [Test]
    public async Task ThePageRead_StillShowsAFailedSection_SoItCanBeRegenerated()
    {
        var store = new DocumentationStore();
        store.AddDocumentation(
            30,
            Later,
            (ProjectDocSectionType.ProjectOverview, "Project Overview", "The shop sells books."),
            (ProjectDocSectionType.DataModel, "Data Model", Failed));

        var result = await new GetProjectDocumentationHandler(store.Factory(), TestProjectAccess.Evaluator(TestProjectAccess.ReadableIn(DocumentationStore.ProjectId)))
            .Handle(new GetProjectDocumentationQuery(DocumentationStore.ProjectId), CancellationToken.None);

        result.Latest!.Sections.Select(x => (x.SectionType, x.Content)).Should().Equal(
            (ProjectDocSectionType.ProjectOverview, "The shop sells books."),
            (ProjectDocSectionType.DataModel, Failed));
    }

    // The fixed text is one constant: no copy of it elsewhere in the product code can drift from the filters above.
    [Test]
    public void TheFailedSectionText_IsWrittenOnceInTheProductCode()
    {
        var root = RepositoryRoot();
        var separator = Path.DirectorySeparatorChar;

        var files = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(x => !x.Contains($"{separator}Beacon.Tests{separator}", StringComparison.Ordinal))
            .Where(x => !x.Contains($"{separator}obj{separator}", StringComparison.Ordinal))
            .Where(x => !x.Contains($"{separator}bin{separator}", StringComparison.Ordinal))
            .Where(x => File.ReadAllText(x).Contains("Generation failed for this section", StringComparison.Ordinal))
            .Select(x => Path.GetRelativePath(root, x).Replace(separator, '/'))
            .ToList();

        files.Should().Equal("src/Beacon.Core/Data/Entities/Projects/FailedDocumentationSection.cs");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Beacon.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the tests run inside the repository");

        return directory!.FullName;
    }
}
