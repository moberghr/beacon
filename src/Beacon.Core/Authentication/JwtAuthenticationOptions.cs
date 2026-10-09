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
    /// Validates that the options are internally consistent. <c>AddBeaconServices</c> and
    /// <c>AddBeaconJwtAuthentication</c> call it, so a host fails at startup rather than accepting tokens it cannot pin.
    /// Any flow that validates tokens — bearer authentication or external login — requires a signing key or a JWKS
    /// endpoint, at least one issuer and one audience, issuer, audience and lifetime validation left on, and a clock skew of
    /// at most five minutes: a shared JWKS (Entra's, for one) signs tokens for every tenant and every application.
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
    /// Additional accepted audiences, combined with <see cref="ValidAudience"/> (e.g. both <c>api://{client-id}</c>
    /// and the bare client id).
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
