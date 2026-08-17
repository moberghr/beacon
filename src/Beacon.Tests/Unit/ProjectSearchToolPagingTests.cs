using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Knowledge;
using Beacon.Core.Data;
using Beacon.MCP.Services;
using Beacon.MCP.Tools;

namespace Beacon.Tests.Unit;

/// <summary>
/// Paging behavior of the <c>search</c> MCP tool: the offset window is cut in memory from a
/// single over-fetch (offset + max_results + 1), the "more results available" notice appears
/// exactly when the over-fetch found an item past the window, negative offsets clamp to zero,
/// offsets above 200 are rejected, and the header reflects the window. Note: <see cref="SearchResult"/> carries no quality
/// score, so result lines render type/source/table/description only — no invented data.
/// </summary>
[TestFixture]
public class ProjectSearchToolPagingTests
{
    private const int ProjectId = 5;
    private const string Query = "customer";

    private Mock<IKnowledgeGraphService> _knowledgeGraph = null!;

    [SetUp]
    public void SetUp()
    {
        _knowledgeGraph = new Mock<IKnowledgeGraphService>();
    }

    [Test]
    public async Task Offset_SkipsIntoResultWindow_AndFlagsMoreAvailable()
    {
        // offset=2, max=2 → fetch 5; returning all 5 proves an item exists past the window.
        SetupSearch(expectedFetchCount: 5, returnedCount: 5);

        var text = await ExecuteAsync(maxResults: 2, offset: 2);

        text.Should().Contain("**Showing 2 results (offset 2)**");
        text.Should().Contain("table-3");
        text.Should().Contain("table-4");
        text.Should().NotContain("table-1");
        text.Should().NotContain("table-2");
        text.Should().NotContain("table-5");
        text.Should().Contain("_More results available — repeat with offset=4._");
    }

    [Test]
    public async Task Offset_LastWindow_HasNoMoreAvailableNotice()
    {
        // offset=2, max=2 → fetch 5; only 4 exist, so the window is the final page.
        SetupSearch(expectedFetchCount: 5, returnedCount: 4);

        var text = await ExecuteAsync(maxResults: 2, offset: 2);

        text.Should().Contain("**Showing 2 results (offset 2)**");
        text.Should().Contain("table-3");
        text.Should().Contain("table-4");
        text.Should().NotContain("More results available");
    }

    [Test]
    public async Task NoOffset_KeepsPlainResultsFoundHeader()
    {
        SetupSearch(expectedFetchCount: 3, returnedCount: 2);

        var text = await ExecuteAsync(maxResults: 2, offset: null);

        text.Should().Contain("**2 results found**");
        text.Should().NotContain("Showing");
        text.Should().NotContain("More results available");
    }

    [Test]
    public async Task NoOffset_OverfetchDetectsMore_AppendsNoticeWithNextOffset()
    {
        SetupSearch(expectedFetchCount: 3, returnedCount: 3);

        var text = await ExecuteAsync(maxResults: 2, offset: null);

        text.Should().Contain("**2 results found**");
        text.Should().NotContain("table-3");
        text.Should().Contain("_More results available — repeat with offset=2._");
    }

    [Test]
    public async Task NegativeOffset_ClampsToZero()
    {
        SetupSearch(expectedFetchCount: 3, returnedCount: 2);

        var text = await ExecuteAsync(maxResults: 2, offset: -5);

        text.Should().Contain("**2 results found**");
        text.Should().Contain("table-1");
        _knowledgeGraph.Verify(x => x.SearchProjectAsync(Query, ProjectId, 3, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task OffsetOverCap_RejectsWithError()
    {
        var result = await CreateTool().ExecuteAsync(Query, project_id: null, max_results: 2, offset: 201, CancellationToken.None);

        (result.IsError ?? false).Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Be("offset must be between 0 and 200.");
        _knowledgeGraph.Verify(x => x.SearchProjectAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task OffsetPastEnd_ReportsTrueTotal()
    {
        SetupSearch(expectedFetchCount: 13, returnedCount: 4);

        var text = await ExecuteAsync(maxResults: 2, offset: 10);

        text.Should().Be($"No results at offset 10 for '{Query}' — only 4 results exist.");
    }

    [Test]
    public async Task EmptyResults_ReturnsNoResultsMessage()
    {
        SetupSearch(expectedFetchCount: 21, returnedCount: 0);

        var text = await ExecuteAsync(maxResults: null, offset: null);

        text.Should().Be($"No results found for '{Query}'.");
    }

    [Test]
    public async Task ResultLine_RendersTypeSourceTableAndDescription_NoQualityScore()
    {
        // SearchResult has no quality-score property — the line must render exactly what exists.
        SetupSearch(expectedFetchCount: 3, returnedCount: 1);

        var text = await ExecuteAsync(maxResults: 2, offset: null);

        text.Should().Contain("- **[TABLE]** `ds-1`.`public.table-1` -- description-1");
        text.Should().NotContain("quality");
    }

    private void SetupSearch(int expectedFetchCount, int returnedCount)
    {
        _knowledgeGraph
            .Setup(x => x.SearchProjectAsync(Query, ProjectId, expectedFetchCount, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeResults(returnedCount));
    }

    private async Task<string> ExecuteAsync(int? maxResults, int? offset)
    {
        var result = await CreateTool().ExecuteAsync(Query, project_id: null, max_results: maxResults, offset: offset, CancellationToken.None);

        (result.IsError ?? false).Should().BeFalse();
        return result.Content.OfType<TextContentBlock>().Single().Text;
    }

    private ProjectSearchTool CreateTool()
    {
        var projectContext = new McpProjectContext { UserId = 1, ApiKeyId = 9, AllowedProjectIds = [ProjectId] };

        // McpAuditService swallows sink failures by design (§1.7), so a bare factory mock suffices —
        // the audit path runs without a database (§4.7).
        var auditService = new McpAuditService(
            new Mock<IDbContextFactory<BeaconContext>>().Object,
            NullLogger<McpAuditService>.Instance);

        return new ProjectSearchTool(
            _knowledgeGraph.Object,
            projectContext,
            auditService,
            NullLogger<ProjectSearchTool>.Instance);
    }

    private static List<SearchResult> MakeResults(int count)
    {
        return Enumerable.Range(1, count)
            .Select(x =>
                new SearchResult
                {
                    Type = "table",
                    DataSourceId = x,
                    DataSourceName = $"ds-{x}",
                    SchemaName = "public",
                    TableName = $"table-{x}",
                    Description = $"description-{x}",
                    Relevance = 1.0 - x * 0.01
                })
            .ToList();
    }
}
