using Beacon.Core.Data.Enums;
using Beacon.Core.Models.DataMigration;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.DataMigration;

internal sealed class RunMigrationJobHandler(IMigrationService migrationService)
    : IRequestHandler<RunMigrationJobCommand, RunMigrationJobResult>
{
    public async Task<RunMigrationJobResult> Handle(
        RunMigrationJobCommand request,
        CancellationToken cancellationToken)
    {
        if (request.MigrationJobId <= 0)
        {
            throw new InvalidOperationException("Migration job id is required.");
        }

        var response = await migrationService.ExecuteMigrationJob(
            new ExecuteMigrationJobRequest(
                request.MigrationJobId,
                IsManualExecution: true,
                ExecutionContext: "manual-ui"),
            cancellationToken);

        return new RunMigrationJobResult(
            response.ExecutionId,
            response.Status,
            response.SourceRowsRead,
            response.DestinationRowsWritten,
            response.RowsFailed,
            response.ErrorMessage);
    }
}

public record RunMigrationJobCommand(int MigrationJobId) : IRequest<RunMigrationJobResult>;

public record RunMigrationJobResult(
    int ExecutionId,
    MigrationStatus Status,
    int SourceRowsRead,
    int DestinationRowsWritten,
    int RowsFailed,
    string? ErrorMessage);
