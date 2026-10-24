using Beacon.AI.Services.Documentation;
using Beacon.AI.Services.Knowledge;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.HostDocs;
using Beacon.Core.HostEndpoints;
using Beacon.MCP.HostEndpoints;
using Beacon.MCP.Services;
using Beacon.MCP.Tools;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using Moq;
using NUnit.Framework;
using Beacon.Core.Mcp;

namespace Beacon.Tests.Unit.HostDocs;

/// <summary>
/// Host-imported documents on the MCP surface: <c>get_documentation</c> lists them at project level and fetches one
/// by path or title, <c>search</c> renders them as IMPORTED_DOC with their path, and both stay inside the caller's
/// project (§1.12).
/// </summary>
[TestFixture]
public class ImportedDocumentToolTests
{
    private const int ProjectId = 5;
    private const int OtherProjectId = 6;

    private DocsStore _store = null!;
    private Mock<IProjectDocumentationService> _documentation = null!;
    private Mock<IDbContextFactory<BeaconContext>> _auditFactory = null!;

    [SetUp]
    public void SetUp()
    {
        _store = new DocsStore();
        _store.Documents.Add(Document(1, ProjectId, "README.md", "Netgiro Platform", "Consumer financing."));
        _store.Documents.Add(Document(2, ProjectId, "50-data/contexts.md", "Contexts", "The NetgiroContext."));
        _store.Documents.Add(Document(3, ProjectId, "40-web/admin.md", "Shared", "Admin."));
        _store.Documents.Add(Document(4, ProjectId, "40-web/partner.md", "Shared", "Partner."));
        _store.Documents.Add(Document(9, OtherProjectId, "secret.md", "Other Project Runbook", "Other project only."));

        _documentation = new Mock<IProjectDocumentationService>();
        _auditFactory = new Mock<IDbContextFactory<BeaconContext>>();
    }

