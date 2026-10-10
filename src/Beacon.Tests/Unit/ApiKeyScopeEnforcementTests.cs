using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using Beacon.Api;
using Beacon.Api.Authentication;
using Beacon.Api.Endpoints;
using Beacon.Core;
using Beacon.Core.Authentication;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Handlers.DataSources;
using Beacon.Core.Handlers.Projects;
using Beacon.Core.Handlers.Queries;
using Beacon.Core.Helpers;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// §1.4 — scope enforcement on <c>/beacon/api</c> over a real request pipeline (TestServer, no DB): the real
/// <see cref="ApiKeyAuthMiddleware"/> fed keys from a mocked key store, the real authorization policies, the group's
/// filters and every endpoint <see cref="BeaconApiEndpoints.MapBeaconApi"/> maps. Authorization and user management
/// stay off (the library default), so the scope gate is proven independent of them. Also covers the Execute scope
/// following the owner's write permission (their roles and the authorization provider), keys that grant no scope, the
/// read surface a Read key reaches (a snapshot), the hub outside the group, and antiforgery for header-authenticated
/// callers versus cookie sessions and forgeries.
/// </summary>
[TestFixture]
public class ApiKeyScopeEnforcementTests
{
    private const string ReadKey = "sk-sem_readscopedintegrationkey";
    private const string ExecuteKey = "sk-sem_executescopedintegrationkey";
    private const string LegacyAdminKey = "sk-sem_legacyadminscopedkey";
    private const string ReadAndUnknownKey = "sk-sem_readandunknownscopekey";
    private const string ViewerExecuteKey = "sk-sem_executekeyofaviewerowner";
    private const string SuperAdminExecuteKey = "sk-sem_executekeyofasuperadmin";
    private const string NullScopesKey = "sk-sem_keystoredwithnullscopes";
    private const string EmptyScopesKey = "sk-sem_keystoredwithemptyscopes";
    private const string UnknownScopesKey = "sk-sem_keystoredwithunknownonly";
    private const string MalformedScopesKey = "sk-sem_keystoredwithmalformed1";
    private const string TestPrincipalHeader = "X-Test-Principal";
    private const string AntiforgeryCookieName = "beacon-test-antiforgery";

    private static readonly string[] SafeMethods = ["GET", "HEAD", "OPTIONS"];

    // A body that fails to bind is answered 400 before any filter runs; these routes need a body with their required
    // members so the request reaches the scope gate.
    private static readonly Dictionary<string, string> BodiesThatBind = new()
    {
        ["POST /beacon/api/ai-actors/"] = "{\"name\":\"actor\",\"instructions\":\"watch\",\"dataSourceId\":1}"
    };

