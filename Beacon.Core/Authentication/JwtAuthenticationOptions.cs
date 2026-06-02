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
    /// Expected audience (aud) claim value.
    /// </summary>
    public string? ValidAudience { get; set; }

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
