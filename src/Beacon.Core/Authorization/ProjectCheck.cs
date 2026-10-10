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

    /// <summary>A check on shared data-source state: <paramref name="action"/>, and Write in every live project linking it.</summary>
    public static ProjectCheck SharedSource(ProjectResource resource, ProjectAction action) =>
        new(resource, action) { InEveryLinkingProject = true };
}
