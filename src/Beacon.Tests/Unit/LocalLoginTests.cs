using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.Api.Authentication;
using Beacon.Api.Endpoints;
using Beacon.Core;
using Beacon.Core.Authentication;
using Beacon.Core.Authentication.Providers;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.PostgreSql;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using Beacon.Core.Worker;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// <c>Beacon:UserManagement:LocalLogin</c>: on by default. Off, the password endpoint is not mapped unless a break-glass
/// account is configured; then every name gets the same password check, and whichever provider authenticated it, only a
/// stored internal user whose own stored user name is on the list (an exact match) signs in: the others get the generic
/// answer and an Information log without the name; a break-glass sign-in is logged at Warning. Beacon's password providers
/// refuse every other account, the hybrid provider does not fall back to its external login, and the external JWT login
/// refuses. The SSO configuration the login page reads says whether the password form is open to everyone.
/// </summary>
[TestFixture]
public class LocalLoginTests
{
    private const string LoginPath = "/beacon/api/auth/login";
    private const string LogoutPath = "/beacon/api/auth/logout";
    private const string Generic = "Invalid username or password.";

    [Test]
    public void LocalLogin_IsOnByDefault()
    {
        var options = new LocalLoginOptions();

        options.Enabled.Should().BeTrue();
        options.IsOffered.Should().BeTrue();
        options.Permits("anyone").Should().BeTrue();
    }

    [Test]
    public void BreakGlassNames_MatchTheStoredUserNameExactly_AndOnlyThem()
    {
        var options = new LocalLoginOptions { Enabled = false, BreakGlassUserNames = [" root "] };

        options.IsOffered.Should().BeTrue();
        options.Permits("root").Should().BeTrue("surrounding space in the configured entry is ignored");
        options.Permits("ROOT").Should().BeFalse("the stored name must match exactly, case included");
        options.Permits(" root").Should().BeFalse();
        options.Permits("ana").Should().BeFalse();
        options.Permits(null).Should().BeFalse();
        new LocalLoginOptions { Enabled = false }.IsOffered.Should().BeFalse();
    }

    [Test]
    public void BreakGlassNames_AreAShortListWithoutBlanks()
    {
        var blank = () => new LocalLoginOptions { BreakGlassUserNames = ["root", " "] }.Validate();
        var tooMany = () => new LocalLoginOptions { BreakGlassUserNames = ["a", "b", "c", "d"] }.Validate();
        var three = () => new LocalLoginOptions { BreakGlassUserNames = ["a", "b", "c"] }.Validate();

        blank.Should().Throw<InvalidOperationException>().WithMessage("*blank*");
        tooMany.Should().Throw<InvalidOperationException>().WithMessage("*at most 3*");
        three.Should().NotThrow();
    }

    [Test]
    public void Startup_BindsLocalLoginFromConfiguration_AndRefusesAnInvalidList()
    {
        var bound = Startup(new Dictionary<string, string?>
        {
            ["Beacon:UserManagement:LocalLogin:Enabled"] = "false",
            ["Beacon:UserManagement:LocalLogin:BreakGlassUserNames:0"] = "root"
        });
        var invalid = () => Startup(new Dictionary<string, string?>
        {
            ["Beacon:UserManagement:LocalLogin:BreakGlassUserNames:0"] = "a",
            ["Beacon:UserManagement:LocalLogin:BreakGlassUserNames:1"] = "b",
            ["Beacon:UserManagement:LocalLogin:BreakGlassUserNames:2"] = "c",
            ["Beacon:UserManagement:LocalLogin:BreakGlassUserNames:3"] = "d"
        });

        bound.UserManagement.LocalLogin.Enabled.Should().BeFalse();
        bound.UserManagement.LocalLogin.BreakGlassUserNames.Should().Equal("root");
        invalid.Should().Throw<InvalidOperationException>().WithMessage("*BreakGlassUserNames*");
    }

