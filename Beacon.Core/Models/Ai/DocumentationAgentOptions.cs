namespace Beacon.Core.Models.Ai;

public class DocumentationAgentOptions
{
    /// <summary>
    /// Maximum number of tables to document in parallel.
    /// </summary>
    public int MaxParallelTables { get; set; } = 5;

    /// <summary>
    /// Maximum number of retry attempts for failed tables.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Whether to include sample data queries (requires AllowSampleDataForAi on data source).
    /// </summary>
    public bool IncludeSampleData { get; set; } = true;

    /// <summary>
    /// Maximum number of sample rows to fetch per table.
    /// </summary>
    public int MaxSampleRows { get; set; } = 5;

    /// <summary>
    /// Whether to include relationship analysis in documentation.
    /// </summary>
    public bool IncludeRelationships { get; set; } = true;

    /// <summary>
    /// Custom title for the generated documentation.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// LLM temperature for generation (0.0-1.0).
    /// </summary>
    public decimal Temperature { get; set; } = 0.3m;

    /// <summary>
    /// Maximum tokens per LLM call.
    /// </summary>
    public int MaxTokens { get; set; } = 4000;

    /// <summary>
    /// List of schemas to include in documentation. If null or empty, all schemas are included.
    /// </summary>
    public List<string>? SelectedSchemas { get; set; }
}
