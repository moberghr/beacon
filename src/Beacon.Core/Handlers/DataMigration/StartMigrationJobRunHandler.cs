using Beacon.Core.Authorization;
using Beacon.Core.BackgroundTasks;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.DataMigration;

internal sealed class StartMigrationJobRunHandler(
    IBackgroundTaskService backgroundTasks,
    IDbContextFactory<BeaconContext> contextFactory,
    MigrationJobAccess access,
    MigrationWritePolicy writePolicy,
    BackgroundTaskRequesterPermissions permissions)
    : IRequestHandler<StartMigrationJobRunCommand, BackgroundTaskTicket>
{
    public async Task<BackgroundTaskTicket> Handle(StartMigrationJobRunCommand request, CancellationToken cancellationToken)
    {
        writePolicy.DemandEnabled();

        if (request.MigrationJobId <= 0)
        {
            throw new InvalidOperationException("Migration job id is required.");
        }

        // The run's own demands, checked here so a refused start never becomes a task; the task checks them again before it
        // runs, as the caller is by then.
        await access.DemandForJobAsync(request.MigrationJobId, cancellationToken);
        await writePolicy.DemandTargetsForJobAsync(request.MigrationJobId, cancellationToken);

        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var job = await context.MigrationJobs
                .Where(x => x.Id == request.MigrationJobId)
                .Select(x =>
                    new
                    {
                        x.IsEnabled
                    })
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new BeaconNotFoundException("Migration job not found.");
            if (!job.IsEnabled)
            {
                throw new InvalidOperationException("Migration job is disabled.");
            }
        }

        // The task records where the caller's MigrationOperator permission came from: the job re-checks a stored role live,
        // and relies on one the identity provider granted at this sign-in only shortly after the start.
        var grant = await permissions.GrantAtStartAsync(BeaconPermissions.MigrationOperator, cancellationToken);

        // One task per job is queued or running at a time; the run itself also claims the job (one run at a time on every
        // host), and records its start and end as security events. The request is on record with the task, ids only.
        return await backgroundTasks.StartAsync(
                BackgroundTaskKind.MigrationRun,
                request.MigrationJobId,
                taskId => new SecurityAuditEntry(SecurityAuditActions.MigrationJobRunRequested)
                    .On(SecurityAuditResources.MigrationJob, request.MigrationJobId)
                    .With(new SecurityAuditDetails()
                        .Add("taskId", taskId)
                        .AddCode("permissionSource", grant?.Source)),
                grant,
                cancellationToken)
            ?? throw new BeaconConflictException(MigrationService.RunInProgressMessage);
    }
}

/// <summary>
/// Starts a manual run of a migration job as a background task and answers at once with its ticket; its state is read with
/// <see cref="GetMigrationJobRunQuery"/>, the run's counts and error in the job's run history, and the user who started it
/// gets <c>JobStatusChanged</c> for its job. The same demands as <see cref="RunMigrationJobCommand"/> apply, checked here and
/// again by the task as the caller is when it runs (a task the caller may no longer run is skipped). A job with a run task
/// queued or running refuses a second one (409); a run already in progress started another way skips the task.
/// </summary>
[ProjectScopeExempt("gated by the MigrationOperator permission; the handler checks Query on the job's source and every step's data source and Write on its destination through project access", Policy = BeaconPermissions.MigrationOperatorPolicy)]
public record StartMigrationJobRunCommand(int MigrationJobId) : IRequest<BackgroundTaskTicket>;
