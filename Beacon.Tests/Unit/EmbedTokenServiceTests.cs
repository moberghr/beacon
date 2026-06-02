using System.Text;
using Beacon.Core.Services.Embed;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

[TestFixture]
public class EmbedTokenServiceTests
{
    private const string SigningKey = "this-is-a-32-byte-test-key-xxxxx";
    private const string OtherSigningKey = "this-is-a-different-32-byte-key!";

    private FakeTimeProvider _clock = null!;
    private EmbedTokenService _service = null!;
    private EmbedTokenOptions _options = null!;

    [SetUp]
    public void SetUp()
    {
        _clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 12, 12, 0, 0, TimeSpan.Zero));
        _options = new EmbedTokenOptions
        {
            SigningKey = SigningKey,
            Issuer = "beacon",
            DefaultTtlSeconds = 300,
            MaxTtlSeconds = 900
        };
        _service = new EmbedTokenService(Options.Create(_options), _clock);
    }

    private static EmbedTokenMintRequest ValidRequest(
        TimeSpan? ttl = null,
        EmbedResource? resource = null,
        IReadOnlyDictionary<string, string>? rowFilters = null,
        IReadOnlyList<string>? origins = null,
        int apiKeyId = 42)
    {
        return new EmbedTokenMintRequest(
            ApiKeyId: apiKeyId,
            Resource: resource ?? new EmbedResource(EmbedResourceType.Query, "monthly-revenue"),
            Ttl: ttl ?? TimeSpan.FromMinutes(5),
            RowFilters: rowFilters ?? new Dictionary<string, string> { ["tenant_id"] = "acme-123" },
            AllowedOrigins: origins ?? new[] { "https://app.acme.com" });
    }

    [Test]
    public void Mint_Returns_Token_And_ExpiresAt_And_Jti()
    {
        var result = _service.Mint(ValidRequest(TimeSpan.FromMinutes(5)));

        result.Token.Should().NotBeNullOrWhiteSpace();
        result.Token.Split('.').Length.Should().Be(3, "JWT has three base64url segments");
        result.ExpiresAt.Should().Be(_clock.GetUtcNow().AddMinutes(5));
        result.Jti.Should().HaveLength(32, "GUID with N format is 32 hex chars");
    }

    [Test]
    public void Mint_Sets_Iat_And_Exp_From_TimeProvider()
    {
        var result = _service.Mint(ValidRequest(TimeSpan.FromMinutes(7)));
        result.ExpiresAt.Should().Be(_clock.GetUtcNow().AddMinutes(7));

        _clock.Advance(TimeSpan.FromHours(1));

        var resultLater = _service.Mint(ValidRequest(TimeSpan.FromMinutes(7)));
        resultLater.ExpiresAt.Should().Be(_clock.GetUtcNow().AddMinutes(7));
    }

    [Test]
    public void Mint_Jti_Is_Unique_Per_Call()
    {
        var a = _service.Mint(ValidRequest());
        var b = _service.Mint(ValidRequest());

        a.Jti.Should().NotBe(b.Jti);
    }

    [Test]
    public void Mint_RowFilters_Roundtrip_In_Token_Payload()
    {
        var filters = new Dictionary<string, string>
        {
            ["tenant_id"] = "acme-123",
            ["unicode-key-✓"] = "value with spaces 🎯",
            ["empty"] = string.Empty
        };

        var result = _service.Mint(ValidRequest(rowFilters: filters));

        var payload = DecodePayload(result.Token);
        payload.Should().Contain("\"tenant_id\":\"acme-123\"");
        payload.Should().Contain("\"empty\":\"\"");
        // unicode keys/values JSON-escape; assert presence of decoded values too
        payload.Should().Contain("acme-123");
    }

    [Test]
    public void Mint_Empty_RowFilters_Serializes_Empty_Object()
    {
        var result = _service.Mint(ValidRequest(rowFilters: new Dictionary<string, string>()));
        var payload = DecodePayload(result.Token);
        payload.Should().Contain("\"rf\":{}");
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(901)]
    public void Mint_Invalid_Ttl_Throws(int ttlSeconds)
    {
        var act = () => _service.Mint(ValidRequest(TimeSpan.FromSeconds(ttlSeconds)));
        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void Mint_Empty_ResourceId_Throws()
    {
        var act = () => _service.Mint(ValidRequest(resource: new EmbedResource(EmbedResourceType.Query, " ")));
        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void Mint_Null_AllowedOrigins_Throws()
    {
        var request = new EmbedTokenMintRequest(
            ApiKeyId: 42,
            Resource: new EmbedResource(EmbedResourceType.Query, "q"),
            Ttl: TimeSpan.FromMinutes(5),
            RowFilters: new Dictionary<string, string>(),
            AllowedOrigins: null!);
        var act = () => _service.Mint(request);
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Mint_Null_RowFilters_Throws()
    {
        var request = new EmbedTokenMintRequest(
            ApiKeyId: 42,
            Resource: new EmbedResource(EmbedResourceType.Query, "q"),
            Ttl: TimeSpan.FromMinutes(5),
            RowFilters: null!,
            AllowedOrigins: new[] { "https://app.acme.com" });
        var act = () => _service.Mint(request);
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Mint_Header_Uses_HS256()
    {
        var result = _service.Mint(ValidRequest());
        var header = DecodeHeader(result.Token);
        header.Should().Contain("\"alg\":\"HS256\"");
    }

    [Test]
    public void Mint_Dashboard_Resource_Serializes_With_Type_Dashboard()
    {
        var result = _service.Mint(ValidRequest(resource: new EmbedResource(EmbedResourceType.Dashboard, "ops-overview")));
        var payload = DecodePayload(result.Token);
        payload.Should().Contain("\"t\":\"dashboard\"");
        payload.Should().Contain("\"id\":\"ops-overview\"");
    }

    [Test]
    public void Mint_Then_Validate_Returns_Same_Claims()
    {
        var filters = new Dictionary<string, string>
        {
            ["tenant_id"] = "acme-123",
            ["region"] = "eu-west"
        };
        var origins = new[] { "https://app.acme.com", "https://staging.acme.com" };
        var request = new EmbedTokenMintRequest(
            ApiKeyId: 42,
            Resource: new EmbedResource(EmbedResourceType.Dashboard, "ops-overview"),
            Ttl: TimeSpan.FromMinutes(5),
            RowFilters: filters,
            AllowedOrigins: origins);

        var minted = _service.Mint(request);
        var validation = _service.Validate(minted.Token);

        validation.Result.Should().Be(EmbedTokenValidationResult.Valid);
        validation.Claims.Should().NotBeNull();
        validation.Claims!.ApiKeyId.Should().Be(42);
        validation.Claims.Resource.Should().Be(new EmbedResource(EmbedResourceType.Dashboard, "ops-overview"));
        validation.Claims.Jti.Should().Be(minted.Jti);
        validation.Claims.Scope.Should().Be("read");
        validation.Claims.AllowedOrigins.Should().BeEquivalentTo(origins);
        validation.Claims.RowFilters.Should().BeEquivalentTo(filters);
        validation.Claims.ExpiresAt.Should().BeCloseTo(minted.ExpiresAt, TimeSpan.FromSeconds(1));
    }

    [Test]
    public void Validate_Expired_Token_Fails_With_Expired()
    {
        var minted = _service.Mint(ValidRequest(TimeSpan.FromSeconds(60)));
        _clock.Advance(TimeSpan.FromMinutes(2));

        var validation = _service.Validate(minted.Token);

        validation.Result.Should().Be(EmbedTokenValidationResult.Expired);
        validation.Claims.Should().BeNull();
    }

    [Test]
    public void Validate_Wrong_Signature_Fails()
    {
        var minted = _service.Mint(ValidRequest());

        var otherOptions = new EmbedTokenOptions { SigningKey = OtherSigningKey, Issuer = "beacon" };
        var otherService = new EmbedTokenService(Options.Create(otherOptions), _clock);
        var validation = otherService.Validate(minted.Token);

        validation.Result.Should().Be(EmbedTokenValidationResult.InvalidSignature);
    }

    [Test]
    public void Validate_Wrong_Audience_Fails()
    {
        var foreignToken = MintForeignToken(audience: "other-audience");

        var validation = _service.Validate(foreignToken);

        validation.Result.Should().Be(EmbedTokenValidationResult.WrongAudience);
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("not.a.jwt")]
    [TestCase("only-one-segment")]
    [TestCase("two.segments")]
    [TestCase("aaaa.bbbb.cccc.dddd")]
    public void Validate_Garbage_Input_Returns_Malformed(string garbage)
    {
        var validation = _service.Validate(garbage);
        validation.Result.Should().Be(EmbedTokenValidationResult.Malformed);
        validation.Claims.Should().BeNull();
    }

    [Test]
    public void Validate_Missing_Res_Claim_Returns_MissingClaims()
    {
        var token = MintForeignToken(includeRes: false);
        var validation = _service.Validate(token);
        validation.Result.Should().Be(EmbedTokenValidationResult.MissingClaims);
    }

    [Test]
    public void Validate_Missing_Org_Claim_Returns_MissingClaims()
    {
        var token = MintForeignToken(includeOrg: false);
        var validation = _service.Validate(token);
        validation.Result.Should().Be(EmbedTokenValidationResult.MissingClaims);
    }

    [Test]
    public void Validate_Empty_Org_Array_Returns_NoAllowedOrigins()
    {
        var token = MintForeignToken(emptyOrg: true);
        var validation = _service.Validate(token);
        validation.Result.Should().Be(EmbedTokenValidationResult.NoAllowedOrigins);
    }

    private string MintForeignToken(
        string? audience = null,
        bool includeRes = true,
        bool includeOrg = true,
        bool emptyOrg = false)
    {
        var now = _clock.GetUtcNow();
        var claims = new Dictionary<string, object>
        {
            ["sub"] = "42",
            ["jti"] = Guid.NewGuid().ToString("N"),
            ["scp"] = "read"
        };

        if (includeRes)
        {
            claims["res"] = new Dictionary<string, string> { ["t"] = "query", ["id"] = "x" };
        }

        if (includeOrg)
        {
            claims["org"] = emptyOrg ? Array.Empty<string>() : new[] { "https://app.acme.com" };
        }

        var key = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey));
        var descriptor = new Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor
        {
            Issuer = "beacon",
            Audience = audience ?? "beacon-embed",
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.UtcDateTime.AddMinutes(5),
            SigningCredentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(
                key, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256),
            Claims = claims
        };

        var handler = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler();
        return handler.CreateToken(descriptor);
    }

    [Test]
    public void OptionsValidator_Rejects_Short_SigningKey()
    {
        var validator = new EmbedTokenOptionsValidator();
        var result = validator.Validate(null, new EmbedTokenOptions { SigningKey = "too-short" });
        result.Failed.Should().BeTrue();
    }

    [Test]
    public void OptionsValidator_Rejects_Default_Ttl_Greater_Than_Max()
    {
        var validator = new EmbedTokenOptionsValidator();
        var result = validator.Validate(null, new EmbedTokenOptions
        {
            SigningKey = SigningKey,
            DefaultTtlSeconds = 1000,
            MaxTtlSeconds = 900
        });
        result.Failed.Should().BeTrue();
    }

    [Test]
    public void OptionsValidator_Accepts_Valid_Options()
    {
        var validator = new EmbedTokenOptionsValidator();
        var result = validator.Validate(null, new EmbedTokenOptions { SigningKey = SigningKey });
        result.Succeeded.Should().BeTrue();
    }

    private static string DecodePayload(string jwt)
    {
        var parts = jwt.Split('.');
        return Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
    }

    private static string DecodeHeader(string jwt)
    {
        var parts = jwt.Split('.');
        return Encoding.UTF8.GetString(Base64UrlDecode(parts[0]));
    }

    private static byte[] Base64UrlDecode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }
}
