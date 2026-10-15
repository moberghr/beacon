using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Beacon.Api.Authentication;
using Beacon.Api.Endpoints;
using Beacon.Api.Hubs;
using Beacon.Core;
using Beacon.Core.Authentication;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Entities;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// A realtime hub connection ends when the session would next be checked, on a host where single sign-on is registered
/// too (so the cookie is not the default authentication scheme): the hub is mapped as <c>MapBeaconApi</c> maps it, behind
/// the API-key, authentication, cookie and authorization middleware in their §1.9 order, and a client opens a real
/// WebSocket connection with a signed-in cookie. The connection is closed (allowing the client to reconnect, which checks
/// the session again) once the expiry its connect request reported passes. An API key gets no hub connection at all, and
/// the API-key middleware reports a bounded expiry for whatever it authenticates.
/// </summary>
[TestFixture]
public class RealtimeConnectionExpiryTests
{
    private const string RecordSeparator = "\u001e";
    private const string ApiKey = "sk-sem_realtime-test-key";

    [Test]
    public async Task AHubConnection_IsClosedWhenTheSessionsNextCheckIsDue_WithSingleSignOnRegistered()
    {
        // The cookie scheme's clock runs five minutes behind but three seconds: the connect request's check reports an
        // expiry three seconds from now, on the clock SignalR closes connections by.
        var clock = new OffsetTimeProvider(TimeSpan.FromSeconds(3) - TimeSpan.FromMinutes(5));
        await using var app = await StartAsync(clock);
        var server = app.GetTestServer();
        using var client = server.CreateClient();
        var cookie = await SignInAsync(client);

        var token = await NegotiateAsync(client, cookie);
        var webSocketClient = server.CreateWebSocketClient();
        webSocketClient.ConfigureRequest = x => x.Headers.Cookie = cookie;
        using var socket = await webSocketClient.ConnectAsync(new Uri(server.BaseAddress, $"{BeaconApiEndpoints.HubPath}?id={token}"), CancellationToken.None);
        await SendAsync(socket, "{\"protocol\":\"json\",\"version\":1}" + RecordSeparator);
        var handshake = await ReceiveAsync(socket, TimeSpan.FromSeconds(5));
        var opened = DateTimeOffset.UtcNow;

        var closing = await ReceiveUntilCloseAsync(socket, TimeSpan.FromSeconds(20));
        var closedAfter = DateTimeOffset.UtcNow - opened;

        handshake.Should().StartWith("{}");
        closing.Should().NotBeNull("the connection outlived the expiry its request reported");
        closing!.Should().Contain("\"type\":7").And.Contain("\"allowReconnect\":true", "the client reconnects, through a new check");
        closedAfter.Should().BeGreaterThan(TimeSpan.FromSeconds(1), "the connection stays open until the expiry passes");
    }

