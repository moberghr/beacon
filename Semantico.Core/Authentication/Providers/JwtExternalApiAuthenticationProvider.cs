using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Semantico.Core.Authentication.Providers;

/// <summary>
/// Authentication provider that authenticates against an external JWT-issuing API.
/// Sends credentials to the external endpoint and validates the returned JWT token.
/// </summary>
public class JwtExternalApiAuthenticationProvider : ISemanticoAuthenticationProvider
{
    private readonly HttpClient _httpClient;
    private readonly JwtAuthenticationOptions _options;
    private readonly ILogger<JwtExternalApiAuthenticationProvider> _logger;
    private readonly JwtSecurityTokenHandler _tokenHandler = new();

    public JwtExternalApiAuthenticationProvider(
        HttpClient httpClient,
        JwtAuthenticationOptions options,
        ILogger<JwtExternalApiAuthenticationProvider> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
    }

    public async Task<AuthenticationResult> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ExternalLoginEndpoint))
        {
            return AuthenticationResult.Failed(
                "JWT external login endpoint is not configured.");
        }

        try
        {
            // Call external API
            var response = await _httpClient.PostAsJsonAsync(
                _options.ExternalLoginEndpoint,
                new { username, password },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "External authentication API returned {StatusCode} for user {Username}",
                    response.StatusCode, username);
                return AuthenticationResult.Failed("Authentication failed.");
            }

            // Parse response - expects { "token": "jwt..." } or { "access_token": "jwt..." }
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var token = ExtractTokenFromResponse(content);

            if (string.IsNullOrEmpty(token))
            {
                _logger.LogWarning("No token found in external API response for user {Username}", username);
                return AuthenticationResult.Failed("No token received from authentication server.");
            }

            // Validate and parse the JWT
            var validationResult = await ValidateTokenAsync(token, cancellationToken);
            if (!validationResult.Success)
            {
                return validationResult;
            }

            // Extract claims and build AuthenticatedUser
            var jwtToken = _tokenHandler.ReadJwtToken(token);
            var user = BuildAuthenticatedUser(jwtToken, token);

            return AuthenticationResult.Succeeded(user);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to reach external authentication API at {Endpoint}", _options.ExternalLoginEndpoint);
            return AuthenticationResult.Failed("Unable to reach authentication server.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during JWT authentication for user {Username}", username);
            return AuthenticationResult.Failed("Authentication error occurred.");
        }
    }

    public Task<bool> ValidateSessionAsync(string userId, CancellationToken cancellationToken = default)
    {
        // JWT-based sessions don't require server-side validation
        // The token validity is checked on each request
        return Task.FromResult(true);
    }

    public Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        // JWT is stateless - no server-side session to invalidate
        return Task.CompletedTask;
    }

    /// <summary>
    /// Validates a JWT token and returns an authentication result.
    /// Can be used directly for bearer token validation.
    /// </summary>
    public async Task<AuthenticationResult> ValidateTokenAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var validationParameters = await BuildValidationParametersAsync(cancellationToken);

            _tokenHandler.ValidateToken(token, validationParameters, out var validatedToken);

            if (validatedToken is not JwtSecurityToken jwtToken)
            {
                return AuthenticationResult.Failed("Invalid token format.");
            }

            var user = BuildAuthenticatedUser(jwtToken, token);
            return AuthenticationResult.Succeeded(user);
        }
        catch (SecurityTokenExpiredException)
        {
            _logger.LogDebug("JWT token has expired");
            return AuthenticationResult.Failed("Token has expired.");
        }
        catch (SecurityTokenInvalidSignatureException)
        {
            _logger.LogWarning("JWT token has invalid signature");
            return AuthenticationResult.Failed("Invalid token signature.");
        }
        catch (SecurityTokenInvalidIssuerException)
        {
            _logger.LogWarning("JWT token has invalid issuer");
            return AuthenticationResult.Failed("Invalid token issuer.");
        }
        catch (SecurityTokenInvalidAudienceException)
        {
            _logger.LogWarning("JWT token has invalid audience");
            return AuthenticationResult.Failed("Invalid token audience.");
        }
        catch (SecurityTokenException ex)
        {
            _logger.LogWarning(ex, "JWT token validation failed");
            return AuthenticationResult.Failed("Invalid token.");
        }
    }

    private async Task<TokenValidationParameters> BuildValidationParametersAsync(
        CancellationToken cancellationToken)
    {
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = _options.Validation.ValidateIssuer && !string.IsNullOrEmpty(_options.Validation.ValidIssuer),
            ValidIssuer = _options.Validation.ValidIssuer,
            ValidateAudience = _options.Validation.ValidateAudience && !string.IsNullOrEmpty(_options.Validation.ValidAudience),
            ValidAudience = _options.Validation.ValidAudience,
            ValidateLifetime = _options.Validation.ValidateLifetime,
            ClockSkew = _options.Validation.ClockSkew
        };

        // Configure signing key
        if (!string.IsNullOrEmpty(_options.Validation.SigningKey))
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Validation.SigningKey));
            parameters.IssuerSigningKey = key;
            parameters.ValidateIssuerSigningKey = true;
        }
        else if (!string.IsNullOrEmpty(_options.Validation.JwksEndpoint))
        {
            // Fetch keys from JWKS endpoint
            var keys = await FetchJwksAsync(_options.Validation.JwksEndpoint, cancellationToken);
            parameters.IssuerSigningKeys = keys;
            parameters.ValidateIssuerSigningKey = true;
        }
        else
        {
            // No signing key configured - allow unsigned tokens (not recommended for production)
            parameters.ValidateIssuerSigningKey = false;
            parameters.SignatureValidator = (token, _) =>
                _tokenHandler.ReadJwtToken(token);
        }

        return parameters;
    }

    private async Task<IEnumerable<SecurityKey>> FetchJwksAsync(
        string jwksEndpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.GetStringAsync(jwksEndpoint, cancellationToken);
            var jwks = new JsonWebKeySet(response);
            return jwks.GetSigningKeys();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch JWKS from {Endpoint}", jwksEndpoint);
            throw new SecurityTokenException("Unable to retrieve signing keys.", ex);
        }
    }

    private AuthenticatedUser BuildAuthenticatedUser(JwtSecurityToken jwtToken, string rawToken)
    {
        var claims = jwtToken.Claims.ToList();
        var mapping = _options.ClaimsMapping;

        var userId = GetClaimValue(claims, mapping.UserIdClaim)
            ?? GetClaimValue(claims, "sub")
            ?? throw new SecurityTokenException("User ID claim not found in token.");

        var userName = GetClaimValue(claims, mapping.UserNameClaim)
            ?? GetClaimValue(claims, "preferred_username")
            ?? GetClaimValue(claims, "name")
            ?? userId;

        var email = GetClaimValue(claims, mapping.EmailClaim);
        var displayName = GetClaimValue(claims, mapping.DisplayNameClaim)
            ?? GetClaimValue(claims, "name");

        var roles = GetRoles(claims, mapping.RolesClaim);

        // Build additional claims dictionary (excluding standard ones)
        var standardClaims = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sub", "iss", "aud", "exp", "nbf", "iat", "jti",
            mapping.UserIdClaim, mapping.UserNameClaim, mapping.EmailClaim,
            mapping.DisplayNameClaim, mapping.RolesClaim
        };

        var additionalClaims = claims
            .Where(c => !standardClaims.Contains(c.Type))
            .GroupBy(c => c.Type)
            .ToDictionary(g => g.Key, g => g.First().Value);

        // Store the raw token for potential downstream use
        additionalClaims["jwt_token"] = rawToken;

        return new AuthenticatedUser
        {
            UserId = userId,
            UserName = userName,
            Email = email,
            DisplayName = displayName,
            Roles = roles,
            Claims = additionalClaims
        };
    }

    private static string? GetClaimValue(IEnumerable<Claim> claims, string claimType)
    {
        return claims.FirstOrDefault(c =>
            c.Type.Equals(claimType, StringComparison.OrdinalIgnoreCase))?.Value;
    }

    private static IEnumerable<string> GetRoles(IEnumerable<Claim> claims, string rolesClaim)
    {
        var roles = new List<string>();

        // Check for standard role claims
        roles.AddRange(claims
            .Where(c => c.Type.Equals(ClaimTypes.Role, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Value));

        // Check for custom roles claim
        var customRoles = claims
            .Where(c => c.Type.Equals(rolesClaim, StringComparison.OrdinalIgnoreCase))
            .SelectMany(c =>
            {
                // Handle array format ["role1", "role2"] or comma-separated "role1,role2"
                var value = c.Value.Trim();
                if (value.StartsWith('[') && value.EndsWith(']'))
                {
                    try
                    {
                        return JsonSerializer.Deserialize<string[]>(value) ?? [];
                    }
                    catch
                    {
                        return [value];
                    }
                }
                return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            });

        roles.AddRange(customRoles);

        return roles.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string? ExtractTokenFromResponse(string jsonContent)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonContent);
            var root = doc.RootElement;

            // Try common token property names
            foreach (var propName in new[] { "token", "access_token", "accessToken", "jwt", "id_token" })
            {
                if (root.TryGetProperty(propName, out var prop) && prop.ValueKind == JsonValueKind.String)
                {
                    return prop.GetString();
                }
            }

            // Check for nested data object
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                foreach (var propName in new[] { "token", "access_token", "accessToken", "jwt" })
                {
                    if (data.TryGetProperty(propName, out var prop) && prop.ValueKind == JsonValueKind.String)
                    {
                        return prop.GetString();
                    }
                }
            }
        }
        catch
        {
            // If it's not JSON, the response might be the token itself
            var trimmed = jsonContent.Trim().Trim('"');
            if (trimmed.Split('.').Length == 3) // Basic JWT format check
            {
                return trimmed;
            }
        }

        return null;
    }
}