    [Test]
    public async Task ProjectLevel_AppendsImportedListing_WithTitlesAndPaths_OfThisProjectOnly()
    {
        _documentation
            .Setup(x => x.ExportLatestToMarkdownAsync(ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync("# Generated\n\nBody.\n");

        var text = await ExecuteAsync();

        text.Should().StartWith("# Generated\n\nBody.\n\n## Imported documents\n");
        text.Should().Contain("- Contexts — `50-data/contexts.md`");
        text.Should().Contain("- Netgiro Platform — `README.md`");
        text.Should().NotContain("secret.md");
        text.IndexOf("40-web/admin.md", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf("README.md", StringComparison.Ordinal), "the listing is ordered by path");
    }

    [Test]
    public async Task ProjectLevel_NoGeneratedDocs_StillListsImportedDocuments()
    {
        var text = await ExecuteAsync();

        text.Should().StartWith("No AI-generated documentation exists for this project yet.");
        text.Should().Contain("## Imported documents");
    }

    [Test]
    public async Task ProjectLevel_NothingAtAll_KeepsTheOriginalMessage()
    {
        _store.Documents.Clear();

        var text = await ExecuteAsync();

        text.Should().Be("No documentation has been generated for this project yet. Use the Beacon UI to generate project documentation.");
    }

    [Test]
    public async Task Document_ByPath_ReturnsTheFullDocument_CaseInsensitive()
    {
        var text = await ExecuteAsync(document: "50-DATA/Contexts.md");

        text.Should().StartWith("# Contexts\n\n**Imported document:** `50-data/contexts.md` (source: docs:dir:docs/wiki");
        text.Should().EndWith("The NetgiroContext.\n");
        _auditFactory.Verify(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Once, "the call is audited");
    }

    [Test]
    public async Task Document_ByTitle_ReturnsIt()
    {
        var text = await ExecuteAsync(document: "netgiro platform");

        text.Should().Contain("Consumer financing.");
    }

    [Test]
    public async Task Document_AmbiguousTitle_IsAnErrorNamingThePaths()
    {
        var result = await ExecuteResultAsync(document: "Shared");

        result.IsError.Should().BeTrue();
        Text(result).Should().Be("Several imported documents are titled 'Shared': `40-web/admin.md`, `40-web/partner.md`. Pass the path instead.");
    }

    [Test]
    public async Task Document_FromAnotherProject_IsNotFound()
    {
        var result = await ExecuteResultAsync(document: "secret.md");

        result.IsError.Should().BeTrue();
        Text(result).Should().StartWith("No imported document with path or title 'secret.md' in this project.");
        _auditFactory.Verify(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Once, "the failure path is audited too");
    }

    [Test]
    public async Task Document_WithDataSource_IsRejected()
    {
        var result = await ExecuteResultAsync(document: "README.md", datasourceName: "warehouse");

        result.IsError.Should().BeTrue();
        Text(result).Should().Be("Pass either document or datasource_name/table_name, not both.");
    }

    [Test]
    public async Task Search_RendersImportedDocs_WithTypeAndPath()
    {
        var knowledgeGraph = new Mock<IKnowledgeGraphService>();
        knowledgeGraph
            .Setup(x => x.SearchProjectAsync("loan", ProjectId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new SearchResult
                {
                    Type = "imported_doc",
                    DataSourceName = "Netgiro",
                    SchemaName = string.Empty,
                    TableName = string.Empty,
                    Description = "Loans are repaid in installments.",
                    Relevance = 0.5,
                    DocIdentity = "importeddoc:2",
                    DocumentPath = "10-domains/loans.md",
                    DocumentTitle = "Loans"
                }
            ]);

        var recorder = Recorder();
        var tool = new ProjectSearchTool(knowledgeGraph.Object, ProjectContext(), recorder);

        var result = await TestToolCalls.InvokeAsync(recorder, () => tool.ExecuteAsync("loan", cancellationToken: CancellationToken.None));

        Text(result).Should().Contain("- **[IMPORTED_DOC]** `10-domains/loans.md` (Loans) -- Loans are repaid in installments.\n");
    }

    [Test]
    public async Task SearchKeywordArm_ImportedDocs_AreProjectScoped_WithMatchingChunkSnippet()
    {
        var project = new Project { Id = ProjectId, Name = "Netgiro" };
        var other = new Project { Id = OtherProjectId, Name = "Other" };
        foreach (var document in _store.Documents)
        {
            document.Project = document.ProjectId == ProjectId ? project : other;
        }

        _store.Chunks.Add(new() { Id = 20, ProjectId = ProjectId, ImportedDocumentId = 2, ChunkText = "Intro.", SortOrder = 0 });
        _store.Chunks.Add(new() { Id = 21, ProjectId = ProjectId, ImportedDocumentId = 2, ChunkText = "Loans live in NetgiroContext.", SortOrder = 1 });
        _store.Chunks.Add(new() { Id = 22, ProjectId = OtherProjectId, ImportedDocumentId = 9, ChunkText = "Loans elsewhere.", SortOrder = 0 });

        await using var context = new DocsTestContext(_store);
        var rows = await KnowledgeGraphService.BuildImportedDocsQuery(context, ProjectId, ["loans"], 10)
            .ToListAsync();

        rows.Should().ContainSingle();
        rows[0].Should().Be(new ImportedDocSearchRow(2, "Netgiro", "50-data/contexts.md", "Contexts", "Loans live in NetgiroContext.", 1));
    }

    [Test]
    public async Task SearchKeywordArm_ImportedDocs_MatchTheQueryWords_MostTermsFirst()
    {
        _store.Documents.Add(Document(5, ProjectId, "60-hr/leave.md", "Annual leave", "Leave policy."));
        _store.Documents.Add(Document(6, ProjectId, "60-hr/sick.md", "Sick leave", "Sick leave."));
        var project = new Project { Id = ProjectId, Name = "Netgiro" };
        foreach (var document in _store.Documents)
        {
            document.Project = project;
        }

        _store.Chunks.Add(new() { Id = 30, ProjectId = ProjectId, ImportedDocumentId = 5, ChunkText = "Leave policy.", SortOrder = 0 });
        _store.Chunks.Add(new() { Id = 31, ProjectId = ProjectId, ImportedDocumentId = 5, ChunkText = "Request annual leave in the portal.", SortOrder = 1 });
        _store.Chunks.Add(new() { Id = 32, ProjectId = ProjectId, ImportedDocumentId = 6, ChunkText = "Report sick leave on the day.", SortOrder = 0 });

        await using var context = new DocsTestContext(_store);
        var terms = KnowledgeGraphService.SearchTermsOf("How do I request annual leave?");
        var rows = await KnowledgeGraphService.BuildImportedDocsQuery(context, ProjectId, terms, 10)
            .ToListAsync();

        rows.Select(x => x.Path).Should().Equal("60-hr/leave.md", "60-hr/sick.md");
        rows[0].Hits.Should().Be(3);
        rows[0].MatchingChunk.Should().Be("Request annual leave in the portal.", "the chunk holding the most terms is the snippet");
        rows[1].Hits.Should().Be(1);
    }

    [Test]
    public async Task GetContext_ProjectWithoutDataSources_ListsHostToolsAndImportedDocuments()
    {
        _store.Projects.Add(new Project { Id = ProjectId, Name = "Netgiro" });
        var tools = new Mock<IHostEndpointToolOverview>();
        tools
            .Setup(x => x.ListForProjectAsync(ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostEndpointToolListing(
                [
                    new HostEndpointToolSummary("leave_balance", "api_leave_balance", "Your remaining annual leave."),
                    new HostEndpointToolSummary("request_leave", "api_request_leave", "Request annual leave.") { ReadOnly = false }
                ],
                false));

        var text = await GetContextAsync(tools.Object);

        text.Should().Be("""
            # Project: Netgiro

            **Host application tools:** 2
            **Imported documents:** 4

            ## Host application tools

            Call them by name. Tools marked (writes) change data.

            - `api_leave_balance` — Your remaining annual leave.
            - `api_request_leave` (writes) — Request annual leave.

            ## Imported documents

            Read one with `get_documentation` and `document: "<path>"`; `search` matches their text.

            - Shared — `40-web/admin.md`
            - Shared — `40-web/partner.md`
            - Contexts — `50-data/contexts.md`
            - Netgiro Platform — `README.md`


            """.ReplaceLineEndings("\n"));
    }

    [Test]
    public async Task GetContext_EmptyProject_SaysSoInsteadOfCountingZeroDataSources()
    {
        _store.Documents.Clear();
        _store.Projects.Add(new Project { Id = ProjectId, Name = "Netgiro" });

        var text = await GetContextAsync(null);

        text.Should().Be("# Project: Netgiro\n\nThis project has no data sources, host application tools or imported documents yet.\n");
    }

    private async Task<string> GetContextAsync(IHostEndpointToolOverview? tools)
    {
        var knowledgeGraph = new Mock<IKnowledgeGraphService>();
        knowledgeGraph
            .Setup(x => x.GetProjectDataSourcesAsync(ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var recorder = Recorder();
        var tool = new GetContextTool(
            knowledgeGraph.Object,
            _store.Factory().Object,
            new Mock<IProjectBriefService>().Object,
            ProjectContext(),
            recorder,
            tools);

        var result = await TestToolCalls.InvokeAsync(recorder, () => tool.ExecuteAsync(cancellationToken: CancellationToken.None));
        result.IsError.Should().NotBe(true, Text(result));

        return Text(result);
    }

    private async Task<string> ExecuteAsync(string? document = null)
    {
        var result = await ExecuteResultAsync(document);
        result.IsError.Should().NotBe(true, Text(result));

        return Text(result);
    }

    private Task<CallToolResult> ExecuteResultAsync(string? document = null, string? datasourceName = null)
    {
        var recorder = Recorder();
        var tool = new ProjectGetDocumentationTool(
            new Mock<IKnowledgeGraphService>().Object,
            _documentation.Object,
            _store.Factory().Object,
            ProjectContext(),
            recorder);

        return TestToolCalls.InvokeAsync(recorder, () => tool.ExecuteAsync(datasource_name: datasourceName, document: document, cancellationToken: CancellationToken.None));
    }

    private McpToolCallRecorder Recorder() =>
        TestToolCalls.Recorder(new McpAuditService(_auditFactory.Object, SettingsProviderMock.Create().Object, new HttpContextAccessor(), Options.Create(new McpDeploymentOptions()), NullLogger<McpAuditService>.Instance, new McpAuditOutcome(), Options.Create(new BeaconTelemetryOptions()), NullLoggerFactory.Instance, TestMcpAudit.ContentHasher, new McpStatementLog()));

    private static McpProjectContext ProjectContext() =>
        new() { UserId = 1, ApiKeyId = 9, AllowedProjectIds = [ProjectId] };

    private static string Text(CallToolResult result) => result.Content.OfType<TextContentBlock>().Single().Text;

    private static ProjectImportedDocument Document(int id, int projectId, string path, string title, string content) =>
        new()
        {
            Id = id,
            ProjectId = projectId,
            SourceKey = "docs:dir:docs/wiki",
            Path = path,
            Title = title,
            ContentHash = "hash",
            Content = content,
            ImportedTime = new DateTime(2026, 9, 25, 8, 0, 0, DateTimeKind.Utc)
        };
}