    // Every GET route pattern a Read-scoped key gets past the scope, role and session gates on: see
    // EveryGetAReadScopedKeyCanReach_IsListed.
    private static readonly string[] ReadScopeSurface =
    [
        "/beacon/api/ai-actors/",
        "/beacon/api/ai-actors/plans/{id:int}",
        "/beacon/api/ai-actors/{id:int}",
        "/beacon/api/ai-actors/{id:int}/pending-plans",
        "/beacon/api/approvals/pending",
        "/beacon/api/approvals/{id:int}",
        "/beacon/api/auth/me",
        "/beacon/api/auth/permissions",
        "/beacon/api/auth/sso",
        "/beacon/api/control-tower/health",
        "/beacon/api/control-tower/statistics",
        "/beacon/api/control-tower/subscriptions/{id:int}/detail",
        "/beacon/api/csrf",
        "/beacon/api/dashboards/",
        "/beacon/api/dashboards/{id:int}",
        "/beacon/api/dashboards/{id:int}/permissions",
        "/beacon/api/data-quality/contracts",
        "/beacon/api/data-quality/contracts/{id:int}",
        "/beacon/api/data-quality/contracts/{id:int}/evaluations",
        "/beacon/api/data-quality/overview",
        "/beacon/api/data-sources/",
        "/beacon/api/data-sources/{dataSourceId:int}/relationships",
        "/beacon/api/data-sources/{dataSourceId:int}/schema-health",
        "/beacon/api/data-sources/{id:int}",
        "/beacon/api/health",
        "/beacon/api/home/activity",
        "/beacon/api/home/migration-summary",
        "/beacon/api/home/task-summary",
        "/beacon/api/home/trends",
        "/beacon/api/home/uptime",
        "/beacon/api/mcp/documentation-patches",
        "/beacon/api/mcp/learned-patterns",
        "/beacon/api/mcp/learning-stats",
        "/beacon/api/mcp/tools",
        "/beacon/api/notifications/",
        "/beacon/api/notifications/{id:int}",
        "/beacon/api/projects/",
        "/beacon/api/projects/documentation/{id:int}/export",
        "/beacon/api/projects/{id:int}",
        "/beacon/api/projects/{id:int}/documentation",
        "/beacon/api/projects/{id:int}/imported-documents",
        "/beacon/api/projects/{id:int}/imported-documents/{documentId:int}",
        "/beacon/api/projects/{id:int}/mcp-context",
        "/beacon/api/queries/",
        "/beacon/api/queries/{id:int}",
        "/beacon/api/queries/{queryId:int}/change-history",
        "/beacon/api/queries/{queryId:int}/versions",
        "/beacon/api/query-folders/",
        "/beacon/api/query-versions/diff",
        "/beacon/api/query-versions/{id:int}",
        "/beacon/api/recipients/",
        "/beacon/api/subscriptions/",
        "/beacon/api/subscriptions/{id:int}",
        "/beacon/api/subscriptions/{id:int}/anomaly-chart",
        "/beacon/api/tasks/",
        "/beacon/api/tasks/{id:int}",
        "/beacon/api/tasks/{id:int}/comments",
        "/beacon/api/tasks/{id:int}/executions",
        "/beacon/api/tasks/{id:int}/related",
        "/beacon/api/tasks/{id:int}/result-history",
        "/beacon/api/user-settings/"
    ];

    private Mock<IApiKeyService>? _apiKeys;

    [Test]
    public async Task EveryMutatingEndpoint_RejectsAReadScopedKey_BeforeItsHandler()
    {
        // Realtime on, so the routes mapped outside the /beacon/api group (the SignalR hub) are enumerated too.
        var mediator = new Mock<IMediator>();
        await using var app = await StartAsync(mediator, realtime: true);

        // An endpoint without method metadata (the hub's) answers any method: it is probed with POST.
        var mutating = BeaconApiEndpoints(app)
            .SelectMany(x => (Methods(x).Count == 0 ? ["POST"] : Methods(x))
                .Where(y => !SafeMethods.Contains(y))
                .Select(y => (Endpoint: x, Method: y)))
            .ToList();
        var failures = new List<string>();
        foreach (var (endpoint, method) in mutating)
        {
            var body = BodiesThatBind.GetValueOrDefault($"{method} {endpoint.RoutePattern.RawText}", "{}");
            var response = await SendAsync(app, new HttpMethod(method), Path(endpoint), ReadKey, body);

            // The hub, mapped outside the group, authenticates with the cookie scheme only: a key is not even signed in there.
            var refused = response.StatusCode == HttpStatusCode.Forbidden
                || (endpoint.Metadata.GetMetadata<HubMetadata>() != null && response.StatusCode == HttpStatusCode.Unauthorized);
            if (!refused)
            {
                failures.Add($"{method} {endpoint.RoutePattern.RawText} -> {(int)response.StatusCode}");
            }
        }

        mutating.Should().HaveCountGreaterThan(50, "the enumeration must cover the whole REST surface");
        mutating.Should().Contain(x => x.Endpoint.RoutePattern.RawText == "/beacon/api/hub/negotiate", "routes outside the group count too");
        failures.Should().BeEmpty("a Read-scoped key must not change state, run SQL or call out on any route");
        mediator.Invocations.Should().BeEmpty("a denied request must never reach its handler");
    }

