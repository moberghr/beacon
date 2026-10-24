using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Beacon.Core.Services.Embed;

/// <summary>Why an embed token was refused, as the audit trail records it (never returned to the caller).</summary>
internal enum EmbedTokenRefusal
{
    /// <summary>Not a token this codec signed in this form: unreadable, unsigned, another algorithm, missing claims.</summary>
    Malformed,

    Expired,

    /// <summary>The signature does not verify with the configured key (another key, a tampered payload, another key id).</summary>
    InvalidSignature,

    /// <summary>Signed for another audience or issuer, such as a token of the obsolete embed token service.</summary>
    WrongAudience
}

/// <summary>What a valid embed token says: the grant it was issued for and the locked parameter values it carries.</summary>
internal sealed record EmbedTokenPayload(
    string GrantPublicId,
    string TokenId,
    IReadOnlyDictionary<string, string> Parameters,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);

/// <summary>A token as <see cref="EmbedTokenCodec.MintAsync"/> made it.</summary>
internal sealed record EmbedTokenIssued(string Token, string TokenId, DateTimeOffset ExpiresAt)
{
    // The compiler-generated ToString prints every member; the secrets are printed redacted.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"Token = {RedactedPrint.Of(Token)}, TokenId = {TokenId}, ExpiresAt = {ExpiresAt}");

        return true;
    }
}

/// <summary>The outcome of <see cref="EmbedTokenCodec.ValidateAsync"/>: a payload, or the reason it was refused.</summary>
internal sealed record EmbedTokenCheck(EmbedTokenPayload? Payload, EmbedTokenRefusal? Refusal)
{
    public bool IsValid => Payload != null;

    public static EmbedTokenCheck Refused(EmbedTokenRefusal refusal) => new(null, refusal);
}

/// <summary>
/// Signs and checks embed tokens: HS256 JWTs for audience <see cref="Audience"/> signed with <c>Beacon:Embed:SigningKey</c>
/// (whose header <c>kid</c> is the first 8 hex characters of the key's SHA-256). A token names its grant by public id
/// (<c>sub</c>), carries a token id (<c>jti</c>), the locked parameter values (<c>prm</c>, readable by whoever holds the
/// token, so never secrets) and the scope <c>read</c>, and lives at most <see cref="EmbedOptions.MaxAllowedTokenTtlSeconds"/>.
/// Only HS256 with this key verifies: an unsigned token, another algorithm, another key id, audience or issuer is
/// refused. A valid token grants nothing by itself: the grant row is read again on every use.
/// </summary>
internal sealed class EmbedTokenCodec(IOptions<EmbedOptions> options, TimeProvider clock)
{
    public const string Audience = "beacon-embed-v2";
    public const string Issuer = "beacon";
    public const string ReadScope = "read";

    private const string ParametersClaim = "prm";
    private const string ScopeClaim = "scp";
    private const string FrameMarkerContext = "beacon-embed-frame:";

    public Task<EmbedTokenIssued> MintAsync(string grantPublicId, IReadOnlyDictionary<string, string> parameters, TimeSpan ttl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(grantPublicId);
        ArgumentNullException.ThrowIfNull(parameters);

        if (ttl <= TimeSpan.Zero || ttl > TimeSpan.FromSeconds(options.Value.MaxTokenTtlSeconds))
        {
            throw new InvalidOperationException($"An embed token lives between 1 and {options.Value.MaxTokenTtlSeconds} seconds.");
        }

        var now = clock.GetUtcNow();
        var expires = now.Add(ttl);
        var tokenId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(SigningKey(), SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = grantPublicId,
                [JwtRegisteredClaimNames.Jti] = tokenId,
                [ParametersClaim] = parameters.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
                [ScopeClaim] = ReadScope
            }
        };

        var token = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(descriptor);

