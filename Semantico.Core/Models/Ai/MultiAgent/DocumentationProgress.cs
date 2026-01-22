namespace Semantico.Core.Models.Ai.MultiAgent;

/// <summary>
/// Progress information for multi-agent documentation generation.
/// Used to provide real-time updates to the UI.
/// </summary>
public record DocumentationProgress
{
    /// <summary>
    /// Current phase of documentation generation.
    /// Values: "Analyzing Schema", "Documenting Domains", "Aggregating Results"
    /// </summary>
    public string CurrentPhase { get; init; } = null!;

    /// <summary>
    /// Total number of domains identified.
    /// Set after orchestrator completes.
    /// </summary>
    public int TotalDomains { get; init; }

    /// <summary>
    /// Number of domains that have completed documentation.
    /// </summary>
    public int CompletedDomains { get; init; }

    /// <summary>
    /// Name of the domain currently being processed (if applicable).
    /// </summary>
    public string? CurrentDomain { get; init; }

    /// <summary>
    /// Time elapsed since documentation generation started.
    /// </summary>
    public TimeSpan ElapsedTime { get; init; }

    /// <summary>
    /// Estimated percentage completion (0-100).
    /// </summary>
    public int PercentComplete
    {
        get
        {
            if (TotalDomains == 0) return 0;

            return CurrentPhase switch
            {
                "Analyzing Schema" => 10,
                "Documenting Domains" => 10 + (int)(80.0 * CompletedDomains / TotalDomains),
                "Aggregating Results" => 90,
                _ => 0
            };
        }
    }

    /// <summary>
    /// Human-readable status message.
    /// </summary>
    public string StatusMessage
    {
        get
        {
            return CurrentPhase switch
            {
                "Analyzing Schema" => "Analyzing database schema and identifying domains...",
                "Documenting Domains" when !string.IsNullOrEmpty(CurrentDomain) =>
                    $"Documenting {CurrentDomain} ({CompletedDomains + 1}/{TotalDomains})...",
                "Documenting Domains" =>
                    $"Documenting domains ({CompletedDomains}/{TotalDomains})...",
                "Aggregating Results" => "Combining and refining documentation...",
                _ => "Processing..."
            };
        }
    }
}
