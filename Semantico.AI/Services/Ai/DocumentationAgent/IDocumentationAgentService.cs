using Semantico.Core.Data.Entities;
using Semantico.AI.Services.Ai.DocumentationAgent.Models;
using Semantico.Core.Models.Ai;


namespace Semantico.AI.Services.Ai.DocumentationAgent;

/// <summary>
/// Service for orchestrating the documentation agent workflow.
/// Uses Microsoft Agent Framework for multi-phase, tool-equipped agent execution.
/// </summary>
public interface IDocumentationAgentService
{
    /// <summary>
    /// Starts a new documentation generation workflow for the specified data source.
    /// </summary>
    /// <param name="dataSourceId">The ID of the data source to document.</param>
    /// <param name="userId">The ID of the user initiating the documentation.</param>
    /// <param name="options">Optional configuration for the documentation generation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created agent run entity with tracking information.</returns>
    Task<DocumentationAgentRun> StartDocumentationAsync(
        int dataSourceId,
        int userId,
        DocumentationAgentOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resumes a paused or failed documentation workflow from its last checkpoint.
    /// </summary>
    /// <param name="agentRunId">The ID of the agent run to resume.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated agent run entity.</returns>
    Task<DocumentationAgentRun> ResumeDocumentationAsync(
        int agentRunId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a running documentation workflow.
    /// </summary>
    /// <param name="agentRunId">The ID of the agent run to cancel.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task CancelDocumentationAsync(
        int agentRunId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current status and progress of an agent run.
    /// </summary>
    /// <param name="agentRunId">The ID of the agent run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The agent run with current status.</returns>
    Task<DocumentationAgentRun?> GetRunStatusAsync(
        int agentRunId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all agent runs for a data source.
    /// </summary>
    /// <param name="dataSourceId">The data source ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of agent runs for the data source.</returns>
    Task<List<DocumentationAgentRun>> GetRunsForDataSourceAsync(
        int dataSourceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retries failed tables from a completed or failed agent run.
    /// </summary>
    /// <param name="agentRunId">The ID of the agent run containing failures.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A new agent run for retrying the failed tables.</returns>
    Task<DocumentationAgentRun> RetryFailedTablesAsync(
        int agentRunId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes the documentation workflow in the background (called by Hangfire).
    /// This method is public for Hangfire to invoke, but should not be called directly.
    /// </summary>
    Task ExecuteWorkflowBackgroundAsync(int agentRunId, DocumentationAgentOptions options);

    /// <summary>
    /// Executes retry of failed tables in the background (called by Hangfire).
    /// This method is public for Hangfire to invoke, but should not be called directly.
    /// </summary>
    Task ExecuteRetryBackgroundAsync(int agentRunId, List<string> failedTables);
}
