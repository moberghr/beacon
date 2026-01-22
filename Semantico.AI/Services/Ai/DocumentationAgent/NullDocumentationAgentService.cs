using Semantico.Core.Data.Entities;
using Semantico.AI.Services.Ai.DocumentationAgent.Models;
using Semantico.Core.Models.Ai;


namespace Semantico.AI.Services.Ai.DocumentationAgent;

/// <summary>
/// No-op implementation of IDocumentationAgentService for when AI features are disabled.
/// This allows handlers to have their dependencies satisfied without requiring LLM configuration.
/// </summary>
internal sealed class NullDocumentationAgentService : IDocumentationAgentService
{
    private const string AiNotConfiguredMessage =
        "AI features are not enabled. To use documentation generation, configure LLM settings in appsettings.json:\n\n" +
        "{\n" +
        "  \"Semantico\": {\n" +
        "    \"LLM\": {\n" +
        "      \"Provider\": \"OpenAI\",\n" +
        "      \"ApiKey\": \"your-api-key\",\n" +
        "      \"Model\": \"gpt-4o\"\n" +
        "    }\n" +
        "  }\n" +
        "}";

    public Task<DocumentationAgentRun> StartDocumentationAsync(
        int dataSourceId,
        int userId,
        DocumentationAgentOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(AiNotConfiguredMessage);
    }

    public Task<DocumentationAgentRun> ResumeDocumentationAsync(
        int agentRunId,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(AiNotConfiguredMessage);
    }

    public Task CancelDocumentationAsync(
        int agentRunId,
        CancellationToken cancellationToken = default)
    {
        // No-op: nothing to cancel when AI is disabled
        return Task.CompletedTask;
    }

    public Task<DocumentationAgentRun?> GetRunStatusAsync(
        int agentRunId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<DocumentationAgentRun?>(null);
    }

    public Task<List<DocumentationAgentRun>> GetRunsForDataSourceAsync(
        int dataSourceId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new List<DocumentationAgentRun>());
    }

    public Task<DocumentationAgentRun> RetryFailedTablesAsync(
        int agentRunId,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(AiNotConfiguredMessage);
    }

    public Task ExecuteWorkflowBackgroundAsync(int agentRunId, DocumentationAgentOptions options)
    {
        throw new InvalidOperationException(AiNotConfiguredMessage);
    }

    public Task ExecuteRetryBackgroundAsync(int agentRunId, List<string> failedTables)
    {
        throw new InvalidOperationException(AiNotConfiguredMessage);
    }
}
