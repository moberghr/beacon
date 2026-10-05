using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models.Ai;
using Beacon.Core.Data.Entities;



namespace Beacon.Core.Handlers.Queries;

internal sealed class GetQueryChangeHistoryHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<GetQueryChangeHistoryQuery, PagedList<QueryChangeHistoryItem>>
{
    public async Task<PagedList<QueryChangeHistoryItem>> Handle(
        GetQueryChangeHistoryQuery request,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = context.QueryStepChangeHistory
            .Where(c => c.QueryStep.QueryId == request.QueryId);

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

        return await query
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
            .ToPagedListAsync(request, cancellationToken, defaultSort: "-changedAt");
    }
}

/// <summary>A query's SQL changes, newest first unless <c>sort</c> says otherwise. <c>QueryId</c> binds from the route.</summary>
public record GetQueryChangeHistoryQuery : ListRequest, IRequest<PagedList<QueryChangeHistoryItem>>
{
    public int QueryId { get; init; }

    public int? StepId { get; init; }

    public ChangeSource? ChangeSource { get; init; }

    public DateTime? FromDate { get; init; }

    public DateTime? ToDate { get; init; }
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
