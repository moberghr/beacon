using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models;
using Semantico.Core.Services.Ai.DocumentationAgent;

namespace Semantico.Core.Handlers.Ai.DocumentationAgent;

internal sealed class RetryFailedTablesHandler(
    IDbContextFactory<SemanticoContext> contextFactory,
    IDocumentationAgentService agentService,
    ILogger<RetryFailedTablesHandler> logger)
    : IRequestHandler<RetryFailedTablesCommand, RetryFailedTablesResult>
{
    public async Task<RetryFailedTablesResult> Handle(RetryFailedTablesCommand request, CancellationToken cancellationToken)
    {
        logger.LogInformation("Retrying failed tables for AgentRun {AgentRunId}", request.AgentRunId);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var agentRun = await context.DocumentationAgentRuns
            .FirstOrDefaultAsync(r => r.Id == request.AgentRunId, cancellationToken)
            ?? throw new SemanticoException($"Agent run {request.AgentRunId} not found");

        // Validate that we can retry
        if (agentRun.Status == DocumentationAgentStatus.Running)
        {
            throw new SemanticoException("Cannot retry while agent is still running");
        }

        if (agentRun.TablesFailed == 0)
        {
            throw new SemanticoException("No failed tables to retry");
        }

        logger.LogInformation("Enqueuing retry for {FailedCount} failed tables", agentRun.TablesFailed);

        // Enqueue retry job
        await agentService.RetryFailedTablesAsync(request.AgentRunId, cancellationToken);

        return new RetryFailedTablesResult
        {
            AgentRunId = request.AgentRunId,
            FailedTableCount = agentRun.TablesFailed
        };
    }
}

// Request
public record RetryFailedTablesCommand : IRequest<RetryFailedTablesResult>
{
    public int AgentRunId { get; set; }
}

// Response
public record RetryFailedTablesResult
{
    public int AgentRunId { get; set; }
    public int FailedTableCount { get; set; }
}
