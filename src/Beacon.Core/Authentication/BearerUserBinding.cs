using System.Security.Claims;
using Microsoft.Extensions.Logging;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;

namespace Beacon.Core.Authentication;

/// <summary>Why a bearer token was refused: logged as a reason code, never returned to the caller.</summary>
internal enum BearerRefusal
{
    None,
    InvalidToken,
    IdToken,
    NotAccessToken,
    NotAdmitted,
    MissingSubject,
    NoBeaconUser,
    DisabledUser,
    ArchivedUser,
    NoUserStore
}

/// <summary>
/// The outcome of <see cref="BearerUserBinding.ScreenAndBindAsync"/>: the bound user, or why there is none.
/// </summary>
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

    // Claims OpenID Connect defines for ID tokens only.
    private static readonly string[] IdTokenClaims = ["nonce", "at_hash", "c_hash"];

    // Microsoft Entra ID issuer hosts (v1.0 and v2.0 tokens).
    private static readonly string[] EntraIssuerHosts = ["login.microsoftonline.com", "sts.windows.net"];

    // RFC 9068 JWT access token type, with and without the media-type prefix.
    private static readonly string[] JwtAccessTokenTypes = ["at+jwt", "application/at+jwt"];

    /// <summary>
    /// Refuses tokens that must never open a Beacon session even when they name a user, the same way whether or not
    /// SSO is enabled: a token carrying an ID-token claim (<c>nonce</c>, <c>at_hash</c>, <c>c_hash</c>); a token without
    /// positive evidence of being an access token (<see cref="HasAccessTokenEvidence"/>; <c>roles</c> alone is no such
    /// evidence); and a token the SSO authority issued to a subject the SSO admission rules
    /// (<see cref="OidcAdmission"/>) do not admit.
    /// </summary>
    /// <param name="tokenType">The token's JOSE header <c>typ</c>, as validated.</param>
    public static BearerRefusal Screen(
        ClaimsPrincipal token,
        string? tokenType,
        JwtAuthenticationOptions options,
        OidcAuthenticationOptions? oidc)
    {
        if (HasIdTokenClaim(token))
        {
            return BearerRefusal.IdToken;
        }

        if (!HasAccessTokenEvidence(token, tokenType, options.AccessTokenClaim))
        {
            return BearerRefusal.NotAccessToken;
        }

        if (oidc is { Enabled: true }
            && IsIssuedByAuthority(token, oidc.Authority)
            && OidcAdmission.Evaluate(token, oidc) != OidcAdmissionDecision.Admitted)
        {
            return BearerRefusal.NotAdmitted;
        }

        return BearerRefusal.None;
    }

    /// <summary>
    /// The one path from a validated token to a Beacon user, shared by REST bearer authentication and the login-form JWT
    /// flow: <see cref="Screen"/> first, then <see cref="BindAsync"/>, so a token one flow refuses never opens a session
    /// through the other. Nothing is looked up for a token the screen refuses.
    /// </summary>
    /// <param name="tokenType">The token's JOSE header <c>typ</c>, as validated.</param>
    public static async Task<BearerBinding> ScreenAndBindAsync(
        IUserManagementService users,
        ClaimsPrincipal token,
        string? tokenType,
        JwtAuthenticationOptions options,
        OidcAuthenticationOptions? oidc,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var screening = Screen(token, tokenType, options, oidc);
        if (screening != BearerRefusal.None)
        {
            return BearerBinding.Refused(screening);
        }

        if (oidc is { Enabled: true } && IsIssuedByAuthority(token, oidc.Authority))
        {
            OidcAdmission.WarnOnceWhenGuestSignalMissing(token, oidc, logger);
        }

        return await BindAsync(users, token, options, cancellationToken);
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

    internal static bool HasIdTokenClaim(ClaimsPrincipal token)
    {
        return IdTokenClaims.Any(x => ExactClaims(token, x).Any());
    }

    /// <summary>
    /// Positive evidence that the token is an access token. A Microsoft Entra ID token (issued by
    /// <c>login.microsoftonline.com</c> or <c>sts.windows.net</c>) names the client it was issued to (<c>azp</c> or
    /// <c>appid</c>) and carries <c>scp</c> or <c>roles</c>. Any other issuer's token has the RFC 9068 header
    /// <c>typ: at+jwt</c>, or carries the configured <paramref name="accessTokenClaim"/>.
    /// </summary>
    internal static bool HasAccessTokenEvidence(ClaimsPrincipal token, string? tokenType, string? accessTokenClaim)
    {
        if (IsEntraIssuer(ExactClaim(token, IssuerClaim)))
        {
            return (HasValue(token, "azp") || HasValue(token, "appid"))
                && (HasValue(token, "scp") || HasValue(token, "roles"));
        }

        if (JwtAccessTokenTypes.Any(x => string.Equals(x, tokenType?.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(accessTokenClaim) && HasValue(token, accessTokenClaim.Trim());
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

    private static bool IsEntraIssuer(string? issuer)
    {
        return Uri.TryCreate(issuer, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && EntraIssuerHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    }

    private static bool HasValue(ClaimsPrincipal token, string type)
    {
        return ExactClaims(token, type).Any(x => !string.IsNullOrWhiteSpace(x));
    }

    private static IEnumerable<string> ExactClaims(ClaimsPrincipal token, string type)
    {
        return token.Claims
            .Where(x => string.Equals(x.Type, type, StringComparison.Ordinal))
            .Select(x => x.Value);
    }
}
