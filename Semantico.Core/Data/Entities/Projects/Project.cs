using Semantico.Core.Data.Entities.Base;

namespace Semantico.Core.Data.Entities.Projects;

public class Project : ArchivableBaseEntity
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public List<ProjectDataSource> DataSources { get; set; } = new();
    public List<GitHubRepository> Repositories { get; set; } = new();
    public List<ProjectReport> Reports { get; set; } = new();
}
