namespace Beacon.Core.Authorization;

/// <summary>
/// One access check an <see cref="IProjectAuthorizedRequest"/> declares: <see cref="Action"/> on <see cref="Resource"/>.
/// A caller that may not read the resource gets 404 (as for a missing one); one that may read it but not take the
/// action gets 403.
/// </summary>
public sealed record ProjectCheck(ProjectResource Resource, ProjectAction Action)
{
    /// <summary>
    /// The resource writes data-source state every project linking the source shares (its metadata, relationships and
    /// descriptions): the caller also needs <see cref="ProjectAction.Write"/> in every live project that links it.
    /// </summary>
    public bool InEveryLinkingProject { get; init; }

    /// <summary>
    /// A functional permission (<see cref="BeaconPermissions"/>) whose holder passes this check without project access,
    /// once the resource exists: an Approver reading an approval request, a UserAdmin managing a project's members.
    /// </summary>
    public string? GrantedBy { get; init; }

    /// <summary>A check on shared data-source state: <paramref name="action"/>, and Write in every live project linking it.</summary>
    public static ProjectCheck SharedSource(ProjectResource resource, ProjectAction action) =>
        new(resource, action) { InEveryLinkingProject = true };

    /// <summary>A check a holder of <paramref name="permission"/> passes without project access (<see cref="GrantedBy"/>).</summary>
    public static ProjectCheck OrPermission(ProjectResource resource, ProjectAction action, string permission) =>
        new(resource, action) { GrantedBy = permission };
}
