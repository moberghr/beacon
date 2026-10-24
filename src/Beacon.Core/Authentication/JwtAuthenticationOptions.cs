using System.Text;

namespace Beacon.Core.Authentication;

/// <summary>
/// Configuration options for JWT authentication integration.
/// Supports both login form flow (external API) and bearer token flow.
/// </summary>
public class JwtAuthenticationOptions
{
    // Claims an ID token can carry (OpenID Connect Core, plus the app roles some providers add): none of them marks an
    // access token, so none can be the AccessTokenClaim.
    private static readonly HashSet<string> IdTokenClaimNames = new(StringComparer.Ordinal)
    {
        "iss", "sub", "aud", "exp", "iat", "nbf", "auth_time", "nonce", "acr", "amr", "azp", "at_hash", "c_hash", "roles"
    };

    /// <summary>
    /// External API endpoint for authentication, an https URL (e.g., "https://auth.example.com/api/login").
    /// When configured, the login form will call this endpoint to authenticate users
    /// and receive a JWT token.
    /// </summary>
    public string? ExternalLoginEndpoint { get; set; }

    /// <summary>
    /// Enable bearer token authentication via Authorization header.
    /// When enabled, requests with "Authorization: Bearer {token}" will be validated.
    /// </summary>
    public bool EnableBearerAuthentication { get; set; }

    /// <summary>
    /// The claim that marks an access token from an issuer other than Microsoft Entra ID, for identity providers that do
    /// not set the RFC 9068 header <c>typ: at+jwt</c> on their access tokens (for example <c>scope</c> or <c>scp</c>).
    /// Outside <c>/beacon/mcp</c> and in the login-form flow, such an issuer's token is accepted only with
    /// <c>typ: at+jwt</c> or with this claim present; Entra tokens are recognised by their own claims and ignore this
    /// setting. Name a claim the provider puts in access tokens and never in ID tokens; a claim any ID token can carry
    /// is refused at startup. Default: null (only <c>at+jwt</c> tokens from issuers other than Entra).
    /// </summary>
    public string? AccessTokenClaim { get; set; }

    /// <summary>
    /// JWT token validation options.
    /// </summary>
    public JwtValidationOptions Validation { get; set; } = new();

    /// <summary>
    /// Maps JWT claims to Beacon user properties.
    /// </summary>
    public JwtClaimsMappingOptions ClaimsMapping { get; set; } = new();

