namespace Beacon.Core.Authorization;

/// <summary>
/// A user's role in one project (<c>ProjectMember.Role</c>). The effective role in a project is the lower of this and
/// the user's global role, and in an open project the open grant applies as well.
/// </summary>
public enum ProjectRole
{
    /// <summary>Reads the project's resources and runs its saved SQL.</summary>
    Viewer = 1,

    /// <summary>Viewer, plus ad-hoc SQL and LLM work, and creating and changing the project's resources.</summary>
    Editor = 2,

    /// <summary>Editor, plus deleting any of the project's resources, managing its members and linking data sources.</summary>
    ProjectAdmin = 3
}
