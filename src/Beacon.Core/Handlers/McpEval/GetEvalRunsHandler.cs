using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;

namespace Beacon.Core.Handlers.McpEval;

/// <summary>Lists eval runs (most recent first), optionally scoped to a project.</summary>
internal sealed class GetEvalRunsHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<GetEvalRunsQuery, List<EvalRunListItem>>
{
    public async Task<List<EvalRunListItem>> Handle(GetEvalRunsQuery request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = context.McpEvalRuns.AsQueryable();
        if (request.ProjectId.HasValue)
        {
            query = query.Where(x => x.ProjectId == request.ProjectId.Value);
        }

        return await query
            .OrderByDescending(x => x.CreatedTime)
            .Take(request.Take ?? 50)
            .Select(x =>
                new EvalRunListItem
                {
                    Id = x.Id,
                    ProjectId = x.ProjectId,
                    TriggeredByUserId = x.TriggeredByUserId,
                    TotalCases = x.TotalCases,
                    PassedCases = x.PassedCases,
                    ExecutionAccuracy = x.ExecutionAccuracy,
                    Status = x.Status,
                    JudgeEnabled = x.JudgeEnabled,
                    CreatedTime = x.CreatedTime
                })
            .ToListAsync(cancellationToken);
    }
}

public record GetEvalRunsQuery : IRequest<List<EvalRunListItem>>
{
    public int? ProjectId { get; init; }
    public int? Take { get; init; }
}

public record EvalRunListItem
{
    public int Id { get; init; }
    public int? ProjectId { get; init; }
    public int? TriggeredByUserId { get; init; }
    public int TotalCases { get; init; }
    public int PassedCases { get; init; }
    public double ExecutionAccuracy { get; init; }
    public string Status { get; init; } = "";
    public bool JudgeEnabled { get; init; }
    public DateTime CreatedTime { get; init; }
}
