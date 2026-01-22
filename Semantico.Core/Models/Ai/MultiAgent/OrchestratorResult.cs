namespace Semantico.Core.Models.Ai.MultiAgent;

/// <summary>
/// Result from the orchestrator agent that analyzes the database schema
/// and identifies logical domain groupings.
/// </summary>
public record OrchestratorResult
{
    /// <summary>
    /// Brief overview of the database's purpose and capabilities (2-3 sentences).
    /// </summary>
    public string DatabaseOverview { get; init; } = null!;

    /// <summary>
    /// Identified logical domain groupings (3-7 groups recommended).
    /// </summary>
    public List<DomainGroup> DomainGroups { get; init; } = new();

    /// <summary>
    /// Key tables that serve as central hubs in the schema (e.g., users, orders, data_sources).
    /// </summary>
    public List<string> KeyHubTables { get; init; } = new();

    /// <summary>
    /// Identified architectural patterns (e.g., "Multi-tenant", "Event sourcing", "CQRS").
    /// </summary>
    public List<string> ArchitecturePatterns { get; init; } = new();

    /// <summary>
    /// Total number of tables analyzed.
    /// </summary>
    public int TotalTablesAnalyzed { get; init; }

    /// <summary>
    /// When the orchestrator analysis was performed.
    /// </summary>
    public DateTime AnalyzedAt { get; init; } = DateTime.UtcNow;
}
