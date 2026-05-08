using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.DataMigration;

internal sealed class DeleteMigrationJobHandler(IMigrationService migrationService)
    : IRequestHandler<DeleteMigrationJobCommand, DeleteMigrationJobResult>
{
    public async Task<DeleteMigrationJobResult> Handle(
        DeleteMigrationJobCommand request,
        CancellationToken cancellationToken)
    {
        if (request.MigrationJobId <= 0)
        {
            throw new InvalidOperationException("Migration job id is required.");
        }

        var response = await migrationService.DeleteMigrationJob(
            request.MigrationJobId,
            cancellationToken,
            request.ForceDelete);

        return new DeleteMigrationJobResult(response.Success, response.Message);
    }
}

public record DeleteMigrationJobCommand(int MigrationJobId, bool ForceDelete = false)
    : IRequest<DeleteMigrationJobResult>;

public record DeleteMigrationJobResult(bool Success, string? ErrorMessage);
