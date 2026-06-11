using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.AI.Services.GitHub;
using Beacon.Core.Handlers.Projects;

namespace Beacon.AI.Handlers.Projects;

internal sealed class ScanAllRepositoriesHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IGitHubScannerService scanner,
    ILogger<ScanAllRepositoriesHandler> logger)
    : IRequestHandler<ScanAllRepositoriesCommand, ScanAllRepositoriesResult>
{
    public async Task<ScanAllRepositoriesResult> Handle(
        ScanAllRepositoriesCommand request,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var repoIds = await context.GitHubRepositories
            .Where(r => r.ProjectId == request.ProjectId)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);

        var errors = new List<string>();
        var scanned = 0;

        foreach (var repoId in repoIds)
        {
            try
            {
                await scanner.ScanRepositoryAsync(repoId, cancellationToken);
                scanned++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to scan repository {RepositoryId}", repoId);
                errors.Add($"Repository {repoId}: {ex.Message}");
            }
        }

        return new ScanAllRepositoriesResult(scanned, errors);
    }
}
