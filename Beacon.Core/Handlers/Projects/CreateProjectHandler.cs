using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.Projects;

internal sealed class CreateProjectHandler(IDbContextFactory<BeaconContext> contextFactory, IEncryptionService encryptionService)
    : IRequestHandler<CreateProjectCommand, CreateProjectResult>
{
    public async Task<CreateProjectResult> Handle(
        CreateProjectCommand request,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var encryptedToken = !string.IsNullOrWhiteSpace(request.AccessToken)
            ? encryptionService.Encrypt(request.AccessToken)
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

public record CreateProjectCommand(
    string Name,
    string? Description,
    List<int> DataSourceIds,
    List<string> RepositoryUrls,
    string? AccessToken = null) : IRequest<CreateProjectResult>;

public record CreateProjectResult(int ProjectId);
