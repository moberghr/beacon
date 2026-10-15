namespace Beacon.Core.Authorization;

/// <summary>
/// A request that addresses resources by id. Before its handler runs, the project authorization behaviour resolves each
/// of its <see cref="ProjectChecks"/> and asks the caller's project access: a resource the caller may not read (or a
/// missing one) is 404, an action it may not take is 403. Every <c>*Id</c>/<c>*Ids</c> property must be covered by a
/// check, or carry <see cref="ProjectScopeExemptAttribute"/> with its reason.
/// </summary>
public interface IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> ProjectChecks { get; }
}
