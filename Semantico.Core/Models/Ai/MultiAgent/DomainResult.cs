namespace Semantico.Core.Models.Ai.MultiAgent;

/// <summary>
/// Result from a domain agent that documents a specific functional area of the database.
/// </summary>
public record DomainResult
{
    /// <summary>
    /// Name of the domain (matches DomainGroup.DomainName).
    /// </summary>
    public string DomainName { get; init; } = null!;

    /// <summary>
    /// Comprehensive explanation of what this domain handles (2-3 paragraphs).
    /// </summary>
    public string PurposeOverview { get; init; } = null!;

    /// <summary>
    /// Documentation of core tables and their business purpose within this domain.
    /// </summary>
    public string CoreTablesDocumentation { get; init; } = null!;

    /// <summary>
    /// Explanation of how tables within this domain relate to each other.
    /// </summary>
    public string Relationships { get; init; } = null!;

    /// <summary>
    /// Example SQL queries demonstrating common operations in this domain.
    /// </summary>
    public string ExampleQueries { get; init; } = null!;

    /// <summary>
    /// Domain-specific recommendations for optimization, data quality, etc.
    /// </summary>
    public string Recommendations { get; init; } = null!;

    /// <summary>
    /// Complete markdown documentation for this domain (formatted and ready to use).
    /// </summary>
    public string FullMarkdown { get; init; } = null!;

    /// <summary>
    /// Number of tables documented in this domain.
    /// </summary>
    public int TablesDocumented { get; init; }

    /// <summary>
    /// Tokens used by the domain agent for this documentation.
    /// </summary>
    public int TokensUsed { get; init; }

    /// <summary>
    /// Estimated cost for this domain's documentation.
    /// </summary>
    public decimal EstimatedCost { get; init; }

    /// <summary>
    /// When this domain was documented.
    /// </summary>
    public DateTime DocumentedAt { get; init; } = DateTime.UtcNow;
}