    /// <summary>
    /// Validates that the options are internally consistent. <c>AddBeaconServices</c> and
    /// <c>AddBeaconJwtAuthentication</c> call it, so a host fails at startup rather than accepting tokens it cannot pin.
    /// Any flow that validates tokens — bearer authentication or external login — requires a signing key or a JWKS
    /// endpoint, at least one issuer and one audience, issuer, audience and lifetime validation left on, and a clock skew of
    /// at most five minutes: a shared JWKS (Entra's, for one) signs tokens for every tenant and every application. An
    /// <see cref="AccessTokenClaim"/>, when set, must not be a claim an ID token can carry. A
    /// <see cref="JwtValidationOptions.SigningKey"/> is at least <see cref="JwtValidationOptions.MinimumSigningKeyBytes"/>
    /// bytes, and <see cref="JwtValidationOptions.JwksEndpoint"/> and <see cref="ExternalLoginEndpoint"/> are absolute
    /// <c>https</c> URLs; <c>http</c> is accepted here for a loopback host only, and the host refuses even that at startup
    /// outside Development.
    /// </summary>
    public void Validate()
    {
        var validatesTokens = EnableBearerAuthentication || !string.IsNullOrWhiteSpace(ExternalLoginEndpoint);
        if (!validatesTokens)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Validation.SigningKey) && string.IsNullOrWhiteSpace(Validation.JwksEndpoint))
        {
            throw new InvalidOperationException(
                "JWT authentication requires Validation.SigningKey or Validation.JwksEndpoint to be configured " +
                "when EnableBearerAuthentication or ExternalLoginEndpoint is set.");
        }

        if (!Validation.ValidateIssuer || Validation.EffectiveIssuers().Count == 0)
        {
            throw new InvalidOperationException(
                "JWT authentication requires issuer validation when EnableBearerAuthentication or ExternalLoginEndpoint " +
                "is set: configure Validation.ValidIssuer (or ValidIssuers) and leave ValidateIssuer on.");
        }

        if (!Validation.ValidateAudience || Validation.EffectiveAudiences().Count == 0)
        {
            throw new InvalidOperationException(
                "JWT authentication requires audience validation when EnableBearerAuthentication or ExternalLoginEndpoint " +
                "is set: configure Validation.ValidAudience (or ValidAudiences) and leave ValidateAudience on.");
        }

        if (!Validation.ValidateLifetime)
        {
            throw new InvalidOperationException(
                "JWT authentication requires lifetime validation when EnableBearerAuthentication or ExternalLoginEndpoint " +
                "is set: leave Validation.ValidateLifetime on.");
        }

        if (Validation.ClockSkew < TimeSpan.Zero || Validation.ClockSkew > JwtValidationOptions.MaxClockSkew)
        {
            throw new InvalidOperationException(
                $"JWT Validation.ClockSkew must be between zero and {JwtValidationOptions.MaxClockSkew.TotalMinutes:0} minutes.");
        }

        if (!string.IsNullOrEmpty(Validation.SigningKey)
            && Encoding.UTF8.GetByteCount(Validation.SigningKey) < JwtValidationOptions.MinimumSigningKeyBytes)
        {
            throw new InvalidOperationException(
                $"JWT Validation.SigningKey must be at least {JwtValidationOptions.MinimumSigningKeyBytes} bytes: use a random " +
                "value (for example base64 of 32 or more random bytes), never a word or a phrase.");
        }

        RequireHttps(nameof(ExternalLoginEndpoint), ExternalLoginEndpoint);
        RequireHttps($"{nameof(Validation)}.{nameof(JwtValidationOptions.JwksEndpoint)}", Validation.JwksEndpoint);

        if (AccessTokenClaim != null && IdTokenClaimNames.Contains(AccessTokenClaim.Trim()))
        {
            throw new InvalidOperationException(
                $"JWT AccessTokenClaim '{AccessTokenClaim.Trim()}' does not tell an access token from an ID token: name a " +
                "claim the identity provider puts in access tokens only (for example 'scope' or 'scp').");
        }
    }

    /// <summary>
    /// The names of the configured endpoints that use <c>http</c> to a loopback host: <see cref="Validate"/> accepts them,
    /// and the host refuses them at startup outside Development.
    /// </summary>
    internal IReadOnlyList<string> LoopbackHttpEndpoints()
    {
        return new[]
            {
                (Name: nameof(ExternalLoginEndpoint), Url: ExternalLoginEndpoint),
                (Name: $"{nameof(Validation)}.{nameof(JwtValidationOptions.JwksEndpoint)}", Url: Validation.JwksEndpoint)
            }
            .Where(x => IsLoopbackHttp(x.Url))
            .Select(x => x.Name)
            .ToList();
    }

    private static void RequireHttps(string name, string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"JWT {name} must be an absolute https URL.");
        }

        if (uri.Scheme == Uri.UriSchemeHttps || IsLoopbackHttp(url))
        {
            return;
        }

        throw new InvalidOperationException(
            $"JWT {name} must be an https URL: keys or passwords sent over plain http can be read or replaced on the way. " +
            "http is accepted only for a loopback host in Development.");
    }

    private static bool IsLoopbackHttp(string? url)
    {
        return Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttp
            && uri.IsLoopback;
    }
}

/// <summary>
/// Options for validating JWT tokens.
/// </summary>
public class JwtValidationOptions
{
    /// <summary>
    /// Expected issuer (iss) claim value.
    /// </summary>
    public string? ValidIssuer { get; set; }

    /// <summary>
    /// Additional accepted issuers, combined with <see cref="ValidIssuer"/> (e.g. the Entra v1 and v2 issuers of one
    /// tenant).
    /// </summary>
    public List<string> ValidIssuers { get; set; } = [];

    /// <summary>
    /// Expected audience (aud) claim value.
    /// </summary>
    public string? ValidAudience { get; set; }