    [Test]
    public async Task ScopeRefusal_IsLoggedWithTheRouteAndTheKeyId()
    {
        var logs = new LogRecorder();
        await using var app = await StartAsync(new Mock<IMediator>(), logs: logs);

        await SendAsync(app, HttpMethod.Put, "/beacon/api/queries/9", ReadKey, "{}");

        logs.Entries
            .Where(x => x.Category == typeof(BeaconScopeEndpointFilter).FullName)
            .Should().ContainSingle()
            .Which.Message.Should().Be("Execute scope required for PUT /beacon/api/queries/{id:int} ApiKeyId=3");
        logs.Contains(ReadKey).Should().BeFalse();
    }

    [Test]
    public async Task EveryGetMarkedRequiresExecuteScope_RejectsAReadScopedKey()
    {
        var mediator = new Mock<IMediator>();
        await using var app = await StartAsync(mediator);

        var marked = BeaconApiEndpoints(app)
            .Where(x => x.Metadata.GetMetadata<RequiresExecuteScopeMetadata>() != null)
            .ToList();
        var statuses = new List<HttpStatusCode>();
        foreach (var endpoint in marked)
        {
            statuses.Add((await SendAsync(app, HttpMethod.Get, Path(endpoint), ReadKey)).StatusCode);
        }

        marked.Select(x => x.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName)
            .Should()
            .Contain("GetDataSourceMetadata", "it scans the data source live when no metadata is stored");
        marked.Should().OnlyContain(x => Methods(x).All(y => SafeMethods.Contains(y)), "the marker is for read routes");
        statuses.Should().OnlyContain(x => x == HttpStatusCode.Forbidden);
        mediator.Invocations.Should().BeEmpty();
    }

