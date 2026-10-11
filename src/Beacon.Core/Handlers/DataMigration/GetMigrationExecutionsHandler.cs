using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models.DataMigration;
using Beacon.Core.Services;
using MediatR;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.DataMigration;

internal sealed class GetMigrationExecutionsHandler(
    IMigrationService migrationService,
    IProjectAccessEvaluator projectAccess,
    MigrationJobAccess access)
    : IRequestHandler<GetMigrationExecutionsQuery, PagedList<MigrationExecutionDto>>
{
    public async Task<PagedList<MigrationExecutionDto>> Handle(
        GetMigrationExecutionsQuery request,
        CancellationToken cancellationToken)
    {
        var readable = (await projectAccess.GetScopeAsync(cancellationToken)).For(ProjectAction.Read);
        var serviceRequest = new GetMigrationExecutionsRequest
        {
            Page = request.Page,
            PageSize = request.PageSize,
            Sort = request.Sort,
            MigrationJobId = request.MigrationJobId,
            Status = request.Status,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            Readable = readable,
        };

        var page = await migrationService.GetMigrationExecutions(serviceRequest, cancellationToken);

        // A failed run's error can quote any data source the job reads: shown only to callers who read all of them.
        var hidden = await access.JobsReadingUnreadableSourcesAsync(
            page.Items
                .Where(x => x.ErrorMessage != null)
                .Select(x => x.MigrationJobId)
                .Distinct()
                .ToList(),
            readable,
            cancellationToken);
        if (hidden.Count == 0)
        {
            return page;
        }

        return page.Map(x => x.ErrorMessage != null && hidden.Contains(x.MigrationJobId)
            ? x with { ErrorMessage = MigrationJobAccess.HiddenErrorMessage }
            : x);
    }
}

/// <summary>
/// Newest first unless <c>sort</c> names <c>startedAt</c>, <c>completedAt</c>, <c>status</c> or
/// <c>sourceRowsRead</c> (optionally <c>-</c> prefixed). Only the runs of jobs whose data sources the caller reads; a run's
/// error text only where the caller also reads the data source of every step the job runs.
/// </summary>
[ProjectScopeExempt("gated by the MigrationOperator permission; the list is filtered to runs of jobs whose data sources the caller reads", Policy = BeaconPermissions.MigrationOperatorPolicy)]
public record GetMigrationExecutionsQuery : ListRequest, IRequest<PagedList<MigrationExecutionDto>>
{
    public int? MigrationJobId { get; init; }

    public MigrationStatus? Status { get; init; }

    public DateTime? StartDate { get; init; }

    public DateTime? EndDate { get; init; }
}
