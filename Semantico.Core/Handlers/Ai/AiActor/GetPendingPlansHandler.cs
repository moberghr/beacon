using MediatR;
using Semantico.Core.Services.Ai.AiActor;
using Semantico.Core.Services.Ai.AiActor.Models;

namespace Semantico.Core.Handlers.Ai.AiActor;

internal sealed class GetPendingPlansHandler(IAiActorService aiActorService)
    : IRequestHandler<GetPendingPlansQuery, GetPendingPlansResult>
{
    public async Task<GetPendingPlansResult> Handle(
        GetPendingPlansQuery request,
        CancellationToken cancellationToken)
    {
        var plans = await aiActorService.GetPendingPlansAsync(request.ActorId, cancellationToken);

        return new GetPendingPlansResult
        {
            Plans = plans
        };
    }
}

public record GetPendingPlansQuery : IRequest<GetPendingPlansResult>
{
    public required int ActorId { get; init; }
}

public record GetPendingPlansResult
{
    public List<PendingPlanSummary> Plans { get; init; } = [];
}
