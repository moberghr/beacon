using Beacon.Core.Data.Enums;
using Beacon.Core.Models.DataMigration;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.DataMigration;

internal sealed class GetMigrationExecutionsHandler(IMigrationService migrationService)
    : IRequestHandler<GetMigrationExecutionsQuery, GetMigrationExecutionsResult>
{
    public async Task<GetMigrationExecutionsResult> Handle(
        GetMigrationExecutionsQuery request,
        CancellationToken cancellationToken)
    {
        var serviceRequest = new GetMigrationExecutionsRequest(
            MigrationJobId: request.MigrationJobId,
            Status: request.Status,
            StartDate: request.StartDate,
            EndDate: request.EndDate,
            Skip: request.Skip,
            Take: request.Take);

        var response = await migrationService.GetMigrationExecutions(serviceRequest, cancellationToken);

        return new GetMigrationExecutionsResult(response.Executions, response.TotalCount, response.HasMore);
    }
}

public record GetMigrationExecutionsQuery(
    int? MigrationJobId = null,
    MigrationStatus? Status = null,
    DateTime? StartDate = null,
    DateTime? EndDate = null,
    int Skip = 0,
    int Take = 100) : IRequest<GetMigrationExecutionsResult>;

public record GetMigrationExecutionsResult(List<MigrationExecutionDto> Executions, int TotalCount, bool HasMore);
