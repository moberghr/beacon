using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Documentation;
using Beacon.AI.Services.Knowledge;
using Beacon.Core.Data;
using Beacon.MCP.Services;
using Beacon.MCP.Tools;

namespace Beacon.Tests.Unit;

/// <summary>
/// The <c>response_format</c> parameter of the <c>get_documentation</c> MCP tool: invalid values
/// are rejected (and audited) before project resolution or any documentation service is touched,
/// the project-level export defaults to concise
/// (truncated at a line boundary within the 8000-char budget, with an explicit notice appended
/// only when content was actually dropped), and 'detailed' returns the full export.
/// </summary>
[TestFixture]
public class ProjectGetDocumentationResponseFormatTests
{
    private const int ProjectId = 5;
    private const string TruncationNotice = "_Truncated (concise). Pass response_format: \"detailed\" for the full document._";

    // --- Parameter validation ---

    [TestCase("verbose")]
    [TestCase("full")]
    [TestCase("Concise ")]
    public async Task InvalidResponseFormat_ReturnsError_AndAudits(string responseFormat)
    {
        // Validation runs before project resolution or any documentation service call — null
        // services prove it. The rejection itself is audited (§1.7).
        var auditFactory = new Mock<IDbContextFactory<BeaconContext>>();
        var auditService = new McpAuditService(auditFactory.Object, NullLogger<McpAuditService>.Instance);
        var projectContext = new McpProjectContext { UserId = 1, ApiKeyId = 9, AllowedProjectIds = [ProjectId] };

        var tool = new ProjectGetDocumentationTool(
            knowledgeGraph: null!,
            documentationService: null!,
            contextFactory: null!,
            projectContext,
            auditService,
            NullLogger<ProjectGetDocumentationTool>.Instance);

        var result = await tool.ExecuteAsync(response_format: responseFormat, cancellationToken: CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text
            .Should().Be("response_format must be 'concise' or 'detailed'.");

        // The audit write was attempted (the bare mock sink swallows it by design, §1.7/§4.7).
        auditFactory.Verify(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // --- Project-level flow through the tool ---

    [Test]
    public async Task ProjectLevel_DefaultsToConcise_TruncatesLongExport()
    {
        var longExport = BuildLinedDocument(totalChars: 12100);
        var text = await ExecuteProjectLevelAsync(longExport, responseFormat: null);

        text.Should().EndWith(TruncationNotice + "\n");
        text.Length.Should().BeLessThan(longExport.Length);
        BodyBefore(text, TruncationNotice).Length.Should().BeLessThanOrEqualTo(ProjectGetDocumentationTool.ConciseCharBudget);
    }

    [Test]
    public async Task ProjectLevel_DetailedIsCaseInsensitive_ReturnsFullExport()
    {
        var longExport = BuildLinedDocument(totalChars: 12100);
        var text = await ExecuteProjectLevelAsync(longExport, responseFormat: "DETAILED");

        text.Should().Be(longExport);
        text.Should().NotContain("Truncated (concise)");
    }

    [Test]
    public async Task ProjectLevel_ConciseShortExport_IsUnchangedWithNoNotice()
    {
        const string shortExport = "# Project Docs\n\nEverything fits.\n";
        var text = await ExecuteProjectLevelAsync(shortExport, responseFormat: "concise");

        text.Should().Be(shortExport);
    }

    // --- Truncation helper ---

    [Test]
    public void TruncateForConcise_ExactBudget_IsUnchanged()
    {
        var document = new string('x', ProjectGetDocumentationTool.ConciseCharBudget);

        ProjectGetDocumentationTool.TruncateForConcise(document).Should().Be(document);
    }

    [Test]
    public void TruncateForConcise_LongDocument_CutsAtLineBoundary()
    {
        var document = BuildLinedDocument(totalChars: 11000);

        var truncated = ProjectGetDocumentationTool.TruncateForConcise(document);

        truncated.Should().Contain(TruncationNotice);
        var body = BodyBefore(truncated, TruncationNotice);
        body.Length.Should().BeLessThanOrEqualTo(ProjectGetDocumentationTool.ConciseCharBudget);

        // Every kept line must be complete — the cut lands on a line boundary, never mid-line.
        body.Split('\n').Should().OnlyContain(x => x == "0123456789");
    }

    [Test]
    public void TruncateForConcise_NoLineBoundaryInBudget_HardCutsAtBudget()
    {
        var document = new string('x', 9000);

        var truncated = ProjectGetDocumentationTool.TruncateForConcise(document);

        truncated.Should().Contain(TruncationNotice);
        BodyBefore(truncated, TruncationNotice).Should().Be(new string('x', ProjectGetDocumentationTool.ConciseCharBudget));
    }

    [Test]
    public void TruncateForConcise_HardCutOnSurrogatePair_NeverSplitsIt()
    {
        // Place an emoji (surrogate pair) so its low surrogate sits exactly AT the budget index —
        // the hard cut would otherwise keep a dangling high surrogate as the last char.
        var document = new string('x', ProjectGetDocumentationTool.ConciseCharBudget - 1)
            + "\U0001F600"
            + new string('y', 500);

        var truncated = ProjectGetDocumentationTool.TruncateForConcise(document);

        var body = BodyBefore(truncated, TruncationNotice);
        char.IsHighSurrogate(body[^1]).Should().BeFalse();
        body.Should().Be(new string('x', ProjectGetDocumentationTool.ConciseCharBudget - 1));
    }

    [Test]
    public void TruncateForConcise_CutInsideCodeFence_ClosesTheFence()
    {
        // Open a fence near the end of the budget so the cut lands inside the code block.
        var prefixLength = ProjectGetDocumentationTool.ConciseCharBudget - 100;
        var document = BuildLinedDocument(totalChars: prefixLength)
            + "```sql\n"
            + string.Concat(Enumerable.Repeat("select 1;\n", 200));

        var truncated = ProjectGetDocumentationTool.TruncateForConcise(document);

        var body = BodyBefore(truncated, TruncationNotice);
        CountOccurrences(body, "```").Should().Be(2, "an opened fence must be closed before the notice");
        body.TrimEnd('\n').Should().EndWith("```");
    }

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    private static async Task<string> ExecuteProjectLevelAsync(string export, string? responseFormat)
    {
        var documentationService = new Mock<IProjectDocumentationService>();
        documentationService
            .Setup(x => x.ExportLatestToMarkdownAsync(ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(export);

        var projectContext = new McpProjectContext { UserId = 1, ApiKeyId = 9, AllowedProjectIds = [ProjectId] };

        // McpAuditService swallows sink failures by design (§1.7), so a bare factory mock suffices —
        // the audit path runs without a database (§4.7).
        var auditService = new McpAuditService(
            new Mock<IDbContextFactory<BeaconContext>>().Object,
            NullLogger<McpAuditService>.Instance);

        var tool = new ProjectGetDocumentationTool(
            new Mock<IKnowledgeGraphService>().Object,
            documentationService.Object,
            new Mock<IDbContextFactory<BeaconContext>>().Object,
            projectContext,
            auditService,
            NullLogger<ProjectGetDocumentationTool>.Instance);

        var result = await tool.ExecuteAsync(response_format: responseFormat, cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeFalse();
        return result.Content.OfType<TextContentBlock>().Single().Text;
    }

    /// <summary>Repeats the line "0123456789\n" until at least totalChars characters.</summary>
    private static string BuildLinedDocument(int totalChars)
    {
        var lineCount = totalChars / 11 + 1;
        return string.Concat(Enumerable.Repeat("0123456789\n", lineCount));
    }

    private static string BodyBefore(string text, string notice)
    {
        var index = text.IndexOf(notice, StringComparison.Ordinal);
        index.Should().BeGreaterThan(0);
        return text[..index].TrimEnd('\n');
    }
}
