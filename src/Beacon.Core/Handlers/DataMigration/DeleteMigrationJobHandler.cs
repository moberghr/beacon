using Beacon.Core.Services;
using MediatR;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.DataMigration;

internal sealed class DeleteMigrationJobHandler(IMigrationService migrationService, MigrationJobAccess access, MigrationWritePolicy writePolicy)
    : IRequestHandler<DeleteMigrationJobCommand, DeleteMigrationJobResult>
{
    public async Task<DeleteMigrationJobResult> Handle(
        DeleteMigrationJobCommand request,
        CancellationToken cancellationToken)
    {
        writePolicy.DemandEnabled();

        if (request.MigrationJobId <= 0)
        {
            throw new InvalidOperationException("Migration job id is required.");
        }

        // Deleting a job needs the access running it needs.
        await access.DemandForJobAsync(request.MigrationJobId, cancellationToken);

        var response = await migrationService.DeleteMigrationJob(
            request.MigrationJobId,
            cancellationToken,
            request.ForceDelete);

        return new DeleteMigrationJobResult(response.Success, response.Message);
    }
}

/// <summary>
/// Archives a migration job (or deletes it, with <c>ForceDelete</c>). For an Admin, or a MigrationOperator who may run the
/// job (<see cref="MigrationJobAccess"/>). Only while the deployment runs migrations (409 otherwise).
/// </summary>
[ProjectScopeExempt("gated by the MigrationOperator permission; the handler demands what running the job demands", Policy = BeaconPermissions.MigrationOperatorPolicy)]
public record DeleteMigrationJobCommand(int MigrationJobId, bool ForceDelete = false)
    : IRequest<DeleteMigrationJobResult>;

public record DeleteMigrationJobResult(bool Success, string? ErrorMessage);
