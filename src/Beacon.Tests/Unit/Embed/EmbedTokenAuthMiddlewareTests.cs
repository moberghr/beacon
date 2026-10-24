using System.Net;
using Beacon.Api.Authentication;
using Beacon.Core.Mcp;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services.Embed;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Embed;

/// <summary>
/// The embed middleware, last in <c>UseBeaconAuthentication</c>: on an embed path it always replaces the caller (the host's
/// default scheme, a Beacon cookie or API key never stand there), with the embed principal for a valid credential and an
/// anonymous one otherwise; refusals are one generic 401 (403 for another origin), recorded with their reason and never
/// logged with the credential; its limits answer 429; off embed paths, and while embedding is off, it does nothing.
/// </summary>
[TestFixture]
public class EmbedTokenAuthMiddlewareTests
{
    [Test]
    public async Task AValidToken_WithItsFrameMarker_IsTheEmbedPrincipal_WithTheTokensValues()
    {
        var kit = new EmbedTestKit();
        var (grant, _) = kit.Grant();
        await using var host = await EmbedSurfaceHost.StartAsync(kit);

        var response = await Render(host, kit, grant, await kit.TokenAsync(grant, branch: "12"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be($"BeaconEmbed|{McpCallerClaimTypes.EmbedTokenAuthMethod}|{grant.Id}|branch=12");
    }

    [Test]
    public async Task AValidGrantSecret_OnTheExchange_IsTheGrantsPrincipal()
    {
        var kit = new EmbedTestKit();
        var (grant, secret) = kit.Grant();
        await using var host = await EmbedSurfaceHost.StartAsync(kit);

        var response = await host.SendAsync(HttpMethod.Post, BeaconEmbedPaths.TokensPath, $"BeaconEmbedGrant {secret}");

        (await response.Content.ReadAsStringAsync()).Should().Be($"BeaconEmbed|{McpCallerClaimTypes.EmbedGrantAuthMethod}|{grant.Id}|");
    }

    [Test]
    public async Task EachCredential_WorksOnItsOwnRouteOnly()
    {
        var kit = new EmbedTestKit();
        var (grant, secret) = kit.Grant();
        var token = await kit.TokenAsync(grant);
        await using var host = await EmbedSurfaceHost.StartAsync(kit);

        var tokenOnExchange = await host.SendAsync(HttpMethod.Post, BeaconEmbedPaths.TokensPath, $"BeaconEmbed {token}");
        var secretOnRender = await host.SendAsync(HttpMethod.Post, BeaconEmbedPaths.RenderPath, $"BeaconEmbedGrant {secret}", kit.Codec.FrameMarkerOf(grant.PublicId));

        tokenOnExchange.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        secretOnRender.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task AMissingCredential_IsTheGenericRefusal_AndRecordsNothing()
    {
        var kit = new EmbedTestKit();
        await using var host = await EmbedSurfaceHost.StartAsync(kit);

        var response = await host.SendAsync(HttpMethod.Post, BeaconEmbedPaths.RenderPath);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().Be("{\"error\":\"embed_unavailable\"}");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        host.Audit.Events.Should().BeEmpty();
    }

    [Test]
    public async Task EveryRefusedToken_GetsTheSameAnswer_AndItsReasonIsRecorded_NeverTheToken()
    {
        var kit = new EmbedTestKit();
        var (revoked, _) = kit.Grant();
        var (superseded, _) = kit.Grant();
        var (live, _) = kit.Grant();
        var revokedToken = await kit.TokenAsync(revoked);
        var expiredToken = await kit.TokenAsync(live, ttlSeconds: 10);
        await using var host = await EmbedSurfaceHost.StartAsync(kit);
        revoked.RevokedTime = EmbedTestKit.Now.UtcDateTime;
        kit.Clock.Advance(TimeSpan.FromSeconds(10));

        var bodies = new List<string>();
        foreach (var (grant, token) in new[] { (revoked, revokedToken), (live, expiredToken), (live, "not-a-token") })
        {
            var response = await Render(host, kit, grant, token);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            bodies.Add(await response.Content.ReadAsStringAsync());
        }

        kit.Supersede();
        var supersededResponse = await Render(host, kit, superseded, await kit.TokenAsync(superseded));
        bodies.Add(await supersededResponse.Content.ReadAsStringAsync());

        bodies.Should().AllBe("{\"error\":\"embed_unavailable\"}");
        host.Audit.Of(SecurityAuditActions.EmbedTokenRefused)
            .Select(x => x.DetailsJson)
            .Should().Equal(
                "{\"reason\":\"revoked\"}",
                "{\"reason\":\"expired\"}",
                "{\"reason\":\"unknown\"}",
                "{\"reason\":\"superseded\"}");
        host.Audit.Of(SecurityAuditActions.EmbedTokenRefused).Should().OnlyContain(x => x.Outcome == SecurityAuditOutcomes.Refused);
        host.Audit.Of(SecurityAuditActions.EmbedTokenRefused).First().ResourceId.Should().Be(revoked.Id.ToString());
        host.Logs.Entries.Should().NotContain(x => x.FullText.Contains(revokedToken) || x.FullText.Contains(expiredToken));
    }

    [Test]
    public async Task ARenderWithoutItsGrantsFrameMarker_IsRefused()
    {
        var kit = new EmbedTestKit();
        var (grant, _) = kit.Grant();
        var (other, _) = kit.Grant();
        var token = await kit.TokenAsync(grant);
        await using var host = await EmbedSurfaceHost.StartAsync(kit);

        var none = await host.SendAsync(HttpMethod.Post, BeaconEmbedPaths.RenderPath, $"BeaconEmbed {token}");
        var another = await host.SendAsync(HttpMethod.Post, BeaconEmbedPaths.RenderPath, $"BeaconEmbed {token}", kit.Codec.FrameMarkerOf(other.PublicId));

        none.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        another.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        host.Audit.Of(SecurityAuditActions.EmbedTokenRefused).Select(x => x.DetailsJson).Should().AllBe("{\"reason\":\"origin\"}");
    }

    [Test]
    public async Task ARenderFromAnotherOrigin_Is403_BeforeTheTokenIsRead()
    {
        var kit = new EmbedTestKit();
        var (grant, _) = kit.Grant();
        await using var host = await EmbedSurfaceHost.StartAsync(kit);
        var token = await kit.TokenAsync(grant);

        var foreign = await Render(host, kit, grant, token, origin: "https://elsewhere.example.net");
        var own = await Render(host, kit, grant, token, origin: "http://localhost");

        foreign.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await foreign.Content.ReadAsStringAsync()).Should().Be("{\"error\":\"embed_unavailable\"}");
        own.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task TheHostsDefaultScheme_ABeaconCookie_AndAnApiKey_NeverStandOnAnEmbedPath()
    {
        var kit = new EmbedTestKit();
        kit.Grant();
        await using var host = await EmbedSurfaceHost.StartAsync(kit);
        var cookie = await host.SignInAsync();
        var page = BeaconEmbedPaths.FramePrefix + "/" + EmbedGrantSecret.NewPublicId();

        var asHostUser = await host.SendAsync(HttpMethod.Get, page, hostUser: true);
        var asCookie = await host.SendAsync(HttpMethod.Get, page, cookie: cookie);
        var asApiKey = await host.SendAsync(HttpMethod.Post, BeaconEmbedPaths.RenderPath, $"Bearer {EmbedSurfaceHost.ApiKey}");

        (await asHostUser.Content.ReadAsStringAsync()).Should().StartWith("anonymous|");
        (await asCookie.Content.ReadAsStringAsync()).Should().StartWith("anonymous|");
        asCookie.Headers.Contains("Set-Cookie").Should().BeFalse("the Beacon.Auth session is neither used nor renewed on an embed path");
        asApiKey.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Off the embed paths, each still authenticates as before.
        (await (await host.SendAsync(HttpMethod.Get, "/api/orders", hostUser: true)).Content.ReadAsStringAsync()).Should().StartWith(EmbedSurfaceHost.HostScheme);
        (await (await host.SendAsync(HttpMethod.Get, "/beacon/api/whoami", cookie: cookie)).Content.ReadAsStringAsync()).Should().StartWith("Cookies");
        (await (await host.SendAsync(HttpMethod.Get, "/beacon/api/whoami", $"Bearer {EmbedSurfaceHost.ApiKey}")).Content.ReadAsStringAsync()).Should().StartWith("ApiKey");
    }

    [TestCase("/Beacon/Api/Embed/Render")]
    [TestCase("/beacon/api/embed/render/")]
    [TestCase("/BEACON/EMBED/0123456789abcdef0123456789abcdef/")]
    public async Task AnEmbedPath_InAnyCase_OrWithATrailingSlash_IsTheEmbedMiddlewaresAlone(string path)
    {
        var kit = new EmbedTestKit();
        await using var host = await EmbedSurfaceHost.StartAsync(kit);
        var cookie = await host.SignInAsync();

        var response = await host.SendAsync(path.Contains("render", StringComparison.OrdinalIgnoreCase) ? HttpMethod.Post : HttpMethod.Get, path, cookie: cookie);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotStartWith("Cookies", "the Beacon.Auth cookie never authenticates an embed path");
    }

    [Test]
    public async Task WhileEmbeddingIsOff_OrInGatewayMode_TheMiddlewareIsNotThere()
    {
        var off = new EmbedTestKit();
        off.Options.Enabled = false;
        await using (var host = await EmbedSurfaceHost.StartAsync(off))
        {
            var cookie = await host.SignInAsync();
            var response = await host.SendAsync(HttpMethod.Get, BeaconEmbedPaths.FramePrefix + "/" + EmbedGrantSecret.NewPublicId(), cookie: cookie);

            (await response.Content.ReadAsStringAsync()).Should().StartWith("Cookies", "an ordinary Beacon path while embedding is off");
        }

        var gateway = new EmbedTestKit();
        await using (var host = await EmbedSurfaceHost.StartAsync(gateway, gatewayMode: true))
        {
            var cookie = await host.SignInAsync();
            var response = await host.SendAsync(HttpMethod.Get, BeaconEmbedPaths.FramePrefix + "/" + EmbedGrantSecret.NewPublicId(), cookie: cookie);

            (await response.Content.ReadAsStringAsync()).Should().StartWith("Cookies", "an MCP gateway serves no embed route");
        }
    }

    [Test]
    public async Task PastItsRefusalBudget_AClientIsAnswered429_BeforeAnyLookup()
    {
        var kit = new EmbedTestKit();
        kit.Options.RateLimits.RefusalsPerIpPerMinute = 2;
        await using var host = await EmbedSurfaceHost.StartAsync(kit);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            statuses.Add((await host.SendAsync(HttpMethod.Post, BeaconEmbedPaths.RenderPath, "BeaconEmbed not-a-token")).StatusCode);
        }

        statuses.Should().Equal(HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, (HttpStatusCode)429, (HttpStatusCode)429);
        host.Audit.Of(SecurityAuditActions.EmbedTokenRefused).Should().HaveCount(2);
    }

    [Test]
    public async Task PastAGrantsPerMinuteBudget_ItsRendersAndExchangesAreAnswered429()
    {
        var kit = new EmbedTestKit();
        kit.Options.RateLimits.RenderPerGrantPerMinute = 1;
        kit.Options.RateLimits.ExchangePerGrantPerMinute = 1;
        var (grant, secret) = kit.Grant();
        var (other, _) = kit.Grant();
        await using var host = await EmbedSurfaceHost.StartAsync(kit);
        var token = await kit.TokenAsync(grant);

        (await Render(host, kit, grant, token)).StatusCode.Should().Be(HttpStatusCode.OK);
        var over = await Render(host, kit, grant, token);
        (await Render(host, kit, other, await kit.TokenAsync(other))).StatusCode.Should().Be(HttpStatusCode.OK, "each grant has its own budget");
        (await host.SendAsync(HttpMethod.Post, BeaconEmbedPaths.TokensPath, $"BeaconEmbedGrant {secret}")).StatusCode.Should().Be(HttpStatusCode.OK);
        var exchangeOver = await host.SendAsync(HttpMethod.Post, BeaconEmbedPaths.TokensPath, $"BeaconEmbedGrant {secret}");

        over.StatusCode.Should().Be((HttpStatusCode)429);
        over.Headers.RetryAfter.Should().NotBeNull();
        exchangeOver.StatusCode.Should().Be((HttpStatusCode)429);
    }

    [Test]
    public async Task AForgedTokenNamingARealGrant_NeverSpendsThatGrantsBudget()
    {
        var kit = new EmbedTestKit();
        kit.Options.RateLimits.RenderPerGrantPerMinute = 1;
        var (grant, _) = kit.Grant();
        await using var host = await EmbedSurfaceHost.StartAsync(kit);
        var foreignKit = new EmbedTestKit();
        var forged = await foreignKit.Codec.MintAsync(grant.PublicId, new Dictionary<string, string> { ["branch"] = "7" }, TimeSpan.FromMinutes(5));

        (await Render(host, kit, grant, forged.Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Render(host, kit, grant, await kit.TokenAsync(grant))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static Task<HttpResponseMessage> Render(EmbedSurfaceHost host, EmbedTestKit kit, Beacon.Core.Data.Entities.EmbedGrant grant, string token, string? origin = null)
    {
        return host.SendAsync(HttpMethod.Post, BeaconEmbedPaths.RenderPath, $"BeaconEmbed {token}", kit.Codec.FrameMarkerOf(grant.PublicId), origin);
    }
}
