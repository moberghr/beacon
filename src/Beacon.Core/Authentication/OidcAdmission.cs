using System.Security.Claims;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.Authentication;

/// <summary>The outcome of <see cref="OidcAdmission.Evaluate"/>; anything but <see cref="Admitted"/> is a rejection.</summary>
internal enum OidcAdmissionDecision
{
    Admitted,
    TenantNotAllowed,
    Guest,
    MissingRequiredMembership
}

/// <summary>
/// Decides whether a subject the identity provider authenticated may use Beacon at all, before Beacon provisions or
/// looks up anything for them: tenant allow-list, Entra B2B guest block, and the optional group/app-role requirement.
/// Reads raw claim types by their exact names (the OIDC handler runs with <c>MapInboundClaims = false</c>, and bearer
/// tokens keep their payload names).
/// </summary>
internal static class OidcAdmission
{
    private static int _guestSignalWarningLogged;

    public static OidcAdmissionDecision Evaluate(ClaimsPrincipal principal, OidcAuthenticationOptions options)
    {
        if (!options.AllowAnyTenant && !IsAllowedTenant(principal, options))
        {
            return OidcAdmissionDecision.TenantNotAllowed;
        }

        if (options.BlockGuests && IsEntraGuest(principal))
        {
            return OidcAdmissionDecision.Guest;
        }

        if (!HasRequiredMembership(principal, options))
        {
            return OidcAdmissionDecision.MissingRequiredMembership;
        }

        return OidcAdmissionDecision.Admitted;
    }

    /// <summary>
    /// An Entra token (it carries <c>tid</c>) whose subject is a guest of the signing tenant: <c>acct</c> is <c>1</c>
    /// (optional claim), or <c>idp</c> names an identity provider other than the signing tenant. A member's <c>idp</c>,
    /// when present at all, is exactly the token issuer or one of the tenant's own Entra issuers
    /// (<c>https://sts.windows.net/{tid}/</c>, <c>https://login.microsoftonline.com/{tid}/v2.0</c>); anything else,
    /// including a URL that merely contains the tenant id, is a guest. Tokens without <c>tid</c> are not Entra tokens
    /// and have no guest notion here. A token with neither <c>acct</c> nor <c>idp</c> is admitted as a member.
    /// </summary>
    internal static bool IsEntraGuest(ClaimsPrincipal principal)
    {
        var tenantId = GetClaim(principal, "tid");
        if (tenantId == null)
        {
            return false;
        }

        if (GetClaim(principal, "acct") == "1")
        {
            return true;
        }

        var identityProvider = GetClaim(principal, "idp");
        if (identityProvider == null)
        {
            return false;
        }

        var ownIssuers = new[]
        {
            GetClaim(principal, "iss"),
            $"https://sts.windows.net/{tenantId}/",
            $"https://login.microsoftonline.com/{tenantId}/v2.0"
        };

        return !ownIssuers.Any(x => string.Equals(x, identityProvider, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True for an Entra token (it carries <c>tid</c>) with neither <c>acct</c> nor <c>idp</c>: guest detection then has
    /// nothing to go on, and such a token is admitted as a member.
    /// </summary>
    internal static bool LacksGuestSignal(ClaimsPrincipal principal)
    {
        return GetClaim(principal, "tid") != null
            && GetClaim(principal, "acct") == null
            && GetClaim(principal, "idp") == null;
    }

    /// <summary>
    /// Logs one Warning per process when guests are blocked but an Entra token carried neither <c>acct</c> nor
    /// <c>idp</c>: the app registration should emit the <c>acct</c> optional claim. No claim value is logged.
    /// </summary>
    internal static void WarnOnceWhenGuestSignalMissing(
        ClaimsPrincipal principal,
        OidcAuthenticationOptions options,
        ILogger logger)
    {
        if (!options.BlockGuests || !LacksGuestSignal(principal))
        {
            return;
        }

        if (Interlocked.Exchange(ref _guestSignalWarningLogged, 1) == 1)
        {
            return;
        }

        logger.LogWarning(
            "BlockGuests is on, but an Entra token carried neither the 'acct' nor the 'idp' claim, so a guest is told apart " +
            "from a member only when the token names a foreign identity provider. Add 'acct' as an optional claim in the " +
            "Entra app registration (ID and access tokens).");
    }

    /// <summary>Re-arms <see cref="WarnOnceWhenGuestSignalMissing"/>; tests only.</summary>
    internal static void ResetWarnings()
    {
        Interlocked.Exchange(ref _guestSignalWarningLogged, 0);
    }

    private static bool IsAllowedTenant(ClaimsPrincipal principal, OidcAuthenticationOptions options)
    {
        var tenantId = GetClaim(principal, "tid");

        return tenantId != null
            && options.AllowedTenants.Any(x => string.Equals(x?.Trim(), tenantId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasRequiredMembership(ClaimsPrincipal principal, OidcAuthenticationOptions options)
    {
        var requiredRoles = NonBlank(options.RequiredRoles);
        var requiredGroups = NonBlank(options.RequiredGroups);
        if (requiredRoles.Count == 0 && requiredGroups.Count == 0)
        {
            return true;
        }

        var roles = GetClaimValues(principal, "roles");
        var groups = GetClaimValues(principal, "groups");

        return requiredRoles.Any(roles.Contains) || requiredGroups.Any(groups.Contains);
    }

    private static List<string> NonBlank(IEnumerable<string> values)
    {
        return values
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToList();
    }

    private static string? GetClaim(ClaimsPrincipal principal, string type)
    {
        var value = principal.Claims
            .Where(x => string.Equals(x.Type, type, StringComparison.Ordinal))
            .Select(x => x.Value)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static HashSet<string> GetClaimValues(ClaimsPrincipal principal, string type)
    {
        return principal.Claims
            .Where(x => string.Equals(x.Type, type, StringComparison.Ordinal))
            .Select(x => x.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
