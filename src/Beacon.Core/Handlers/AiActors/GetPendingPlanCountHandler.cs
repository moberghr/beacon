using Beacon.Core.Authorization;
using MediatR;

namespace Beacon.Core.Handlers.AiActors;

/// <summary>
/// How many AI actor plans wait for the caller's decision: pending, not expired, of actors whose data source the caller
/// may write. An Admin, and with the approval workflow on an Approver, counts every such plan; anyone else only the plans
/// of the actors they created (whose plans they may reject, revise or, with the workflow off, approve).
/// </summary>
public record GetPendingPlanCountQuery : IRequest<GetPendingPlanCountResult>, IProjectFilteredRequest;

public record GetPendingPlanCountResult
{
    public int Count { get; init; }
}
