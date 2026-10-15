using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Beacon.Api.Authentication;
using Beacon.Api.Endpoints;
using Beacon.Core;
using Beacon.Core.Authentication;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Telemetry;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.SecurityAudit;

/// <summary>
/// The password sign-in and sign-out events, over the real login endpoints: a failed attempt names the user the name
/// resolved to (only when it did) and never the name typed or the password; a refusal because local login is off is told
/// apart; a sign-in names its user; a break-glass sign-in while local login is off is its own action; a sign-out names the
/// session's user. Writing an event never fails the sign-in it records, and nothing is written for an attempt the endpoint
/// turns away before checking a password.
/// </summary>
[TestFixture]
public class SignInEventTests
{
    private const string TypedName = "ana.typed@corp.example";
    private const string Password = "the-password-typed";
    private const string SessionCookieName = "Beacon.Auth";

    [Test]
    public async Task AWrongPassword_ForAKnownUser_IsRecordedAgainstThatUser_NeverWithWhatWasTyped()
    {
        var audit = new RecordingSecurityAudit();
        await using var app = await StartAsync(Failed(userId: 7), audit);

        var response = await LoginAsync(app);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        audit.Single(TypedName, Password, "ana.typed").Should().BeEquivalentTo(
            new
            {
                Action = SecurityAuditActions.PasswordSignInFailed,
                Outcome = SecurityAuditOutcomes.Failure,
                ActorKind = SecurityAuditActorKinds.Anonymous,
                ActorUserId = (int?)null,
                ResourceType = SecurityAuditResources.User,
                ResourceId = "7",
                DetailsJson = "{\"reason\":\"invalid_credentials\"}"
            },
            x => x.ExcludingMissingMembers());
    }

    [Test]
    public async Task FailedAttempts_OnOneName_ShareAKeyedHashOfIt_AndAnotherNameGetsAnother_NeverTheNameItself()
    {
        var audit = new RecordingSecurityAudit();
        var provider = new Mock<IBeaconAuthenticationProvider>();
        provider
            .Setup(x => x.AuthenticateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Failed(userId: null));
        var services = new ServiceCollection();
        services.AddSingleton<ISecurityAuditRecorder>(audit);
        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        var service = new BeaconAuthenticationService(provider.Object, new HttpContextAccessor { HttpContext = httpContext }, new BeaconConfiguration());

        await service.SignInAsync(TypedName, Password);
        await service.SignInAsync("  " + TypedName.ToUpperInvariant() + " ", Password);
        await service.SignInAsync("someone.else", Password);

        var hashes = audit.Events
            .Select(x => x.CallerHash)
            .ToList();
        hashes.Should().OnlyContain(x => !string.IsNullOrEmpty(x));
        hashes[0].Should().Be(SecurityAuditTestKit.CallerHasher.Hash(SignInAudit.LoginTenant, TypedName), "the name is trimmed and compared ignoring case");
        hashes[1].Should().Be(hashes[0]);
        hashes[2].Should().NotBe(hashes[0]);
        audit.Events.Should().OnlyContain(x => x.ActorKind == SecurityAuditActorKinds.Anonymous && x.ActorUserId == null);
        foreach (var auditEvent in audit.Events)
        {
            SecurityAuditTestKit.AssertNoPii(auditEvent, TypedName, "someone.else", Password);
        }
    }

    [Test]
    public async Task AnUnknownName_IsRecordedWithoutAUser()
    {
        var audit = new RecordingSecurityAudit();
        await using var app = await StartAsync(Failed(userId: null), audit);

        await LoginAsync(app);

        audit.Single(TypedName, Password).ResourceId.Should().BeNull();
    }

