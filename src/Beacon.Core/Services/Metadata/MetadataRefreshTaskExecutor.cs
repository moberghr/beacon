using Beacon.Core.BackgroundTasks;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Microsoft.Extensions.Options;

namespace Beacon.Core.Services.Metadata;

/// <summary>
/// Runs a <see cref="BackgroundTaskKind.MetadataRefresh"/> task: the forced refresh of one data source, as the system (the
/// request that started it ran the caller's project check and the cooldown). While the cooldown is on, it runs only when no
/// other refresh of the source runs in this process, and its success starts the cooldown here.
/// </summary>
internal sealed class MetadataRefreshTaskExecutor(
    ForcedMetadataRefresh refresh,
    ForcedMetadataRefreshCooldown cooldown,
    IOptionsMonitor<MetadataRefreshOptions> refreshOptions) : IBackgroundTaskExecutor
{
    public BackgroundTaskKind Kind => BackgroundTaskKind.MetadataRefresh;

    public TimeSpan AbandonAfter => TimeSpan.FromHours(1);

    public async Task<BackgroundTaskOutcome> ExecuteAsync(BackgroundTaskContext task, CancellationToken cancellationToken)
    {
        var cooldownMinutes = refreshOptions.CurrentValue.ForcedRefreshCooldownMinutes;
        var refusal = cooldown.TryStart(task.SubjectId, cooldownMinutes, ignoreCooldown: true);
        if (refusal != null)
        {
            throw new BeaconRateLimitedException(refusal);
        }

        var succeeded = false;
        try
        {
            var snapshot = await refresh.RunAsync(task.SubjectId, cancellationToken);
            succeeded = true;

            return new BackgroundTaskOutcome(snapshot.SkippedNameCount);
        }
        finally
        {
            cooldown.Finish(task.SubjectId, cooldownMinutes, succeeded);
        }
    }
}
