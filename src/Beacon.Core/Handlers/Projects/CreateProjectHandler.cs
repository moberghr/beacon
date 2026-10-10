using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.Projects;

internal sealed class CreateProjectHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IEncryptionService encryptionService,
    IBeaconActorAccessor actorAccessor,
    ILogger<CreateProjectHandler> logger)
    : IRequestHandler<CreateProjectCommand, CreateProjectResult>
{
    public async Task<CreateProjectResult> Handle(
        CreateProjectCommand request,
        CancellationToken cancellationToken)
    {
        // A repository access token is an Admin's to set, whether here or on the repository's token route.
        var hasAccessToken = !string.IsNullOrWhiteSpace(request.AccessToken);
        if (hasAccessToken)
        {
            var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
            if (!actor.IsAdmin)
            {
                throw AccessRefusal.Of(logger, actor, "repository access token", null, "Only an Admin can set a repository access token.");
            }
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var encryptedToken = hasAccessToken
            ? encryptionService.Encrypt(request.AccessToken!)
            : null;

        var project = new Project
        {
            Name = request.Name,
            Description = request.Description,
            DataSources = request.DataSourceIds
                .Select(x => new ProjectDataSource { DataSourceId = x })
                .ToList(),
            Repositories = request.RepositoryUrls
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => new GitHubRepository
                {
                    RepositoryUrl = x,
                    EncryptedAccessToken = encryptedToken
                })
                .ToList()
        };

        context.Projects.Add(project);
        await context.SaveChangesAsync(cancellationToken);

        return new CreateProjectResult(project.Id);
    }
}

/// <summary>Creates a project. An <c>AccessToken</c> for its repositories may be given by an Admin only.</summary>
public record CreateProjectCommand(
    string Name,
    string? Description,
    List<int> DataSourceIds,
    List<string> RepositoryUrls,
    string? AccessToken = null) : IRequest<CreateProjectResult>;

public record CreateProjectResult(int ProjectId);
