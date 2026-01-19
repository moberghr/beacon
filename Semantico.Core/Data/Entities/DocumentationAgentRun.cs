using Semantico.Core.Data.Entities.Base;

namespace Semantico.Core.Data.Entities;

/// <summary>
/// Tracks the execution of a documentation agent workflow, including progress and state.
/// </summary>
public class DocumentationAgentRun : BaseEntity
{
    public int DataSourceId { get; set; }
    public int? DocumentationId { get; set; }
    public int StartedByUserId { get; set; }

    public DocumentationAgentPhase CurrentPhase { get; set; }
    public DocumentationAgentStatus Status { get; set; }

    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public int ProgressPercent { get; set; }
    public string? ProgressMessage { get; set; }

    // Phase 1 outputs
    public int TotalTablesDiscovered { get; set; }
    public string? DiscoveredTablesJson { get; set; }
    public string? DomainGroupsJson { get; set; }

    // Phase 2 progress
    public int TablesCompleted { get; set; }
    public int TablesFailed { get; set; }
    public string? CompletedTablesJson { get; set; }
    /// <summary>
    /// JSON serialized list of TableFailure objects containing table names, error messages, and retry information.
    /// </summary>
    public string? FailedTablesJson { get; set; }
    public int CurrentBatchIndex { get; set; }

    // Error tracking
    public string? LastError { get; set; }
    public int RetryCount { get; set; }

    // Cost tracking
    public int TotalTokensUsed { get; set; }
    public decimal EstimatedCost { get; set; }

    // Checkpoint for resumability
    public DateTime? LastCheckpointAt { get; set; }
    public string? CheckpointStateJson { get; set; }

    // Navigation properties
    public DataSource DataSource { get; set; } = null!;
    public DataSourceDocumentation? Documentation { get; set; }
}

public enum DocumentationAgentPhase
{
    NotStarted = 0,
    Discovery = 1,
    TableDocumentation = 2,
    Synthesis = 3,
    Completed = 4,
    Failed = 5
}

public enum DocumentationAgentStatus
{
    Pending = 0,
    Running = 1,
    Paused = 2,
    Completed = 3,
    Failed = 4,
    Cancelled = 5
}
