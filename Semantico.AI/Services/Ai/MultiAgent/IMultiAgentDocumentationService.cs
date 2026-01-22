using Semantico.AI.Models.MultiAgent;
using Semantico.Core.Data.Entities;
using DocumentationProgress = Semantico.Core.Models.Ai.MultiAgent.DocumentationProgress;

namespace Semantico.AI.Services.Ai.MultiAgent;

/// <summary>
/// Service for generating database documentation using a multi-agent workflow.
/// Orchestrates multiple specialized LLM agents working in parallel on different
/// parts of the database schema.
/// </summary>
public interface IMultiAgentDocumentationService
{
    /// <summary>
    /// Generates comprehensive database documentation using multiple specialized agents.
    /// </summary>
    /// <param name="dataSourceId">The data source to document.</param>
    /// <param name="userId">The user requesting the documentation.</param>
    /// <param name="options">Configuration options for multi-agent generation.</param>
    /// <param name="progress">Optional progress reporter for UI updates.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The generated documentation entity.</returns>
    Task<DataSourceDocumentation> GenerateDocumentationAsync(
        int dataSourceId,
        int userId,
        MultiAgentGenerationOptions options,
        IProgress<DocumentationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets cached orchestrator result for a data source (if available).
    /// </summary>
    /// <param name="dataSourceId">The data source ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Cached orchestrator result, or null if not found or expired.</returns>
    Task<OrchestratorResult?> GetCachedOrchestratorResultAsync(
        int dataSourceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the cached orchestrator result for a data source.
    /// Useful when schema has changed significantly.
    /// </summary>
    /// <param name="dataSourceId">The data source ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ClearOrchestratorCacheAsync(
        int dataSourceId,
        CancellationToken cancellationToken = default);
}
