namespace Semantico.AI.Models.MultiAgent;

/// <summary>
/// Configuration options for multi-agent documentation generation.
/// </summary>
public record MultiAgentGenerationOptions
{
    /// <summary>
    /// Maximum number of domain agents that can run concurrently.
    /// Higher values = faster but more resource intensive.
    /// Default: 5
    /// </summary>
    public int MaxConcurrentAgents { get; init; } = 5;

    /// <summary>
    /// Minimum number of tables required to form a domain group.
    /// Smaller domains may be merged with related domains.
    /// Default: 3
    /// </summary>
    public int MinTablesPerDomain { get; init; } = 3;

    /// <summary>
    /// Maximum number of domain groups the orchestrator should identify.
    /// This prevents over-fragmentation for large databases.
    /// Default: 7
    /// </summary>
    public int MaxDomainsToIdentify { get; init; } = 7;

    /// <summary>
    /// Temperature parameter for LLM calls (0.0 = deterministic, 1.0 = creative).
    /// Default: 0.3 (slightly creative but mostly consistent)
    /// </summary>
    public decimal Temperature { get; init; } = 0.3m;

    /// <summary>
    /// Whether to cache orchestrator results for the data source.
    /// Useful for repeated documentation regeneration without re-analyzing schema.
    /// Default: true
    /// </summary>
    public bool EnableOrchestratorCache { get; init; } = true;

    /// <summary>
    /// How long to cache orchestrator results (in minutes).
    /// Default: 60 minutes
    /// </summary>
    public int OrchestratorCacheDurationMinutes { get; init; } = 60;

    /// <summary>
    /// Specific tables to include (empty = include all).
    /// </summary>
    public List<string>? SpecificTables { get; init; }

    /// <summary>
    /// Tables to exclude from documentation.
    /// </summary>
    public List<string>? ExcludedTables { get; init; }

    /// <summary>
    /// Maximum number of tables to document (limits scope).
    /// Default: 200
    /// </summary>
    public int MaxTables { get; init; } = 200;

    /// <summary>
    /// Optional title for the generated documentation.
    /// If not provided, will use "{DataSourceName} Documentation".
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Maximum tokens per agent call.
    /// Default: 4096
    /// </summary>
    public int MaxTokens { get; init; } = 4096;

    /// <summary>
    /// Whether to include sample data in prompts (increases token usage but improves quality).
    /// Default: false (not implemented yet)
    /// </summary>
    public bool IncludeSampleData { get; init; } = false;
}