    [Test]
    public void Startup_BindingTheSectionTwice_ListsEachBreakGlassNameOnce()
    {
        // The host binds the section onto Beacon's options itself; Beacon's own binding appends to the same list.
        var settings = new Dictionary<string, string?>
        {
            ["Beacon:UserManagement:LocalLogin:Enabled"] = "false",
            ["Beacon:UserManagement:LocalLogin:BreakGlassUserNames:0"] = "root",
            ["Beacon:UserManagement:LocalLogin:BreakGlassUserNames:1"] = "ops"
        };

        var bound = Startup(settings, hostBindsTheSection: true);

        bound.UserManagement.LocalLogin.BreakGlassUserNames.Should().Equal("root", "ops");
    }

    [Test]
    public async Task LocalLoginOff_WithoutBreakGlass_ThePasswordEndpointIsNotMapped_ButSignOutIs()
    {
        var provider = new Mock<IBeaconAuthenticationProvider>();
        await using var app = await StartAsync(provider.Object, Off());
        using var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync(LoginPath, new LoginRequest("root", "right"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        Routes(app).Should().Contain(LogoutPath).And.NotContain(LoginPath);
        provider.Verify(x => x.AuthenticateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task LocalLoginOn_ThePasswordEndpointIsMapped()
    {
        await using var app = await StartAsync(Succeeds().Object, new LocalLoginOptions());

        Routes(app).Should().Contain(LoginPath);
    }

    [Test]
    public async Task LocalLoginOff_AnyOtherName_GetsTheSamePasswordCheck_ThenTheGenericAnswer_AndIsLogged()
    {
        var provider = Succeeds(beaconUserId: 2, userId: "ext-ana");
        var logs = new LogRecorder();
        await using var app = await StartAsync(provider.Object, Off("root"), logs, Stored(InternalUser(1, "root"), InternalUser(2, "ana")));
        using var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync(LoginPath, new LoginRequest("ana", "right"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadFromJsonAsync<LoginResponse>())!.Error.Should().Be(Generic);
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
        provider.Verify(
            x => x.AuthenticateAsync("ana", "right", It.IsAny<CancellationToken>()),
            Times.Once,
            "every name costs a password check, so the timing tells no break-glass account from another");
        logs.Entries.Should().ContainSingle(x => x.Message.StartsWith("Password sign-in refused", StringComparison.Ordinal))
            .Which.Should().Match<RecordedLog>(x => x.Level == LogLevel.Information && !x.Message.Contains("ana"));
    }

    [Test]
    public async Task LocalLoginOff_ABreakGlassAccount_SignsIn_AndTheSignInIsLoggedAtWarning()
    {
        var provider = Succeeds(beaconUserId: 1);
        var logs = new LogRecorder();
        await using var app = await StartAsync(provider.Object, Off("root"), logs, Stored(InternalUser(1, "root")));
        using var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync(LoginPath, new LoginRequest("Root", "right"));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the stored user name decides, not the name typed");
        response.Headers.GetValues("Set-Cookie").Should().Contain(x => x.StartsWith("Beacon.Auth=", StringComparison.Ordinal));
        logs.Entries.Should().ContainSingle(x => x.Message.StartsWith("Break-glass password sign-in", StringComparison.Ordinal))
            .Which.Should().Match<RecordedLog>(x => x.Level == LogLevel.Warning && x.Message.Contains("user 1") && !x.Message.Contains("Root"));
    }

    // A host's own provider, or the external JWT login, authenticating someone while local login is off: only a stored
    // internal user whose own stored name is on the list passes, and only the user the session would name.
    [TestCase("ext-root", 1, "root", true, true)]
    [TestCase("ext-root", 1, "Root", true, false)]
    [TestCase("ext-root", 1, "root", false, false)]
    [TestCase("ext-root", 2, "root", true, false)]
    [TestCase("ext-unknown", null, "root", true, false)]
    public async Task LocalLoginOff_WhicheverProviderAuthenticated_OnlyAStoredInternalBreakGlassUserSignsIn(
        string sessionUserId,
        int? boundUserId,
        string storedName,
        bool internalUser,
        bool signsIn)
    {
        var root = InternalUser(1, storedName);
        root.IsInternalUser = internalUser;
        var provider = new Mock<IBeaconAuthenticationProvider>();
        provider
            .Setup(x => x.AuthenticateAsync(It.IsAny<string>(), "right", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthenticationResult
            {
                Success = true,
                BeaconUserId = boundUserId,
                User = new AuthenticatedUser { UserId = sessionUserId, UserName = "root" }
            });
        await using var app = await StartAsync(provider.Object, Off("root"), gateUsers: Stored(root, InternalUser(2, "ana")));
        using var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync(LoginPath, new LoginRequest("root", "right"));

        response.StatusCode.Should().Be(signsIn ? HttpStatusCode.OK : HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task LocalLoginOff_WithoutTheGate_NobodySignsInWithAPassword()
    {
        await using var app = await StartAsync(Succeeds(beaconUserId: 1).Object, Off("root"));
        using var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync(LoginPath, new LoginRequest("root", "right"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a host without Beacon's data layer has no stored break-glass user");
    }

    [Test]
    public async Task TheExternalJwtLogin_RefusesWhileLocalLoginIsOff_WithoutCallingTheExternalApi()
    {
        var external = new CountingHandler();
        var configuration = new BeaconConfiguration();
        configuration.UserManagement.LocalLogin = Off("root");
        var logs = new LogRecorder();
        var jwt = new JwtExternalApiAuthenticationProvider(
            new HttpClient(external),
            new JwtAuthenticationOptions { ExternalLoginEndpoint = "https://login.example.test/token" },
            logs.For<JwtExternalApiAuthenticationProvider>(),
            configuration);

        var result = await jwt.AuthenticateAsync("root", "pw");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be(Generic);
        external.Calls.Should().Be(0);
        logs.Entries.Should().Contain(x => x.Message.Contains("local login is off", StringComparison.Ordinal));
    }

    [Test]
    public async Task LocalLoginOff_TheAuthenticationService_SignsInOnlyABreakGlassUser()
    {
        var configuration = new BeaconConfiguration();
        configuration.UserManagement.LocalLogin = Off("root");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Gate(configuration, Stored(InternalUser(1, "root"), InternalUser(2, "ana"))));
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        using var provider = services.BuildServiceProvider();
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { RequestServices = provider } };

        var ana = await new BeaconAuthenticationService(Succeeds(beaconUserId: 2, userId: "ext-ana").Object, accessor, configuration)
            .SignInAsync("ana", "right");
        var root = await new BeaconAuthenticationService(Succeeds(beaconUserId: 1).Object, accessor, configuration)
            .SignInAsync("root", "right");

        ana.Success.Should().BeFalse();
        ana.ErrorMessage.Should().Be(Generic);
        root.Success.Should().BeTrue();
    }

    [Test]
    public async Task LocalLoginOn_ASignIn_IsNoBreakGlassEvent()
    {
        var logs = new LogRecorder();
        await using var app = await StartAsync(Succeeds(beaconUserId: 1).Object, new LocalLoginOptions { BreakGlassUserNames = ["root"] }, logs);
        using var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync(LoginPath, new LoginRequest("root", "right"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        logs.Entries.Should().NotContain(x => x.Message.StartsWith("Break-glass", StringComparison.Ordinal));
    }

    [Test]
    public async Task LocalLoginOff_TheDatabaseProvider_RefusesEveryAccountButABreakGlassOne()
    {
        var users = new List<BeaconUser>
        {
            InternalUser(1, "root", email: "root@example.com"),
            InternalUser(2, "ana", email: "ana@example.com")
        };
        var provider = new DatabaseAuthenticationProvider(Service(users, Off("root")));

        var ana = await provider.AuthenticateAsync("ana", "right");
        var root = await provider.AuthenticateAsync("root", "right");
        var rootByEmail = await provider.AuthenticateAsync("root@example.com", "right");

        ana.Success.Should().BeFalse();
        ana.ErrorMessage.Should().Be(Generic, "a refused account is indistinguishable from a wrong password");
        root.Success.Should().BeTrue();
        root.BeaconUserId.Should().Be(1);
        rootByEmail.Success.Should().BeTrue("the account, not the name typed, is the break-glass account");
    }

    [Test]
    public async Task LocalLoginOff_ARightPasswordOfAnotherAccount_IsRefusedAndLogged_WithTheUsersIdOnly()
    {
        var logs = new LogRecorder();
        var users = new List<BeaconUser> { InternalUser(1, "root"), InternalUser(2, "ana") };
        var service = Service(users, Off("root"), logs);

        var right = await service.AuthenticateInternalUserAsync("ana", "right");
        var wrong = await service.AuthenticateInternalUserAsync("ana", "wrong");

        right.Success.Should().BeFalse();
        right.ErrorMessage.Should().Be(Generic);
        wrong.ErrorMessage.Should().Be(Generic);
        logs.Entries.Should().ContainSingle(x => x.Message.StartsWith("Password sign-in refused", StringComparison.Ordinal))
            .Which.Message.Should().Be("Password sign-in refused: local login is off and user 2 is not a break-glass account");
    }

    [Test]
    public async Task LocalLoginOff_ABreakGlassNameInAnotherCase_IsNotTheStoredAccount()
    {
        var users = new List<BeaconUser> { InternalUser(1, "Root") };
        var provider = new DatabaseAuthenticationProvider(Service(users, Off("root")));

        var result = await provider.AuthenticateAsync("Root", "right");

        result.Success.Should().BeFalse("the configured name must match the stored name exactly");
    }

    [Test]
    public async Task LocalLoginOff_ANameThatMatchesAnotherAccountsEmail_IsRefused()
    {
        // "root" is a break-glass name, but the only account it finds is mallory's (by e-mail).
        var users = new List<BeaconUser> { InternalUser(3, "mallory", email: "root") };
        var provider = new DatabaseAuthenticationProvider(Service(users, Off("root")));

        var result = await provider.AuthenticateAsync("root", "right");

        result.Success.Should().BeFalse();
    }

    [TestCase(true, 1)]
    [TestCase(false, 0)]
    public async Task TheHybridProvider_FallsBackToItsExternalLogin_OnlyWhileLocalLoginIsOn(bool enabled, int externalCalls)
    {
        var users = new Mock<IUserManagementService>();
        users
            .Setup(x => x.AuthenticateInternalUserAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AuthenticationResult.Failed(Generic));
        var external = new CountingHandler();
        var jwt = new JwtExternalApiAuthenticationProvider(
            new HttpClient(external),
            new JwtAuthenticationOptions { ExternalLoginEndpoint = "https://login.example.test/token" },
            NullLogger<JwtExternalApiAuthenticationProvider>.Instance);
        var configuration = new BeaconConfiguration();
        configuration.UserManagement.LocalLogin.Enabled = enabled;
        configuration.UserManagement.LocalLogin.BreakGlassUserNames = ["root"];

        var result = await new HybridAuthenticationProvider(users.Object, jwt, configuration).AuthenticateAsync("root", "pw");

        result.Success.Should().BeFalse();
        external.Calls.Should().Be(externalCalls);
    }

    [Test]
    public async Task TheHybridProvider_NeverFallsBack_ForAUserWhoMustChooseANewPassword()
    {
        var users = new Mock<IUserManagementService>();
        users
            .Setup(x => x.AuthenticateInternalUserAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AuthenticationResult.PasswordChangeNeeded("Choose a new password."));
        var external = new CountingHandler();
        var jwt = new JwtExternalApiAuthenticationProvider(
            new HttpClient(external),
            new JwtAuthenticationOptions { ExternalLoginEndpoint = "https://login.example.test/token" },
            NullLogger<JwtExternalApiAuthenticationProvider>.Instance);

        var result = await new HybridAuthenticationProvider(users.Object, jwt, new BeaconConfiguration()).AuthenticateAsync("ana", "pw");

        result.PasswordChangeRequired.Should().BeTrue();
        external.Calls.Should().Be(0);
    }

    // Local login off with no other way in: nobody could sign in, so the host does not start.
    [TestCase(false, false, false, true)]
    [TestCase(true, false, false, false)]
    [TestCase(false, true, false, false)]
    [TestCase(false, false, true, false)]
    public void Startup_FailsWhenLocalLoginIsOff_AndNobodyCouldSignIn(bool singleSignOn, bool externalLogin, bool breakGlass, bool fails)
    {
        var configuration = new BeaconConfiguration();
        configuration.UserManagement.LocalLogin = breakGlass ? Off("root") : Off();
        var apiOptions = new Beacon.Api.BeaconApiOptions
        {
            ExternalLogin = externalLogin ? new Beacon.Api.ExternalLoginOptions { LoginUrl = "https://login.example.test" } : null
        };
        var check = new LocalLoginStartupCheck(
            NullLogger<LocalLoginStartupCheck>.Instance,
            configuration,
            Options.Create(new OidcAuthenticationOptions { Enabled = singleSignOn }),
            apiOptions);

        var act = () => check.Configure(_ => { });

        if (fails)
        {
            act.Should().Throw<InvalidOperationException>().WithMessage("*Enabled is false*nobody could sign in*");
            return;
        }

        act.Should().NotThrow();
    }

    [Test]
    public async Task ABreakGlassNameNoEnabledInternalUserHasExactly_IsWarnedAbout_ByItsPositionOnly()
    {
        var logs = new LogRecorder();
        var configuration = new BeaconConfiguration();
        configuration.UserManagement.LocalLogin = Off("root", "Ops", "ghost");
        var external = InternalUser(3, "ghost");
        external.IsInternalUser = false;
        var users = Stored(InternalUser(1, "root"), InternalUser(2, "ops"), external);
        var services = new ServiceCollection()
            .AddSingleton(Factory(users))
            .BuildServiceProvider();
        var check = new LocalLoginStartupCheck(logs.For<LocalLoginStartupCheck>(), configuration);

        await check.WarnAboutUnknownBreakGlassNamesAsync(services, configuration.UserManagement.LocalLogin, CancellationToken.None);

        logs.Entries.Select(x => x.Message).Should().BeEquivalentTo(
            "Beacon:UserManagement:LocalLogin:BreakGlassUserNames entry 1 matches no enabled internal user (names are compared exactly, case included, with the stored user name): that account cannot sign in while local login is off",
            "Beacon:UserManagement:LocalLogin:BreakGlassUserNames entry 2 matches no enabled internal user (names are compared exactly, case included, with the stored user name): that account cannot sign in while local login is off");
        logs.Entries.Should().OnlyContain(x => x.Level == LogLevel.Warning && !x.Message.Contains("ghost") && !x.Message.Contains("Ops"));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void TheLoginPage_IsToldWhetherThePasswordFormIsOpenToEveryone(bool enabled)
    {
        var configuration = new BeaconConfiguration();
        configuration.UserManagement.LocalLogin.Enabled = enabled;
        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(configuration).BuildServiceProvider()
        };

        var response = AuthEndpoints.GetSsoConfig(Options.Create(new OidcAuthenticationOptions { Enabled = true }), httpContext);

        response.Should().Be(new SsoConfigResponse(true, enabled));
    }

    private static LocalLoginOptions Off(params string[] breakGlass) => new() { Enabled = false, BreakGlassUserNames = [.. breakGlass] };

    private static Mock<IBeaconAuthenticationProvider> Succeeds(int beaconUserId = 7, string userId = "ext-root")
    {
        var provider = new Mock<IBeaconAuthenticationProvider>();
        provider
            .Setup(x => x.AuthenticateAsync(It.IsAny<string>(), "right", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthenticationResult
            {
                Success = true,
                BeaconUserId = beaconUserId,
                User = new AuthenticatedUser { UserId = userId, UserName = userId["ext-".Length..] }
            });

        return provider;
    }

    private static List<BeaconUser> Stored(params BeaconUser[] users) => [.. users];

    private static PasswordSignInGate Gate(BeaconConfiguration configuration, List<BeaconUser> users)
    {
        return new PasswordSignInGate(configuration, Factory(users));
    }

    private static IDbContextFactory<BeaconContext> Factory(List<BeaconUser> users)
    {
        var sets = new Dictionary<Type, object> { [typeof(BeaconUser)] = RecordingBeaconContext.MemorySet(users, []) };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(sets, []));

        return factory.Object;
    }

    private static async Task<WebApplication> StartAsync(
        IBeaconAuthenticationProvider provider,
        LocalLoginOptions localLogin,
        LogRecorder? logs = null,
        List<BeaconUser>? gateUsers = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        if (logs != null)
        {
            builder.Logging.ClearProviders().AddProvider(logs);
        }

        builder.Services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(x => x.Cookie.Name = "Beacon.Auth");
        var configuration = new BeaconConfiguration();
        configuration.UserManagement.LocalLogin = localLogin;
        builder.Services.AddSingleton(provider);
        builder.Services.AddSingleton(new Mock<IAntiforgery>().Object);
        builder.Services.AddSingleton(new LoginRateLimiter());
        if (gateUsers != null)
        {
            builder.Services.AddSingleton(Gate(configuration, gateUsers));
        }

        var app = builder.Build();
        app.MapLoginEndpoints("/beacon", configuration);
        await app.StartAsync();

        return app;
    }

    private static List<string?> Routes(WebApplication app)
    {
        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(x => x.RoutePattern.RawText)
            .ToList();
    }

    // With hostBindsTheSection, the host's own configuration callback binds the local-login section onto Beacon's options
    // before Beacon binds it.
    private static BeaconConfiguration Startup(Dictionary<string, string?> settings, bool hostBindsTheSection = false)
    {
        settings["Beacon:EncryptionKey"] = TestEncryptionKey.Value;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services
            .AddBeaconServices(configuration, x =>
            {
                if (hostBindsTheSection)
                {
                    configuration.GetSection(LocalLoginOptions.SectionName).Bind(x.UserManagement.LocalLogin);
                }

                x.AddBeaconScheduler<NoOpScheduler>();
            })
            .UsePostgreSql("Host=localhost;Database=unused;Username=unused;Password=unused");

        using var provider = services.BuildServiceProvider();

        return provider.GetRequiredService<BeaconConfiguration>();
    }

    private static BeaconUser InternalUser(int id, string userName, string? email = null)
    {
        return new BeaconUser
        {
            Id = id,
            ExternalId = $"ext-{userName}",
            UserName = userName,
            Email = email,
            IsInternalUser = true,
            IsEnabled = true,
            PasswordHash = "hash",
            PasswordSalt = "salt"
        };
    }

    private static UserManagementService Service(List<BeaconUser> users, LocalLoginOptions localLogin, LogRecorder? logs = null)
    {
        var hasher = new Mock<IPasswordHasher>();
        hasher
            .Setup(x => x.VerifyPassword(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns<string, string, string>((password, hash, _) => password == "right" && hash == "hash");
        var saved = new List<object>();
        var sets = new Dictionary<Type, object> { [typeof(BeaconUser)] = RecordingBeaconContext.MemorySet(users, saved) };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(sets, saved));
        var configuration = new BeaconConfiguration();
        configuration.UserManagement.LocalLogin = localLogin;

        return new UserManagementService(
            factory.Object,
            hasher.Object,
            Mock.Of<IRoleService>(),
            configuration,
            TimeProvider.System,
            logs?.For<UserManagementService>());
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }
    }

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }
}
