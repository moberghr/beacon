namespace Semantico.AI.Models.MultiAgent;

/// <summary>
/// Final aggregated documentation combining orchestrator overview and all domain results.
/// </summary>
public record AggregatedDocumentation
{
    /// <summary>
    /// Executive summary of the entire database (combines orchestrator overview with insights).
    /// </summary>
    public string ExecutiveSummary { get; init; } = null!;

    /// <summary>
    /// High-level ER diagram in Mermaid format showing key entities and relationships.
    /// </summary>
    public string ArchitectureDiagram { get; init; } = null!;

    /// <summary>
    /// All domain sections, ordered logically.
    /// </summary>
    public List<DomainSection> DomainSections { get; init; } = new();

    /// <summary>
    /// Documentation of how different domains interact with each other.
    /// </summary>
    public string CrossDomainRelationships { get; init; } = null!;

    /// <summary>
    /// Complete markdown documentation ready for export.
    /// </summary>
    public string CompleteMarkdown { get; init; } = null!;

    /// <summary>
    /// Total tokens used across all agents (orchestrator + domain agents + aggregator).
    /// </summary>
    public int TotalTokensUsed { get; init; }

    /// <summary>
    /// Total estimated cost for the multi-agent documentation generation.
    /// </summary>
    public decimal TotalEstimatedCost { get; init; }

    /// <summary>
    /// Total time taken for the entire multi-agent workflow.
    /// </summary>
    public TimeSpan TotalDuration { get; init; }
}

/// <summary>
/// Represents a domain section in the final aggregated documentation.
/// </summary>
public record DomainSection
{
    /// <summary>
    /// Domain name (e.g., "User Management").
    /// </summary>
    public string DomainName { get; init; } = null!;

    /// <summary>
    /// Complete markdown content for this domain section.
    /// </summary>
    public string Content { get; init; } = null!;

    /// <summary>
    /// Sort order for presentation.
    /// </summary>
    public int SortOrder { get; init; }
}
