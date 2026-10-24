using Beacon.AI.Services.Ai.AiActor;
using Beacon.Core.Authorization;
using Beacon.Core.BackgroundTasks;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.AiActors;
using Beacon.Core.Models;
using Beacon.Core.SecurityAudit;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Beacon.AI.Handlers.AiActors;

internal sealed class StartAiActorThinkHandler(
    IBackgroundTaskService backgroundTasks,
    IBeaconActorAccessor actorAccessor,
    IDbContextFactory<BeaconContext> contextFactory,
    ILogger<StartAiActorThinkHandler> logger)
    : IRequestHandler<StartAiActorThinkCommand, BackgroundTaskTicket>
{
    public async Task<BackgroundTaskTicket> Handle(StartAiActorThinkCommand request, CancellationToken cancellationToken)
    {
        await AiActorOwnership.EnsureCreatorOrAdminAsync(actorAccessor, contextFactory, request.ActorId, logger, cancellationToken);

        // On record when it is accepted, in the same save that records the task (before the job runner has it).
        return await backgroundTasks.StartAsync(
                BackgroundTaskKind.AiActorThink,
                request.ActorId,
                taskId => new SecurityAuditEntry(SecurityAuditActions.AiActorThinkStarted)
                    .On(SecurityAuditResources.AiActor, request.ActorId)
                    .With(new SecurityAuditDetails().Add("taskId", taskId)),
                cancellationToken)
            ?? throw new BeaconRateLimitedException(AiActorThinkTaskExecutor.AlreadyRunningMessage);
    }
}
