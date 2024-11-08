using Semantico.Core.Data.Enums;

namespace Semantico.Core.Models.Projects;

public class ProjectData
{
    public int? ProjectId { get; init; }

    public string Name { get; set; }

    public string ConnectionString { get; set; }

    public DatabaseEngineType DatabaseEngineType { get; set; }
}