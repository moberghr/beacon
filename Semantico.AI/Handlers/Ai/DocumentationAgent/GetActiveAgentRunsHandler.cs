using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities;
using Semantico.Core.Data.Enums;
using Semantico.Core.Handlers.Ai.DocumentationAgent;

namespace Semantico.AI.Handlers.Ai.DocumentationAgent;

internal sealed class GetActiveAgentRunsHandler(
    IDbContextFactory<SemanticoContext> contextFactory)
    : IRequestHandler<GetActiveAgentRunsQuery, List<DocumentationAgentRun>>
{
    public async Task<List<DocumentationAgentRun>> Handle(GetActiveAgentRunsQuery request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var activeStatuses = new[]
        {
            DocumentationAgentStatus.Pending,
            DocumentationAgentStatus.Running
        };

        var agentRuns = await context.DocumentationAgentRuns
            .Where(r => r.DataSourceId == request.DataSourceId && activeStatuses.Contains(r.Status))
            .OrderByDescending(r => r.StartedAt)
            .Take(5) // Limit to 5 most recent active runs
            .ToListAsync(cancellationToken);

        return agentRuns;
    }
}

// Request
