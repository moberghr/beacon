using Beacon.Core.Authorization;
using Beacon.Core.BackgroundTasks;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Enums;
using Microsoft.Extensions.Options;

namespace Beacon.Core.Services.Metadata;

/// <summary>
/// Runs a <see cref="BackgroundTaskKind.MetadataRefresh"/> task: the forced refresh of one data source, as the user who
/// started it (the request that started it ran that user's project check and the cooldown). Right before the scan it checks
/// again that the user may still manage the source, and skips the task (<see cref="BackgroundTaskFailureCodes.Forbidden"/>)
/// when not. It runs only when no other refresh of the source runs in this process (otherwise it is skipped as
/// <see cref="BackgroundTaskFailureCodes.Busy"/>: that refresh's result is the current one), and its success starts the
/// cooldown here.
/// </summary>
internal sealed class MetadataRefreshTaskExecutor(
    ForcedMetadataRefresh refresh,
    ForcedMetadataRefreshCooldown cooldown,
    IOptionsMonitor<MetadataRefreshOptions> refreshOptions,
    IExecutionAccessCheck accessCheck) : IBackgroundTaskExecutor
{
    /// <summary>The resource name the access check logs a refused task under.</summary>
    internal const string AccessResource = "metadata refresh";

    public BackgroundTaskKind Kind => BackgroundTaskKind.MetadataRefresh;

    public TimeSpan AbandonAfter => TimeSpan.FromHours(1);

    public async Task<BackgroundTaskOutcome> ExecuteAsync(BackgroundTaskContext task, CancellationToken cancellationToken)
    {
        if (!await accessCheck.AllowsAsync(ProjectAction.Manage, [task.SubjectId], AccessResource, task.TaskId, cancellationToken))
        {
            return BackgroundTaskOutcome.Skipped(BackgroundTaskFailureCodes.Forbidden);
        }

        var cooldownMinutes = refreshOptions.CurrentValue.ForcedRefreshCooldownMinutes;
        if (cooldown.TryStart(task.SubjectId, cooldownMinutes, ignoreCooldown: true) != null)
        {
            return BackgroundTaskOutcome.Skipped(BackgroundTaskFailureCodes.Busy);
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
