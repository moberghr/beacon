using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Models;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.DataMigration;

/// <summary>
/// What every migration demands whoever asks (an Admin included), on top of <see cref="MigrationJobAccess"/>: the
/// deployment runs migrations (<see cref="MigrationOptions.Enabled"/>, 409 otherwise), the destination is a migration write
/// target (<see cref="MigrationWriteTargets.DestinationRefusal"/>), and no source the job reads is one (Beacon never reads a
/// write target). The migration service checks the same again before it reads or connects to the destination.
/// </summary>
internal sealed class MigrationWritePolicy(IOptions<MigrationOptions> options, IDbContextFactory<BeaconContext> contextFactory)
{
    /// <summary>Throws <see cref="BeaconConflictException"/> while the deployment does not run migrations.</summary>
    public void DemandEnabled()
    {
        if (!options.Value.Enabled)
        {
            throw new BeaconConflictException(MigrationWriteTargets.DisabledMessage);
        }
    }

    /// <summary>The data sources of a new job: its declared source, the steps of <paramref name="queryText"/>, its destination.</summary>
    public Task DemandTargetsAsync(int sourceDataSourceId, string? queryText, int destinationDataSourceId, CancellationToken cancellationToken)
    {
        return DemandTargetsAsync(MigrationQuerySteps.SourceIds(sourceDataSourceId, queryText), destinationDataSourceId, cancellationToken);
    }

    /// <summary>The data sources of a stored job as it is stored now; a missing job is left to the caller.</summary>
    public async Task DemandTargetsForJobAsync(int migrationJobId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var job = await context.MigrationJobs
            .Where(x => x.Id == migrationJobId)
            .Select(x =>
                new
                {
                    x.DataSourceId,
                    x.QueryText,
                    x.DestinationDataSourceId
                })
            .FirstOrDefaultAsync(cancellationToken);
        if (job != null)
        {
            await DemandTargetsAsync(MigrationQuerySteps.SourceIds(job.DataSourceId, job.QueryText), job.DestinationDataSourceId, cancellationToken);
        }
    }

    private async Task DemandTargetsAsync(IReadOnlyList<int> sourceDataSourceIds, int destinationDataSourceId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var destination = await context.DataSources
            .Where(x => x.Id == destinationDataSourceId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Destination data source not found.");

        var refusal = MigrationWriteTargets.DestinationRefusal(destination);
        if (refusal != null)
        {
            throw new InvalidOperationException(refusal);
        }

        var readsWriteTarget = await context.DataSources
            .Where(x => sourceDataSourceIds.Contains(x.Id))
            .Where(x => x.AllowMigrationWrites)
            .AnyAsync(cancellationToken);
        if (readsWriteTarget)
        {
            throw new InvalidOperationException(MigrationWriteTargets.ReadRefusedMessage);
        }
    }
}
