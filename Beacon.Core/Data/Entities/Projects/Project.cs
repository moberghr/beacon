using Beacon.Core.Data.Entities.Base;

namespace Beacon.Core.Data.Entities.Projects;

public class Project : ArchivableBaseEntity
{
    public required string Name { get; set; }
    public string? Description { get; set; }

    public List<ProjectDataSource> DataSources { get; set; } = new();
    public List<GitHubRepository> Repositories { get; set; } = new();
    public List<ProjectDocumentation> Documentations { get; set; } = new();
}
