using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Beacon.Core.Services;

namespace Beacon.Core.Authentication.Providers;

/// <summary>
/// Authentication provider that authenticates against an external JWT-issuing API.
/// Sends credentials to the external endpoint and validates the returned JWT token. The token passes the same screen as
/// a REST bearer token (an access token, admitted by the SSO rules when the SSO authority issued it), and a login only
/// succeeds for an existing, enabled Beacon user the token names (<see cref="BearerUserBinding.ScreenAndBindAsync"/>);
/// the session carries that user's Beacon roles, never the token's. Without user management
/// (<see cref="IUserManagementService"/>) every login fails. Every failure answers with the same message; the reason is
/// logged without the user name.
/// </summary>
public class JwtExternalApiAuthenticationProvider : IBeaconAuthenticationProvider
{
    private const string LoginFailed = "Invalid username or password.";

    private readonly HttpClient _httpClient;
    private readonly JwtAuthenticationOptions _options;
    private readonly ILogger<JwtExternalApiAuthenticationProvider> _logger;
    private readonly JwksSigningKeyCache _keyCache;
    private readonly IUserManagementService? _userService;
    private readonly IOptions<OidcAuthenticationOptions>? _oidcOptions;
    private readonly JwtSecurityTokenHandler _tokenHandler = new();

    public JwtExternalApiAuthenticationProvider(
        HttpClient httpClient,
        JwtAuthenticationOptions options,
        ILogger<JwtExternalApiAuthenticationProvider> logger,
        IUserManagementService? userService = null,
        IOptions<OidcAuthenticationOptions>? oidcOptions = null)
        : this(httpClient, options, logger, JwksSigningKeyCache.Shared, userService, oidcOptions)
    {
    }

