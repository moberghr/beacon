using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.AI.Services.GitHub;
using Beacon.Core.Handlers.Projects;

namespace Beacon.AI.Handlers.Projects;

/// <summary>
/// A scan of a project's repositories, shared by the scan in the request (<see cref="ScanAllRepositoriesHandler"/>) and the
/// background scan (<see cref="RepositoryScanTaskExecutor"/>): each repository in turn, a failure of one kept and the others
/// scanned, and the scanned ones whose scan hit a limit reported with why.
/// </summary>
internal static class RepositoryScans
{
    /// <summary>The ids of the project's repositories, in id order.</summary>
    public static Task<List<int>> RepositoryIdsAsync(BeaconContext context, int projectId, CancellationToken cancellationToken)
        => context.GitHubRepositories
            .Where(x => x.ProjectId == projectId)
            .OrderBy(x => x.Id)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

    /// <param name="beforeEach">
    /// Asked before each repository's scan, with its id; false stops the scan there and leaves the rest unscanned (a
    /// background scan past its deadline, or given up).
    /// </param>
    public static async Task<ScanAllRepositoriesResult> ScanAsync(
        BeaconContext context,
        IGitHubScannerService scanner,
        ILogger logger,
        int projectId,
        IReadOnlyList<int> repositoryIds,
        CancellationToken cancellationToken,
        Func<int, CancellationToken, Task<bool>>? beforeEach = null)
    {
        var errors = new List<string>();
        var scannedIds = new List<int>();

        foreach (var repoId in repositoryIds)
        {
            if (beforeEach != null && !await beforeEach(repoId, cancellationToken))
            {
                break;
            }

            try
            {
                await scanner.ScanRepositoryAsync(projectId, repoId, cancellationToken);
                scannedIds.Add(repoId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Failed to scan repository {RepositoryId}", repoId);
                errors.Add($"Repository {repoId}: {ex.Message}");
            }
        }

        // A scan that hit a limit still completes: the caller is told which ones are partial, and why
        var outcomes = await context.GitHubRepositories
            .Where(x => x.ProjectId == projectId)
            .Where(x => scannedIds.Contains(x.Id))
            .Select(x =>
                new
                {
                    x.Id,
                    x.ScanStatus,
                    x.LastScanError
                })
            .ToListAsync(cancellationToken);
        var partialScans = outcomes
            .Select(x => new RepositoryPartialScan(x.Id, RepositoryScanNotes.PartialReasons(x.ScanStatus, x.LastScanError)))
            .Where(x => x.Reasons.Count > 0)
            .OrderBy(x => x.RepositoryId)
            .ToList();

        return new ScanAllRepositoriesResult(scannedIds.Count, errors) { PartialScans = partialScans };
    }
}
