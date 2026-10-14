using Beacon.Core.Authorization;
using Beacon.Core.Mcp;

namespace Beacon.Core.Handlers.ApiKeys;

/// <summary>
/// The projects a new API key of a user may name: the ones the user reads as one of its keys would, read-scoped and
/// without the Admin bypass, which keys never carry. Key creation checks each named project against it; the project
/// picker lists only these.
/// </summary>
internal static class ApiKeyProjects
{
    public static Task<ProjectAccessScope> ScopeOfOwnKeyAsync(
        IProjectAccessEvaluator projectAccess,
        BeaconActor owner,
        CancellationToken cancellationToken)
    {
        var asKey = owner with
        {
            Kind = BeaconPrincipalKind.ApiKey,
            ScopeCeiling = McpCallerScope.Read,
            AllowedProjectIds = null
        };

        return projectAccess.GetScopeAsync(asKey, cancellationToken);
    }
}