    internal JwtExternalApiAuthenticationProvider(
        HttpClient httpClient,
        JwtAuthenticationOptions options,
        ILogger<JwtExternalApiAuthenticationProvider> logger,
        JwksSigningKeyCache keyCache,
        IUserManagementService? userService = null,
        IOptions<OidcAuthenticationOptions>? oidcOptions = null)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
        _keyCache = keyCache;
        _userService = userService;
        _oidcOptions = oidcOptions;
    }

    public async Task<AuthenticationResult> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        // Every failure answers the caller the same way; the reason goes to the log, without the user name (§1.11).
        if (string.IsNullOrWhiteSpace(_options.ExternalLoginEndpoint))
        {
            _logger.LogError("JWT login refused: the external login endpoint is not configured.");
            return AuthenticationResult.Failed(LoginFailed);
        }

        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                _options.ExternalLoginEndpoint,
                new { username, password },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation("JWT login refused: the external login API answered {StatusCode}.", response.StatusCode);
                return AuthenticationResult.Failed(LoginFailed);
            }

            // Expects { "token": "jwt..." } or { "access_token": "jwt..." } (see ExtractTokenFromResponse).
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var token = ExtractTokenFromResponse(content);
            if (string.IsNullOrEmpty(token))
            {
                _logger.LogWarning("JWT login refused: the external login API answered without a token.");
                return AuthenticationResult.Failed(LoginFailed);
            }

            var validationResult = await ValidateTokenAsync(token, cancellationToken);
            if (!validationResult.Success || validationResult.TokenPrincipal == null)
            {
                _logger.LogWarning("JWT login refused: the issued token was not accepted ({Reason}).", validationResult.ErrorMessage);
                return AuthenticationResult.Failed(LoginFailed);
            }

            // The session belongs to the Beacon user the token names, with Beacon's roles (never the token's).
            if (_userService == null)
            {
                _logger.LogError(
                    "JWT login refused: binding a token to a Beacon user needs user management, and a token alone never builds a session.");
                return AuthenticationResult.Failed(LoginFailed);
            }

            // The same screen and binding as a REST bearer token: an ID token, a token without access-token evidence or
            // one the SSO admission rules refuse never reaches the user lookup.
            var binding = await BearerUserBinding.ScreenAndBindAsync(
                _userService,
                validationResult.TokenPrincipal,
                validationResult.TokenType,
                _options,
                _oidcOptions?.Value,
                _logger,
                cancellationToken);
            if (binding.User == null)
            {
                _logger.LogInformation(
                    "JWT login refused ({Reason}) for subject {SubjectHash}.",
                    binding.Refusal,
                    SubjectFingerprint.Of(BearerUserBinding.ExactClaim(validationResult.TokenPrincipal, "sub")));
                return AuthenticationResult.Failed(LoginFailed);
            }

            await _userService.UpdateLastLoginAsync(binding.User.Id, cancellationToken);

            return new AuthenticationResult
            {
                Success = true,
                User = BearerUserBinding.ToAuthenticatedUser(binding.User),
                BeaconUserId = binding.User.Id
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "JWT login refused: the external login API at {Endpoint} could not be reached.", _options.ExternalLoginEndpoint);
            return AuthenticationResult.Failed(LoginFailed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JWT login refused: unexpected error.");
            return AuthenticationResult.Failed(LoginFailed);
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
            var validatedToken = await ValidateWithKeyRotationAsync(token, cancellationToken);

            if (validatedToken is not JwtSecurityToken jwtToken)
            {
                return AuthenticationResult.Failed("Invalid token format.");
            }

            var user = BuildAuthenticatedUser(jwtToken);
            return new AuthenticationResult
            {
                Success = true,
                User = user,
                TokenPrincipal = new ClaimsPrincipal(new ClaimsIdentity(jwtToken.Claims, "Bearer")),
                TokenType = jwtToken.Header.Typ
            };
        }
        // The reasons below are fixed strings, logged by the caller (rate-limited for bearer requests); nothing here is
        // logged above Debug, so an anonymous stream of bad tokens cannot flood the log.
        catch (SecurityTokenExpiredException)
        {
            _logger.LogDebug("JWT token has expired");
            return AuthenticationResult.Failed("Token has expired.");
        }
        catch (SecurityTokenInvalidSignatureException)
        {
            _logger.LogDebug("JWT token has invalid signature");
            return AuthenticationResult.Failed("Invalid token signature.");
        }
        catch (SecurityTokenInvalidIssuerException)
        {
            _logger.LogDebug("JWT token has invalid issuer");
            return AuthenticationResult.Failed("Invalid token issuer.");
        }
        catch (SecurityTokenInvalidAudienceException)
        {
            _logger.LogDebug("JWT token has invalid audience");
            return AuthenticationResult.Failed("Invalid token audience.");
        }
        catch (SecurityTokenException ex)
        {
            _logger.LogDebug(ex, "JWT token validation failed");
            return AuthenticationResult.Failed("Invalid token.");
        }
        catch (ArgumentException)
        {
            // A string that is not a JWT at all (the handler cannot even read it).
            _logger.LogDebug("JWT token is malformed");
            return AuthenticationResult.Failed("Invalid token.");
        }
    }

    // A token signed with a key the cached JWKS does not hold may follow a key rotation: refresh once (rate-limited by
    // the cache) and retry before rejecting it.
    private async Task<SecurityToken> ValidateWithKeyRotationAsync(string token, CancellationToken cancellationToken)
    {
        var validationParameters = await BuildValidationParametersAsync(forceKeyRefresh: false, cancellationToken);
        try
        {
            _tokenHandler.ValidateToken(token, validationParameters, out var validatedToken);

            return validatedToken;
        }
        catch (SecurityTokenSignatureKeyNotFoundException) when (UsesJwks)
        {
            var refreshed = await BuildValidationParametersAsync(forceKeyRefresh: true, cancellationToken);
            _tokenHandler.ValidateToken(token, refreshed, out var validatedToken);

            return validatedToken;
        }
    }

    private bool UsesJwks =>
        string.IsNullOrEmpty(_options.Validation.SigningKey) && !string.IsNullOrEmpty(_options.Validation.JwksEndpoint);

    private async Task<TokenValidationParameters> BuildValidationParametersAsync(
        bool forceKeyRefresh,
        CancellationToken cancellationToken)
    {
        // Issuer and audience are always validated: JwtAuthenticationOptions.Validate() refuses to start without them,
        // and an empty list here fails every token closed rather than accepting any issuer or audience.
        var issuers = _options.Validation.EffectiveIssuers();
        var audiences = _options.Validation.EffectiveAudiences();
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = issuers,
            ValidateAudience = true,
            ValidAudiences = audiences,
            // Always on, whatever the options say: JwtAuthenticationOptions.Validate() refuses to start otherwise.
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = _options.Validation.ClockSkew > JwtValidationOptions.MaxClockSkew
                ? JwtValidationOptions.MaxClockSkew
                : _options.Validation.ClockSkew
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
            // Keys from the JWKS endpoint, cached (JwksSigningKeyCache) rather than fetched per request.
            var keys = await FetchJwksAsync(_options.Validation.JwksEndpoint, forceKeyRefresh, cancellationToken);
            parameters.IssuerSigningKeys = keys;
            parameters.ValidateIssuerSigningKey = true;
        }
        else
        {
            // Fail closed: without a signing key or JWKS endpoint we cannot verify the token
            // signature, so we refuse to validate rather than installing a pass-through validator.
            _logger.LogError("JWT validation requires a SigningKey or JwksEndpoint, but neither is configured.");
            throw new SecurityTokenException("No signing key configured.");
        }

        return parameters;
    }

    private async Task<IEnumerable<SecurityKey>> FetchJwksAsync(
        string jwksEndpoint,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _keyCache.GetKeysAsync(
                jwksEndpoint,
                x => _httpClient.GetStringAsync(jwksEndpoint, x),
                forceRefresh,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch JWKS from {Endpoint}", jwksEndpoint);
            throw new SecurityTokenException("Unable to retrieve signing keys.", ex);
        }
    }

    private AuthenticatedUser BuildAuthenticatedUser(JwtSecurityToken jwtToken)
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
