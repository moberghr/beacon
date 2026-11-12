using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using Beacon.Core.Authentication;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// How fresh a bearer token and its signing keys must be: a token issued before its user was last signed out everywhere
/// is refused, with no clock-skew allowance, and the JWKS refresh interval and maximum staleness come from the validation
/// options, checked at startup.
/// </summary>
[TestFixture]
public class BearerTokenFreshnessTests
{
    private const string Endpoint = "https://idp.example/jwks";

    private static readonly DateTime SignedOut = new(2026, 11, 11, 9, 0, 0, DateTimeKind.Utc);

    [Test]
    public void TokenIssuedBeforeTheSignOut_IsRefused_AndOneIssuedAtOrAfterItIsNot()
    {
        TokenIssueTime.IssuedBeforeSignOut(Token(SignedOut.AddMinutes(-6)), SignedOut).Should().BeTrue();
        TokenIssueTime.IssuedBeforeSignOut(Token(SignedOut), SignedOut).Should().BeFalse();
        TokenIssueTime.IssuedBeforeSignOut(Token(SignedOut.AddMinutes(1)), SignedOut).Should().BeFalse();
    }

    [Test]
    public void TokenIssuedWithinTheClockSkewBeforeTheSignOut_IsRefused()
    {
        TokenIssueTime.IssuedBeforeSignOut(Token(SignedOut.AddMinutes(-4)), SignedOut).Should().BeTrue();
        TokenIssueTime.IssuedBeforeSignOut(Token(SignedOut.AddSeconds(-1)), SignedOut).Should().BeTrue("no allowance is given for the clock skew");
    }

    [Test]
    public void UserNeverSignedOutEverywhere_RefusesNoToken()
    {
        TokenIssueTime.IssuedBeforeSignOut(Token(SignedOut.AddYears(-1)), tokensValidAfter: null).Should().BeFalse();
        TokenIssueTime.IssuedBeforeSignOut(new ClaimsPrincipal(new ClaimsIdentity()), tokensValidAfter: null).Should().BeFalse();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("yesterday")]
    [TestCase("NaN")]
    [TestCase("1e300")]
    public void TokenWithoutAReadableIssueTime_IsRefusedOnceTheUserWasSignedOut(string? issuedAt)
    {
        var claims = issuedAt == null ? new List<Claim>() : [new Claim("iat", issuedAt)];
        var token = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));

        TokenIssueTime.IssuedBeforeSignOut(token, SignedOut).Should().BeTrue();
    }

    [Test]
    public void FractionalIssueTime_IsRead()
    {
        var seconds = new DateTimeOffset(SignedOut.AddMinutes(1)).ToUnixTimeSeconds() + 0.5;
        var token = new ClaimsPrincipal(new ClaimsIdentity([new Claim("iat", seconds.ToString(CultureInfo.InvariantCulture))], "test"));

        TokenIssueTime.IssuedBeforeSignOut(token, SignedOut).Should().BeFalse();
    }

    [Test]
    public async Task ConfiguredRefreshInterval_FetchesTheKeysAgainWhenItEnds()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(SignedOut));
        var cache = new JwksSigningKeyCache(time);
        var fetches = 0;
        var document = KeySet();

        Task<string> Fetch(CancellationToken cancellationToken)
        {
            fetches++;
            return Task.FromResult(document);
        }

        var refresh = TimeSpan.FromMinutes(10);
        await cache.GetKeysAsync(Endpoint, Fetch, false, refresh, TimeSpan.FromHours(1), CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(9));
        await cache.GetKeysAsync(Endpoint, Fetch, false, refresh, TimeSpan.FromHours(1), CancellationToken.None);
        fetches.Should().Be(1, "the keys are younger than the refresh interval");

        time.Advance(TimeSpan.FromMinutes(1));
        var keys = await cache.GetKeysAsync(Endpoint, Fetch, false, refresh, TimeSpan.FromHours(1), CancellationToken.None);

        fetches.Should().Be(2);
        keys.Should().ContainSingle();
    }

    [Test]
    public async Task ConfiguredMaximumStaleness_StopsServingKeysTheEndpointNoLongerConfirms()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(SignedOut));
        var cache = new JwksSigningKeyCache(time);
        var reachable = true;
        var document = KeySet();

        Task<string> Fetch(CancellationToken cancellationToken)
        {
            return reachable ? Task.FromResult(document) : throw new HttpRequestException("unreachable");
        }

        var refresh = TimeSpan.FromMinutes(10);
        var staleness = TimeSpan.FromMinutes(30);
        await cache.GetKeysAsync(Endpoint, Fetch, false, refresh, staleness, CancellationToken.None);
        reachable = false;

        time.Advance(TimeSpan.FromMinutes(11));
        var stillServed = await cache.GetKeysAsync(Endpoint, Fetch, false, refresh, staleness, CancellationToken.None);
        stillServed.Should().ContainSingle("the keys are within the maximum staleness");

        time.Advance(TimeSpan.FromMinutes(20));
        var act = async () => await cache.GetKeysAsync(Endpoint, Fetch, false, refresh, staleness, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>("keys older than the maximum staleness are no longer used");
    }

    [Test]
    public void JwksTimes_DefaultToOneHourAndOneDay()
    {
        var validation = new JwtValidationOptions();

        validation.JwksRefreshInterval.Should().Be(TimeSpan.FromHours(1));
        validation.JwksMaximumStaleness.Should().Be(TimeSpan.FromHours(24));
        Options().Invoking(x => x.Validate()).Should().NotThrow();
    }

    [TestCase(4, 24 * 60, "JwksRefreshInterval")]
    [TestCase(25 * 60, 48 * 60, "JwksRefreshInterval")]
    [TestCase(60, 30, "JwksMaximumStaleness")]
    [TestCase(60, 8 * 24 * 60, "JwksMaximumStaleness")]
    public void JwksTimesOutOfBounds_FailStartup(int refreshMinutes, int stalenessMinutes, string named)
    {
        var options = Options();
        options.Validation.JwksRefreshInterval = TimeSpan.FromMinutes(refreshMinutes);
        options.Validation.JwksMaximumStaleness = TimeSpan.FromMinutes(stalenessMinutes);

        options.Invoking(x => x.Validate()).Should().Throw<InvalidOperationException>().WithMessage($"*{named}*");
    }

    private static ClaimsPrincipal Token(DateTime issuedAt)
    {
        var seconds = new DateTimeOffset(issuedAt).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        return new ClaimsPrincipal(new ClaimsIdentity([new Claim("iat", seconds, ClaimValueTypes.Integer64)], "test"));
    }

    private static JwtAuthenticationOptions Options()
    {
        return new JwtAuthenticationOptions
        {
            EnableBearerAuthentication = true,
            Validation = new JwtValidationOptions
            {
                JwksEndpoint = Endpoint,
                ValidIssuer = "https://idp.example/",
                ValidAudience = "api://beacon"
            }
        };
    }

    private static string KeySet()
    {
        using var rsa = RSA.Create(2048);
        var parameters = rsa.ExportParameters(includePrivateParameters: false);

        return $$"""{"keys":[{"kty":"RSA","use":"sig","kid":"k1","n":"{{Base64UrlEncoder.Encode(parameters.Modulus)}}","e":"{{Base64UrlEncoder.Encode(parameters.Exponent)}}"}]}""";
    }
}