    [Test]
    public async Task AnApiKey_GetsNoHubConnection()
    {
        await using var app = await StartAsync(TimeProvider.System);
        using var client = app.GetTestServer().CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BeaconApiEndpoints.HubPath}/negotiate?negotiateVersion=1");
        request.Headers.Add("Authorization", $"Bearer {ApiKey}");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the hub admits cookie sessions only");
    }

    [Test]
    public async Task TheApiKeyMiddleware_ReportsAnExpiryOneSessionCheckAway()
    {
        var now = new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);
        var services = new ServiceCollection()
            .AddSingleton<TimeProvider>(new Microsoft.Extensions.Time.Testing.FakeTimeProvider(now))
            .AddSingleton(Options.Create(new SessionLifetimeOptions { RevalidateMinutes = 5 }))
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = BeaconApiEndpoints.HubPath;
        context.Request.Headers.Authorization = $"Bearer {ApiKey}";
        var middleware = new ApiKeyAuthMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context, ApiKeys().Object, Mock.Of<ILogger<ApiKeyAuthMiddleware>>());

        context.User.Identity!.IsAuthenticated.Should().BeTrue();
        var result = context.Features.Get<IAuthenticateResultFeature>()!.AuthenticateResult!;
        result.Principal.Should().BeSameAs(context.User);
        result.Properties!.ExpiresUtc.Should().Be(now.AddMinutes(5));
    }

    [Test]
    public async Task TheCookieMiddleware_RecordsTheCookiesResult_AsTheRequestsAuthenticationResult()
    {
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "ana")], CookieAuthenticationDefaults.AuthenticationScheme)),
            new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UnixEpoch.AddDays(1) },
            CookieAuthenticationDefaults.AuthenticationScheme);
        var authentication = new Mock<IAuthenticationService>();
        authentication
            .Setup(x => x.AuthenticateAsync(It.IsAny<HttpContext>(), CookieAuthenticationDefaults.AuthenticationScheme))
            .ReturnsAsync(AuthenticateResult.Success(ticket));
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(authentication.Object).BuildServiceProvider()
        };
        context.Request.Path = BeaconApiEndpoints.HubPath;

        await new BeaconCookieAuthMiddleware(_ => Task.CompletedTask).InvokeAsync(context);

        context.User.Should().BeSameAs(ticket.Principal);
        context.Features.Get<IAuthenticateResultFeature>()!.AuthenticateResult!.Properties!.ExpiresUtc
            .Should().Be(DateTimeOffset.UnixEpoch.AddDays(1));
        context.User = new ClaimsPrincipal();
        context.Features.Get<IAuthenticateResultFeature>()!.AuthenticateResult.Should().BeNull("a result never outlives the user it described");
    }

    private static async Task<WebApplication> StartAsync(TimeProvider cookieClock)
    {
        var authorization = new Mock<IBeaconAuthorizationProvider>();
        authorization.Setup(x => x.HasReadPermissionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var configuration = new BeaconConfiguration();
        configuration.UserManagement.Enabled = true;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(configuration);
        builder.Services.AddSingleton<IUserSessionStore>(new OneUserStore());
        builder.Services.AddSingleton(authorization.Object);
        builder.Services.AddSingleton(ApiKeys().Object);
        builder.Services.AddSingleton(Options.Create(new SessionLifetimeOptions { RevalidateMinutes = 5 }));
        builder.Services.AddSingleton(Options.Create(new RoleMappingOptions()));
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddBeaconCookieAuthentication();
        builder.Services.PostConfigure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, x => x.TimeProvider = cookieClock);

        // Single sign-on registered next to the cookie: no default authentication scheme remains.
        builder.Services.AddAuthentication().AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, x =>
        {
            x.Authority = "https://login.example.test";
            x.ClientId = "beacon";
            x.ClientSecret = "unused";
            x.Configuration = new OpenIdConnectConfiguration();
        });
        builder.Services.AddBeaconApiAuthorization();
        builder.Services.AddSignalR();

        var app = builder.Build();
        app.UseRouting();
        app.UseMiddleware<ApiKeyAuthMiddleware>();
        app.UseAuthentication();
        app.UseMiddleware<BeaconCookieAuthMiddleware>();
        app.UseAuthorization();
        app.MapGet("/test/sign-in", async (HttpContext context) =>
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "ana"), new Claim(ClaimTypes.Name, "ana")],
                CookieAuthenticationDefaults.AuthenticationScheme));
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        });

        // As MapBeaconApi maps it.
        app.MapHub<BeaconHub>(BeaconApiEndpoints.HubPath, x => x.CloseOnAuthenticationExpiration = true)
            .RequireAuthorization(BeaconApiEndpoints.AuthPolicyName)
            .RequireAuthorization(BeaconApiEndpoints.HubAccessPolicy);
        await app.StartAsync();

        return app;
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
                Name = "realtime",
                KeyHash = "hash",
                KeyPrefix = ApiKey[..12],
                Scopes = "[\"Read\"]",
                User = new BeaconUser { Id = 7, UserName = "ana", ExternalId = "ana", IsEnabled = true }
            });

        return apiKeys;
    }

    private static async Task<string> SignInAsync(HttpClient client)
    {
        var response = await client.GetAsync("/test/sign-in");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return response.Headers.GetValues("Set-Cookie")
            .Single(x => x.StartsWith("Beacon.Auth=", StringComparison.Ordinal))
            .Split(';')[0];
    }

    private static async Task<string> NegotiateAsync(HttpClient client, string cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BeaconApiEndpoints.HubPath}/negotiate?negotiateVersion=1");
        request.Headers.Add("Cookie", cookie);

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var negotiation = await response.Content.ReadFromJsonAsync<JsonElement>();

        return negotiation.GetProperty("connectionToken").GetString()!;
    }

    private static Task SendAsync(WebSocket socket, string message)
    {
        return socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<string> ReceiveAsync(WebSocket socket, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        var buffer = new byte[4096];
        var text = new StringBuilder();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellation.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return string.Empty;
            }

            text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        }
        while (!result.EndOfMessage);

        return text.ToString();
    }

    // The server's close message, skipping pings; null when the connection is still open after the timeout.
    private static async Task<string?> ReceiveUntilCloseAsync(WebSocket socket, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            string message;
            try
            {
                message = await ReceiveAsync(socket, deadline - DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (message.Contains("\"type\":7", StringComparison.Ordinal))
            {
                return message;
            }
        }

        return null;
    }

    // The signed-in "ana": user 7, active, with a stamp.
    private sealed class OneUserStore : IUserSessionStore
    {
        public Task<SessionUser?> FindAsync(string externalId, CancellationToken cancellationToken)
        {
            return Task.FromResult(externalId == "ana"
                ? new SessionUser { Id = 7, IsActive = true, SecurityStamp = "stamp-1", RoleNames = ["Viewer"] }
                : null);
        }

        public Task<bool> RenewStampAsync(int userId, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    // The system clock moved by a fixed offset.
    private sealed class OffsetTimeProvider(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + offset;
    }
}
