using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.AI.Services.GitHub;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Handlers.Projects;
using Beacon.Core.SecurityAudit;

namespace Beacon.AI.Handlers.Projects;

internal sealed class ScanAllRepositoriesHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IGitHubScannerService scanner,
    IBeaconActorAccessor actorAccessor,
    ISecurityAuditRecorder securityAudit,
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
        await securityAudit.RecordAsync(
            new SecurityAuditEntry(SecurityAuditActions.RepositoryScanRequested)
                .On(SecurityAuditResources.Project, request.ProjectId)
                .InProject(request.ProjectId)
                .With(new SecurityAuditDetails().Add("repositoryIds", repoIds)),
            cancellationToken);

        var errors = new List<string>();
        var scannedIds = new List<int>();

        foreach (var repoId in repoIds)
        {
            try
            {
                await scanner.ScanRepositoryAsync(request.ProjectId, repoId, cancellationToken);
                scannedIds.Add(repoId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to scan repository {RepositoryId}", repoId);
                errors.Add($"Repository {repoId}: {ex.Message}");
            }
        }

        // A scan that hit a limit still completes: the caller is told which ones are partial, and why
        var outcomes = await context.GitHubRepositories
            .Where(x => x.ProjectId == request.ProjectId)
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
