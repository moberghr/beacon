using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Beacon.Api.Authentication;
using Beacon.Api.Endpoints;
using Beacon.Core;
using Beacon.Core.Authentication;
using Beacon.Core.Authentication.Providers;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// A password an administrator chose (a new internal user, a reset) must be replaced at the user's first sign-in: the
/// right credentials then answer "choose a new password" without a session, and the same sign-in with a new password that
/// the rules accept (and that differs from the old one) stores it, clears the requirement, changes the security stamp
/// and signs the user in. Wrong credentials answer as before; the user's own password change clears the requirement. A
/// verified password records the stamp read with it, and a new password the stamp it wrote: the sign-in that follows must
/// still find it stored.
/// </summary>
[TestFixture]
public class RequiredPasswordChangeTests
{
    private const string Generic = "Invalid username or password.";
    private const string LoginPath = "/beacon/api/auth/login";
    private const string NewPassword = "Brand-new-1!";

    [Test]
    public async Task AFlaggedUser_WithTheRightPassword_MustChooseANewOne_AndIsNotSignedIn()
    {
        var user = Flagged();

        var result = await Service([user]).AuthenticateInternalUserAsync("ana", "right");

        result.Success.Should().BeFalse();
        result.PasswordChangeRequired.Should().BeTrue();
        result.User.Should().BeNull();
        user.LastLoginAt.Should().BeNull("nobody signed in");
    }

    [Test]
    public async Task AFlaggedUser_WithAWrongPassword_GetsTheGenericAnswer()
    {
        var result = await Service([Flagged()]).AuthenticateInternalUserAsync("ana", "wrong");

        result.PasswordChangeRequired.Should().BeFalse("only the right password learns that a change is pending");
        result.ErrorMessage.Should().Be(Generic);
    }

    [Test]
    public async Task ChoosingAnAcceptedNewPassword_StoresIt_ClearsTheRequirement_AndSignsIn()
    {
        var user = Flagged();
        user.SecurityStamp = "before";

        var result = await Service([user]).CompleteRequiredPasswordChangeAsync("ana", "right", NewPassword);

        result.Success.Should().BeTrue();
        result.User!.UserName.Should().Be("ana");
        user.PasswordHash.Should().Be($"hash-of-{NewPassword}");
        user.MustChangePassword.Should().BeFalse();
        user.SecurityStamp.Should().NotBe("before");
        user.LastLoginAt.Should().NotBeNull();
    }

    [TestCase("abc", "Password must be at least*")]
    [TestCase("right", "The new password must differ*")]
    public async Task ANewPasswordTheRulesRefuse_KeepsTheRequirement_AndSaysWhy(string newPassword, string reason)
    {
        var user = Flagged();
        var service = Service([user], minimumLength: 5, complexity: false);

        var result = await service.CompleteRequiredPasswordChangeAsync("ana", "right", newPassword);

        result.Success.Should().BeFalse();
        result.PasswordChangeRequired.Should().BeTrue();
        result.ErrorMessage.Should().Match(reason);
        user.PasswordHash.Should().Be("hash");
        user.MustChangePassword.Should().BeTrue();
    }

    [Test]
    public async Task CompletingAChange_WithWrongCredentials_OrWithoutAPendingChange_IsTheGenericFailure()
    {
        var flagged = Flagged();
        var unflagged = Flagged();
        unflagged.MustChangePassword = false;

        var wrong = await Service([flagged]).CompleteRequiredPasswordChangeAsync("ana", "wrong", NewPassword);
        var notPending = await Service([unflagged]).CompleteRequiredPasswordChangeAsync("ana", "right", NewPassword);

        wrong.ErrorMessage.Should().Be(Generic);
        notPending.ErrorMessage.Should().Be(Generic);
        flagged.PasswordHash.Should().Be("hash");
        unflagged.PasswordHash.Should().Be("hash");
    }

    [Test]
    public async Task AnAdministratorsPassword_IsFlagged_TheUsersOwnIsNot()
    {
        var saved = new List<object>();
        var created = await Service([], saved: saved).CreateInternalUserAsync(new CreateInternalUserRequest
        {
            UserName = "newbie",
            Password = "Aa1!aaaaaaaa"
        });
        var reset = Flagged();
        reset.MustChangePassword = false;
        var own = Flagged();

        await Service([reset]).ResetPasswordAsync(reset.Id, "Bb2@bbbbbbbb");
        await Service([own]).ChangePasswordAsync(own.Id, "right", "Cc3#cccccccc");

        created.Success.Should().BeTrue();
        saved.OfType<BeaconUser>().Should().ContainSingle().Which.MustChangePassword.Should().BeTrue();
        reset.MustChangePassword.Should().BeTrue("an administrator reset the password");
        own.MustChangePassword.Should().BeFalse("the user chose this one");
    }

