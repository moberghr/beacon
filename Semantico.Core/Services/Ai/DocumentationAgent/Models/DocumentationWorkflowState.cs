using Semantico.Core.Data.Entities;

namespace Semantico.Core.Services.Ai.DocumentationAgent.Models;

/// <summary>
/// Represents the state of a documentation agent workflow execution.
/// Used for checkpointing and resuming workflows.
/// </summary>
public class DocumentationWorkflowState
{
    public int DataSourceId { get; set; }
    public int DocumentationId { get; set; }
    public int AgentRunId { get; set; }
    public int UserId { get; set; }

    public DocumentationAgentPhase CurrentPhase { get; set; }

    // Phase 1: Discovery outputs
    public List<string> DiscoveredTables { get; set; } = [];
    public List<TableGroup> DomainGroups { get; set; } = [];

    // Phase 2: Table documentation progress
    public List<string> CompletedTables { get; set; } = [];
    public List<TableFailure> FailedTables { get; set; } = [];
    public int CurrentBatchIndex { get; set; }
    public int BatchSize { get; set; } = 5;

    // Resumability
    public DateTime LastCheckpoint { get; set; }
    public string? LastError { get; set; }

    // LLM usage tracking
    public int TotalTokensUsed { get; set; }
    public decimal TotalCost { get; set; }
    public string? ModelUsed { get; set; }

    // Configuration
    public DocumentationAgentOptions Options { get; set; } = new();
}

/// <summary>
/// A group of related tables identified during the discovery phase.
/// </summary>
public class TableGroup
{
    public string GroupName { get; set; } = null!;
    public string? Description { get; set; }
    public List<string> Tables { get; set; } = [];
}

/// <summary>
/// Records a failed table documentation attempt with retry information.
/// </summary>
public class TableFailure
{
    public string TableName { get; set; } = null!;
    public string ErrorMessage { get; set; } = null!;
    public int RetryCount { get; set; }
    public DateTime LastAttempt { get; set; }
}

/// <summary>
/// Configuration options for the documentation agent workflow.
/// </summary>
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
