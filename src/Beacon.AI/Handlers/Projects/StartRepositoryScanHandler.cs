using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.BackgroundTasks;
using Beacon.Core.Handlers.Projects;
using Beacon.Core.SecurityAudit;

namespace Beacon.AI.Handlers.Projects;

internal sealed class StartRepositoryScanHandler(
    IBackgroundTaskService backgroundTasks,
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconActorAccessor actorAccessor,
    ILogger<StartRepositoryScanHandler> logger)
    : IRequestHandler<StartRepositoryScanCommand, BackgroundTaskTicket>
{
    internal const string AlreadyRunningMessage = "A scan of this project's repositories is already queued or running.";

    public async Task<BackgroundTaskTicket> Handle(StartRepositoryScanCommand request, CancellationToken cancellationToken)
    {
        List<int> repoIds;
        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            repoIds = await RepositoryScans.RepositoryIdsAsync(context, request.ProjectId, cancellationToken);
        }

        // A scan reads the project's code with its stored credentials: on record when it is accepted, ids only, in the save
        // that records the task (before the job runner has it).
        var ticket = await backgroundTasks.StartAsync(
                BackgroundTaskKind.RepositoryScan,
                request.ProjectId,
                taskId => new SecurityAuditEntry(SecurityAuditActions.RepositoryScanRequested)
                    .On(SecurityAuditResources.Project, request.ProjectId)
                    .InProject(request.ProjectId)
                    .With(new SecurityAuditDetails()
                        .Add("repositoryIds", repoIds)
                        .Add("taskId", taskId)),
                cancellationToken)
            ?? throw new BeaconConflictException(AlreadyRunningMessage);

        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        logger.LogInformation(
            ProjectAccessEvents.RepositoryScanRequested,
            "Repository scan requested: project {ProjectId}, repositories {RepositoryIds}, by user {ActorId}, task {TaskId}",
            request.ProjectId,
            repoIds,
            actor.LogId,
            ticket.TaskId);

        return ticket;
    }
}
