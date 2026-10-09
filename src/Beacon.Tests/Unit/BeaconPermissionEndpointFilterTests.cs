using System.Net;
using System.Security.Claims;
using System.Text;
using Beacon.Api;
using Beacon.Api.Authentication;
using Beacon.Api.Endpoints;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.Providers;
using Beacon.Core.Data;
using Beacon.Core.Handlers.Queries;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// SC3 — server-side Viewer/Editor enforcement on <c>/beacon/api</c>. The filter is driven directly with a
/// <see cref="DefaultHttpContext"/> and a mocked <see cref="IBeaconAuthorizationProvider"/>; the handler delegate
/// records whether it ran, so every deny also proves the endpoint was never reached.
/// </summary>
[TestFixture]
public class BeaconPermissionEndpointFilterTests
{
    private const string TestUserHeader = "X-Test-User";
    private const string AntiforgeryCookieName = "test.antiforgery";
    private const string DraftBody =
        "{\"draft\":{\"steps\":[{\"stepOrder\":1,\"name\":\"s\",\"sqlValue\":\"SELECT 1\",\"dataSourceId\":1}]}}";

    private static readonly ClaimsPrincipal CookieUser = new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())],
        "Cookies"));

    private Mock<IBeaconAuthorizationProvider> _authorization = null!;

    [SetUp]
    public void SetUp()
    {
        _authorization = new Mock<IBeaconAuthorizationProvider>();
    }

    [Test]
    public async Task Inactive_WhenAuthorizationAndUserManagementAreBothOff()
    {
        GrantPermissions(read: false, write: false);
        var context = CreateContext(HttpMethods.Delete, CookieUser, authorizationEnabled: false, userManagementEnabled: false);

        var outcome = await InvokeAsync(context);

        outcome.NextInvoked.Should().BeTrue("enforcement is opt-in through Authorization.Enabled or UserManagement.Enabled");
        _authorization.VerifyNoOtherCalls();
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task Active_WhenEitherFlagIsOn(bool authorizationEnabled, bool userManagementEnabled)
    {
        GrantPermissions(read: false, write: false);
        var context = CreateContext(HttpMethods.Get, CookieUser, authorizationEnabled, userManagementEnabled);

        var outcome = await InvokeAsync(context);

        outcome.NextInvoked.Should().BeFalse();
        AssertForbidden(outcome.Result);
    }

    [Test]
    public async Task DefaultProvider_WithEnforcementOn_LogsOneWarningPerHost_WithoutRequestData()
    {
        var logger = new RecordingLogger();
        var configuration = new BeaconConfiguration();
        configuration.Authorization.Enabled = true;
        var first = CreateContext(HttpMethods.Get, CookieUser, authorizationEnabled: true, userManagementEnabled: false, provider: new DefaultAuthorizationProvider(), configuration: configuration);
        var second = CreateContext(HttpMethods.Post, CookieUser, authorizationEnabled: true, userManagementEnabled: false, provider: new DefaultAuthorizationProvider(), configuration: configuration);

        var firstOutcome = await InvokeAsync(first, logger);
        var secondOutcome = await InvokeAsync(second, logger);

        firstOutcome.NextInvoked.Should().BeTrue("the allow-all default provider lets the call through");
        secondOutcome.NextInvoked.Should().BeTrue();
        logger.Warnings.Should().ContainSingle()
            .Which.Should().Be("Beacon authorization is enabled but no authorization provider is configured; all authenticated callers are allowed.");
    }

    [Test]
    public async Task ConfiguredProvider_WithEnforcementOn_LogsNoWarning()
    {
        GrantPermissions(read: true, write: true);
        var logger = new RecordingLogger();
        var context = CreateContext(HttpMethods.Get, CookieUser);

        await InvokeAsync(context, logger);

        logger.Warnings.Should().BeEmpty();
    }

    [Test]
    public async Task AllowAnonymousEndpoint_IsNeverPermissionChecked()
    {
        GrantPermissions(read: false, write: false);
        var context = CreateContext(HttpMethods.Post, new ClaimsPrincipal(new ClaimsIdentity()), metadata: new AllowAnonymousAttribute());

        var outcome = await InvokeAsync(context);

        outcome.NextInvoked.Should().BeTrue();
        _authorization.VerifyNoOtherCalls();
    }

    [TestCase("GET")]
    [TestCase("HEAD")]
    [TestCase("OPTIONS")]
    public async Task SafeMethod_NeedsOnlyReadPermission(string method)
    {
        GrantPermissions(read: true, write: false);
        var context = CreateContext(method, CookieUser);

        var outcome = await InvokeAsync(context);

        outcome.NextInvoked.Should().BeTrue();
        _authorization.Verify(x => x.HasReadPermissionAsync(It.IsAny<CancellationToken>()), Times.Once);
        _authorization.Verify(x => x.HasWritePermissionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Get_WithoutReadPermission_IsForbidden()
    {
        GrantPermissions(read: false, write: true);
        var context = CreateContext(HttpMethods.Get, CookieUser);

        var outcome = await InvokeAsync(context);

        outcome.NextInvoked.Should().BeFalse();
        AssertForbidden(outcome.Result);
    }

    [TestCase("POST")]
    [TestCase("PUT")]
    [TestCase("PATCH")]
    [TestCase("DELETE")]
    public async Task MutatingMethod_WithReadOnlyPermission_IsForbidden(string method)
    {
        GrantPermissions(read: true, write: false);
        var context = CreateContext(method, CookieUser);

        var outcome = await InvokeAsync(context);

        outcome.NextInvoked.Should().BeFalse("a Viewer must not reach a mutating endpoint");
        AssertForbidden(outcome.Result);
        _authorization.Verify(x => x.HasWritePermissionAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestCase("POST")]
    [TestCase("PUT")]
    [TestCase("DELETE")]
    public async Task MutatingMethod_WithWritePermission_Passes(string method)
    {
        GrantPermissions(read: true, write: true);
        var context = CreateContext(method, CookieUser);

        var outcome = await InvokeAsync(context);

        outcome.NextInvoked.Should().BeTrue();
        outcome.Result.Should().Be("next");
    }

    [Test]
    public async Task ViewerAccessEndpoint_PostNeedsOnlyReadPermission()
    {
        GrantPermissions(read: true, write: false);
        var context = CreateContext(HttpMethods.Post, CookieUser, metadata: BeaconViewerAccessMetadata.Instance);

        var outcome = await InvokeAsync(context);

        outcome.NextInvoked.Should().BeTrue();
        _authorization.Verify(x => x.HasWritePermissionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ViewerAccessEndpoint_WithoutReadPermission_IsForbidden()
    {
        GrantPermissions(read: false, write: false);
        var context = CreateContext(HttpMethods.Post, CookieUser, metadata: BeaconViewerAccessMetadata.Instance);

        var outcome = await InvokeAsync(context);

        outcome.NextInvoked.Should().BeFalse();
        AssertForbidden(outcome.Result);
    }

    [Test]
    public async Task ReadScopedApiKey_OnMutatingRequest_IsForbiddenEvenWithWritePermission()
    {
        GrantPermissions(read: true, write: true);
        var context = CreateContext(HttpMethods.Put, ScopedCaller("api_key", "Read"));

        var outcome = await InvokeAsync(context);

        outcome.NextInvoked.Should().BeFalse("a Read-scoped key must not write even when its user is an Editor (§1.4)");
        AssertForbidden(outcome.Result);
    }

    [Test]
    public async Task ReadScopedMcpCaller_OnMutatingRequest_IsForbidden()
    {
        GrantPermissions(read: true, write: true);
        var context = CreateContext(HttpMethods.Post, ScopedCaller("mcp_caller", "Read"));

        var outcome = await InvokeAsync(context);

        outcome.NextInvoked.Should().BeFalse();
        AssertForbidden(outcome.Result);
    }

    [TestCase("Execute")]
    [TestCase("Admin")]
    public async Task WriteScopedApiKey_OnMutatingRequest_Passes(string scope)
    {
        GrantPermissions(read: true, write: true);
        var context = CreateContext(HttpMethods.Put, ScopedCaller("api_key", scope));

        var outcome = await InvokeAsync(context);

        outcome.NextInvoked.Should().BeTrue();
    }

    [Test]
    public async Task WriteScopedApiKey_WithoutWritePermission_IsForbidden()
    {
        GrantPermissions(read: true, write: false);
        var context = CreateContext(HttpMethods.Put, ScopedCaller("api_key", "Execute"));

        var outcome = await InvokeAsync(context);

        outcome.NextInvoked.Should().BeFalse("the scope is a ceiling, not a grant — the user's role still decides");
        AssertForbidden(outcome.Result);
    }

    [Test]
    public async Task ReadScopedApiKey_OnReadRequest_Passes()
    {
        GrantPermissions(read: true, write: false);
        var context = CreateContext(HttpMethods.Get, ScopedCaller("api_key", "Read"));

        var outcome = await InvokeAsync(context);

        outcome.NextInvoked.Should().BeTrue();
    }

    [Test]
    public async Task Denial_IsAnRfc7807ProblemWithStatus403()
    {
        GrantPermissions(read: true, write: false);
        var context = CreateContext(HttpMethods.Delete, CookieUser);
        context.Response.Body = new MemoryStream();

        var outcome = await InvokeAsync(context);
        await ((IResult)outcome.Result!).ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        context.Response.ContentType.Should().StartWith("application/problem+json");
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().Contain("\"title\":\"Forbidden\"");
    }

    [Test]
    public async Task AllowViewerAccess_MarksExactlyTheViewerEndpoints()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Services.AddSingleton(Mock.Of<IMediator>());
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        var group = app.MapGroup("/beacon/api");
        group.MapQueriesEndpoints();
        group.MapUserSettingsEndpoints();

        var viewerEndpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(x => x.Endpoints)
            .Where(x => x.Metadata.GetMetadata<BeaconViewerAccessMetadata>() != null)
            .Select(x => x.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName)
            .ToList();

        viewerEndpoints.Should().BeEquivalentTo("ExecuteQueryPreview", "ExecuteStepPreview", "ChangeOwnPassword");
    }

    // The tests above drive the filter directly; these prove MapBeaconApi actually puts it on the group and that it
    // composes with the group's auth policy and antiforgery filter, over a real request pipeline (TestServer, no DB).
    [Test]
    public async Task MapBeaconApi_Viewer_CanReadAndRunPreviews_ButCannotWrite()
    {
        GrantPermissions(read: true, write: false);
        var mediator = new Mock<IMediator>();
        await using var app = await StartBeaconApiAsync(mediator.Object);

        var read = await SendAsync(app, HttpMethod.Get, "/beacon/api/queries/1");
        var preview = await SendAsync(app, HttpMethod.Post, "/beacon/api/queries/1/preview");
        var write = await SendAsync(app, HttpMethod.Post, "/beacon/api/queries/1/lock", "{\"lock\":true}");

        read.StatusCode.Should().Be(HttpStatusCode.OK);
        preview.StatusCode.Should().Be(HttpStatusCode.OK, "Viewers may run existing queries");
        write.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        write.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        mediator.Invocations
            .Select(x => x.Arguments[0])
            .OfType<ToggleQueryLockCommand>()
            .Should()
            .BeEmpty("a denied request must never reach its handler");
    }

    [TestCase("/beacon/api/queries/1/preview")]
    [TestCase("/beacon/api/queries/1/steps/1/preview")]
    public async Task MapBeaconApi_Viewer_CanRunTheStoredQuery_ButNotADraft(string path)
    {
        GrantPermissions(read: true, write: false);
        var mediator = new Mock<IMediator>();
        await using var app = await StartBeaconApiAsync(mediator.Object);

        var stored = await SendAsync(app, HttpMethod.Post, path, "{\"draft\":null}");
        var draft = await SendAsync(app, HttpMethod.Post, path, DraftBody);

        stored.StatusCode.Should().Be(HttpStatusCode.OK, "Viewers may run existing queries");
        draft.StatusCode.Should().Be(HttpStatusCode.Forbidden, "a draft is unsaved SQL, which needs write permission");
        mediator.Invocations.Should().ContainSingle("the draft request must never reach its handler");
    }

    [Test]
    public async Task MapBeaconApi_Editor_CanRunADraft()
    {
        GrantPermissions(read: true, write: true);
        var mediator = new Mock<IMediator>();
        await using var app = await StartBeaconApiAsync(mediator.Object);

        var response = await SendAsync(app, HttpMethod.Post, "/beacon/api/queries/1/preview", DraftBody);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        mediator.Invocations
            .Select(x => x.Arguments[0])
            .OfType<ExecuteQueryPreviewCommand>()
            .Should()
            .ContainSingle(x => x.Draft != null);
    }

    [Test]
    public async Task MapBeaconApi_Editor_CanWrite()
    {
        GrantPermissions(read: true, write: true);
        var mediator = new Mock<IMediator>();
        await using var app = await StartBeaconApiAsync(mediator.Object);

        var write = await SendAsync(app, HttpMethod.Post, "/beacon/api/queries/1/lock", "{\"lock\":true}");

        write.StatusCode.Should().Be(HttpStatusCode.OK);
        mediator.Invocations
            .Select(x => x.Arguments[0])
            .OfType<ToggleQueryLockCommand>()
            .Should()
            .ContainSingle();
    }

    [Test]
    public async Task MapBeaconApi_UnauthenticatedWrite_IsRejectedByTheGroupPolicy_BeforeTheProvider()
    {
        GrantPermissions(read: true, write: true);
        var mediator = new Mock<IMediator>();
        await using var app = await StartBeaconApiAsync(mediator.Object);

        var response = await app.GetTestClient().PostAsync(
            "/beacon/api/queries/1/lock",
            new StringContent("{\"lock\":true}", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _authorization.VerifyNoOtherCalls();
        mediator.Invocations.Should().BeEmpty();
    }

    [Test]
    public async Task MapBeaconApi_AnonymousEndpoint_IsNotPermissionChecked()
    {
        GrantPermissions(read: false, write: false);
        await using var app = await StartBeaconApiAsync(Mock.Of<IMediator>());

        var response = await app.GetTestClient().GetAsync("/beacon/api/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _authorization.VerifyNoOtherCalls();
    }

    [Test]
    public async Task MapBeaconApi_UserWithoutRole_LearnsItHasNoPermissions_InsteadOfA403()
    {
        GrantPermissions(read: false, write: false);
        await using var app = await StartBeaconApiAsync(Mock.Of<IMediator>());

        var permissions = await SendAsync(app, HttpMethod.Get, "/beacon/api/auth/permissions");
        var read = await SendAsync(app, HttpMethod.Get, "/beacon/api/queries/1");

        permissions.StatusCode.Should().Be(HttpStatusCode.OK, "the shell needs the answer to explain that no role is assigned");
        (await permissions.Content.ReadAsStringAsync()).Should().Contain("\"canRead\":false").And.Contain("\"canWrite\":false");
        read.StatusCode.Should().Be(HttpStatusCode.Forbidden, "a user without a role still reads nothing");
    }

    [Test]
    public async Task MapBeaconApi_PermissionsEndpoint_StillRequiresAnAuthenticatedUser()
    {
        GrantPermissions(read: true, write: true);
        await using var app = await StartBeaconApiAsync(Mock.Of<IMediator>());

        var response = await app.GetTestClient().GetAsync("/beacon/api/auth/permissions");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _authorization.VerifyNoOtherCalls();
    }

    private async Task<WebApplication> StartBeaconApiAsync(IMediator mediator)
    {
        var configuration = new BeaconConfiguration();
        configuration.UserManagement.Enabled = true;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(configuration);
        builder.Services.AddSingleton(new BeaconApiOptions { Realtime = false });
        builder.Services.AddSingleton(mediator);
        builder.Services.AddSingleton(_authorization.Object);
        builder.Services.AddSingleton(Mock.Of<IBeaconUserContext>());
        builder.Services.AddSingleton(Mock.Of<IActorUserResolver>());
        builder.Services.AddSingleton(Mock.Of<IDbContextFactory<BeaconContext>>());
        builder.Services.AddAntiforgery(x =>
        {
            x.Cookie.Name = AntiforgeryCookieName;
            x.HeaderName = "X-XSRF-TOKEN";
        });
        builder.Services.AddBeaconApiAuthorization();
        // Only to give an unauthenticated request a challenge to answer: the host's cookie scheme returns 401 for the API.
        builder.Services
            .AddAuthentication("Test")
            .AddCookie("Test", x => x.Events.OnRedirectToLogin = y =>
            {
                y.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            });

        var app = builder.Build();
        // Stands in for the host's auth middlewares: the test user is authenticated before UseAuthorization runs.
        app.Use((context, next) =>
        {
            if (context.Request.Headers.ContainsKey(TestUserHeader))
            {
                context.User = CookieUser;
            }

            return next(context);
        });
        app.UseAuthorization();
        app.MapBeaconApi();
        await app.StartAsync();

        return app;
    }

    // Authenticated request with a valid antiforgery pair, minted for the same user the way GET /beacon/api/csrf does.
    private static Task<HttpResponseMessage> SendAsync(WebApplication app, HttpMethod method, string path, string? json = null)
    {
        var tokens = app.Services
            .GetRequiredService<IAntiforgery>()
            .GetAndStoreTokens(new DefaultHttpContext { RequestServices = app.Services, User = CookieUser });

        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(TestUserHeader, "1");
        request.Headers.Add("Cookie", $"{AntiforgeryCookieName}={tokens.CookieToken}");
        request.Headers.Add("X-XSRF-TOKEN", tokens.RequestToken);
        if (json != null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return app.GetTestClient().SendAsync(request);
    }

    private void GrantPermissions(bool read, bool write)
    {
        _authorization
            .Setup(x => x.HasReadPermissionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(read);
        _authorization
            .Setup(x => x.HasWritePermissionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(write);
    }

    private DefaultHttpContext CreateContext(
        string method,
        ClaimsPrincipal user,
        bool authorizationEnabled = false,
        bool userManagementEnabled = true,
        object? metadata = null,
        IBeaconAuthorizationProvider? provider = null,
        BeaconConfiguration? configuration = null)
    {
        configuration ??= new BeaconConfiguration();
        configuration.Authorization.Enabled = authorizationEnabled;
        configuration.UserManagement.Enabled = userManagementEnabled;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddSingleton(provider ?? _authorization.Object);

        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = user
        };
        context.Request.Method = method;

        var endpointMetadata = metadata == null
            ? new EndpointMetadataCollection()
            : new EndpointMetadataCollection(metadata);
        context.SetEndpoint(new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/beacon/api/things/{id:int}"),
            0,
            endpointMetadata,
            "test"));

        return context;
    }

    private static ClaimsPrincipal ScopedCaller(string authMethod, params string[] scopes)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "7"),
            new("auth_method", authMethod)
        };
        claims.AddRange(scopes.Select(x => new Claim("scope", x)));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "ApiKey"));
    }

    private static async Task<(object? Result, bool NextInvoked)> InvokeAsync(
        HttpContext context,
        ILogger<BeaconPermissionEndpointFilter>? logger = null)
    {
        var filter = new BeaconPermissionEndpointFilter(logger ?? NullLogger<BeaconPermissionEndpointFilter>.Instance);
        var nextInvoked = false;

        var result = await filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(context),
            _ =>
            {
                nextInvoked = true;
                return ValueTask.FromResult<object?>("next");
            });

        return (result, nextInvoked);
    }

    private static void AssertForbidden(object? result)
    {
        var problem = result.Should().BeOfType<ProblemHttpResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        problem.ProblemDetails.Title.Should().Be("Forbidden");
        problem.ContentType.Should().Be("application/problem+json");
    }

    private sealed class RecordingLogger : ILogger<BeaconPermissionEndpointFilter>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
