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
    /// descriptions): the caller also needs <see cref="EveryLinkingProjectAction"/> in every live project that links it,
    /// or <see cref="BeaconPermissions.DataSourceAdmin"/> (with <see cref="Action"/> where it acts). A resource whose change
    /// writes no shared state (a documentation patch to a project's own documentation) needs
    /// <see cref="EveryLinkingProjectAction"/> in its own project instead, which no permission replaces.
    /// </summary>
    public bool InEveryLinkingProject { get; init; }

    /// <summary>
    /// The action <see cref="InEveryLinkingProject"/> demands in every live project linking the source: Write by default;
    /// <see cref="ProjectAction.Curate"/> for grounding curation. An action no open project grants (Curate) is never
    /// granted for a source no live project links: only a DataSourceAdmin or an Admin curates that one.
    /// </summary>
    public ProjectAction EveryLinkingProjectAction { get; init; } = ProjectAction.Write;

    /// <summary>
    /// A functional permission (<see cref="BeaconPermissions"/>) whose holder passes this check without project access,
    /// once the resource exists: an Approver reading an approval request, a UserAdmin managing a project's members.
    /// </summary>
    public string? GrantedBy { get; init; }

    /// <summary>
    /// A check on shared data-source state: <paramref name="action"/>, and <paramref name="everyLinkingProjectAction"/>
    /// (default Write) in every live project linking it.
    /// </summary>
    public static ProjectCheck SharedSource(
        ProjectResource resource,
        ProjectAction action,
        ProjectAction everyLinkingProjectAction = ProjectAction.Write) =>
        new(resource, action) { InEveryLinkingProject = true, EveryLinkingProjectAction = everyLinkingProjectAction };

    /// <summary>
    /// A curation of shared grounding (verifying a relationship, applying a patch to a data source's descriptions): Write
    /// where the caller acts, and <see cref="ProjectAction.Curate"/> in every live project linking the source — or a
    /// DataSourceAdmin.
    /// </summary>
    public static ProjectCheck CurateShared(ProjectResource resource) =>
        SharedSource(resource, ProjectAction.Write, ProjectAction.Curate);

    /// <summary>A check a holder of <paramref name="permission"/> passes without project access (<see cref="GrantedBy"/>).</summary>
    public static ProjectCheck OrPermission(ProjectResource resource, ProjectAction action, string permission) =>
        new(resource, action) { GrantedBy = permission };
}
