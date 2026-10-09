using System.Security.Claims;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;

namespace Beacon.Core.Authentication;

/// <summary>Why a bearer token was refused: logged as a reason code, never returned to the caller.</summary>
internal enum BearerRefusal
{
    None,
    InvalidToken,
    IdToken,
    NotAdmitted,
    MissingSubject,
    NoBeaconUser,
    DisabledUser,
    ArchivedUser,
    NoUserStore
}

/// <summary>The outcome of <see cref="BearerUserBinding.BindAsync"/>: the bound user, or why there is none.</summary>
internal sealed record BearerBinding(BeaconUserData? User, BearerRefusal Refusal)
{
    public static BearerBinding Refused(BearerRefusal refusal) => new(null, refusal);
}

/// <summary>
/// Binds a validated bearer token to the existing Beacon user it names. A token only proves who the caller is at the
/// identity provider: the session's user, enabled state and roles always come from Beacon's user store, never from
/// the token. Nothing is provisioned here; an unknown subject has no Beacon session.
/// <para>
/// Claims are read by their exact (case-sensitive) names, as they appear in the token payload.
/// </para>
/// </summary>
internal static class BearerUserBinding
{
    private const string SubjectClaim = "sub";
    private const string IssuerClaim = "iss";

    /// <summary>
    /// Refuses tokens that must never open a REST session even when they name a user: an ID token (it carries a
    /// <c>nonce</c>, or no <c>scp</c>/<c>scope</c>/<c>roles</c> and the SSO client id as its audience), and a token
    /// the SSO authority issued to a subject the SSO admission rules (<see cref="OidcAdmission"/>) do not admit.
    /// </summary>
    public static BearerRefusal Screen(ClaimsPrincipal token, OidcAuthenticationOptions? oidc)
    {
        var ssoEnabled = oidc is { Enabled: true };
        if (IsIdToken(token, ssoEnabled ? oidc!.ClientId : null))
        {
            return BearerRefusal.IdToken;
        }

        if (ssoEnabled
            && IsIssuedByAuthority(token, oidc!.Authority)
            && OidcAdmission.Evaluate(token, oidc) != OidcAdmissionDecision.Admitted)
        {
            return BearerRefusal.NotAdmitted;
        }

        return BearerRefusal.None;
    }

    /// <summary>
    /// The enabled, non-archived external Beacon user the token names, in one lookup. The subject is the configured
    /// user-id claim (<c>sub</c> by default; a configured claim the token lacks is a refusal, never a fallback to
    /// <c>sub</c>). It matches a user stored under the token issuer, as SSO sign-in stores users, and, only when exactly
    /// one issuer is configured, a user an administrator pre-registered without an identity provider. Internal
    /// (password) users and super admins never match. A disabled or archived match refuses the token even when another
    /// row would match, so a disabled account is never reached by a second key.
    /// </summary>
    public static async Task<BearerBinding> BindAsync(
        IUserManagementService users,
        ClaimsPrincipal token,
        JwtAuthenticationOptions options,
        CancellationToken cancellationToken)
    {
        var subject = ReadSubject(token, options.ClaimsMapping.UserIdClaim);
        var issuer = ExactClaim(token, IssuerClaim);
        if (subject == null || issuer == null)
        {
            return BearerBinding.Refused(BearerRefusal.MissingSubject);
        }

        var includeWithoutIdentityProvider = options.Validation.EffectiveIssuers().Count == 1;
        var candidates = await users.GetBearerUserCandidatesAsync(
            subject,
            issuer,
            includeWithoutIdentityProvider,
            cancellationToken);

        // Re-checked ordinally after the fetch: a case-insensitive database collation must not widen the match.
        var matches = candidates
            .Where(x => !x.User.IsInternalUser)
            .Where(x => !x.User.IsSuperAdmin)
            .Where(x => string.Equals(x.User.ExternalId, subject, StringComparison.Ordinal))
            .Where(x => x.User.IdentityProvider == null
                ? includeWithoutIdentityProvider
                : string.Equals(x.User.IdentityProvider, issuer, StringComparison.Ordinal))
            .ToList();
        if (matches.Count == 0)
        {
            return BearerBinding.Refused(BearerRefusal.NoBeaconUser);
        }

        if (matches.Any(x => x.IsArchived))
        {
            return BearerBinding.Refused(BearerRefusal.ArchivedUser);
        }

        if (matches.Any(x => !x.User.IsEnabled))
        {
            return BearerBinding.Refused(BearerRefusal.DisabledUser);
        }

        // The user stored under the token issuer wins over a pre-registered user without an identity provider.
        var user = matches
            .Where(x => x.User.IdentityProvider != null)
            .Select(x => x.User)
            .FirstOrDefault() ?? matches[0].User;

        return new BearerBinding(user, BearerRefusal.None);
    }

    /// <summary>The session identity of a bound user: Beacon's ids and Beacon's roles, no token claims.</summary>
    public static AuthenticatedUser ToAuthenticatedUser(BeaconUserData user)
    {
        return new AuthenticatedUser
        {
            UserId = user.ExternalId,
            UserName = user.UserName,
            Email = user.Email,
            DisplayName = user.DisplayName,
            Roles = user.Roles
                .Select(x => x.Name)
                .ToList()
        };
    }

    /// <summary>
    /// The single, non-blank value of the claim named exactly <paramref name="type"/>; null when the claim is absent,
    /// blank or repeated.
    /// </summary>
    public static string? ExactClaim(ClaimsPrincipal token, string type)
    {
        var values = ExactClaims(token, type)
            .Take(2)
            .ToList();

        return values.Count == 1 && !string.IsNullOrWhiteSpace(values[0]) ? values[0] : null;
    }

    internal static bool IsIdToken(ClaimsPrincipal token, string? ssoClientId)
    {
        if (ExactClaims(token, "nonce").Any())
        {
            return true;
        }

        var carriesAccess = ExactClaims(token, "scp").Any()
            || ExactClaims(token, "scope").Any()
            || ExactClaims(token, "roles").Any();
        if (carriesAccess || string.IsNullOrWhiteSpace(ssoClientId))
        {
            return false;
        }

        return ExactClaims(token, "aud").Any(x => string.Equals(x, ssoClientId.Trim(), StringComparison.Ordinal));
    }

    internal static bool IsIssuedByAuthority(ClaimsPrincipal token, string? authority)
    {
        var issuer = ExactClaim(token, IssuerClaim);
        if (issuer == null || string.IsNullOrWhiteSpace(authority))
        {
            return false;
        }

        return string.Equals(issuer.TrimEnd('/'), authority.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadSubject(ClaimsPrincipal token, string? userIdClaim)
    {
        var claimType = string.IsNullOrWhiteSpace(userIdClaim) ? SubjectClaim : userIdClaim.Trim();

        return ExactClaim(token, claimType);
    }

    private static IEnumerable<string> ExactClaims(ClaimsPrincipal token, string type)
    {
        return token.Claims
            .Where(x => string.Equals(x.Type, type, StringComparison.Ordinal))
            .Select(x => x.Value);
    }
}
