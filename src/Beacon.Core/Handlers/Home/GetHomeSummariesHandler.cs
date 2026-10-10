using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;

namespace Beacon.Core.Handlers.Home;

// ── Migration summary ─────────────────────────────────────────────────────────

internal sealed class GetHomeMigrationSummaryHandler(IDbContextFactory<BeaconContext> contextFactory, IProjectAccessEvaluator projectAccess)
    : IRequestHandler<GetHomeMigrationSummaryQuery, GetHomeMigrationSummaryResult>
{
    public async Task<GetHomeMigrationSummaryResult> Handle(GetHomeMigrationSummaryQuery request, CancellationToken cancellationToken)
    {
        // Data migration is an Admin's (its routes are Admin-gated): anyone else sees zeros.
        var scope = await projectAccess.GetScopeAsync(cancellationToken);
        if (!scope.IsUnrestricted)
        {
            return new GetHomeMigrationSummaryResult(0, 0, 0, 0);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Global query filter on ArchivableBaseEntity excludes archived rows.
        var total = await context.MigrationJobs
            .CountAsync(cancellationToken);

        var executions = await context.MigrationExecutions
            .CountAsync(cancellationToken);

        var successful = await context.MigrationJobs
            .Where(x => x.Executions.Any(e => e.Status == MigrationStatus.Completed || e.Status == MigrationStatus.PartialSuccess))
            .CountAsync(cancellationToken);

        var errored = await context.MigrationJobs
            .Where(x => x.Executions.Any(e => e.Status == MigrationStatus.Failed))
            .CountAsync(cancellationToken);

        return new GetHomeMigrationSummaryResult(total, successful, executions, errored);
    }
}

[ProjectScopeExempt("migration data is an Admin's: everyone else gets zeros, with no project data")]
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
