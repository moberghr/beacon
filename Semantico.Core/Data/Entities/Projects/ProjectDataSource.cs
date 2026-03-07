using Semantico.Core.Data.Entities.Base;

namespace Semantico.Core.Data.Entities.Projects;

public class ProjectDataSource : BaseEntity
{
    public int ProjectId { get; set; }
    public int DataSourceId { get; set; }

    public Project Project { get; set; } = null!;
    public DataSource DataSource { get; set; } = null!;
}