    [Test]
    public async Task TheLoginEndpoint_AsksForANewPassword_WithoutASession()
    {
        var users = new Mock<IUserManagementService>();
        users
            .Setup(x => x.AuthenticateInternalUserAsync("ana", "right", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AuthenticationResult.PasswordChangeNeeded("Choose a new password."));
        await using var app = await StartAsync(users);
        using var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync(LoginPath, new LoginRequest("ana", "right"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        body!.PasswordChangeRequired.Should().BeTrue();
        body.Error.Should().Be("Choose a new password.");
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
        users.Verify(x => x.CompleteRequiredPasswordChangeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task TheLoginEndpoint_WithANewPassword_StoresItAndSignsIn()
    {
        var users = new Mock<IUserManagementService>();
        users
            .Setup(x => x.AuthenticateInternalUserAsync("ana", "right", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AuthenticationResult.PasswordChangeNeeded("Choose a new password."));
        users
            .Setup(x => x.CompleteRequiredPasswordChangeAsync("ana", "right", NewPassword, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AuthenticationResult.Succeeded(new AuthenticatedUser { UserId = "ext-ana", UserName = "ana" }));
        await using var app = await StartAsync(users);
        using var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync(LoginPath, new LoginRequest("ana", "right", NewPassword: NewPassword));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<LoginResponse>())!.Success.Should().BeTrue();
        response.Headers.GetValues("Set-Cookie").Should().Contain(x => x.StartsWith("Beacon.Auth=", StringComparison.Ordinal));
    }

    [Test]
    public async Task TheLoginEndpoint_WithARefusedNewPassword_SaysWhy_AndStillAsksForOne()
    {
        var users = new Mock<IUserManagementService>();
        users
            .Setup(x => x.AuthenticateInternalUserAsync("ana", "right", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AuthenticationResult.PasswordChangeNeeded("Choose a new password."));
        users
            .Setup(x => x.CompleteRequiredPasswordChangeAsync("ana", "right", "short", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AuthenticationResult.PasswordChangeNeeded("Password must be at least 8 characters long."));
        await using var app = await StartAsync(users);
        using var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync(LoginPath, new LoginRequest("ana", "right", NewPassword: "short"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        body!.PasswordChangeRequired.Should().BeTrue();
        body.Error.Should().StartWith("Password must be at least");
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
    }

    [Test]
    public async Task TheLoginOperation_DeclaresItsAnswer_ForEveryStatusItReturns()
    {
        await using var app = await StartAsync(new Mock<IUserManagementService>());

        var login = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(x => x.RoutePattern.RawText == LoginPath);
        var declared = login.Metadata
            .GetOrderedMetadata<IProducesResponseTypeMetadata>()
            .Where(x => x.Type == typeof(LoginResponse))
            .Select(x => x.StatusCode);

        declared.Should().BeEquivalentTo([StatusCodes.Status200OK, StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized]);
    }

    [Test]
    public async Task AnImplementationWithoutTheNewMember_AnswersTheGenericFailure_AndTheEndpointSignsNobodyIn()
    {
        // An IUserManagementService written before CompleteRequiredPasswordChangeAsync existed: its default body runs.
        var users = new Mock<IUserManagementService> { CallBase = true };
        users
            .Setup(x => x.AuthenticateInternalUserAsync("ana", "right", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AuthenticationResult.PasswordChangeNeeded("Choose a new password."));
        await using var app = await StartAsync(users);
        using var client = app.GetTestClient();

        var direct = await users.Object.CompleteRequiredPasswordChangeAsync("ana", "right", NewPassword);
        var response = await client.PostAsJsonAsync(LoginPath, new LoginRequest("ana", "right", NewPassword: NewPassword));

        direct.Success.Should().BeFalse();
        direct.PasswordChangeRequired.Should().BeFalse();
        direct.ErrorMessage.Should().Be(Generic);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadFromJsonAsync<LoginResponse>())!.Error.Should().Be(Generic);
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
    }

    [Test]
    public async Task TheLoginEndpoint_NeverChangesAPasswordTheProviderDidNotAskToChange()
    {
        var users = new Mock<IUserManagementService>();
        users
            .Setup(x => x.AuthenticateInternalUserAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AuthenticationResult.Failed(Generic));
        await using var app = await StartAsync(users);
        using var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync(LoginPath, new LoginRequest("ana", "wrong", NewPassword: NewPassword));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        users.Verify(x => x.CompleteRequiredPasswordChangeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task AVerifiedPassword_RecordsTheStampReadWithIt_ForTheSignInThatFollows()
    {
        var user = Flagged();
        user.MustChangePassword = false;
        user.SecurityStamp = "stamp-1";
        var expectation = new SessionStampExpectation();

        var result = await Service([user], expectation: expectation).AuthenticateInternalUserAsync("ana", "right");

        result.Success.Should().BeTrue();
        expectation.StampFor("ext-ana").Should().Be("stamp-1");
        expectation.StampFor("ext-bob").Should().BeNull("only the verified user's sign-in is held to it");
    }

    [Test]
    public async Task AWrongPassword_OrOneThatMustBeReplacedFirst_RecordsNoStamp()
    {
        var expectation = new SessionStampExpectation();
        var service = Service([Flagged()], expectation: expectation);

        await service.AuthenticateInternalUserAsync("ana", "wrong");
        await service.AuthenticateInternalUserAsync("ana", "right");

        expectation.StampFor("ext-ana").Should().BeNull("no sign-in follows either answer");
    }

    [Test]
    public async Task ANewPassword_RecordsTheStampItWrote_ForTheSignInThatFollows()
    {
        var required = Flagged();
        var own = Flagged();
        own.MustChangePassword = false;
        own.SecurityStamp = "before";
        var afterRequired = new SessionStampExpectation();
        var afterOwn = new SessionStampExpectation();

        await Service([required], expectation: afterRequired).CompleteRequiredPasswordChangeAsync("ana", "right", NewPassword);
        await Service([own], expectation: afterOwn).ChangePasswordAsync(own.Id, "right", NewPassword);

        afterRequired.StampFor("ext-ana").Should().NotBeNullOrEmpty().And.Be(required.SecurityStamp);
        afterOwn.StampFor("ext-ana").Should().Be(own.SecurityStamp).And.NotBe("before");
    }

    private static BeaconUser Flagged()
    {
        return new BeaconUser
        {
            Id = 5,
            ExternalId = "ext-ana",
            UserName = "ana",
            IsInternalUser = true,
            IsEnabled = true,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            MustChangePassword = true
        };
    }

    // The login endpoints over the database provider, backed by the user store double.
    private static async Task<WebApplication> StartAsync(Mock<IUserManagementService> users)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(x => x.Cookie.Name = "Beacon.Auth");
        builder.Services.AddSingleton(users.Object);
        builder.Services.AddSingleton<IBeaconAuthenticationProvider>(new DatabaseAuthenticationProvider(users.Object));
        builder.Services.AddSingleton(new Mock<IAntiforgery>().Object);
        builder.Services.AddSingleton(new LoginRateLimiter());

        var app = builder.Build();
        app.MapLoginEndpoints("/beacon", new BeaconConfiguration());
        await app.StartAsync();

        return app;
    }

    private static UserManagementService Service(
        List<BeaconUser> users,
        int minimumLength = 8,
        bool complexity = true,
        List<object>? saved = null,
        SessionStampExpectation? expectation = null)
    {
        var hasher = new Mock<IPasswordHasher>();
        hasher
            .Setup(x => x.VerifyPassword(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns<string, string, string>((password, hash, _) => password == "right" && hash == "hash");
        hasher
            .Setup(x => x.HashPassword(It.IsAny<string>()))
            .Returns<string>(x => ($"hash-of-{x}", "new-salt"));
        saved ??= [];
        var userSet = RecordingBeaconContext.MemorySet(users, saved);
        Mock.Get(userSet)
            .Setup(x => x.FindAsync(It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns<object[], CancellationToken>((keys, _) => ValueTask.FromResult(users.FirstOrDefault(x => x.Id == (int)keys[0])));
        var sets = new Dictionary<Type, object>
        {
            [typeof(BeaconUser)] = userSet,
            [typeof(BeaconRole)] = RecordingBeaconContext.MemorySet(new List<BeaconRole>(), saved)
        };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(sets, saved));
        var configuration = new BeaconConfiguration();
        configuration.UserManagement.MinimumPasswordLength = minimumLength;
        configuration.UserManagement.RequirePasswordComplexity = complexity;

        return new UserManagementService(factory.Object, hasher.Object, Mock.Of<IRoleService>(), configuration, TimeProvider.System, null, expectation);
    }
}
