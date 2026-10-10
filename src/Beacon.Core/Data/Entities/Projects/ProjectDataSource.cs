using Beacon.Core.Data.Entities.Base;

namespace Beacon.Core.Data.Entities.Projects;

public class ProjectDataSource : BaseEntity
{
    public int ProjectId { get; set; }
    public int DataSourceId { get; set; }

    /// <summary>
    /// The <c>Users.Id</c> of the user who linked the data source to the project; null for links made by host
    /// synchronization or before links recorded who made them.
    /// </summary>
    public int? LinkedByUserId { get; set; }

    public Project Project { get; set; } = null!;
    public DataSource DataSource { get; set; } = null!;
}