    [Test]
    public async Task ARightPasswordRefusedBecauseLocalLoginIsOff_IsARefusal()
    {
        var audit = new RecordingSecurityAudit();
        await using var app = await StartAsync(new AuthenticationResult { Success = false, ErrorMessage = "Invalid username or password.", BeaconUserId = 7, LocalLoginRefused = true }, audit);

        await LoginAsync(app);

        audit.Single(TypedName, Password).Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.PasswordSignInRefused, Outcome = SecurityAuditOutcomes.Refused, ResourceId = "7", DetailsJson = "{\"reason\":\"local_login_off\"}" },
            x => x.ExcludingMissingMembers());
    }

    [Test]
    public async Task ASignIn_IsRecordedAsItsUsers()
    {
        var audit = new RecordingSecurityAudit();
        await using var app = await StartAsync(SignedIn(7, "ext-ana-0001"), audit);

        var response = await LoginAsync(app);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        audit.Single(TypedName, Password, "ext-ana-0001").Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.PasswordSignInSucceeded, ActorKind = SecurityAuditActorKinds.User, ActorUserId = (int?)7, ResourceId = "7" },
            x => x.ExcludingMissingMembers());
    }

    [Test]
    public async Task WhileLocalLoginIsOff_ABreakGlassSignIn_IsItsOwnAction_AndAnyOtherIsRefused()
    {
        var glassAudit = new RecordingSecurityAudit();
        var otherAudit = new RecordingSecurityAudit();
        await using var glass = await StartAsync(SignedIn(7, "ext-glass"), glassAudit, breakGlassUser: new BeaconUser { Id = 7, ExternalId = "ext-glass", UserName = "emergency.ops", IsInternalUser = true, IsEnabled = true });
        await using var other = await StartAsync(SignedIn(8, "ext-other"), otherAudit, breakGlassUser: new BeaconUser { Id = 7, ExternalId = "ext-glass", UserName = "emergency.ops", IsInternalUser = true, IsEnabled = true });

        (await LoginAsync(glass)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await LoginAsync(other)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        glassAudit.Single(TypedName, Password, "emergency.ops", "ext-glass").Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.BreakGlassSignedIn, ActorUserId = (int?)7, ResourceId = "7" },
            x => x.ExcludingMissingMembers());
        otherAudit.Single(TypedName, Password).Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.PasswordSignInRefused, Outcome = SecurityAuditOutcomes.Refused, ResourceId = "8" },
            x => x.ExcludingMissingMembers());
    }

    [Test]
    public async Task ARequestWithoutAPassword_RecordsNothing()
    {
        var audit = new RecordingSecurityAudit();
        await using var app = await StartAsync(SignedIn(7, "ext-ana"), audit);
        using var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/beacon/api/auth/login", new LoginRequest(TypedName, ""));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        audit.Events.Should().BeEmpty();
    }

    [Test]
    public async Task AnEventThatCannotBeWritten_NeverFailsTheSignIn_AndIsLoggedAtError()
    {
        var logs = new LogRecorder();
        var broken = new Mock<IDbContextFactory<BeaconContext>>();
        broken
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the audit table is unreachable"));
        var recorder = new Core.SecurityAudit.SecurityAuditRecorder(
            broken.Object,
            TestProjectAccess.Actors(),
            SecurityAuditTestKit.Integrity,
            SecurityAuditTestKit.CallerHasher,
            TimeProvider.System,
            LoggerFactory.Create(x => x.AddProvider(logs)));
        await using var app = await StartAsync(SignedIn(7, "ext-ana"), recorder);

        var response = await LoginAsync(app);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the sign-in goes on: a security audit write fails open");
        logs.Entries.Should().ContainSingle(x => x.EventId.Id == SecurityAuditLogEvents.WriteFailed.Id)
            .Which.Should().BeEquivalentTo(new { Level = LogLevel.Error, Category = BeaconTelemetry.AuditLogCategory }, x => x.ExcludingMissingMembers());
    }

    [Test]
    public async Task ASignOut_IsRecordedAsTheSessionsUser_AndAnAnonymousOneIsNot()
    {
        var audit = new RecordingSecurityAudit(new BeaconActor("ext-ana", false) { Kind = BeaconPrincipalKind.User, BeaconUserId = 7, IsActiveUser = true });
        await using var app = await StartAsync(SignedIn(7, "ext-ana"), audit, actors: TestProjectAccess.Actors(7, "ext-ana"));
        using var client = app.GetTestClient();
        var session = await StartSessionAsync(client);

        var signedOut = await client.SendAsync(LogoutRequest(session));
        var anonymous = await client.SendAsync(LogoutRequest(session with { AllCookies = session.AllCookies.Replace(session.SessionCookie, string.Empty, StringComparison.Ordinal) }));

        signedOut.StatusCode.Should().Be(HttpStatusCode.OK);
        anonymous.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
        audit.Single("ext-ana").Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.SignedOut, ActorUserId = (int?)7, ResourceId = "7" },
            x => x.ExcludingMissingMembers());
    }

    [Test]
    public async Task ASignOut_WhoseCallerCannotBeResolved_StillSignsOut_AndIsRecordedWithoutAUser()
    {
        var audit = new RecordingSecurityAudit(new BeaconActor("ext-ana", false) { Kind = BeaconPrincipalKind.User, BeaconUserId = 7, IsActiveUser = true });
        var failing = new Mock<IBeaconActorAccessor>();
        failing
            .Setup(x => x.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the user store is down"));
        await using var app = await StartAsync(SignedIn(7, "ext-ana"), audit, actors: failing.Object);
        using var client = app.GetTestClient();
        var session = await StartSessionAsync(client);

        var signedOut = await client.SendAsync(LogoutRequest(session));

        signedOut.StatusCode.Should().Be(HttpStatusCode.OK, "resolving the caller for the audit never fails the sign-out");
        audit.Single("ext-ana").Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.SignedOut, ResourceId = (string?)null },
            x => x.ExcludingMissingMembers());
    }

    [Test]
    public async Task TheAuthenticationService_RecordsItsSignInsAsTheEndpointDoes()
    {
        var audit = new RecordingSecurityAudit();
        var provider = new Mock<IBeaconAuthenticationProvider>();
        provider
            .SetupSequence(x => x.AuthenticateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Failed(userId: 7))
            .ReturnsAsync(new AuthenticationResult { Success = false, PasswordChangeRequired = true, ErrorMessage = "Choose a new password." });
        var services = new ServiceCollection();
        services.AddSingleton<ISecurityAuditRecorder>(audit);
        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        var service = new BeaconAuthenticationService(provider.Object, new HttpContextAccessor { HttpContext = httpContext }, new BeaconConfiguration());

        await service.SignInAsync(TypedName, Password);
        await service.SignInAsync(TypedName, Password);

        audit.Of(SecurityAuditActions.PasswordSignInFailed).Should().ContainSingle();
        var refused = audit.Of(SecurityAuditActions.PasswordSignInRefused).Should().ContainSingle("a right password that must be changed first issues no session: refused by rule, not failed").Subject;
        refused.Outcome.Should().Be(SecurityAuditOutcomes.Refused);
        refused.DetailsJson.Should().Be("{\"reason\":\"password_change_required\"}");
        audit.Events.Should().HaveCount(2);
        foreach (var auditEvent in audit.Events)
        {
            SecurityAuditTestKit.AssertNoPii(auditEvent, TypedName, Password);
        }
    }

    private static AuthenticationResult Failed(int? userId)
    {
        return new AuthenticationResult { Success = false, ErrorMessage = "Invalid username or password.", BeaconUserId = userId };
    }

    private static AuthenticationResult SignedIn(int userId, string externalId)
    {
        return new AuthenticationResult
        {
            Success = true,
            BeaconUserId = userId,
            User = new AuthenticatedUser { UserId = externalId, UserName = "ana", Roles = ["Viewer"] }
        };
    }

    private static async Task<HttpResponseMessage> LoginAsync(WebApplication app)
    {
        using var client = app.GetTestClient();

        return await client.PostAsJsonAsync("/beacon/api/auth/login", new LoginRequest(TypedName, Password));
    }

    // The login endpoints over a real cookie scheme; with a break-glass user, local login is off and that user is the
    // stored break-glass account. GET /test/session stands in for a completed sign-in.
    private static async Task<WebApplication> StartAsync(
        AuthenticationResult result,
        ISecurityAuditRecorder audit,
        BeaconUser? breakGlassUser = null,
        IBeaconActorAccessor? actors = null)
    {
        var configuration = new BeaconConfiguration();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(x => x.Cookie.Name = SessionCookieName);
        builder.Services.AddAntiforgery(x => x.Cookie.Name = ".Beacon.Antiforgery");
        var provider = new Mock<IBeaconAuthenticationProvider>();
        provider
            .Setup(x => x.AuthenticateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        builder.Services.AddSingleton(provider.Object);
        builder.Services.AddSingleton(new LoginRateLimiter());
        builder.Services.AddSingleton(audit);
        if (actors != null)
        {
            builder.Services.AddSingleton(actors);
        }

        if (breakGlassUser != null)
        {
            configuration.UserManagement.LocalLogin.Enabled = false;
            configuration.UserManagement.LocalLogin.BreakGlassUserNames = [breakGlassUser.UserName];
            var saved = new List<object>();
            var factory = new Mock<IDbContextFactory<BeaconContext>>();
            factory
                .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new RecordingBeaconContext(new Dictionary<Type, object> { [typeof(BeaconUser)] = RecordingBeaconContext.MemorySet([breakGlassUser], saved) }, saved));
            builder.Services.AddSingleton(new PasswordSignInGate(configuration, factory.Object));
        }

        var app = builder.Build();
        app.UseAuthentication();
        app.MapGet("/test/session", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "ext-ana"), new Claim(ClaimTypes.Name, "ana")],
                CookieAuthenticationDefaults.AuthenticationScheme));
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
            context.User = principal;

            return Results.Text(antiforgery.GetAndStoreTokens(context).RequestToken);
        });
        app.MapLoginEndpoints("/beacon", configuration);
        await app.StartAsync();

        return app;
    }

    private static async Task<Session> StartSessionAsync(HttpClient client)
    {
        var response = await client.GetAsync("/test/session");
        var cookies = response.Headers.GetValues("Set-Cookie")
            .Select(x => x.Split(';')[0])
            .ToList();

        return new Session(
            cookies.First(x => x.StartsWith($"{SessionCookieName}=", StringComparison.Ordinal)),
            string.Join("; ", cookies),
            await response.Content.ReadAsStringAsync());
    }

    private static HttpRequestMessage LogoutRequest(Session session)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/beacon/api/auth/logout");
        request.Headers.Add("Cookie", session.AllCookies);
        request.Headers.Add(AntiforgeryEndpointFilter.SpaHeaderName, session.RequestToken);
        request.Headers.Add("Origin", "http://localhost");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");

        return request;
    }

    private sealed record Session(string SessionCookie, string AllCookies, string RequestToken);
}
