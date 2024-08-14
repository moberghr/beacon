using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities;
using Semantico.Core.Helpers;
using Semantico.Core.Models;
using Semantico.Core.Models.Projects;
using Semantico.Core.Models.Queries;

namespace Semantico.Core.Services
{
    public interface IProjectService
    {
        Task CreateProjectAsync(ProjectData projectData, CancellationToken cancellationToken);

        Task UpdateProjectAsync(ProjectData projectData, CancellationToken cancellationToken);

        Task DeleteProjectAsync(int projectId, CancellationToken cancellationToken);

        Task<List<ProjectListData>> GetProjectsAsync(int? projectId, CancellationToken cancellationToken);
    }

    internal class ProjectService : IProjectService
    {
        private readonly SemanticoContext _context;

        public ProjectService(SemanticoContext context)
        {
            _context = context;
        }

        public async Task CreateProjectAsync(ProjectData projectData, CancellationToken cancellationToken)
        {
            var project = new Project
            {
                Name = projectData.Name,
                ConnectionString = projectData.ConnectionString,
                DatabaseEngineType = projectData.DatabaseEngineType
            };

            _context.Projects.Add(project);
            await _context.SaveChangesAsync(cancellationToken);
        }

        public async Task DeleteProjectAsync(int projectId, CancellationToken cancellationToken)
        {
            var project = await _context.Projects
                .Where(x => x.Id == projectId)
                .SingleAsync(cancellationToken);

            if (project.Queries.Count > 0)
            {
                throw new SemanticoException($"Unable to remove project due to existing queries");
            }

            project.Archive();
            await _context.SaveChangesAsync(cancellationToken);
        }

        public async Task<List<ProjectListData>> GetProjectsAsync(int? projectId, CancellationToken cancellationToken)
        {
            return await _context.Projects
                .WhereIf(projectId.HasValue, x => x.Id == projectId)
                .Select(x => new ProjectListData
                    {
                        Name = x.Name,
                        DatabaseEngineType = x.DatabaseEngineType,
                        Queries = x.Queries.Select(y => new QueryData
                        {
                            QueryId = y.Id,
                            SqlValue = y.SqlValue,
                            ProjectId = y.ProjectId,
                            Parameters = y.Parameters.Select(z =>
                                new QueryParameterData
                                {
                                    Name = z.Name,
                                    Type = z.Type,
                                    Description = z.Description,
                                    Placeholder = z.Placeholder
                                }).ToList()
                        }).ToList()
                    })
                 .ToListAsync(cancellationToken);
        }

        public async Task UpdateProjectAsync(ProjectData projectData, CancellationToken cancellationToken)
        {
            var project = await _context.Projects
                .Where(x => x.Id == projectData.ProjectId)
                .SingleAsync(cancellationToken);

            project.Name = projectData.Name;
            project.ConnectionString = projectData.ConnectionString;
            project.DatabaseEngineType = projectData.DatabaseEngineType;

            await _context.SaveChangesAsync(cancellationToken);
        }
    }
}
