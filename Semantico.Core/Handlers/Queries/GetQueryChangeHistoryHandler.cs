using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models.Ai;
using Semantico.Core.Data.Entities;



namespace Semantico.Core.Handlers.Queries;

internal sealed class GetQueryChangeHistoryHandler(IDbContextFactory<SemanticoContext> contextFactory)
    : IRequestHandler<GetQueryChangeHistoryQuery, GetQueryChangeHistoryResult>
{
    public async Task<GetQueryChangeHistoryResult> Handle(
        GetQueryChangeHistoryQuery request,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // First get all step IDs for this query
        var stepIds = await context.QuerySteps
            .Where(s => s.QueryId == request.QueryId)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);

        if (stepIds.Count == 0)
        {
            return new GetQueryChangeHistoryResult
            {
                QueryId = request.QueryId,
                Changes = []
            };
        }

        var query = context.QueryStepChangeHistory
            .Where(c => stepIds.Contains(c.QueryStepId));

        // Apply optional filters
        if (request.StepId.HasValue)
        {
            query = query.Where(c => c.QueryStepId == request.StepId.Value);
        }

        if (request.ChangeSource.HasValue)
        {
            query = query.Where(c => c.ChangeSource == request.ChangeSource.Value);
        }

        if (request.FromDate.HasValue)
        {
            query = query.Where(c => c.ChangedAt >= request.FromDate.Value);
        }

        if (request.ToDate.HasValue)
        {
            query = query.Where(c => c.ChangedAt <= request.ToDate.Value);
        }

        var changes = await query
            .OrderByDescending(c => c.ChangedAt)
            .Select(c => new QueryChangeHistoryItem
            {
                Id = c.Id,
                QueryStepId = c.QueryStepId,
                QueryStepName = c.QueryStep.Name,
                QueryStepOrder = c.QueryStep.StepOrder,
                AiActorId = c.AiActorId,
                AiActorName = c.AiActor != null ? c.AiActor.Name : null,
                AiActorExecutionId = c.AiActorExecutionId,
                AiActorPlanId = c.AiActorPlanId,
                UserId = c.UserId,
                PreviousSql = c.PreviousSql,
                NewSql = c.NewSql,
                ChangeReason = c.ChangeReason,
                ChangeSource = c.ChangeSource,
                ChangedAt = c.ChangedAt
            })
            .Take(request.MaxResults)
            .ToListAsync(cancellationToken);

        return new GetQueryChangeHistoryResult
        {
            QueryId = request.QueryId,
            Changes = changes
        };
    }
}

public record GetQueryChangeHistoryQuery : IRequest<GetQueryChangeHistoryResult>
{
    public required int QueryId { get; init; }
    public int? StepId { get; init; }
    public ChangeSource? ChangeSource { get; init; }
    public DateTime? FromDate { get; init; }
    public DateTime? ToDate { get; init; }
    public int MaxResults { get; init; } = 50;
}

public record GetQueryChangeHistoryResult
{
    public int QueryId { get; init; }
    public List<QueryChangeHistoryItem> Changes { get; init; } = [];
}

public record QueryChangeHistoryItem
{
    public int Id { get; init; }
    public int QueryStepId { get; init; }
    public string? QueryStepName { get; init; }
    public int QueryStepOrder { get; init; }
    public int? AiActorId { get; init; }
    public string? AiActorName { get; init; }
    public int? AiActorExecutionId { get; init; }
    public int? AiActorPlanId { get; init; }
    public string? UserId { get; init; }
    public string PreviousSql { get; init; } = null!;
    public string NewSql { get; init; } = null!;
    public string? ChangeReason { get; init; }
    public ChangeSource ChangeSource { get; init; }
    public DateTime ChangedAt { get; init; }
}
