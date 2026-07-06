namespace Beacon.Core.Authorization;

/// <summary>
/// Standard claim types used by Beacon.
/// External systems should map their claims to these types.
/// </summary>
public static class BeaconClaims
{
    public const string UserId = "beacon:user_id";
    public const string UserName = "beacon:user_name";
    public const string Role = "beacon:role";
    public const string Permission = "beacon:permission";
}
