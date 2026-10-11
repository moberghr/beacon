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

    /// <summary>
    /// A functional permission (<see cref="BeaconPermissions"/>) the session's single sign-on mapped from an identity
    /// provider app role (<c>Beacon:Authorization:IdpRoles</c>). Added by Beacon's sign-in only (claims the identity
    /// provider sends under the <c>beacon:</c> prefix are removed), with <see cref="Issuer"/> as its issuer and next to
    /// <see cref="IdpPermissionsIssuedAt"/>. Honoured only while the mapping is on, from that issuer, and for
    /// <c>IdpRoles:ClaimLifetimeMinutes</c> after the sign-in.
    /// </summary>
    public const string IdpPermission = "beacon:idp_permission";

    /// <summary>
    /// When the sign-in added the session's <see cref="IdpPermission"/> claims: Unix time in seconds, issued by
    /// <see cref="Issuer"/>.
    /// </summary>
    public const string IdpPermissionsIssuedAt = "beacon:idp_permissions_issued_at";

    /// <summary>
    /// The issuer of the claims Beacon's own sign-in adds for functional permissions. Claims of those types from any other
    /// issuer grant nothing.
    /// </summary>
    public const string Issuer = "urn:beacon:sign-in";
}
