using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.Projects;

internal sealed class CreateProjectHandler(BeaconContext context, IEncryptionService encryptionService)
    : IRequestHandler<CreateProjectCommand, CreateProjectResult>
{
    public async Task<CreateProjectResult> Handle(
        CreateProjectCommand request,
        CancellationToken cancellationToken)
    {
        var project = new Project
        {
            Name = request.Name,
            Description = request.Description
        };

        context.Projects.Add(project);
        await context.SaveChangesAsync(cancellationToken);

        // Link data sources
        foreach (var dsId in request.DataSourceIds)
        {
            context.ProjectDataSources.Add(new ProjectDataSource
            {
                ProjectId = project.Id,
                DataSourceId = dsId
            });
        }

        // Link repositories
        var encryptedToken = !string.IsNullOrWhiteSpace(request.AccessToken)
            ? encryptionService.Encrypt(request.AccessToken)
            : null;

        foreach (var repoUrl in request.RepositoryUrls.Where(u => !string.IsNullOrWhiteSpace(u)))
        {
            context.GitHubRepositories.Add(new GitHubRepository
            {
                ProjectId = project.Id,
                RepositoryUrl = repoUrl,
                EncryptedAccessToken = encryptedToken
            });
        }

        await context.SaveChangesAsync(cancellationToken);

        return new CreateProjectResult(project.Id);
    }
}

public record CreateProjectCommand(
    string Name,
    string? Description,
    List<int> DataSourceIds,
    List<string> RepositoryUrls,
    string? AccessToken = null) : IRequest<CreateProjectResult>;

public record CreateProjectResult(int ProjectId);
