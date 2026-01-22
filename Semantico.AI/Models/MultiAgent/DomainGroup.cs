namespace Semantico.AI.Models.MultiAgent;

/// <summary>
/// Represents a logical grouping of related tables within a domain.
/// </summary>
public record DomainGroup
{
    /// <summary>
    /// Name of the domain (e.g., "User Management", "Order Processing", "Notification System").
    /// </summary>
    public string DomainName { get; init; } = null!;

    /// <summary>
    /// Brief description of what this domain handles in business terms.
    /// </summary>
    public string Purpose { get; init; } = null!;

    /// <summary>
    /// List of table names belonging to this domain.
    /// </summary>
    public List<string> Tables { get; init; } = new();

    /// <summary>
    /// Priority order for processing (lower numbers processed first).
    /// Useful for ensuring core domains are documented before dependent domains.
    /// </summary>
    public int Priority { get; init; } = 100;
}
