using System.Security.Cryptography;
using System.Text;
using Beacon.Core.Services.Embed;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Embed;

/// <summary>
/// Embed tokens: HS256 for audience <c>beacon-embed-v2</c> with the configured key and its key id. A token round-trips its
/// grant, token id and locked parameters; an expired, not-yet-valid, overlong, unsigned, otherwise-signed, tampered or
/// otherwise-addressed token is refused, as is a token of the obsolete embed token service.
/// </summary>
[TestFixture]
public class EmbedTokenCodecTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

    private FakeTimeProvider _clock = null!;
    private string _key = null!;
    private EmbedTokenCodec _codec = null!;

    [SetUp]
    public void SetUp()
    {
        _clock = new FakeTimeProvider(Now);
        _key = NewKey();
        _codec = Codec(_key);
    }

    [Test]
    public async Task AMintedToken_RoundTripsItsGrantAndParameters()
    {
        var issued = await _codec.MintAsync("0123456789abcdef0123456789abcdef", new Dictionary<string, string> { ["branch"] = "12" }, TimeSpan.FromMinutes(5));

        var check = await _codec.ValidateAsync(issued.Token);

        check.IsValid.Should().BeTrue();
        check.Payload!.GrantPublicId.Should().Be("0123456789abcdef0123456789abcdef");
        check.Payload.TokenId.Should().Be(issued.TokenId).And.HaveLength(32);
        check.Payload.Parameters.Should().Equal(new Dictionary<string, string> { ["branch"] = "12" });
        check.Payload.ExpiresAt.Should().Be(Now.AddMinutes(5));
        issued.ExpiresAt.Should().Be(Now.AddMinutes(5));
        var header = new JsonWebToken(issued.Token);
        header.Alg.Should().Be(SecurityAlgorithms.HmacSha256);
        header.Kid.Should().Be(EmbedTokenCodec.KeyIdOf(Convert.FromBase64String(_key))).And.HaveLength(8);
        header.Audiences.Should().Equal("beacon-embed-v2");
    }

    [Test]
    public async Task AnExpiredToken_IsRefused()
    {
        var issued = await _codec.MintAsync("grant", new Dictionary<string, string>(), TimeSpan.FromMinutes(5));
        _clock.Advance(TimeSpan.FromMinutes(5));

        (await _codec.ValidateAsync(issued.Token)).Refusal.Should().Be(EmbedTokenRefusal.Expired);
    }

    [Test]
    public async Task ATokenNotValidYet_IsRefused()
    {
        var later = new EmbedTokenCodec(Options.Create(new EmbedOptions { Enabled = true, SigningKey = _key }), new FakeTimeProvider(Now.AddSeconds(1)));
        var issued = await later.MintAsync("grant", new Dictionary<string, string>(), TimeSpan.FromMinutes(5));

        (await _codec.ValidateAsync(issued.Token)).Refusal.Should().Be(EmbedTokenRefusal.Expired);
    }

    [Test]
    public async Task ATokenSignedWithAnotherKey_IsRefused()
    {
        var issued = await Codec(NewKey()).MintAsync("grant", new Dictionary<string, string>(), TimeSpan.FromMinutes(5));

        (await _codec.ValidateAsync(issued.Token)).Refusal.Should().Be(EmbedTokenRefusal.InvalidSignature);
    }

    [Test]
    public async Task ATokenWithAnotherKeyId_IsRefused()
    {
        var token = Sign(Descriptor(), new SymmetricSecurityKey(Convert.FromBase64String(_key)) { KeyId = "ffffffff" }, SecurityAlgorithms.HmacSha256);

        (await _codec.ValidateAsync(token)).Refusal.Should().Be(EmbedTokenRefusal.InvalidSignature);
    }

    [Test]
    public async Task ATokenForAnotherAudience_IsRefused()
    {
        var descriptor = Descriptor();
        descriptor.Audience = "beacon-embed";

        (await _codec.ValidateAsync(Sign(descriptor, Key(), SecurityAlgorithms.HmacSha256))).Refusal.Should().Be(EmbedTokenRefusal.WrongAudience);
    }

    [Test]
    public async Task ATokenOfTheObsoleteEmbedTokenService_IsRefused()
    {
#pragma warning disable CS0618 // the obsolete service mints the token a host may still hold
        var options = new EmbedTokenOptions { SigningKey = _key };
        var obsolete = new EmbedTokenService(Options.Create(options), _clock);
        var token = obsolete.Mint(new EmbedTokenMintRequest(1, new EmbedResource(EmbedResourceType.Query, "5"), TimeSpan.FromMinutes(5), new Dictionary<string, string>(), ["https://a.example"])).Token;
#pragma warning restore CS0618

        (await _codec.ValidateAsync(token)).IsValid.Should().BeFalse();
    }

    [Test]
    public async Task AnUnsignedToken_IsRefused()
    {
        var token = new JsonWebTokenHandler().CreateToken(Descriptor());

        (await _codec.ValidateAsync(token)).IsValid.Should().BeFalse();
        new JsonWebToken(token).Alg.Should().Be("none");
    }

    [Test]
    public async Task ATokenSignedWithAnAsymmetricAlgorithm_IsRefused()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = EmbedTokenCodec.KeyIdOf(Convert.FromBase64String(_key)) };

        (await _codec.ValidateAsync(Sign(Descriptor(), key, SecurityAlgorithms.RsaSha256))).IsValid.Should().BeFalse();
    }

    [Test]
    public async Task ATokenSignedWithAnotherHmacAlgorithm_IsRefused()
    {
        var key = new SymmetricSecurityKey(Convert.FromBase64String(_key).Concat(Convert.FromBase64String(_key)).ToArray())
        {
            KeyId = EmbedTokenCodec.KeyIdOf(Convert.FromBase64String(_key))
        };

        (await _codec.ValidateAsync(Sign(Descriptor(), key, SecurityAlgorithms.HmacSha512))).IsValid.Should().BeFalse();
    }

    [Test]
    public async Task ATamperedParameterClaim_IsRefused()
    {
        var issued = await _codec.MintAsync("grant", new Dictionary<string, string> { ["branch"] = "12" }, TimeSpan.FromMinutes(5));
        var parts = issued.Token.Split('.');
        var payload = Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(parts[1])).Replace("\"12\"", "\"13\"", StringComparison.Ordinal);
        var tampered = $"{parts[0]}.{Base64UrlEncoder.Encode(payload)}.{parts[2]}";

        (await _codec.ValidateAsync(tampered)).Refusal.Should().Be(EmbedTokenRefusal.InvalidSignature);
    }

    [Test]
    public async Task ATokenLivingLongerThanAnyBeaconIssues_IsRefused()
    {
        var descriptor = Descriptor();
        descriptor.Expires = Now.UtcDateTime.AddSeconds(EmbedOptions.MaxAllowedTokenTtlSeconds + 1);

        (await _codec.ValidateAsync(Sign(descriptor, Key(), SecurityAlgorithms.HmacSha256))).Refusal.Should().Be(EmbedTokenRefusal.Expired);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not-a-token")]
    public async Task NoToken_OrGarbage_IsRefused(string? token)
    {
        (await _codec.ValidateAsync(token)).Refusal.Should().Be(EmbedTokenRefusal.Malformed);
    }

    [Test]
    public async Task ATokenWithoutTheReadScope_IsRefused()
    {
        var descriptor = Descriptor();
        descriptor.Claims["scp"] = "write";

        (await _codec.ValidateAsync(Sign(descriptor, Key(), SecurityAlgorithms.HmacSha256))).Refusal.Should().Be(EmbedTokenRefusal.Malformed);
    }

    [TestCase(0)]
    [TestCase(901)]
    public async Task MintingOutsideTheTokenLifetime_IsRefused(int seconds)
    {
        var act = () => _codec.MintAsync("grant", new Dictionary<string, string>(), TimeSpan.FromSeconds(seconds));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*between 1 and 900 seconds*");
    }

    [Test]
    public async Task WhileEmbeddingIsOff_NothingIsMintedOrAccepted()
    {
        var issued = await _codec.MintAsync("grant", new Dictionary<string, string>(), TimeSpan.FromMinutes(5));
        var off = new EmbedTokenCodec(Options.Create(new EmbedOptions { Enabled = false, SigningKey = _key }), _clock);

        (await off.ValidateAsync(issued.Token)).IsValid.Should().BeFalse();
        var act = () => off.MintAsync("grant", new Dictionary<string, string>(), TimeSpan.FromMinutes(5));
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private EmbedTokenCodec Codec(string key) =>
        new(Options.Create(new EmbedOptions { Enabled = true, SigningKey = key }), _clock);

    private SymmetricSecurityKey Key()
    {
        var bytes = Convert.FromBase64String(_key);

        return new SymmetricSecurityKey(bytes) { KeyId = EmbedTokenCodec.KeyIdOf(bytes) };
    }

    private static SecurityTokenDescriptor Descriptor() =>
        new()
        {
            Issuer = "beacon",
            Audience = "beacon-embed-v2",
            IssuedAt = Now.UtcDateTime,
            NotBefore = Now.UtcDateTime,
            Expires = Now.UtcDateTime.AddMinutes(5),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = "grant",
                ["jti"] = "token",
                ["prm"] = new Dictionary<string, string>(),
                ["scp"] = "read"
            }
        };

    private static string Sign(SecurityTokenDescriptor descriptor, SecurityKey key, string algorithm)
    {
        descriptor.SigningCredentials = new SigningCredentials(key, algorithm);

        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(descriptor);
    }

    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
