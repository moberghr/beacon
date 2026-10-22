using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Models.DataMigration;
using Beacon.Core.Services;
using MediatR;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.DataMigration;

internal sealed class RunMigrationJobHandler(
    IMigrationService migrationService,
    MigrationJobAccess access,
    MigrationWritePolicy writePolicy)
    : IRequestHandler<RunMigrationJobCommand, RunMigrationJobResult>
{
    public async Task<RunMigrationJobResult> Handle(
        RunMigrationJobCommand request,
        CancellationToken cancellationToken)
    {
        writePolicy.DemandEnabled();

        if (request.MigrationJobId <= 0)
        {
            throw new InvalidOperationException("Migration job id is required.");
        }

        // Checked at every run, on the job as stored now: the caller may since have lost access to a data source.
        await access.DemandForJobAsync(request.MigrationJobId, cancellationToken);
        await writePolicy.DemandTargetsForJobAsync(request.MigrationJobId, cancellationToken);

        var response = await migrationService.ExecuteMigrationJob(
            new ExecuteMigrationJobRequest(
                request.MigrationJobId,
                IsManualExecution: true,
                ExecutionContext: "manual-ui"),
            cancellationToken);

        // A job runs once at a time: a run refused because another is starting or in progress is a conflict to retry later.
        if (response.RunInProgress)
        {
            throw new BeaconConflictException(response.ErrorMessage ?? MigrationService.RunInProgressMessage);
        }

        return new RunMigrationJobResult(
            response.ExecutionId,
            response.Status,
            response.SourceRowsRead,
            response.DestinationRowsWritten,
            response.RowsFailed,
            response.ErrorMessage);
    }
}

/// <summary>
/// Runs a migration job now. For an Admin, or a MigrationOperator who may still query its source and the data source of
/// every step it runs and write its destination, and whose destination is no more widely reachable than any of those
/// sources (<see cref="MigrationJobAccess"/>). Only while the deployment runs migrations, into a migration write target,
/// from sources that are not one (<see cref="MigrationWritePolicy"/>), and, while the change approval workflow covers
/// migration jobs, only a job whose definition a second person approved (a refused run is recorded as failed). A refused
/// run never starts.
/// </summary>
[ProjectScopeExempt("gated by the MigrationOperator permission; the handler checks Query on the job's source and every step's data source and Write on its destination through project access", Policy = BeaconPermissions.MigrationOperatorPolicy)]
public record RunMigrationJobCommand(int MigrationJobId) : IRequest<RunMigrationJobResult>;

public record RunMigrationJobResult(
    int ExecutionId,
    MigrationStatus Status,
    int SourceRowsRead,
    int DestinationRowsWritten,
    int RowsFailed,
    string? ErrorMessage);
