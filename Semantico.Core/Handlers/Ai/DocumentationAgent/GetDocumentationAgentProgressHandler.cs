using System.Text.Json;
using MediatR;
using Microsoft.Extensions.Logging;
using Semantico.Core.Data.Entities;
using Semantico.Core.Services.Ai.DocumentationAgent;
using Semantico.Core.Services.Ai.DocumentationAgent.Models;

namespace Semantico.Core.Handlers.Ai.DocumentationAgent;

internal sealed class GetDocumentationAgentProgressHandler
    : IRequestHandler<GetDocumentationAgentProgressQuery, DocumentationAgentProgressResult?>
{
    private readonly IDocumentationAgentService _agentService;
    private readonly ILogger<GetDocumentationAgentProgressHandler> _logger;

    public GetDocumentationAgentProgressHandler(
        IDocumentationAgentService agentService,
        ILogger<GetDocumentationAgentProgressHandler> logger)
    {
        _agentService = agentService;
        _logger = logger;
    }

    public async Task<DocumentationAgentProgressResult?> Handle(
        GetDocumentationAgentProgressQuery request,
        CancellationToken cancellationToken)
    {
        var agentRun = await _agentService.GetRunStatusAsync(request.AgentRunId, cancellationToken);

        if (agentRun == null)
        {
            _logger.LogWarning("Agent run {AgentRunId} not found", request.AgentRunId);
            return null;
        }

        // Deserialize failed tables with error messages
        List<TableFailure>? failedTables = null;
        if (!string.IsNullOrEmpty(agentRun.FailedTablesJson))
        {
            try
            {
                failedTables = JsonSerializer.Deserialize<List<TableFailure>>(agentRun.FailedTablesJson);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize FailedTablesJson for AgentRun {AgentRunId}", agentRun.Id);
            }
        }

        return new DocumentationAgentProgressResult
        {
            AgentRunId = agentRun.Id,
            DataSourceId = agentRun.DataSourceId,
            DataSourceName = agentRun.DataSource?.Name,
            DocumentationId = agentRun.DocumentationId,
            CurrentPhase = agentRun.CurrentPhase,
            Status = agentRun.Status,
            ProgressPercent = agentRun.ProgressPercent,
            ProgressMessage = agentRun.ProgressMessage,
            TotalTables = agentRun.TotalTablesDiscovered,
            TablesCompleted = agentRun.TablesCompleted,
            TablesFailed = agentRun.TablesFailed,
            FailedTables = failedTables ?? [],
            StartedAt = agentRun.StartedAt,
            CompletedAt = agentRun.CompletedAt,
            LastError = agentRun.LastError,
            TotalTokensUsed = agentRun.TotalTokensUsed,
            EstimatedCost = agentRun.EstimatedCost
        };
    }
}

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
