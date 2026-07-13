using Beacon.AI.Services.Documentation;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Models;

namespace Beacon.AI.Services.Mcp;

internal sealed class KnowledgeAnswerService(
    IKnowledgeGraphService knowledgeGraph,
    IProjectDocumentationService documentationService) : IKnowledgeAnswerService
{
    public async Task<string> AnswerAsync(
        ILlmProvider llmProvider,
        int projectId,
        string question,
        McpSettingsData settings,
        CancellationToken ct)
    {
        var projectContextTask = knowledgeGraph.GetProjectContextForLlmAsync(projectId, ct);
        var searchTask = knowledgeGraph.SearchProjectAsync(question, projectId, 10, ct);
        var docChunksTask = knowledgeGraph.GetRelevantDocChunksAsync(projectId, question, settings.DocChunkTopK, ct);
        var docTask = documentationService.ExportLatestToMarkdownAsync(projectId, ct);

        await Task.WhenAll(projectContextTask, searchTask, docChunksTask, docTask);

        var projectContext = projectContextTask.Result;
        var searchResults = searchTask.Result;
        var docChunks = docChunksTask.Result;
        var documentation = docTask.Result;

        var context = "";

        // Tier-3 ⑨: when doc-chunk embeddings exist for the project, build the documentation context from the
        // top-K masked-question-nearest chunks (situating blurb + chunk) instead of char-truncating the whole
        // documentation. Fall back to today's char-truncation when nothing is indexed / the embedder is off.
        if (docChunks.Count > 0)
        {
            context += "## Project Documentation\n\n";
            foreach (var chunk in docChunks)
            {
                context += string.IsNullOrWhiteSpace(chunk.ContextualBlurb)
                    ? chunk.ChunkText
                    : chunk.ContextualBlurb + "\n\n" + chunk.ChunkText;
                context += "\n\n";
            }
        }
        else if (!string.IsNullOrEmpty(documentation))
        {
            context += "## Project Documentation\n\n";
            context += documentation.Length > 6000 ? documentation[..6000] + "\n\n[... truncated ...]\n" : documentation;
            context += "\n\n";
        }

        if (!string.IsNullOrEmpty(projectContext))
        {
            context += "## Project Schema & Data Sources\n\n";
            context += projectContext + "\n\n";
        }

        if (searchResults.Count > 0)
        {
            context += "## Relevant Search Results\n\n";
            foreach (var result in searchResults)
            {
                context += $"- **{result.SchemaName}.{result.TableName}** ({result.DataSourceName})";
                if (!string.IsNullOrEmpty(result.Description))
                {
                    context += $": {result.Description}";
                }

                context += "\n";
            }

            context += "\n";
        }

        if (string.IsNullOrWhiteSpace(context))
        {
            return "No documentation or knowledge base available for this project. Generate project documentation first.";
        }

        var systemPrompt = """
            You are a knowledgeable assistant for a data project. Answer the user's question based on the provided project documentation, schema information, and knowledge base.

            Rules:
            - Answer based on the provided context only — do not make up information
            - Be clear and concise
            - Reference specific tables, schemas, or data sources when relevant
            - If the context doesn't contain enough information to fully answer, say what you do know and suggest what documentation or data the user might need
            """;

        var userMessage = "";
        if (!string.IsNullOrWhiteSpace(settings.GlobalInstruction))
        {
            userMessage += $"INSTRUCTIONS:\n{settings.GlobalInstruction}\n\n";
        }

        userMessage += $"CONTEXT:\n{context}\n\nQUESTION: {question}";

        var request = new LlmRequest
        {
            SystemPrompt = systemPrompt,
            Messages = [new ChatMessage(ConversationRole.User, userMessage)],
            Temperature = 0.3m,
            MaxTokens = 2048
        };

        var response = await llmProvider.CompleteAsync(request, ct);

        var text = $"# {question}\n\n";
        text += response.Content;

        return text;
    }
}
