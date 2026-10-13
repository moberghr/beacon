using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models.Approvals;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;

namespace Beacon.Core.Handlers.DataMigration;

internal sealed class GetMigrationJobsHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IProjectAccessEvaluator projectAccess,
    MigrationWritePolicy writePolicy,
    MigrationJobApprovalState approvalState)
    : IRequestHandler<GetMigrationJobsQuery, PagedList<MigrationJobListItem>>
{
    public async Task<PagedList<MigrationJobListItem>> Handle(
        GetMigrationJobsQuery request,
        CancellationToken cancellationToken)
    {
        writePolicy.DemandEnabled();

        var readable = (await projectAccess.GetScopeAsync(cancellationToken)).For(ProjectAction.Read);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // The jobs whose data sources the caller reads at both ends (an Admin's: every job).
        var page = await context.MigrationJobs
            .WhereSourceAccessible(context, readable, x => x.DataSourceId)
            .WhereSourceAccessible(context, readable, x => x.DestinationDataSourceId)
            .WhereIf(!string.IsNullOrWhiteSpace(request.Search), x => x.Name.Contains(request.Search!))
            .Select(x =>
                new MigrationJobListItem
                {
                    Id = x.Id,
                    Name = x.Name,
                    Description = x.Description,
                    DataSourceId = x.DataSourceId,
                    DataSourceName = x.DataSource.Name,
                    DestinationDataSourceId = x.DestinationDataSourceId,
                    DestinationDataSourceName = x.DestinationDataSource.Name,
                    DestinationTable = x.DestinationTable,
                    Mode = x.Mode,
                    IsEnabled = x.IsEnabled,
                    Schedule = x.Schedule,
                    CreatedTime = x.CreatedTime,
                })
            .ToPagedListAsync(request, cancellationToken, defaultSort: "-createdTime");

        var awaiting = await approvalState.AwaitingAsync(
            context,
            page.Items
                .Select(x => x.Id)
                .ToList(),
            cancellationToken);

        return page.Map(x => awaiting.TryGetValue(x.Id, out var state)
            ? x with { AwaitingApproval = true, PendingApprovalRequestId = state.PendingRequestId, LastApprovalDecision = state.LastDecision }
            : x);
    }
}

/// <summary>
/// Newest first unless <c>sort</c> says otherwise; <c>search</c> matches the job name. Only the jobs whose source and
/// destination data sources the caller reads. Only while the deployment runs migrations (409 otherwise).
/// </summary>
[ProjectScopeExempt("gated by the MigrationOperator permission; the list is filtered to jobs whose data sources the caller reads", Policy = BeaconPermissions.MigrationOperatorPolicy)]
public record GetMigrationJobsQuery : ListRequest, IRequest<PagedList<MigrationJobListItem>>
{
    public string? Search { get; init; }
}

public record MigrationJobListItem
{
    public int Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public int DataSourceId { get; init; }

    public string DataSourceName { get; init; } = string.Empty;

    public int DestinationDataSourceId { get; init; }

    public string DestinationDataSourceName { get; init; } = string.Empty;

    public string DestinationTable { get; init; } = string.Empty;

    public MigrationMode Mode { get; init; }

    public bool IsEnabled { get; init; }

    public string? Schedule { get; init; }

    public DateTime CreatedTime { get; init; }

    /// <summary>
    /// While the approval workflow covers migration jobs: the job's definition waits for approval (it was created and is
    /// not approved yet, its change was rejected, or it no longer holds the approved definition), so it does not run.
    /// </summary>
    public bool AwaitingApproval { get; init; }

    /// <summary>The pending change request of the job's definition, if one waits; an Approver opens it.</summary>
    public int? PendingApprovalRequestId { get; init; }

    /// <summary>
    /// While the job waits and no request of it is pending: the last decision on its definition (a rejection, with the
    /// reviewer's comment: the job never runs as it is, so delete it and create it again).
    /// </summary>
    public ChangeApprovalOutcome? LastApprovalDecision { get; init; }
}
