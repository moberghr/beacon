using Beacon.AI.Services.Ai.AiActor;
using Beacon.AI.Services.Documentation;
using Warp.Core.Handlers;

namespace Beacon.SampleProject.Warp.Jobs;

// Fire-and-forget AI background work. The enqueueing user id (when present) travels as job
// metadata under "BeaconUserId" so JobStatusChangedBehavior can push status to that user only.

public sealed class GenerateProjectDocumentationJob : IJob
{
    public int ProjectId { get; init; }

    public int UserId { get; init; }
}

public sealed class GenerateProjectDocumentationJobHandler(IProjectDocumentationService documentationService)
    : IJobHandler<GenerateProjectDocumentationJob>
{
    public Task HandleAsync(GenerateProjectDocumentationJob message, CancellationToken cancellationToken)
        => documentationService.GenerateDocumentationAsync(message.ProjectId, message.UserId, cancellationToken);
}

public sealed class AiActorThinkCycleJob : IJob
{
    public int ActorId { get; init; }

    public int SubscriptionId { get; init; }
}

public sealed class AiActorThinkCycleJobHandler(IAiActorServiceExtended aiActorService)
    : IJobHandler<AiActorThinkCycleJob>
{
    public Task HandleAsync(AiActorThinkCycleJob message, CancellationToken cancellationToken)
        => aiActorService.ExecuteThinkCycleBackgroundAsync(message.ActorId, message.SubscriptionId);
}
