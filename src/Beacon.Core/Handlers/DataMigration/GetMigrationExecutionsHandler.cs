using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models.DataMigration;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.DataMigration;

internal sealed class GetMigrationExecutionsHandler(IMigrationService migrationService)
    : IRequestHandler<GetMigrationExecutionsQuery, PagedList<MigrationExecutionDto>>
{
    public Task<PagedList<MigrationExecutionDto>> Handle(
        GetMigrationExecutionsQuery request,
        CancellationToken cancellationToken)
    {
        var serviceRequest = new GetMigrationExecutionsRequest
        {
            Page = request.Page,
            PageSize = request.PageSize,
            Sort = request.Sort,
            MigrationJobId = request.MigrationJobId,
            Status = request.Status,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
        };

        return migrationService.GetMigrationExecutions(serviceRequest, cancellationToken);
    }
}

/// <summary>
/// Newest first unless <c>sort</c> names <c>startedAt</c>, <c>completedAt</c>, <c>status</c> or
/// <c>sourceRowsRead</c> (optionally <c>-</c> prefixed).
/// </summary>
public record GetMigrationExecutionsQuery : ListRequest, IRequest<PagedList<MigrationExecutionDto>>
{
    public int? MigrationJobId { get; init; }

    public MigrationStatus? Status { get; init; }

    public DateTime? StartDate { get; init; }

    public DateTime? EndDate { get; init; }
}
