using System.Net;
using System.Text.Json;
using Beacon.Api.Authentication;
using Beacon.Api.Endpoints;
using Beacon.Core.Data.Entities;
using Beacon.Core.Handlers.Embed;
using Beacon.Core.Models;
using Beacon.Core.Services.Embed;
using FluentAssertions;
using MediatR;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Embed;

/// <summary>
/// Beacon's embed endpoints: the render and exchange answer every refusal with the one generic body (401 for an unavailable
/// grant or lost project access, 503 with Retry-After for a full render queue or access that could not be determined),
/// never cached; the exchange's own request errors are told to the backend as 400; the embed page carries the exact strict
/// policy with the grant's origins as <c>frame-ancestors</c>, and a grant that is not live gets 404 no site may frame.
/// </summary>
[TestFixture]
public class EmbedEndpointsTests
{
    private string _webRoot = null!;

    [SetUp]
    public void SetUp()
    {
        _webRoot = Directory.CreateTempSubdirectory("beacon-embed-").FullName;
        Directory.CreateDirectory(Path.Combine(_webRoot, "beacon"));
        File.WriteAllText(Path.Combine(_webRoot, "beacon", "embed.html"), "<!doctype html><html><head><title>Embed</title></head><body></body></html>");
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_webRoot, recursive: true);
    }

    [Test]
    public async Task ARender_ReturnsTheResult_NeverCached()
    {
        var kit = new EmbedTestKit();
        var (grant, _) = kit.Grant();
        var mediator = new Mock<IMediator>();
        RenderCommand? sent = null;
        mediator
            .Setup(x => x.Send(It.IsAny<RenderEmbedQueryCommand>(), It.IsAny<CancellationToken>()))
            .Callback((IRequest<RenderEmbedQueryResult> x, CancellationToken _) => sent = new RenderCommand((RenderEmbedQueryCommand)x))
            .ReturnsAsync(new RenderEmbedQueryResult("Loan book", ["id"], [new Dictionary<string, object?> { ["id"] = 1 }], false, 1000));
        await using var host = await EmbedSurfaceHost.StartAsync(kit, mediator.Object);

        var response = await Render(host, kit, grant, await kit.TokenAsync(grant, branch: "9"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await response.Content.ReadAsStringAsync()).Should().Contain("\"title\":\"Loan book\"");
        sent!.Command.Should().BeEquivalentTo(new { ProjectId = EmbedTestKit.ProjectId, GrantId = grant.Id });
        sent.Command.Parameters.Should().Equal(new Dictionary<string, string> { ["branch"] = "9" });
    }

    [Test]
    public async Task EveryRefusedRender_GetsTheGenericBody()
    {
        var kit = new EmbedTestKit();
        var (grant, _) = kit.Grant();
        var failures = new Queue<Exception>(
        [
            new EmbedUnavailableException(RenderEmbedQueryHandler.RenderFailed, grant.Id),
            new BeaconForbiddenException("You cannot run queries in project 1."),
            new BeaconNotFoundException("Project 1 not found.")
        ]);
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(x => x.Send(It.IsAny<RenderEmbedQueryCommand>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromException<RenderEmbedQueryResult>(failures.Dequeue()));
        await using var host = await EmbedSurfaceHost.StartAsync(kit, mediator.Object);
        var token = await kit.TokenAsync(grant);

        for (var i = 0; i < 3; i++)
        {
            var response = await Render(host, kit, grant, token);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await response.Content.ReadAsStringAsync()).Should().Be("{\"error\":\"embed_unavailable\"}");
        }
    }

    [Test]
    public async Task UndeterminedAccess_OrAFullRenderQueue_Is503_WithRetryAfter()
    {
        var kit = new EmbedTestKit();
        var (grant, _) = kit.Grant();
        var failures = new Queue<Exception>([new InvalidOperationException("membership store down: relation loans"), new EmbedBusyException()]);
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(x => x.Send(It.IsAny<RenderEmbedQueryCommand>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromException<RenderEmbedQueryResult>(failures.Dequeue()));
        await using var host = await EmbedSurfaceHost.StartAsync(kit, mediator.Object);
        var token = await kit.TokenAsync(grant);

        for (var i = 0; i < 2; i++)
        {
            var response = await Render(host, kit, grant, token);
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter.Should().NotBeNull();
            (await response.Content.ReadAsStringAsync()).Should().Be("{\"error\":\"embed_unavailable\"}");
        }
    }

    [Test]
    public async Task AnExchange_ReturnsTheTokenAndPath_UnderTheHostsPathBase_AndItsRequestErrorsAre400()
    {
        var kit = new EmbedTestKit();
        var (grant, secret) = kit.Grant();
        var mediator = new Mock<IMediator>();
        mediator
            .SetupSequence(x => x.Send(It.IsAny<IssueEmbedTokenCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueEmbedTokenResult("tok", EmbedTestKit.Now.AddMinutes(5), "/beacon/embed/" + grant.PublicId + "#t=tok"))
            .ThrowsAsync(new EmbedRequestInvalidException("Missing value for parameter(s): branch."));
        await using var host = await EmbedSurfaceHost.StartAsync(kit, mediator.Object, pathBase: "/portal");

        var issued = await host.SendAsync(HttpMethod.Post, "/portal" + BeaconEmbedPaths.TokensPath, $"BeaconEmbedGrant {secret}");
        var invalid = await host.SendAsync(HttpMethod.Post, "/portal" + BeaconEmbedPaths.TokensPath, $"BeaconEmbedGrant {secret}");

        issued.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonDocument.Parse(await issued.Content.ReadAsStringAsync()).RootElement.GetProperty("embedPath").GetString()
            .Should().Be($"/portal/beacon/embed/{grant.PublicId}#t=tok");
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await invalid.Content.ReadAsStringAsync()).Should().Contain("Missing value for parameter(s): branch.");
    }

    [Test]
    public async Task ThePage_OfALiveGrant_CarriesTheStrictPolicy_WithItsOriginsAsFrameAncestors_AndItsFrameMarker()
    {
        var kit = new EmbedTestKit();
        var (grant, _) = kit.Grant();
        await using var host = await EmbedSurfaceHost.StartAsync(kit, Frame(available: true), _webRoot);

        var response = await host.SendAsync(HttpMethod.Get, BeaconEmbedPaths.FramePrefix + "/" + grant.PublicId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("Content-Security-Policy").Single().Should().Be(
            "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; font-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors https://portal.example.com");
        response.Headers.GetValues("Referrer-Policy").Single().Should().Be("no-referrer");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.GetValues("X-Content-Type-Options").Single().Should().Be("nosniff");
        (await response.Content.ReadAsStringAsync()).Should().Contain($"<meta name=\"beacon-embed-frame\" content=\"{kit.Codec.FrameMarkerOf(grant.PublicId)}\"></head>");
    }

    [Test]
    public async Task ThePage_OfAGrantThatIsNotLive_Is404_ThatNoSiteMayFrame()
    {
        var kit = new EmbedTestKit();
        var (grant, _) = kit.Grant();
        await using var host = await EmbedSurfaceHost.StartAsync(kit, Frame(available: false), _webRoot);

        var response = await host.SendAsync(HttpMethod.Get, BeaconEmbedPaths.FramePrefix + "/" + grant.PublicId);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Headers.GetValues("Content-Security-Policy").Single().Should().EndWith("frame-ancestors 'none'");
        (await response.Content.ReadAsStringAsync()).Should().NotContain("beacon-embed-frame");
    }

    [Test]
    public async Task WithoutTheEmbedPageInstalled_ThePageIs404()
    {
        var kit = new EmbedTestKit();
        var (grant, _) = kit.Grant();
        File.Delete(Path.Combine(_webRoot, "beacon", "embed.html"));
        await using var host = await EmbedSurfaceHost.StartAsync(kit, Frame(available: true), _webRoot);

        var response = await host.SendAsync(HttpMethod.Get, BeaconEmbedPaths.FramePrefix + "/" + grant.PublicId);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Headers.GetValues("Content-Security-Policy").Single().Should().EndWith("frame-ancestors 'none'");
    }

    [Test]
    public void TheFrameAncestors_ListOnlyOriginsAsTheGrantsRulesWriteThem()
    {
        EmbedFrameHeaders.ContentSecurityPolicy(["https://a.example.com", "https://b.example.com:8443; script-src *", "*"])
            .Should().EndWith("frame-ancestors https://a.example.com");
        EmbedFrameHeaders.ContentSecurityPolicy([]).Should().EndWith("frame-ancestors 'none'");
    }

    private static IMediator Frame(bool available)
    {
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(x => x.Send(It.IsAny<GetEmbedFrameQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(available ? new GetEmbedFrameResult(true, [EmbedTestKit.Origin]) : new GetEmbedFrameResult(false, []));

        return mediator.Object;
    }

    private static Task<HttpResponseMessage> Render(EmbedSurfaceHost host, EmbedTestKit kit, EmbedGrant grant, string token)
    {
        return host.SendAsync(HttpMethod.Post, BeaconEmbedPaths.RenderPath, $"BeaconEmbed {token}", kit.Codec.FrameMarkerOf(grant.PublicId));
    }

    private sealed record RenderCommand(RenderEmbedQueryCommand Command);
}