    [Test]
    public async Task ReadScopedKey_CanStillCallOrdinaryGets()
    {
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(x => x.Send(It.IsAny<GetProjectsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PagedList<ProjectSummaryEntry>.Create([], 0, 25));
        await using var app = await StartAsync(mediator);

        var projects = await SendAsync(app, HttpMethod.Get, "/beacon/api/projects", ReadKey);
        var query = await SendAsync(app, HttpMethod.Get, "/beacon/api/queries/1", ReadKey);
        var dataSources = await SendAsync(app, HttpMethod.Get, "/beacon/api/data-sources", ReadKey);

        projects.StatusCode.Should().Be(HttpStatusCode.OK);
        query.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        dataSources.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        mediator.Verify(x => x.Send(It.IsAny<GetProjectsQuery>(), It.IsAny<CancellationToken>()), Times.Once);
        mediator.Verify(x => x.Send(It.IsAny<GetQueryDetailQuery>(), It.IsAny<CancellationToken>()), Times.Once);
        mediator.Verify(x => x.Send(It.IsAny<GetDataSourcesQuery>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ExecuteScopedKey_PassesTheScopeGate_AndNeedsNoAntiforgeryToken()
    {
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(x => x.Send(It.IsAny<UpdateQueryCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpdateQueryResult { QueryId = 9, Success = true });
        await using var app = await StartAsync(mediator);

        var update = await SendAsync(app, HttpMethod.Put, "/beacon/api/queries/9", ExecuteKey, "{\"name\":\"daily\",\"steps\":[]}");
        var metadata = await SendAsync(app, HttpMethod.Get, "/beacon/api/data-sources/3/metadata", ExecuteKey);

        update.StatusCode.Should().Be(HttpStatusCode.OK);
        metadata.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        mediator.Verify(x => x.Send(It.Is<UpdateQueryCommand>(y => y.QueryId == 9), It.IsAny<CancellationToken>()), Times.Once);
        mediator.Verify(x => x.Send(It.Is<GetDataSourceMetadataQuery>(y => y.DataSourceId == 3), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task KeyStoredWithTheRetiredAdminScope_ActsAsExecute()
    {
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(x => x.Send(It.IsAny<UpdateQueryCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpdateQueryResult { QueryId = 9, Success = true });
        await using var app = await StartAsync(mediator);

        var update = await SendAsync(app, HttpMethod.Put, "/beacon/api/queries/9", LegacyAdminKey, "{\"name\":\"daily\",\"steps\":[]}");

        update.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task UnknownStoredScopes_AreDropped_SoTheyGrantNothing()
    {
        var mediator = new Mock<IMediator>();
        await using var app = await StartAsync(mediator);

        var update = await SendAsync(app, HttpMethod.Put, "/beacon/api/queries/9", ReadAndUnknownKey, "{}");

        update.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        mediator.Invocations.Should().BeEmpty();
    }

    [Test]
    public async Task ExecuteKeyOfAnOwnerWithoutWritePermission_ActsAsAReadKey()
    {
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(x => x.Send(It.IsAny<GetProjectsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PagedList<ProjectSummaryEntry>.Create([], 0, 25));
        await using var app = await StartAsync(mediator);

        var update = await SendAsync(app, HttpMethod.Put, "/beacon/api/queries/9", ViewerExecuteKey, "{\"name\":\"daily\",\"steps\":[]}");
        var metadata = await SendAsync(app, HttpMethod.Get, "/beacon/api/data-sources/3/metadata", ViewerExecuteKey);
        var projects = await SendAsync(app, HttpMethod.Get, "/beacon/api/projects", ViewerExecuteKey);

        update.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the owner lost write permission, so the key lost Execute");
        metadata.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        projects.StatusCode.Should().Be(HttpStatusCode.OK, "it still reads");
        mediator.Verify(x => x.Send(It.IsAny<UpdateQueryCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ExecuteKey_ActsAsAReadKey_OnceTheAuthorizationProviderWithdrawsTheOwnersWritePermission()
    {
        // The key was issued while the provider granted its owner write permission; the owner keeps the Editor role.
        var providerGrantsWrite = true;
        var authorization = new Mock<IBeaconAuthorizationProvider>();
        authorization
            .Setup(x => x.HasWritePermissionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => providerGrantsWrite);
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(x => x.Send(It.IsAny<UpdateQueryCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpdateQueryResult { QueryId = 9, Success = true });
        mediator
            .Setup(x => x.Send(It.IsAny<GetProjectsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PagedList<ProjectSummaryEntry>.Create([], 0, 25));
        await using var app = await StartAsync(mediator, authorization: authorization);

        var allowed = await SendAsync(app, HttpMethod.Put, "/beacon/api/queries/9", ExecuteKey, "{\"name\":\"daily\",\"steps\":[]}");
        providerGrantsWrite = false;
        var update = await SendAsync(app, HttpMethod.Put, "/beacon/api/queries/9", ExecuteKey, "{\"name\":\"daily\",\"steps\":[]}");
        var metadata = await SendAsync(app, HttpMethod.Get, "/beacon/api/data-sources/3/metadata", ExecuteKey);
        var projects = await SendAsync(app, HttpMethod.Get, "/beacon/api/projects", ExecuteKey);

        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        update.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the provider no longer lets the owner write, so the key lost Execute");
        metadata.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        projects.StatusCode.Should().Be(HttpStatusCode.OK, "it still reads");
        mediator.Verify(x => x.Send(It.IsAny<UpdateQueryCommand>(), It.IsAny<CancellationToken>()), Times.Once);
        mediator.Verify(x => x.Send(It.IsAny<GetDataSourceMetadataQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ReadKey_NeverAsksTheAuthorizationProviderForWritePermission()
    {
        var authorization = new Mock<IBeaconAuthorizationProvider>();
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(x => x.Send(It.IsAny<GetProjectsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PagedList<ProjectSummaryEntry>.Create([], 0, 25));
        await using var app = await StartAsync(mediator, authorization: authorization);

        var projects = await SendAsync(app, HttpMethod.Get, "/beacon/api/projects", ReadKey);

        projects.StatusCode.Should().Be(HttpStatusCode.OK);
        authorization.Verify(x => x.HasWritePermissionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ExecuteKeyOfASuperAdminWithoutRoles_KeepsExecute()
    {
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(x => x.Send(It.IsAny<UpdateQueryCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpdateQueryResult { QueryId = 9, Success = true });
        await using var app = await StartAsync(mediator);

        var update = await SendAsync(app, HttpMethod.Put, "/beacon/api/queries/9", SuperAdminExecuteKey, "{\"name\":\"daily\",\"steps\":[]}");

        update.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [TestCase(NullScopesKey)]
    [TestCase(EmptyScopesKey)]
    [TestCase(UnknownScopesKey)]
    [TestCase(MalformedScopesKey)]
    public async Task KeyWithNoValidScope_IsUnauthorized_EvenForReads(string key)
    {
        var mediator = new Mock<IMediator>();
        await using var app = await StartAsync(mediator);

        var response = await SendAsync(app, HttpMethod.Get, "/beacon/api/projects", key);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        mediator.Invocations.Should().BeEmpty();
        _apiKeys!.Verify(x => x.UpdateLastUsedAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task EveryGetAReadScopedKeyCanReach_IsListed()
    {
        // The read surface of a Read-scoped key. A new GET route under /beacon/api lands here and fails this test until
        // the list is updated on purpose: if the route runs SQL, dials a data source, makes an outbound call or calls the
        // LLM, mark it .RequiresExecuteScope() instead of listing it.
        var mediator = new Mock<IMediator>();
        await using var app = await StartAsync(mediator, realtime: true);

        var reachable = new List<string>();
        foreach (var endpoint in BeaconApiEndpoints(app).Where(x => Methods(x).Contains("GET")))
        {
            var response = await SendAsync(app, HttpMethod.Get, Path(endpoint), ReadKey);
            if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
            {
                reachable.Add(endpoint.RoutePattern.RawText!);
            }
        }

        reachable.Distinct().Order(StringComparer.Ordinal).Should().Equal(ReadScopeSurface);
    }

    [Test]
    public async Task Hub_IsClosedToKeys_WhateverTheirScope()
    {
        // The hub is mapped outside the /beacon/api group and authenticates with the cookie scheme only, so the key
        // principal never reaches it: no key opens a real-time connection.
        await using var app = await StartAsync(new Mock<IMediator>(), realtime: true);

        var readKey = await SendAsync(app, HttpMethod.Post, "/beacon/api/hub/negotiate?negotiateVersion=1", ReadKey);
        var executeKey = await SendAsync(app, HttpMethod.Post, "/beacon/api/hub/negotiate?negotiateVersion=1", ExecuteKey);

        readKey.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        executeKey.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Csrf_IsNotIssuedToAnApiKeyCaller()
    {
        await using var app = await StartAsync(new Mock<IMediator>());

        var response = await SendAsync(app, HttpMethod.Get, "/beacon/api/csrf", ExecuteKey);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Headers.Contains("Set-Cookie").Should().BeFalse("no antiforgery cookie is bound to a key identity");
    }

    [Test]
    public async Task Csrf_IsNotIssuedToABearerCaller()
    {
        await using var app = await StartAsync(new Mock<IMediator>());

        var response = await SendAsync(app, HttpMethod.Get, "/beacon/api/csrf", principal: "jwt");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
    }

    [Test]
    public async Task Csrf_IsStillIssuedToACookieSession()
    {
        await using var app = await StartAsync(new Mock<IMediator>());

        var response = await SendAsync(app, HttpMethod.Get, "/beacon/api/csrf", principal: "cookie");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<CsrfBody>())!.Token.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task BearerCaller_MutationWithoutToken_SkipsAntiforgery()
    {
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(x => x.Send(It.IsAny<UpdateQueryCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpdateQueryResult { QueryId = 9, Success = true });
        await using var app = await StartAsync(mediator);

        var response = await SendAsync(app, HttpMethod.Put, "/beacon/api/queries/9", json: "{\"name\":\"daily\",\"steps\":[]}", principal: "jwt");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task CookieSession_MutationWithoutToken_IsStillRejected()
    {
        var mediator = new Mock<IMediator>();
        await using var app = await StartAsync(mediator);

        var response = await SendAsync(app, HttpMethod.Put, "/beacon/api/queries/9", json: "{}", principal: "cookie");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        mediator.Invocations.Should().BeEmpty();
    }

    [Test]
    public async Task CookieSession_MutationWithToken_Passes()
    {
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(x => x.Send(It.IsAny<UpdateQueryCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpdateQueryResult { QueryId = 9, Success = true });
        await using var app = await StartAsync(mediator);

        var response = await SendAsync(app, HttpMethod.Put, "/beacon/api/queries/9", json: "{\"name\":\"daily\",\"steps\":[]}", principal: "cookie", withAntiforgeryToken: true);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [TestCase("cookie-with-bearer-markers")]
    [TestCase("cookie-with-bearer-markers-and-header")]
    public async Task CookieSessionCarryingBearerMarkers_IsStillAntiforgeryChecked(string principal)
    {
        var mediator = new Mock<IMediator>();
        await using var app = await StartAsync(mediator);

        var response = await SendAsync(app, HttpMethod.Put, "/beacon/api/queries/9", json: "{}", principal: principal);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "only the identities the header middlewares build skip the check");
        mediator.Invocations.Should().BeEmpty();
    }

    [TestCase("apikey-identity-without-header", null)]
    [TestCase("bearer-identity-jwt", null)]
    [TestCase("bearer-identity-jwt", "Basic dXNlcjpwYXNz")]
    [TestCase("cookie-identity-apikey-marker", "Bearer header.payload.signature")]
    public async Task ForgedHeaderAuthentication_IsStillAntiforgeryChecked(string principal, string? authorization)
    {
        // Each principal carries the Execute scope where it is scoped, so the scope gate passes and the request reaches
        // the antiforgery check — which only the identity a header middleware builds, with its marker and its header, skips.
        var mediator = new Mock<IMediator>();
        await using var app = await StartAsync(mediator);

        var response = await SendAsync(app, HttpMethod.Put, "/beacon/api/queries/9", json: "{}", principal: principal, authorization: authorization);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        mediator.Invocations.Should().BeEmpty();
    }

    [Test]
    public async Task UnauthenticatedIdentityWithMarkersAndHeader_IsUnauthorized()
    {
        var mediator = new Mock<IMediator>();
        await using var app = await StartAsync(mediator);

        var response = await SendAsync(app, HttpMethod.Put, "/beacon/api/queries/9", json: "{}", principal: "unauthenticated-with-markers", authorization: "Bearer header.payload.signature");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        mediator.Invocations.Should().BeEmpty();
    }

    [Test]
    public async Task McpCallerWithExecute_MutationWithoutToken_SkipsAntiforgery()
    {
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(x => x.Send(It.IsAny<UpdateQueryCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpdateQueryResult { QueryId = 9, Success = true });
        await using var app = await StartAsync(mediator);

        var response = await SendAsync(app, HttpMethod.Put, "/beacon/api/queries/9", json: "{\"name\":\"daily\",\"steps\":[]}", principal: "mcp-caller-execute", authorization: "Bearer header.payload.signature");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task Csrf_IsIssuedToACookieSession_EvenWithABearerHeader()
    {
        await using var app = await StartAsync(new Mock<IMediator>());

        var response = await SendAsync(app, HttpMethod.Get, "/beacon/api/csrf", principal: "cookie", authorization: "Bearer header.payload.signature");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<CsrfBody>())!.Token.Should().NotBeNullOrEmpty();
    }

    private static IEnumerable<RouteEndpoint> BeaconApiEndpoints(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(x => x.RoutePattern.RawText?.StartsWith("/beacon/api/", StringComparison.Ordinal) == true);

    private static IReadOnlyList<string> Methods(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];

    private static string Path(RouteEndpoint endpoint) =>
        Regex.Replace(endpoint.RoutePattern.RawText!, "\\{[^}]+\\}", "1");

    private static Task<HttpResponseMessage> SendAsync(
        WebApplication app,
        HttpMethod method,
        string path,
        string? apiKey = null,
        string? json = null,
        string? principal = null,
        bool withAntiforgeryToken = false,
        string? authorization = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (apiKey != null)
        {
            request.Headers.Add("Authorization", $"Bearer {apiKey}");
        }

        if (authorization != null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        if (principal != null)
        {
            request.Headers.Add(TestPrincipalHeader, principal);
            if (principal is "jwt" or "cookie-with-bearer-markers-and-header")
            {
                request.Headers.Add("Authorization", "Bearer header.payload.signature");
            }
        }

        if (withAntiforgeryToken)
        {
            var tokens = app.Services
                .GetRequiredService<IAntiforgery>()
                .GetAndStoreTokens(new DefaultHttpContext { RequestServices = app.Services, User = Principal("cookie") });
            request.Headers.Add("Cookie", $"{AntiforgeryCookieName}={tokens.CookieToken}");
            request.Headers.Add("X-XSRF-TOKEN", tokens.RequestToken);
        }

        if (json != null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return app.GetTestClient().SendAsync(request);
    }

    // Stand-ins for the identities the cookie and JWT middlewares build, and forgeries of them.
    private static ClaimsPrincipal Principal(string kind) => kind switch
    {
        "cookie" => new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "ext-ada")], "Beacon.Auth")),
        "jwt" or "bearer-identity-jwt" => new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "sub-ada"), new Claim("auth_method", "jwt")],
            "Bearer")),
        "apikey-identity-without-header" => new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "7"), new Claim("auth_method", "api_key"), new Claim("scope", "Execute")],
            "ApiKey")),
        "cookie-identity-apikey-marker" => new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "ext-ada"), new Claim("auth_method", "api_key"), new Claim("scope", "Execute")],
            "Beacon.Auth")),
        "unauthenticated-with-markers" => new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "7"), new Claim("auth_method", "api_key"), new Claim("scope", "Execute")])),
        "mcp-caller-execute" => new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "7"), new Claim("auth_method", "mcp_caller"), new Claim("scope", "Execute")],
            "Bearer")),
        _ => new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "ext-ada"), new Claim("auth_method", "jwt")],
            "Beacon.Auth"))
    };

    // The authorization provider grants write permission unless a test brings its own.
    private async Task<WebApplication> StartAsync(
        Mock<IMediator> mediator,
        bool realtime = false,
        LogRecorder? logs = null,
        Mock<IBeaconAuthorizationProvider>? authorization = null)
    {
        if (authorization == null)
        {
            authorization = new Mock<IBeaconAuthorizationProvider>();
            authorization
                .Setup(x => x.HasWritePermissionAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
        }

        var apiKeys = new Mock<IApiKeyService>();
        SetupKey(apiKeys, ReadKey, 3, "[\"Read\"]");
        SetupKey(apiKeys, ExecuteKey, 4, "[\"Execute\"]");
        SetupKey(apiKeys, LegacyAdminKey, 5, "[\"Admin\"]");
        SetupKey(apiKeys, ReadAndUnknownKey, 6, "[\"Read\",\"Write\",\"execute\"]");
        SetupKey(apiKeys, ViewerExecuteKey, 8, "[\"Execute\"]", Owner(RoleService.RoleLevels.Viewer));
        SetupKey(apiKeys, SuperAdminExecuteKey, 9, "[\"Execute\"]", Owner(roleLevel: null, isSuperAdmin: true));
        SetupKey(apiKeys, NullScopesKey, 10, null);
        SetupKey(apiKeys, EmptyScopesKey, 11, "[]");
        SetupKey(apiKeys, UnknownScopesKey, 12, "[\"Write\",\"execute\"]");
        SetupKey(apiKeys, MalformedScopesKey, 13, "not json");
        _apiKeys = apiKeys;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        if (logs != null)
        {
            builder.Logging.AddProvider(logs);
        }

        builder.Services.AddSingleton(mediator.Object);
        builder.Services.AddSingleton(apiKeys.Object);
        builder.Services.AddSingleton(new BeaconConfiguration());
        builder.Services.AddSingleton(Mock.Of<IActorUserResolver>());
        builder.Services.AddSingleton(Mock.Of<IBeaconAuthenticationProvider>());
        builder.Services.AddSingleton(authorization.Object);
        builder.Services.AddSingleton(Mock.Of<IBeaconUserContext>());
        builder.Services.AddSingleton(Mock.Of<IRoleService>());
        builder.Services.AddSingleton(Mock.Of<IUserManagementService>());
        builder.Services.AddSingleton(Mock.Of<IDbContextFactory<BeaconContext>>());
        builder.Services.AddSingleton(new BeaconApiOptions { Realtime = realtime });
        if (realtime)
        {
            builder.Services.AddSignalR();
            builder.Services.AddAuthentication().AddCookie();
        }

        builder.Services.AddRouting();
        builder.Services.AddAntiforgery(x =>
        {
            x.Cookie.Name = AntiforgeryCookieName;
            x.HeaderName = "X-XSRF-TOKEN";
        });
        builder.Services.AddBeaconApiAuthorization();
        builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, StatusCodeResultHandler>();

        var app = builder.Build();

        // Stands in for the host's exception handler: a handler that throws on the mocked mediator's default answer
        // still shows the request got past every gate.
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (Exception) when (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            }
        });
        app.UseMiddleware<ApiKeyAuthMiddleware>();
        app.Use((context, next) =>
        {
            if (context.Request.Headers.TryGetValue(TestPrincipalHeader, out var kind))
            {
                context.User = Principal(kind.ToString());
            }

            return next(context);
        });
        app.UseRouting();
        app.UseAuthorization();
        app.MapBeaconApi();
        await app.StartAsync();

        return app;
    }

    private static void SetupKey(Mock<IApiKeyService> apiKeys, string key, int id, string? scopes, BeaconUser? owner = null)
    {
        apiKeys
            .Setup(x => x.ValidateApiKeyAsync(key, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiKeyCredential
            {
                Id = id,
                UserId = 7,
                Name = "integration",
                KeyHash = "hash",
                KeyPrefix = key[..12],
                Scopes = scopes,
                AllowedProjectIds = "[1]",
                User = owner ?? Owner(RoleService.RoleLevels.Editor)
            });
    }

    // The key's owner as the key store loads it: with their roles, which decide whether Execute is kept.
    private static BeaconUser Owner(int? roleLevel, bool isSuperAdmin = false) =>
        new()
        {
            Id = 7,
            UserName = "ada",
            ExternalId = "ext-ada",
            IsEnabled = true,
            IsSuperAdmin = isSuperAdmin,
            UserRoles = roleLevel == null
                ? []
                : [new BeaconUserRole { UserId = 7, Role = new BeaconRole { Name = "role", Level = roleLevel.Value } }]
        };

    private sealed record CsrfBody(string Token);

    /// <summary>Writes 401/403 directly so no authentication scheme is needed for Forbid/Challenge.</summary>
    private sealed class StatusCodeResultHandler : IAuthorizationMiddlewareResultHandler
    {
        public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
        {
            if (authorizeResult.Succeeded)
            {
                return next(context);
            }

            context.Response.StatusCode = authorizeResult.Forbidden
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }
    }
}
