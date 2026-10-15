using Microsoft.Extensions.Logging;
using Beacon.Core.Authorization;
using Beacon.Core.Models;

namespace Beacon.Core.Handlers.DataQuality;

/// <summary>
/// A data contract is changed, disabled, retargeted, evaluated on demand or deleted by its owner (the caller that
/// created it, or the user an Admin made its owner) or an Admin. A contract without an owner is an Admin's only. A
/// contract with a Custom SQL rule needs an Admin besides (<c>DataQualityRuleGuard</c>). To anyone but an Admin, a
/// contract that does not exist is refused like a contract that is not theirs.
/// </summary>
internal static class DataContractOwnership
{
    private const string Resource = "data contract";
    private const string Refusal = "Only the data contract's owner or an Admin can change, evaluate or delete it.";

    public static void EnsureOwnerOrAdmin(BeaconActor actor, int contractId, string? ownerUserId, ILogger logger)
    {
        if (actor.IsOwnerOrAdmin(ownerUserId))
        {
            return;
        }

        throw AccessRefusal.Of(logger, actor, Resource, contractId, Refusal);
    }

    /// <summary>A contract that does not exist: not found for an Admin; for anyone else, the refusal above.</summary>
    public static Exception Missing(BeaconActor actor, int contractId, ILogger logger)
    {
        if (actor.IsAdmin)
        {
            return new BeaconException($"Data contract {contractId} not found");
        }

        return AccessRefusal.Of(logger, actor, Resource, contractId, Refusal);
    }
}