    /// <summary>
    /// Additional accepted audiences, combined with <see cref="ValidAudience"/>.
    /// <para>
    /// Microsoft Entra ID: list both the Application ID URI (<c>api://{client-id}</c>) and the bare client id GUID.
    /// Which one a token's <c>aud</c> carries depends on the API app registration's token version — v2 access tokens
    /// (<c>accessTokenAcceptedVersion: 2</c>) carry the client id GUID, v1 tokens carry the Application ID URI as
    /// requested. The audience is always the app that owns the requested scope (e.g.
    /// <c>api://{client-id}/mcp.access</c>), never the MCP endpoint URL — even though MCP clients also send that URL as
    /// the RFC 8707 <c>resource</c> parameter. Entra does not ignore that parameter: it must exactly match one of the
    /// app's Application ID URIs (<c>identifierUris</c>), or the token request fails with <c>AADSTS9010010</c>.
    /// </para>
    /// </summary>
    public List<string> ValidAudiences { get; set; } = [];

    /// <summary>The fewest bytes (UTF-8) <see cref="SigningKey"/> may have: the size of an HS256 key.</summary>
    public const int MinimumSigningKeyBytes = 32;

    /// <summary>
    /// HMAC secret key for symmetric signing (HS256, HS384, HS512), at least <see cref="MinimumSigningKeyBytes"/> bytes
    /// (UTF-8) and random (for example base64 of 32 or more random bytes). HS384 and HS512 tokens need a key of at least 48
    /// and 64 bytes. With a signing key, only HS256, HS384 and HS512 tokens are accepted.
    /// Either SigningKey or JwksEndpoint must be provided.
    /// </summary>
    public string? SigningKey { get; set; }

    /// <summary>
    /// JWKS endpoint for asymmetric key discovery, an https URL. With a JWKS endpoint, only RS256/384/512, PS256/384/512 and
    /// ES256/384/512 tokens are accepted.
    /// Used for OIDC integration. Either SigningKey or JwksEndpoint must be provided.
    /// </summary>
    public string? JwksEndpoint { get; set; }

    /// <summary>The largest <see cref="ClockSkew"/> <see cref="JwtAuthenticationOptions.Validate"/> accepts.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Allowed clock skew for token expiration validation, at most <see cref="MaxClockSkew"/>.
    /// Default: 5 minutes.
    /// </summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether to validate the token lifetime (exp claim). Must stay true whenever tokens are validated:
    /// <see cref="JwtAuthenticationOptions.Validate"/> refuses to start with it off.
    /// Default: true.
    /// </summary>
    public bool ValidateLifetime { get; set; } = true;

    /// <summary>
    /// Whether to validate the issuer (iss claim). Must stay true whenever tokens are validated:
    /// <see cref="JwtAuthenticationOptions.Validate"/> refuses to start with it off.
    /// </summary>
    public bool ValidateIssuer { get; set; } = true;

    /// <summary>
    /// Whether to validate the audience (aud claim). Must stay true whenever tokens are validated:
    /// <see cref="JwtAuthenticationOptions.Validate"/> refuses to start with it off.
    /// </summary>
    public bool ValidateAudience { get; set; } = true;

    /// <summary><see cref="ValidIssuer"/> plus <see cref="ValidIssuers"/>, blanks and duplicates removed.</summary>
    public IReadOnlyList<string> EffectiveIssuers() => Combine(ValidIssuer, ValidIssuers);

    /// <summary><see cref="ValidAudience"/> plus <see cref="ValidAudiences"/>, blanks and duplicates removed.</summary>
    public IReadOnlyList<string> EffectiveAudiences() => Combine(ValidAudience, ValidAudiences);

    private static List<string> Combine(string? single, IEnumerable<string>? many)
    {
        return new[] { single }
            .Concat(many ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}

/// <summary>
/// Maps JWT claims to Beacon user properties.
/// </summary>
public class JwtClaimsMappingOptions
{
    /// <summary>
    /// JWT claim containing the user ID.
    /// Default: "sub" (standard JWT subject claim).
    /// </summary>
    public string UserIdClaim { get; set; } = "sub";

    /// <summary>
    /// JWT claim containing the username.
    /// Default: "preferred_username" (OIDC standard).
    /// </summary>
    public string UserNameClaim { get; set; } = "preferred_username";

    /// <summary>
    /// JWT claim containing the email address.
    /// Default: "email" (OIDC standard).
    /// </summary>
    public string EmailClaim { get; set; } = "email";

    /// <summary>
    /// JWT claim containing user roles (can be array or comma-separated string).
    /// Default: "roles".
    /// </summary>
    public string RolesClaim { get; set; } = "roles";

    /// <summary>
    /// JWT claim containing the display name.
    /// Default: "name" (OIDC standard).
    /// </summary>
    public string DisplayNameClaim { get; set; } = "name";
}
