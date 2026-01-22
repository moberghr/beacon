using Semantico.Core.Data.Entities;
using Semantico.Core.Models.Ai;

namespace Semantico.AI.Services.Ai.DocumentationAgent.Models;

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

