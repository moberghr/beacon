using Beacon.Core.Authorization;
using Beacon.Core.Data.Entities.Base;

namespace Beacon.Core.Data.Entities.Projects;

/// <summary>
/// A Beacon user's role in a project. In a restricted project (<see cref="Project.RequiresMembership"/>) the row is what
/// grants access; in an open project it adds only <see cref="ProjectRole.ProjectAdmin"/> rights. Not archivable: a
/// revocation deletes the row. A disabled or archived user's rows grant nothing. <see cref="BaseEntity.CreatedTime"/> is
/// the grant time.
/// </summary>
public class ProjectMember : BaseEntity
{
    public int ProjectId { get; set; }

    public int UserId { get; set; }

    public ProjectRole Role { get; set; }

    /// <summary>The <c>Users.Id</c> of whoever granted the row. Not a foreign key (a second path from Users).</summary>
    public int? AddedByUserId { get; set; }

    /// <summary>When the role last changed; null while it is the role it was granted with.</summary>
    public DateTime? ModifiedTime { get; set; }

    public Project Project { get; set; } = null!;

    public BeaconUser User { get; set; } = null!;
}
