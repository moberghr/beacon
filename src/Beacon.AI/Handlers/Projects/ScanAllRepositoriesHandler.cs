using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.AI.Services.GitHub;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Handlers.Projects;

namespace Beacon.AI.Handlers.Projects;

internal sealed class ScanAllRepositoriesHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IGitHubScannerService scanner,
    IBeaconActorAccessor actorAccessor,
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

        // A scan reads the project's code with its stored credentials: logged as a security event, ids only.
        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        logger.LogInformation(
            ProjectAccessEvents.RepositoryScanRequested,
            "Repository scan requested: project {ProjectId}, repositories {RepositoryIds}, by user {ActorId}",
            request.ProjectId,
            repoIds,
            actor.LogId);

        var errors = new List<string>();
        var scanned = 0;

        foreach (var repoId in repoIds)
        {
            try
            {
                await scanner.ScanRepositoryAsync(request.ProjectId, repoId, cancellationToken);
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
