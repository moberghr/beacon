using System.Security.Claims;
using System.Text.Encodings.Web;
using Beacon.Api;
using Beacon.Api.Authentication;
using Beacon.Api.Endpoints;
using Beacon.Core;
using Beacon.Core.Authentication;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Entities;
using Beacon.Core.Mcp;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services;
using Beacon.Core.Services.Embed;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Beacon.Tests.Unit.Embed;

/// <summary>
/// A minimal host wired as a Beacon host is: <c>UseBeaconAuthentication</c> with the host's own default scheme (which
/// authenticates any request carrying <see cref="HostUserHeader"/>), Beacon's cookie and API keys, and embedding on over
/// <see cref="EmbedTestKit"/>'s store. It maps either echo routes at the embed paths (what the middleware decided) or
/// Beacon's own embed endpoints over a mediator the test supplies. No database (§4.7).
/// </summary>
internal sealed class EmbedSurfaceHost : IAsyncDisposable
{
    public const string HostScheme = "Host";
    public const string HostUserHeader = "X-Host-User";
    public const string ApiKey = "sk-sem_embed-surface-key";

    private readonly WebApplication _app;

    private EmbedSurfaceHost(WebApplication app, RecordingSecurityAudit audit, LogRecorder logs)
    {
        _app = app;
        Audit = audit;
        Logs = logs;
        Client = app.GetTestClient();
    }

    public HttpClient Client { get; }

    public RecordingSecurityAudit Audit { get; }

    public LogRecorder Logs { get; }

    public static async Task<EmbedSurfaceHost> StartAsync(
        EmbedTestKit kit,
        IMediator? mediator = null,
        string? webRoot = null,
        bool gatewayMode = false,
        string? pathBase = null)
    {
        var configuration = new BeaconConfiguration();
        configuration.UserManagement.Enabled = true;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
            WebRootPath = webRoot
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var logs = new LogRecorder();
        builder.Logging.AddProvider(logs);
        builder.Logging.SetMinimumLevel(LogLevel.Trace);

        builder.Services.AddOptions<AuthenticationPathOptions>();
        builder.Services.AddSingleton<AuthenticationPathPrefixes>();
        builder.Services.AddSingleton(configuration);
        builder.Services.AddSingleton(Mock.Of<IUserManagementService>());
        builder.Services.AddSingleton<TimeProvider>(kit.Clock);
        builder.Services.AddSingleton<IUserSessionStore>(new Unit.BeaconAuthenticationScopeTests.CountingSessionStore());
        builder.Services.AddSingleton(Options.Create(new SessionLifetimeOptions { RevalidateMinutes = 0, AbsoluteLifetimeHours = 12 }));
        builder.Services.AddSingleton(ApiKeys().Object);
        builder.Services.AddSingleton(Mock.Of<IMcpCallerMapper>());
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddBeaconCookieAuthentication("/beacon");
        builder.Services.AddAuthentication(x =>
            {
                x.DefaultScheme = HostScheme;
                x.DefaultChallengeScheme = HostScheme;
                x.DefaultForbidScheme = HostScheme;
            })
            .AddScheme<AuthenticationSchemeOptions, HostAuthenticationHandler>(HostScheme, null);
        builder.Services.AddAuthorization();

        // Embedding, over the kit's store and clock.
        var audit = new RecordingSecurityAudit();
        builder.Services.AddSingleton(kit.EmbedOptions());
        builder.Services.AddSingleton(new BeaconApiOptions { Realtime = false, GatewayMode = gatewayMode });
        builder.Services.AddSingleton(kit.Codec);
        builder.Services.AddScoped(_ => kit.Resolver());
        builder.Services.AddSingleton<EmbedRateLimiter>();
        builder.Services.AddSingleton<ISecurityAuditRecorder>(audit);
        if (mediator != null)
        {
            builder.Services.AddSingleton(mediator);
        }

        var app = builder.Build();
        if (pathBase != null)
        {
            app.UsePathBase(pathBase);
        }

        app.UseRouting();
        app.UseBeaconAuthentication();
        app.UseAuthorization();

        if (mediator != null)
        {
            app.MapEmbedEndpoints();
        }
        else
        {
            app.MapMethods(BeaconEmbedPaths.TokensPath, ["GET", "POST"], WhoAmI);
            app.MapMethods(BeaconEmbedPaths.RenderPath, ["GET", "POST"], WhoAmI);
            app.MapGet(BeaconEmbedPaths.FramePrefix + "/{publicId}", WhoAmI);
        }

        app.MapGet("/api/orders", WhoAmI);
        app.MapGet("/beacon/api/whoami", WhoAmI);
        app.MapGet("/beacon/api/test/sign-in", async (HttpContext context) =>
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "ana"), new Claim(ClaimTypes.Name, "ana")],
                CookieAuthenticationDefaults.AuthenticationScheme));
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        });

        await app.StartAsync();

        return new EmbedSurfaceHost(app, audit, logs);
    }

    public async Task<string> SignInAsync()
    {
        var response = await Client.GetAsync("/beacon/api/test/sign-in");

        return response.Headers.GetValues("Set-Cookie")
            .Single(x => x.StartsWith("Beacon.Auth=", StringComparison.Ordinal))
            .Split(';')[0];
    }

    public Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string? authorization = null,
        string? frameMarker = null,
        string? origin = null,
        string? cookie = null,
        bool hostUser = false)
    {
        var request = new HttpRequestMessage(method, path);
        if (authorization != null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        if (frameMarker != null)
        {
            request.Headers.TryAddWithoutValidation(EmbedTokenAuthMiddleware.FrameHeader, frameMarker);
        }

        if (origin != null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        if (cookie != null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        if (hostUser)
        {
            request.Headers.TryAddWithoutValidation(HostUserHeader, "host-user");
        }

        if (method == HttpMethod.Post)
        {
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        }

        return Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
    }

    // Who the request was authenticated as, by whom, and what the embed middleware verified.
    private static string WhoAmI(HttpContext context)
    {
        var feature = context.Features.Get<EmbedRequestFeature?>();

        return string.Join(
            "|",
            context.User.Identity?.AuthenticationType ?? "anonymous",
            context.User.FindFirst(McpCallerClaimTypes.AuthMethod)?.Value ?? "-",
            context.User.FindFirst(McpCallerClaimTypes.EmbedGrantId)?.Value ?? "-",
            feature == null ? "-" : string.Join(",", feature.Parameters.Select(x => $"{x.Key}={x.Value}")));
    }

    private static Mock<IApiKeyService> ApiKeys()
    {
        var apiKeys = new Mock<IApiKeyService>();
        apiKeys
            .Setup(x => x.ValidateApiKeyAsync(ApiKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiKeyCredential
            {
                Id = 3,
                UserId = 7,
                Name = "embed",
                KeyHash = "hash",
                KeyPrefix = ApiKey[..12],
                Scopes = "[\"Read\"]",
                User = new BeaconUser { Id = 7, UserName = "ana", ExternalId = "ana", IsEnabled = true }
            });

        return apiKeys;
    }

    // The host's own scheme: a request with the host's header is its signed-in user.
    private sealed class HostAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey(HostUserHeader))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "host-user")], HostScheme));

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, HostScheme)));
        }
    }
}
