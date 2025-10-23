using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities;
using Semantico.Core.Helpers;
using Semantico.Core.Models;
using Semantico.Core.Models.Projects;
using Semantico.Core.Models.Queries;

namespace Semantico.Core.Services;

public interface IProjectService
{
    Task <BaseResponse>CreateProject(ProjectData projectData, CancellationToken cancellationToken);

    Task UpdateProject(ProjectData projectData, CancellationToken cancellationToken);

    Task DeleteProject(int projectId, CancellationToken cancellationToken);

    Task<List<ProjectListData>> GetProjects(int? projectId, CancellationToken cancellationToken);
}

internal class ProjectService(IDbContextFactory<SemanticoContext> contextFactory) : IProjectService
{
    public async Task<BaseResponse> CreateProject(ProjectData projectData, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        
        var project = new Project
        {
            Name = projectData.Name,
            ConnectionString = projectData.ConnectionString,
            DatabaseEngineType = projectData.DatabaseEngineType
        };

        context.Projects.Add(project);
        await context.SaveChangesAsync(cancellationToken);

        return new BaseResponse
        {
            Success = true
        };
    }

    public async Task DeleteProject(int projectId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        
        var project = await context.Projects
            .Where(x => x.Id == projectId)
            .SingleAsync(cancellationToken);

        if (project.QuerySteps.Count > 0)
        {
            throw new SemanticoException($"Unable to remove project due to existing query steps");
        }

        project.Archive();
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<ProjectListData>> GetProjects(int? projectId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        
        return await context.Projects
            .Include(x => x.QuerySteps)
                .ThenInclude(qs => qs.Query)
                    .ThenInclude(q => q.Subscriptions)
            .Include(x => x.QuerySteps)
                .ThenInclude(qs => qs.Parameters)
            .WhereIf(projectId.HasValue, x => x.Id == projectId)
            .Select(x => new ProjectListData
            {
                Id = x.Id,
                Name = x.Name,
                DatabaseEngineType = x.DatabaseEngineType,
                Queries = x.QuerySteps
                    .GroupBy(qs => qs.QueryId)
                    .Select(g => new QueryData
                    {
                        QueryId = g.Key,
                        Name = g.First().Query.Name,
                        Description = g.First().Query.Description,
                        CreatedTime = g.First().Query.CreatedTime,
                        SubscriptionsCount = g.First().Query.Subscriptions.Count,
                        Steps = g.OrderBy(qs => qs.StepOrder).Select(qs => new QueryStepData
                        {
                            StepId = qs.Id,
                            StepOrder = qs.StepOrder,
                            Name = qs.Name ?? $"Step {qs.StepOrder}",
                            Description = qs.Description,
                            SqlValue = qs.SqlValue,
                            ProjectId = qs.ProjectId,
                            ProjectName = x.Name,
                            DatabaseEngineType = x.DatabaseEngineType,
                            Parameters = qs.Parameters.Select(p => new QueryStepParameterData
                            {
                                Name = p.Name,
                                Type = p.Type,
                                Description = p.Description,
                                Placeholder = p.Placeholder
                            }).ToList()
                        }).ToList()
                    }).ToList()
            })
            .ToListAsync(cancellationToken);
    }

    public async Task UpdateProject(ProjectData projectData, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        
        var project = await context.Projects
            .Where(x => x.Id == projectData.ProjectId)
            .SingleAsync(cancellationToken);

        project.Name = projectData.Name;
        project.ConnectionString = projectData.ConnectionString;
        project.DatabaseEngineType = projectData.DatabaseEngineType;

        await context.SaveChangesAsync(cancellationToken);
    }
}