        return Task.FromResult(new EmbedTokenIssued(token, tokenId, expires));
    }

    public async Task<EmbedTokenCheck> ValidateAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || !options.Value.Enabled)
        {
            return EmbedTokenCheck.Refused(EmbedTokenRefusal.Malformed);
        }

        var parameters = new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidateIssuer = true,
            ValidAudience = Audience,
            ValidateAudience = true,
            IssuerSigningKey = SigningKey(),
            ValidateIssuerSigningKey = true,
            TryAllIssuerSigningKeys = false,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
            LifetimeValidator = (notBefore, expires, _, _) => IsLive(notBefore, expires)
        };

        TokenValidationResult result;
        try
        {
            result = await new JsonWebTokenHandler().ValidateTokenAsync(token, parameters);
        }
        catch (Exception ex) when (ex is ArgumentException or SecurityTokenException)
        {
            return EmbedTokenCheck.Refused(EmbedTokenRefusal.Malformed);
        }

        if (!result.IsValid)
        {
            return EmbedTokenCheck.Refused(result.Exception switch
            {
                SecurityTokenExpiredException or SecurityTokenNotYetValidException or SecurityTokenInvalidLifetimeException => EmbedTokenRefusal.Expired,
                SecurityTokenInvalidSignatureException or SecurityTokenSignatureKeyNotFoundException => EmbedTokenRefusal.InvalidSignature,
                SecurityTokenInvalidAudienceException or SecurityTokenInvalidIssuerException => EmbedTokenRefusal.WrongAudience,
                _ => EmbedTokenRefusal.Malformed
            });
        }

        return result.SecurityToken is JsonWebToken jwt ? PayloadOf(jwt) : EmbedTokenCheck.Refused(EmbedTokenRefusal.Malformed);
    }

    /// <summary>
    /// The marker Beacon's embed page for <paramref name="grantPublicId"/> carries and sends back with every render: an HMAC
    /// of the public id under the signing key. Only the page Beacon serves for the grant (with its frame headers) carries it,
    /// so a copy of the page served any other way (a static file, without <c>frame-ancestors</c>) cannot render.
    /// </summary>
    public string FrameMarkerOf(string grantPublicId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(grantPublicId);

        var mac = HMACSHA256.HashData(SigningKey().Key, System.Text.Encoding.UTF8.GetBytes(FrameMarkerContext + grantPublicId));

        return Convert.ToHexStringLower(mac)[..32];
    }

    /// <summary>Whether <paramref name="marker"/> is <paramref name="grantPublicId"/>'s frame marker (compared in fixed time).</summary>
    public bool IsFrameMarkerOf(string grantPublicId, string? marker)
    {
        if (string.IsNullOrEmpty(marker))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(FrameMarkerOf(grantPublicId)),
            System.Text.Encoding.ASCII.GetBytes(marker));
    }

    /// <summary>The first 8 hex characters of the SHA-256 of <paramref name="key"/>'s bytes: the <c>kid</c> its tokens carry.</summary>
    internal static string KeyIdOf(byte[] key)
    {
        return Convert.ToHexStringLower(SHA256.HashData(key))[..8];
    }

    private SymmetricSecurityKey SigningKey()
    {
        var configured = options.Value.SigningKey;
        if (!options.Value.Enabled || EncryptionKeyRequirements.Problem(configured, EncryptionKeyRequirements.EmbedSigningKey) != null)
        {
            throw new InvalidOperationException("Embedding is not enabled, or Beacon:Embed:SigningKey is not a valid key.");
        }

        var bytes = EncryptionKeyRequirements.Decode(configured!);

        return new SymmetricSecurityKey(bytes) { KeyId = KeyIdOf(bytes) };
    }

    // Started, not yet ended, and never longer than the longest token Beacon issues: a token claiming a longer life was not
    // minted here.
    private bool IsLive(DateTime? notBefore, DateTime? expires)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (expires == null || now >= expires.Value)
        {
            throw new SecurityTokenExpiredException("The embed token has expired.");
        }

        if (notBefore == null || now < notBefore.Value)
        {
            throw new SecurityTokenNotYetValidException("The embed token is not valid yet.");
        }

        if (expires.Value - notBefore.Value > TimeSpan.FromSeconds(EmbedOptions.MaxAllowedTokenTtlSeconds))
        {
            throw new SecurityTokenInvalidLifetimeException("The embed token lives longer than any token Beacon issues.");
        }

        return true;
    }

    private static EmbedTokenCheck PayloadOf(JsonWebToken jwt)
    {
        if (!jwt.TryGetPayloadValue<string>(ScopeClaim, out var scope)
            || !string.Equals(scope, ReadScope, StringComparison.Ordinal)
            || !jwt.TryGetPayloadValue<string>(JwtRegisteredClaimNames.Sub, out var subject)
            || string.IsNullOrWhiteSpace(subject)
            || !jwt.TryGetPayloadValue<string>(JwtRegisteredClaimNames.Jti, out var tokenId)
            || string.IsNullOrWhiteSpace(tokenId)
            || !jwt.TryGetPayloadValue<JsonElement>(ParametersClaim, out var parametersElement)
            || parametersElement.ValueKind != JsonValueKind.Object)
        {
            return EmbedTokenCheck.Refused(EmbedTokenRefusal.Malformed);
        }

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in parametersElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                return EmbedTokenCheck.Refused(EmbedTokenRefusal.Malformed);
            }

            parameters[property.Name] = property.Value.GetString()!;
        }

        return new EmbedTokenCheck(
            new EmbedTokenPayload(
                subject,
                tokenId,
                parameters,
                new DateTimeOffset(DateTime.SpecifyKind(jwt.IssuedAt, DateTimeKind.Utc)),
                new DateTimeOffset(DateTime.SpecifyKind(jwt.ValidTo, DateTimeKind.Utc))),
            null);
    }
}
