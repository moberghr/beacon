using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;

namespace Beacon.Core.Handlers.Home;

// ── Migration summary ─────────────────────────────────────────────────────────

internal sealed class GetHomeMigrationSummaryHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IProjectAccessEvaluator projectAccess,
    IBeaconPermissionAccessor permissionAccessor)
    : IRequestHandler<GetHomeMigrationSummaryQuery, GetHomeMigrationSummaryResult>
{
    public async Task<GetHomeMigrationSummaryResult> Handle(GetHomeMigrationSummaryQuery request, CancellationToken cancellationToken)
    {
        // Data migration is an Admin's or a MigrationOperator's (its routes are gated by that permission): anyone else sees
        // zeros.
        var scope = await projectAccess.GetScopeAsync(cancellationToken);
        if (!scope.IsUnrestricted && !(await permissionAccessor.GetCurrentAsync(cancellationToken)).Holds(BeaconPermissions.MigrationOperator))
        {
            return new GetHomeMigrationSummaryResult(0, 0, 0, 0);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // The jobs the migration list shows the caller: those whose source and destination it reads (an Admin's: all).
        // Global query filter on ArchivableBaseEntity excludes archived rows.
        var readable = scope.For(ProjectAction.Read);
        var jobs = context.MigrationJobs
            .WhereSourceAccessible(context, readable, x => x.DataSourceId)
            .WhereSourceAccessible(context, readable, x => x.DestinationDataSourceId);

        var total = await jobs
            .CountAsync(cancellationToken);

        var executions = await context.MigrationExecutions
            .WhereSourceAccessible(context, readable, x => x.MigrationJob.DataSourceId)
            .WhereSourceAccessible(context, readable, x => x.MigrationJob.DestinationDataSourceId)
            .CountAsync(cancellationToken);

        var successful = await jobs
            .Where(x => x.Executions.Any(e => e.Status == MigrationStatus.Completed || e.Status == MigrationStatus.PartialSuccess))
            .CountAsync(cancellationToken);

        var errored = await jobs
            .Where(x => x.Executions.Any(e => e.Status == MigrationStatus.Failed))
            .CountAsync(cancellationToken);

        return new GetHomeMigrationSummaryResult(total, successful, executions, errored);
    }
}

/// <summary>
/// The migration counts of the Home page: for an Admin every job, for a MigrationOperator the jobs whose source and
/// destination it reads (as the migration list shows them), for anyone else zeros.
/// </summary>
[ProjectScopeExempt("migration data is an Admin's or a MigrationOperator's, counted over the jobs whose data sources the caller reads: everyone else gets zeros, with no project data")]
public record GetHomeMigrationSummaryQuery : IRequest<GetHomeMigrationSummaryResult>;

public record GetHomeMigrationSummaryResult(
    int Total,
    int Successful,
    int Executions,
    int Errored
);

// ── Task summary ──────────────────────────────────────────────────────────────

internal sealed class GetHomeTaskSummaryHandler(IDbContextFactory<BeaconContext> contextFactory, IProjectAccessEvaluator projectAccess)
    : IRequestHandler<GetHomeTaskSummaryQuery, GetHomeTaskSummaryResult>
{
    public async Task<GetHomeTaskSummaryResult> Handle(GetHomeTaskSummaryQuery request, CancellationToken cancellationToken)
    {
        var readable = (await projectAccess.GetScopeAsync(cancellationToken)).For(ProjectAction.Read);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Global query filter on ArchivableBaseEntity excludes archived rows; a task counts when its subscription's query
        // is readable.
        var tasks = context.QueryTasks
            .WhereAllSourcesInOneProject(context, readable, x => x.Subscription.Query.Steps.Select(y => y.DataSourceId), x => x.Subscription.Query.OwnerUserId);

        var total = await tasks
            .CountAsync(cancellationToken);

        var open = await tasks
            .Where(x => !x.Resolved)
            .CountAsync(cancellationToken);

        var resolved = await tasks
            .Where(x => x.Resolved)
            .CountAsync(cancellationToken);

        return new GetHomeTaskSummaryResult(total, open, resolved);
    }
}

public record GetHomeTaskSummaryQuery : IRequest<GetHomeTaskSummaryResult>, IProjectFilteredRequest;

public record GetHomeTaskSummaryResult(int Total, int Open, int Resolved);
