namespace Beacon.Core.Authentication;

/// <summary>
/// Configuration options for JWT authentication integration.
/// Supports both login form flow (external API) and bearer token flow.
/// </summary>
public class JwtAuthenticationOptions
{
    /// <summary>
    /// External API endpoint for authentication (e.g., "https://auth.example.com/api/login").
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
    /// JWT token validation options.
    /// </summary>
    public JwtValidationOptions Validation { get; set; } = new();

    /// <summary>
    /// Maps JWT claims to Beacon user properties.
    /// </summary>
    public JwtClaimsMappingOptions ClaimsMapping { get; set; } = new();

    /// <summary>
    /// Validates that the options are internally consistent. Call after binding configuration
    /// (e.g. from <c>AddBeaconServices</c> / the host composition root) before the provider is used.
    /// Any flow that validates tokens — bearer authentication or external login — requires either a
    /// signing key or a JWKS endpoint; otherwise token signatures cannot be verified.
    /// </summary>
    public void Validate()
    {
        var requiresKey = EnableBearerAuthentication || !string.IsNullOrWhiteSpace(ExternalLoginEndpoint);
        if (!requiresKey)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Validation.SigningKey) && string.IsNullOrWhiteSpace(Validation.JwksEndpoint))
        {
            throw new InvalidOperationException(
                "JWT authentication requires Validation.SigningKey or Validation.JwksEndpoint to be configured " +
                "when EnableBearerAuthentication or ExternalLoginEndpoint is set.");
        }
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

    /// <summary>
    /// HMAC secret key for symmetric signing (HS256, HS384, HS512).
    /// Either SigningKey or JwksEndpoint must be provided.
    /// </summary>
    public string? SigningKey { get; set; }

    /// <summary>
    /// JWKS endpoint for asymmetric key discovery (RS256, etc.).
    /// Used for OIDC integration. Either SigningKey or JwksEndpoint must be provided.
    /// </summary>
    public string? JwksEndpoint { get; set; }

    /// <summary>
    /// Allowed clock skew for token expiration validation.
    /// Default: 5 minutes.
    /// </summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether to validate the token lifetime (exp claim).
    /// Default: true.
    /// </summary>
    public bool ValidateLifetime { get; set; } = true;

    /// <summary>
    /// Whether to validate the issuer (iss claim).
    /// Default: true when ValidIssuer is set.
    /// </summary>
    public bool ValidateIssuer { get; set; } = true;

    /// <summary>
    /// Whether to validate the audience (aud claim).
    /// Default: true when ValidAudience is set.
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
