using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;
using Beacon.Core.Authentication;
using Beacon.Core.Authentication.Providers;

namespace Beacon.Tests.Unit;

/// <summary>
/// Bearer validation against a JWKS endpoint: keys are cached (not fetched per request), refreshed on schedule and
/// early — but rate-limited — for an unknown key id, and issuer/audience lists are enforced.
/// </summary>
[TestFixture]
public class JwtBearerJwksCacheTests
{
    private const string JwksEndpoint = "https://login.example.test/tenant/discovery/v2.0/keys";
    private const string Issuer = "https://login.example.test/tenant/v2.0";
    private const string Audience = "api://beacon";

    [Test]
    public async Task RepeatedValidations_FetchTheKeySetOnce()
    {
        using var key = new RsaKey("k1");
        var handler = new JwksHandler(key);
        var provider = CreateProvider(handler, new FakeTimeProvider());

        (await provider.ValidateTokenAsync(key.Mint(Issuer, Audience))).Success.Should().BeTrue();
        (await provider.ValidateTokenAsync(key.Mint(Issuer, Audience))).Success.Should().BeTrue();
        (await provider.ValidateTokenAsync(key.Mint(Issuer, Audience))).Success.Should().BeTrue();

        handler.Requests.Should().Be(1, "the JWKS is cached between validations");
    }

    [Test]
    public async Task CachedKeys_AreRefreshedAfterTheRefreshInterval()
    {
        using var key = new RsaKey("k1");
        var handler = new JwksHandler(key);
        var time = new FakeTimeProvider();
        var provider = CreateProvider(handler, time);

        await provider.ValidateTokenAsync(key.Mint(Issuer, Audience));
        time.Advance(JwksSigningKeyCache.RefreshInterval);
        await provider.ValidateTokenAsync(key.Mint(Issuer, Audience));

        handler.Requests.Should().Be(2);
    }

    [Test]
    public async Task UnknownKeyId_ForcesOneRateLimitedRefresh()
    {
        using var oldKey = new RsaKey("k1");
        using var newKey = new RsaKey("k2");
        var handler = new JwksHandler(oldKey);
        var time = new FakeTimeProvider();
        var provider = CreateProvider(handler, time);
        await provider.ValidateTokenAsync(oldKey.Mint(Issuer, Audience));

        time.Advance(JwksSigningKeyCache.MinimumForcedRefreshInterval);
        handler.Keys = [oldKey, newKey];
        var rotated = await provider.ValidateTokenAsync(newKey.Mint(Issuer, Audience));

        using var forged = new RsaKey("k3");
        var unknown = await provider.ValidateTokenAsync(forged.Mint(Issuer, Audience));
        var unknownAgain = await provider.ValidateTokenAsync(forged.Mint(Issuer, Audience));

        rotated.Success.Should().BeTrue("a rotated signing key is picked up by an early refresh");
        unknown.Success.Should().BeFalse();
        unknownAgain.Success.Should().BeFalse();
        handler.Requests.Should().Be(2, "unknown key ids cannot trigger more than one refresh per interval");
    }

    [Test]
    public async Task IssuerAndAudienceLists_AreEnforced()
    {
        using var key = new RsaKey("k1");
        var handler = new JwksHandler(key);
        var provider = CreateProvider(handler, new FakeTimeProvider(), x =>
        {
            x.ValidIssuer = null;
            x.ValidIssuers = ["https://sts.example.test/tenant/", Issuer];
            x.ValidAudience = null;
            x.ValidAudiences = ["00000000-0000-0000-0000-00000000beac", Audience];
        });

        (await provider.ValidateTokenAsync(key.Mint(Issuer, Audience))).Success.Should().BeTrue();
        (await provider.ValidateTokenAsync(key.Mint("https://login.example.test/other-tenant/v2.0", Audience))).Success.Should().BeFalse();
        (await provider.ValidateTokenAsync(key.Mint(Issuer, "https://graph.microsoft.com"))).Success.Should().BeFalse();
    }

    private static JwtExternalApiAuthenticationProvider CreateProvider(
        JwksHandler handler,
        TimeProvider time,
        Action<JwtValidationOptions>? configure = null)
    {
        var options = new JwtAuthenticationOptions
        {
            EnableBearerAuthentication = true,
            Validation = new JwtValidationOptions
            {
                JwksEndpoint = JwksEndpoint,
                ValidIssuer = Issuer,
                ValidAudience = Audience
            }
        };
        configure?.Invoke(options.Validation);

        return new JwtExternalApiAuthenticationProvider(
            new HttpClient(handler),
            options,
            NullLogger<JwtExternalApiAuthenticationProvider>.Instance,
            new JwksSigningKeyCache(time));
    }

    private sealed class RsaKey(string keyId) : IDisposable
    {
        private readonly RSA _rsa = RSA.Create(2048);

        public string KeyId { get; } = keyId;

        public string ToJwk()
        {
            var parameters = _rsa.ExportParameters(false);

            return $$"""{"kty":"RSA","use":"sig","kid":"{{KeyId}}","n":"{{Base64UrlEncoder.Encode(parameters.Modulus)}}","e":"{{Base64UrlEncoder.Encode(parameters.Exponent)}}"}""";
        }

        public string Mint(string issuer, string audience)
        {
            var credentials = new SigningCredentials(new RsaSecurityKey(_rsa) { KeyId = KeyId }, SecurityAlgorithms.RsaSha256);
            var token = new JwtSecurityToken(
                issuer,
                audience,
                [new Claim("sub", "subject-1")],
                notBefore: DateTime.UtcNow.AddMinutes(-1),
                expires: DateTime.UtcNow.AddMinutes(10),
                signingCredentials: credentials);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        public void Dispose() => _rsa.Dispose();
    }

    private sealed class JwksHandler(params RsaKey[] keys) : HttpMessageHandler
    {
        public RsaKey[] Keys { get; set; } = keys;

        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var body = "{\"keys\":[" + string.Join(",", Keys.Select(x => x.ToJwk())) + "]}";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
