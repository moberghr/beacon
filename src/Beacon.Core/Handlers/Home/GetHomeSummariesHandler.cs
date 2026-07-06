using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.Home;

// ── Migration summary ─────────────────────────────────────────────────────────

internal sealed class GetHomeMigrationSummaryHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<GetHomeMigrationSummaryQuery, GetHomeMigrationSummaryResult>
{
    public async Task<GetHomeMigrationSummaryResult> Handle(GetHomeMigrationSummaryQuery request, CancellationToken cancellationToken)
    {
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

public record GetHomeMigrationSummaryQuery : IRequest<GetHomeMigrationSummaryResult>;

public record GetHomeMigrationSummaryResult(
    int Total,
    int Successful,
    int Executions,
    int Errored
);

// ── Task summary ──────────────────────────────────────────────────────────────

internal sealed class GetHomeTaskSummaryHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<GetHomeTaskSummaryQuery, GetHomeTaskSummaryResult>
{
    public async Task<GetHomeTaskSummaryResult> Handle(GetHomeTaskSummaryQuery request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Global query filter on ArchivableBaseEntity excludes archived rows.
        var total = await context.QueryTasks
            .CountAsync(cancellationToken);

        var open = await context.QueryTasks
            .Where(x => !x.Resolved)
            .CountAsync(cancellationToken);

        var resolved = await context.QueryTasks
            .Where(x => x.Resolved)
            .CountAsync(cancellationToken);

        return new GetHomeTaskSummaryResult(total, open, resolved);
    }
}

public record GetHomeTaskSummaryQuery : IRequest<GetHomeTaskSummaryResult>;

public record GetHomeTaskSummaryResult(int Total, int Open, int Resolved);
