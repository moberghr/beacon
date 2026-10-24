using Beacon.Core.Authorization;
using Beacon.Core.BackgroundTasks;
using Beacon.Core.Data.Enums;
using MediatR;

namespace Beacon.Core.Handlers.DataMigration;

internal sealed class GetMigrationJobRunHandler(IBackgroundTaskService backgroundTasks, MigrationJobAccess access)
    : IRequestHandler<GetMigrationJobRunQuery, BackgroundTaskStatus?>
{
    public async Task<BackgroundTaskStatus?> Handle(GetMigrationJobRunQuery request, CancellationToken cancellationToken)
    {
        await access.DemandForJobAsync(request.MigrationJobId, cancellationToken);

        return await backgroundTasks.GetStatusAsync(BackgroundTaskKind.MigrationRun, request.MigrationJobId, request.TaskId, cancellationToken);
    }
}

/// <summary>
/// The state of a background run of the migration job (<see cref="StartMigrationJobRunCommand"/>), or null when the job has
/// no run task with that id. Read with the demands that start one.
/// </summary>
[ProjectScopeExempt("gated by the MigrationOperator permission; the handler checks Query on the job's source and every step's data source and Write on its destination through project access, and a task of another job is not found", Policy = BeaconPermissions.MigrationOperatorPolicy)]
public record GetMigrationJobRunQuery(int MigrationJobId, int TaskId) : IRequest<BackgroundTaskStatus?>;
