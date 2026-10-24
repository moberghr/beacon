using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.AI.Services.GitHub;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.BackgroundTasks;

namespace Beacon.AI.Handlers.Projects;

/// <summary>
/// Runs a <see cref="BackgroundTaskKind.RepositoryScan"/> task: a scan of every repository the project has when it runs, as
/// the user who started it. Right before the scan it checks again that the user may still change the project, and skips the
/// task (<see cref="BackgroundTaskFailureCodes.Forbidden"/>) when not. Each repository's scan keeps its bounds (files, file
/// size, time; <see cref="GitHubScanOptions"/>) and its own result; the task reports how many failed or hit a limit.
/// </summary>
internal sealed class RepositoryScanTaskExecutor(
    IDbContextFactory<BeaconContext> contextFactory,
    IGitHubScannerService scanner,
    ProjectRequestAuthorizer authorizer,
    ILogger<RepositoryScanTaskExecutor> logger) : IBackgroundTaskExecutor
{
    public BackgroundTaskKind Kind => BackgroundTaskKind.RepositoryScan;

    // Each repository's scan stops at its own timeout; a project has a handful of them.
    public TimeSpan AbandonAfter => TimeSpan.FromHours(4);

    public async Task<BackgroundTaskOutcome> ExecuteAsync(BackgroundTaskContext task, CancellationToken cancellationToken)
    {
        try
        {
            await authorizer.DemandAsync([new ProjectCheck(ProjectResource.Project(task.SubjectId), ProjectAction.Write)], cancellationToken);
        }
        catch (BeaconException ex) when (ex is BeaconForbiddenException or BeaconNotFoundException)
        {
            return BackgroundTaskOutcome.Skipped(BackgroundTaskFailureCodes.Forbidden);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var repoIds = await RepositoryScans.RepositoryIdsAsync(context, task.SubjectId, cancellationToken);
        var result = await RepositoryScans.ScanAsync(context, scanner, logger, task.SubjectId, repoIds, cancellationToken);

        return new BackgroundTaskOutcome(result.Errors.Count + result.PartialScans.Count);
    }
}
