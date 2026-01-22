using MediatR;
using Semantico.AI.Services.Ai.AiActor;
using Semantico.AI.Services.Ai.AiActor.Models;
using Semantico.Core.Handlers.Ai.AiActor;

namespace Semantico.AI.Handlers.Ai.AiActor;

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

