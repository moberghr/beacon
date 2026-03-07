using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities.Projects;

namespace Semantico.Core.Handlers.Projects;

internal sealed class CreateProjectHandler(SemanticoContext context)
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
        foreach (var repoUrl in request.RepositoryUrls.Where(u => !string.IsNullOrWhiteSpace(u)))
        {
            context.GitHubRepositories.Add(new GitHubRepository
            {
                ProjectId = project.Id,
                RepositoryUrl = repoUrl
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
    List<string> RepositoryUrls) : IRequest<CreateProjectResult>;

public record CreateProjectResult(int ProjectId);
