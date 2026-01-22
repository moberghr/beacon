using MediatR;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models.Ai;
using Semantico.Core.Data.Entities;



namespace Semantico.Core.Handlers.Ai.DocumentationAgent;

public record GetDocumentationAgentProgressQuery : IRequest<DocumentationAgentProgressResult?>
{
    public int AgentRunId { get; init; }
}

public record DocumentationAgentProgressResult
{
    public int AgentRunId { get; init; }
    public int DataSourceId { get; init; }
    public string? DataSourceName { get; init; }
    public int? DocumentationId { get; init; }
    public DocumentationAgentPhase CurrentPhase { get; init; }
    public DocumentationAgentStatus Status { get; init; }
    public int ProgressPercent { get; init; }
    public string? ProgressMessage { get; init; }
    public int TotalTables { get; init; }
    public int TablesCompleted { get; init; }
    public int TablesFailed { get; init; }
    public List<TableFailure> FailedTables { get; init; } = [];
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public string? LastError { get; init; }
    public int TotalTokensUsed { get; init; }
    public decimal EstimatedCost { get; init; }

    public bool IsRunning => Status == DocumentationAgentStatus.Running;
    public bool IsCompleted => Status == DocumentationAgentStatus.Completed;
    public bool IsFailed => Status == DocumentationAgentStatus.Failed;
}
