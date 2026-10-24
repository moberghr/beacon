using Beacon.Api;
using Beacon.Api.Authentication;
using Beacon.Core.Services.Embed;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Embed;

/// <summary>
/// The anonymous embed routes are matched as routing matches them (whole segments, any case, a trailing slash allowed) and,
/// while embedding is on and the host is no MCP gateway, they are not Beacon requests; nothing else is an embed route.
/// </summary>
[TestFixture]
public class BeaconEmbedPathsTests
{
    [TestCase("/beacon/api/embed/tokens", EmbedRoute.Tokens)]
    [TestCase("/Beacon/API/Embed/Tokens/", EmbedRoute.Tokens)]
    [TestCase("/beacon/api/embed/render", EmbedRoute.Render)]
    [TestCase("/BEACON/api/embed/RENDER/", EmbedRoute.Render)]
    [TestCase("/beacon/embed/0123456789abcdef0123456789abcdef", EmbedRoute.Frame)]
    [TestCase("/beacon/Embed/anything/", EmbedRoute.Frame)]
    [TestCase("/beacon/api/embed", EmbedRoute.None)]
    [TestCase("/beacon/api/embed/render/x", EmbedRoute.None)]
    [TestCase("/beacon/api/embed//render", EmbedRoute.None)]
    [TestCase("/beacon/api/embed-grants", EmbedRoute.None)]
    [TestCase("/beacon/embed", EmbedRoute.None)]
    [TestCase("/beacon/embed/a/b", EmbedRoute.None)]
    [TestCase("/beacon/embed.html", EmbedRoute.None)]
    [TestCase("/beaconx/api/embed/render", EmbedRoute.None)]
    [TestCase("/api/embed/render", EmbedRoute.None)]
    [TestCase("", EmbedRoute.None)]
    public void ARoute_IsMatchedBySegments_IgnoringCase(string path, EmbedRoute expected)
    {
        BeaconEmbedPaths.RouteOf(new PathString(path)).Should().Be(expected);
    }

    [Test]
    public void WhileEmbeddingIsOn_TheEmbedRoutesAreNotBeaconRequests_AndEveryOtherBeaconPathStillIs()
    {
        var on = Context("/beacon/api/embed/render", enabled: true);
        var otherOn = Context("/beacon/api/queries", enabled: true);
        var off = Context("/beacon/api/embed/render", enabled: false);
        var gateway = Context("/beacon/api/embed/render", enabled: true, gatewayMode: true);

        BeaconRequestPaths.IsBeaconRequest(on).Should().BeFalse();
        BeaconRequestPaths.IsBeaconRequest(otherOn).Should().BeTrue();
        BeaconRequestPaths.IsBeaconRequest(off).Should().BeTrue("an ordinary Beacon path while embedding is off");
        BeaconRequestPaths.IsBeaconRequest(gateway).Should().BeTrue("an MCP gateway serves no embed route");
        BeaconEmbedPaths.RouteOf(gateway).Should().Be(EmbedRoute.None);
    }

    private static HttpContext Context(string path, bool enabled, bool gatewayMode = false)
    {
        var services = new ServiceCollection()
            .AddSingleton(Options.Create(new EmbedOptions { Enabled = enabled }))
            .AddSingleton(new BeaconApiOptions { GatewayMode = gatewayMode })
            .BuildServiceProvider();

        return new DefaultHttpContext { RequestServices = services, Request = { Path = path } };
    }
}
