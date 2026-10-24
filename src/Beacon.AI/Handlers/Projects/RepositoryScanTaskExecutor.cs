using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.AI.Services.GitHub;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.BackgroundTasks;
using Beacon.Core.SecurityAudit;

namespace Beacon.AI.Handlers.Projects;

/// <summary>
/// Runs a <see cref="BackgroundTaskKind.RepositoryScan"/> task: a scan of every repository the project has when it runs, as
/// the user who started it. Right before the scan it checks again that the user may still change the project, and skips the
/// task (<see cref="BackgroundTaskFailureCodes.Forbidden"/>) when not. Each repository's scan keeps its bounds (files, file
/// size, time; <see cref="GitHubScanOptions"/>) and its own result; the task reports how many failed, hit a limit or were
/// not scanned. The whole task is bounded by <see cref="GitHubScanOptions.TaskDeadline"/>: no repository's scan starts after
/// it. Before each repository the task shows it is still going (<see cref="IBackgroundTaskHeartbeat"/>) and stops when it
/// was given up. What it scanned is on record when it ends (<c>access.repository_scan_ran</c>, ids and counts only), and a
/// skipped task as a refused one.
/// </summary>
internal sealed class RepositoryScanTaskExecutor(
    IDbContextFactory<BeaconContext> contextFactory,
    IGitHubScannerService scanner,
    ProjectRequestAuthorizer authorizer,
    IBackgroundTaskHeartbeat heartbeat,
    ISecurityAuditRecorder securityAudit,
    IOptions<GitHubScanOptions> options,
    TimeProvider timeProvider,
    ILogger<RepositoryScanTaskExecutor> logger) : IBackgroundTaskExecutor
{
    /// <summary>The time past the deadline and one repository's own timeout after which a silent task is given up.</summary>
    internal static readonly TimeSpan AbandonMargin = TimeSpan.FromMinutes(15);

    public BackgroundTaskKind Kind => BackgroundTaskKind.RepositoryScan;

    // No repository's scan starts after the deadline, and one that started stops at its own timeout.
    public TimeSpan AbandonAfter => options.Value.TaskDeadline + options.Value.ScanTimeout + AbandonMargin;

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

        var deadline = timeProvider.GetUtcNow() + options.Value.TaskDeadline;
        var attempted = new List<int>();
        var stoppedAtDeadline = false;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var repoIds = await RepositoryScans.RepositoryIdsAsync(context, task.SubjectId, cancellationToken);
        var result = await RepositoryScans.ScanAsync(
            context,
            scanner,
            logger,
            task.SubjectId,
            repoIds,
            cancellationToken,
            async (repoId, ct) =>
            {
                if (timeProvider.GetUtcNow() >= deadline)
                {
                    stoppedAtDeadline = true;

                    return false;
                }

                if (!await heartbeat.BeatAsync(task.TaskId, ct))
                {
                    return false;
                }

                attempted.Add(repoId);

                return true;
            });

        var notScanned = repoIds.Count - attempted.Count;
        if (notScanned > 0)
        {
            logger.LogWarning(
                "Repository scan task {TaskId} of project {ProjectId} stopped with {NotScanned} of {Total} repositories not scanned ({Reason}).",
                task.TaskId,
                task.SubjectId,
                notScanned,
                repoIds.Count,
                stoppedAtDeadline ? "deadline" : "given up");
        }

        await securityAudit.RecordAsync(
            new SecurityAuditEntry(SecurityAuditActions.RepositoryScanRan)
                .On(SecurityAuditResources.Project, task.SubjectId)
                .InProject(task.SubjectId)
                .With(new SecurityAuditDetails()
                    .Add("taskId", task.TaskId)
                    .Add("repositoryIds", attempted)
                    .Add("failedCount", result.Errors.Count)
                    .Add("partialCount", result.PartialScans.Count)
                    .Add("notScannedCount", notScanned)),
            CancellationToken.None);

        return new BackgroundTaskOutcome(result.Errors.Count + result.PartialScans.Count + notScanned);
    }

    /// <summary>A scan the task refused before it read anything, on record as a refused scan with the task and the reason.</summary>
    public SecurityAuditEntry? SkippedEvent(BackgroundTaskContext task, string code)
        => new SecurityAuditEntry(SecurityAuditActions.RepositoryScanRan)
            .On(SecurityAuditResources.Project, task.SubjectId)
            .InProject(task.SubjectId)
            .Ending(SecurityAuditOutcomes.Refused)
            .With(new SecurityAuditDetails()
                .Add("taskId", task.TaskId)
                .AddCode("reason", code));
}
