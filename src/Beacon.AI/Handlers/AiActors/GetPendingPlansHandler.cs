using MediatR;
using Beacon.AI.Services.Ai.AiActor;
using Beacon.AI.Services.Ai.AiActor.Models;
using Beacon.Core.Handlers.AiActors;

namespace Beacon.AI.Handlers.AiActors;

internal sealed class GetPendingPlansHandler(IAiActorServiceExtended aiActorService)
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

