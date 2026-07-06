using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Beacon.Core.Services.Embed;

internal sealed class EmbedTokenService(
    IOptions<EmbedTokenOptions> options,
    TimeProvider clock) : IEmbedTokenService
{
    private const string Audience = "beacon-embed";
    private const string ReadScope = "read";

    public EmbedTokenResult Mint(EmbedTokenMintRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.RowFilters);
        ArgumentNullException.ThrowIfNull(request.AllowedOrigins);
        ArgumentNullException.ThrowIfNull(request.Resource);

        if (request.Ttl <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Embed token TTL must be greater than zero.");
        }

        var maxTtl = TimeSpan.FromSeconds(options.Value.MaxTtlSeconds);
        if (request.Ttl > maxTtl)
        {
            throw new InvalidOperationException($"Embed token TTL must not exceed {options.Value.MaxTtlSeconds} seconds.");
        }

        if (string.IsNullOrWhiteSpace(request.Resource.Id))
        {
            throw new InvalidOperationException("Embed resource id must not be empty.");
        }

        var now = clock.GetUtcNow();
        var expires = now.Add(request.Ttl);
        var jti = Guid.NewGuid().ToString("N");

        var resourceClaim = new Dictionary<string, string>
        {
            ["t"] = ResourceTypeToString(request.Resource.Type),
            ["id"] = request.Resource.Id
        };

        var claims = new Dictionary<string, object>
        {
            ["sub"] = request.ApiKeyId.ToString(),
            ["jti"] = jti,
            ["res"] = resourceClaim,
            ["rf"] = request.RowFilters.ToDictionary(x => x.Key, x => x.Value),
            ["org"] = request.AllowedOrigins.ToArray(),
            ["scp"] = ReadScope
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Value.SigningKey));
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = options.Value.Issuer,
            Audience = Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
            Claims = claims
        };

        var handler = new JsonWebTokenHandler();
        var token = handler.CreateToken(descriptor);

        return new EmbedTokenResult(token, expires, jti);
    }

    public EmbedTokenValidation Validate(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return new EmbedTokenValidation(EmbedTokenValidationResult.Malformed, null);
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Value.SigningKey));
        var parameters = new TokenValidationParameters
        {
            ValidIssuer = options.Value.Issuer,
            ValidateIssuer = true,
            ValidAudience = Audience,
            ValidateAudience = true,
            IssuerSigningKey = key,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
            LifetimeValidator = (notBefore, expires, _, _) =>
            {
                var now = clock.GetUtcNow().UtcDateTime;
                if (expires.HasValue && now >= expires.Value)
                {
                    throw new SecurityTokenExpiredException("Embed token has expired.");
                }
                if (notBefore.HasValue && now < notBefore.Value)
                {
                    throw new SecurityTokenNotYetValidException("Embed token is not yet valid.");
                }
                return true;
            }
        };

        var handler = new JsonWebTokenHandler();
        TokenValidationResult result;
        try
        {
            result = handler.ValidateTokenAsync(token, parameters).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return new EmbedTokenValidation(EmbedTokenValidationResult.Malformed, null);
        }

        if (!result.IsValid)
        {
            var mapped = result.Exception switch
            {
                SecurityTokenExpiredException => EmbedTokenValidationResult.Expired,
                SecurityTokenInvalidSignatureException => EmbedTokenValidationResult.InvalidSignature,
                SecurityTokenInvalidAudienceException => EmbedTokenValidationResult.WrongAudience,
                _ => EmbedTokenValidationResult.Malformed
            };
            return new EmbedTokenValidation(mapped, null);
        }

        var jwt = (JsonWebToken)result.SecurityToken;
        return BuildClaims(jwt);
    }

    private static EmbedTokenValidation BuildClaims(JsonWebToken jwt)
    {
        if (!jwt.TryGetPayloadValue<string>("scp", out var scope) ||
            !jwt.TryGetPayloadValue<string>("sub", out var subRaw) ||
            !jwt.TryGetPayloadValue<string>("jti", out var jti))
        {
            return new EmbedTokenValidation(EmbedTokenValidationResult.MissingClaims, null);
        }

        if (!jwt.TryGetPayloadValue<JsonElement>("res", out var resElement) ||
            resElement.ValueKind != JsonValueKind.Object ||
            !resElement.TryGetProperty("t", out var resTypeProp) ||
            !resElement.TryGetProperty("id", out var resIdProp))
        {
            return new EmbedTokenValidation(EmbedTokenValidationResult.MissingClaims, null);
        }

        if (!jwt.TryGetPayloadValue<JsonElement>("org", out var orgElement) ||
            orgElement.ValueKind != JsonValueKind.Array)
        {
            return new EmbedTokenValidation(EmbedTokenValidationResult.MissingClaims, null);
        }

        if (orgElement.GetArrayLength() == 0)
        {
            return new EmbedTokenValidation(EmbedTokenValidationResult.NoAllowedOrigins, null);
        }

        if (!int.TryParse(subRaw, out var apiKeyId))
        {
            return new EmbedTokenValidation(EmbedTokenValidationResult.MissingClaims, null);
        }

        if (!TryParseResourceType(resTypeProp.GetString(), out var resourceType))
        {
            return new EmbedTokenValidation(EmbedTokenValidationResult.MissingClaims, null);
        }

        var resourceId = resIdProp.GetString();
        if (string.IsNullOrEmpty(resourceId))
        {
            return new EmbedTokenValidation(EmbedTokenValidationResult.MissingClaims, null);
        }

        var origins = new List<string>(orgElement.GetArrayLength());
        foreach (var item in orgElement.EnumerateArray())
        {
            var s = item.GetString();
            if (s != null)
            {
                origins.Add(s);
            }
        }

        var rowFilters = new Dictionary<string, string>();
        if (jwt.TryGetPayloadValue<JsonElement>("rf", out var rfElement) &&
            rfElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in rfElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    rowFilters[prop.Name] = prop.Value.GetString() ?? string.Empty;
                }
            }
        }

        var claims = new EmbedTokenClaims(
            ApiKeyId: apiKeyId,
            Resource: new EmbedResource(resourceType, resourceId),
            ExpiresAt: jwt.ValidTo == default ? DateTimeOffset.MinValue : new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero),
            IssuedAt: jwt.IssuedAt == default ? DateTimeOffset.MinValue : new DateTimeOffset(jwt.IssuedAt, TimeSpan.Zero),
            Jti: jti,
            RowFilters: rowFilters,
            AllowedOrigins: origins,
            Scope: scope);

        return new EmbedTokenValidation(EmbedTokenValidationResult.Valid, claims);
    }

    private static string ResourceTypeToString(EmbedResourceType type)
    {
        return type switch
        {
            EmbedResourceType.Query => "query",
            EmbedResourceType.Dashboard => "dashboard",
            _ => throw new InvalidOperationException($"Unknown EmbedResourceType: {type}")
        };
    }

    private static bool TryParseResourceType(string? value, out EmbedResourceType type)
    {
        switch (value)
        {
            case "query":
                type = EmbedResourceType.Query;
                return true;
            case "dashboard":
                type = EmbedResourceType.Dashboard;
                return true;
            default:
                type = default;
                return false;
        }
    }
}
