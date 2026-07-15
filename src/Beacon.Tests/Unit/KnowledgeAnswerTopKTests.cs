using FluentAssertions;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Documentation;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.AI.Services.Mcp;
using Beacon.Core.Models;
using Beacon.Core.Models.Ai;

namespace Beacon.Tests.Unit;

/// <summary>
/// Unit coverage for the T3-B3 retrieval seam in <see cref="KnowledgeAnswerService"/> (⑨). Proves that when
/// <see cref="IKnowledgeGraphService.GetRelevantDocChunksAsync"/> returns top-K chunks, the answer context is
/// built from those chunk texts INSTEAD of the char-truncated documentation; and that when it returns empty
/// (embedder unavailable / nothing indexed), the service falls back to today's
/// <see cref="IProjectDocumentationService.ExportLatestToMarkdownAsync"/> + truncation path exactly. The
/// mocked <see cref="ILlmProvider"/> echoes the built prompt back so the assembled CONTEXT is observable.
/// </summary>
[TestFixture]
public class KnowledgeAnswerTopKTests
{
    private const int ProjectId = 7;
    private const string Question = "What does the orders table track?";
    private const string FallbackSentinel = "FALLBACK_DOCUMENTATION_MARKDOWN_SENTINEL";

    [Test]
    public async Task AnswerAsync_WhenChunksReturned_BuildsContextFromChunks_NotTruncatedDocumentation()
    {
        var chunks = new List<DocChunkHit>
        {
            new("CHUNK_ALPHA orders are immutable once shipped.", null),
            new("CHUNK_BETA the status column is an enum.", "Situating: order lifecycle."),
            new("CHUNK_GAMMA totals are stored in cents.", null)
        };

        var service = BuildService(chunks, out var llm);

        var answer = await service.AnswerAsync(llm, ProjectId, Question, new McpSettingsData(), CancellationToken.None);

        // The documentation context is built from the chunk texts…
        answer.Should().Contain("CHUNK_ALPHA orders are immutable once shipped.");
        answer.Should().Contain("CHUNK_BETA the status column is an enum.");
        answer.Should().Contain("CHUNK_GAMMA totals are stored in cents.");
        // …including the situating blurb when present (blurb + chunk).
        answer.Should().Contain("Situating: order lifecycle.");
        // …and NOT from the char-truncated documentation fallback.
        answer.Should().NotContain(FallbackSentinel);
    }

    [Test]
    public async Task AnswerAsync_WhenNoChunks_FallsBackToTruncatedDocumentation()
    {
        var service = BuildService(chunks: [], out var llm);

        var answer = await service.AnswerAsync(llm, ProjectId, Question, new McpSettingsData(), CancellationToken.None);

        // No chunks indexed → today's documentation-export + truncation fallback is used.
        answer.Should().Contain("## Project Documentation");
        answer.Should().Contain(FallbackSentinel);
    }

    [Test]
    public async Task AnswerAsync_ThreadsDocChunkTopK_FromSettings_IntoRetrieval()
    {
        // A distinctive value (not the default 6) proves settings.DocChunkTopK is threaded through to the
        // retrieval call rather than a hard-coded / arbitrary limit.
        const int distinctiveTopK = 17;

        var knowledgeGraph = new Mock<IKnowledgeGraphService>();
        knowledgeGraph
            .Setup(x => x.GetRelevantDocChunksAsync(ProjectId, Question, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        knowledgeGraph
            .Setup(x => x.GetProjectContextForLlmAsync(ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);
        knowledgeGraph
            .Setup(x => x.SearchProjectAsync(Question, ProjectId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var documentationService = new Mock<IProjectDocumentationService>();
        documentationService
            .Setup(x => x.ExportLatestToMarkdownAsync(ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync("some documentation");

        var llm = new Mock<ILlmProvider>();
        llm
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse { Content = "answer" });

        var service = new KnowledgeAnswerService(knowledgeGraph.Object, documentationService.Object);

        await service.AnswerAsync(
            llm.Object, ProjectId, Question, new McpSettingsData { DocChunkTopK = distinctiveTopK }, CancellationToken.None);

        // The EXACT topK from settings must reach retrieval (asserted precisely, not It.IsAny).
        knowledgeGraph.Verify(
            x => x.GetRelevantDocChunksAsync(ProjectId, Question, distinctiveTopK, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static KnowledgeAnswerService BuildService(IReadOnlyList<DocChunkHit> chunks, out ILlmProvider llmProvider)
    {
        var knowledgeGraph = new Mock<IKnowledgeGraphService>();
        knowledgeGraph
            .Setup(x => x.GetRelevantDocChunksAsync(ProjectId, Question, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(chunks);
        knowledgeGraph
            .Setup(x => x.GetProjectContextForLlmAsync(ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);
        knowledgeGraph
            .Setup(x => x.SearchProjectAsync(Question, ProjectId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var documentationService = new Mock<IProjectDocumentationService>();
        documentationService
            .Setup(x => x.ExportLatestToMarkdownAsync(ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync($"{FallbackSentinel}\n\nThe orders table stores order rows.");

        // Echo the built user message back as the completion so the assembled CONTEXT is observable in the answer.
        var llm = new Mock<ILlmProvider>();
        llm
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LlmRequest request, CancellationToken _) => new LlmResponse { Content = request.Messages[0].Content });

        llmProvider = llm.Object;
        return new KnowledgeAnswerService(knowledgeGraph.Object, documentationService.Object);
    }
}
