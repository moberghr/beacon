using System.Net;
using System.Security.Claims;
using Beacon.Api.Authentication;
using Beacon.Core.Authentication;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// SSO admission: who may sign in at all (tenant allow-list, Entra guest block, optional app role / group), the startup
/// rule that SSO cannot be enabled without a tenant decision, and front-channel sign-out being opt-in.
/// </summary>
[TestFixture]
public class OidcAdmissionTests
{
    private const string TenantId = "tenant-1";
    private const string TenantIssuer = "https://login.microsoftonline.com/tenant-1/v2.0";

    [Test]
    public void AllowedTenantMember_IsAdmitted()
    {
        OidcAdmission.Evaluate(Principal(), Options()).Should().Be(OidcAdmissionDecision.Admitted);
    }

    [Test]
    public void TenantMatch_IgnoresCase()
    {
        OidcAdmission.Evaluate(Principal(), Options(x => x.AllowedTenants = ["TENANT-1"]))
            .Should().Be(OidcAdmissionDecision.Admitted);
    }

    [Test]
    public void ForeignTenant_IsRejected()
    {
        OidcAdmission.Evaluate(Principal(tenantId: "tenant-2"), Options())
            .Should().Be(OidcAdmissionDecision.TenantNotAllowed);
    }

    [Test]
    public void MissingTenantClaim_IsRejected_UnlessAnyTenantIsAllowed()
    {
        OidcAdmission.Evaluate(Principal(tenantId: null), Options())
            .Should().Be(OidcAdmissionDecision.TenantNotAllowed);
        OidcAdmission.Evaluate(Principal(tenantId: null), Options(x => x.AllowAnyTenant = true))
            .Should().Be(OidcAdmissionDecision.Admitted);
    }

    [TestCase("acct", "1")]
    [TestCase("idp", "https://sts.windows.net/guest-home-tenant/")]
    [TestCase("idp", "live.com")]
    public void Guest_IsRejected(string claimType, string value)
    {
        OidcAdmission.Evaluate(Principal(extra: new Claim(claimType, value)), Options())
            .Should().Be(OidcAdmissionDecision.Guest);
    }

    [TestCase("acct", "0")]
    [TestCase("idp", TenantIssuer)]
    [TestCase("idp", "https://sts.windows.net/tenant-1/")]
    public void Member_WithAccountOrProviderClaims_IsAdmitted(string claimType, string value)
    {
        OidcAdmission.Evaluate(Principal(extra: new Claim(claimType, value)), Options())
            .Should().Be(OidcAdmissionDecision.Admitted);
    }

    [TestCase("https://sts.windows.net/tenant-1/")]
    [TestCase("https://login.microsoftonline.com/tenant-1/v2.0")]
    public void Member_WhoseIdentityProviderIsTheTenantsOwnIssuer_IsAdmitted_EvenUnderAnotherIssuer(string identityProvider)
    {
        var principal = Principal(extra: new Claim("idp", identityProvider));
        principal.Identities.First().RemoveClaim(principal.FindFirst("iss")!);
        principal.Identities.First().AddClaim(new Claim("iss", "https://login.microsoftonline.com/tenant-1/"));

        OidcAdmission.Evaluate(principal, Options()).Should().Be(OidcAdmissionDecision.Admitted);
    }

    [TestCase("https://evil.example/tenant-1")]
    [TestCase("https://evil.example/tenant-1/v2.0")]
    [TestCase("https://sts.windows.net/tenant-1/extra")]
    [TestCase("tenant-1")]
    public void IdentityProvider_MerelyContainingTheTenantId_IsAGuest(string identityProvider)
    {
        OidcAdmission.Evaluate(Principal(extra: new Claim("idp", identityProvider)), Options())
            .Should().Be(OidcAdmissionDecision.Guest);
    }

    [Test]
    public void TokenWithoutTenant_WithAGuestAccountFlag_HasNoGuestNotion_ButNeedsAnyTenant()
    {
        var principal = Principal(tenantId: null, extra: new Claim("acct", "1"));

        OidcAdmission.Evaluate(principal, Options()).Should().Be(OidcAdmissionDecision.TenantNotAllowed);
        OidcAdmission.Evaluate(principal, Options(x => x.AllowAnyTenant = true)).Should().Be(OidcAdmissionDecision.Admitted);
    }

    [Test]
    public void PersonalMicrosoftAccounts_AreNotAnAllowedTenant()
    {
        const string personalAccountsTenant = "9188040d-6c67-4c5b-b112-36a304b66dad";

        OidcAdmission.Evaluate(Principal(tenantId: personalAccountsTenant), Options())
            .Should().Be(OidcAdmissionDecision.TenantNotAllowed);
    }

    [Test]
    public void AllowedTenants_AreTrimmed()
    {
        OidcAdmission.Evaluate(Principal(), Options(x => x.AllowedTenants = ["  tenant-1  "]))
            .Should().Be(OidcAdmissionDecision.Admitted);
    }

    [Test]
    public void BlankRequiredRolesAndGroups_AreNoRequirement()
    {
        var options = Options(x =>
        {
            x.RequiredRoles = ["", "  "];
            x.RequiredGroups = [" "];
        });

        OidcAdmission.Evaluate(Principal(), options).Should().Be(OidcAdmissionDecision.Admitted);
    }

    [Test]
    public void RequiredRolesAndGroups_MatchIgnoringCase_AndTrimmed()
    {
        var options = Options(x =>
        {
            x.RequiredRoles = [" beacon.user "];
            x.RequiredGroups = ["GROUP-A"];
        });

        OidcAdmission.Evaluate(Principal(extra: new Claim("roles", "Beacon.User")), options)
            .Should().Be(OidcAdmissionDecision.Admitted);
        OidcAdmission.Evaluate(Principal(extra: new Claim("groups", "group-a")), options)
            .Should().Be(OidcAdmissionDecision.Admitted);
    }

    [Test]
    public void ClaimsAreReadByTheirExactNames()
    {
        // "TID" is not the tenant claim, and "ACCT" is not the guest flag.
        OidcAdmission.Evaluate(Principal(tenantId: null, extra: new Claim("TID", TenantId)), Options())
            .Should().Be(OidcAdmissionDecision.TenantNotAllowed);
        OidcAdmission.Evaluate(Principal(extra: new Claim("ACCT", "1")), Options())
            .Should().Be(OidcAdmissionDecision.Admitted);
    }

    [Test]
    public void TokenWithNeitherAccountNorIdentityProvider_IsAdmitted_AndLacksAGuestSignal()
    {
        OidcAdmission.Evaluate(Principal(), Options()).Should().Be(OidcAdmissionDecision.Admitted);
        OidcAdmission.LacksGuestSignal(Principal()).Should().BeTrue();
        OidcAdmission.LacksGuestSignal(Principal(extra: new Claim("acct", "0"))).Should().BeFalse();
        OidcAdmission.LacksGuestSignal(Principal(tenantId: null)).Should().BeFalse("only Entra tokens have a guest notion");
    }

    [Test]
    public void Guest_IsAdmitted_WhenTheHostTurnsTheGuestBlockOff()
    {
        OidcAdmission.Evaluate(Principal(extra: new Claim("acct", "1")), Options(x => x.BlockGuests = false))
            .Should().Be(OidcAdmissionDecision.Admitted);
    }

    [Test]
    public void TokenWithoutTenant_HasNoGuestNotion()
    {
        // A non-Entra identity provider (AllowAnyTenant) may use "idp" for something else entirely.
        var principal = Principal(tenantId: null, extra: new Claim("idp", "corporate-ldap"));

        OidcAdmission.Evaluate(principal, Options(x => x.AllowAnyTenant = true))
            .Should().Be(OidcAdmissionDecision.Admitted);
    }

    [Test]
    public void RequiredRoleOrGroup_AnyOfThemAdmits()
    {
        var options = Options(x =>
        {
            x.RequiredRoles = ["Beacon.User"];
            x.RequiredGroups = ["group-a"];
        });

        OidcAdmission.Evaluate(Principal(extra: new Claim("roles", "Beacon.User")), options)
            .Should().Be(OidcAdmissionDecision.Admitted);
        OidcAdmission.Evaluate(Principal(extra: new Claim("groups", "group-a")), options)
            .Should().Be(OidcAdmissionDecision.Admitted);
        OidcAdmission.Evaluate(Principal(extra: new Claim("roles", "Other")), options)
            .Should().Be(OidcAdmissionDecision.MissingRequiredMembership);
        OidcAdmission.Evaluate(Principal(), options)
            .Should().Be(OidcAdmissionDecision.MissingRequiredMembership);
    }

    [Test]
    public void Validate_EnabledWithoutATenantDecision_FailsStartup()
    {
        var options = Options(x => x.AllowedTenants = []);

        options.Invoking(x => x.Validate())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*AllowedTenants*");
    }

    [Test]
    public void Validate_EnabledWithAllowAnyTenant_Passes()
    {
        var options = Options(x =>
        {
            x.AllowedTenants = [];
            x.AllowAnyTenant = true;
        });

        options.Invoking(x => x.Validate()).Should().NotThrow();
    }

    [TestCase(null, "beacon-client", "secret")]
    [TestCase("https://login.example.test/", null, "secret")]
    [TestCase("https://login.example.test/", "beacon-client", null)]
    [TestCase(" ", "beacon-client", "secret")]
    public void Validate_EnabledWithoutAuthorityClientIdOrSecret_FailsStartup(string? authority, string? clientId, string? secret)
    {
        var options = Options(x =>
        {
            x.Authority = authority;
            x.ClientId = clientId;
            x.ClientSecret = secret;
        });

        options.Invoking(x => x.Validate())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*Authority, ClientId, or ClientSecret*");
    }

    [Test]
    public void Validate_OnlyBlankAllowedTenants_FailsStartup()
    {
        var options = Options(x => x.AllowedTenants = ["", "   "]);

        options.Invoking(x => x.Validate())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*AllowedTenants*");
    }

    [TestCase("https://login.microsoftonline.com/organizations/v2.0", true)]
    [TestCase("https://login.microsoftonline.us/tenant-1/v2.0", true)]
    [TestCase("https://sts.windows.net/tenant-1/", true)]
    [TestCase("https://keycloak.example.test/realms/beacon", false)]
    [TestCase("not a url", false)]
    public void StartupWarning_AllowAnyTenantWithAnEntraAuthority_IsLogged(string authority, bool warns)
    {
        var logs = new Beacon.Tests.Common.LogRecorder();
        var options = Options(x =>
        {
            x.Authority = authority;
            x.AllowAnyTenant = true;
        });
        var filter = new OidcStartupWarnings(Microsoft.Extensions.Options.Options.Create(options), logs.For<OidcStartupWarnings>());

        filter.Configure(_ => { });

        logs.Entries.Any(x => x.Message.Contains("AllowAnyTenant")).Should().Be(warns);
    }

    [Test]
    public void StartupWarning_TenantAllowList_IsQuiet()
    {
        var logs = new Beacon.Tests.Common.LogRecorder();
        var filter = new OidcStartupWarnings(Microsoft.Extensions.Options.Options.Create(Options()), logs.For<OidcStartupWarnings>());

        filter.Configure(_ => { });

        logs.Entries.Should().BeEmpty();
    }

    [Test]
    public void Validate_Disabled_NeedsNothing()
    {
        new OidcAuthenticationOptions().Invoking(x => x.Validate()).Should().NotThrow();
    }

    [Test]
    public void Defaults_GiveNoRoleAndBlockGuests()
    {
        var options = new OidcAuthenticationOptions();

        options.DefaultRoleName.Should().BeNull();
        options.BlockGuests.Should().BeTrue();
        options.AllowAnyTenant.Should().BeFalse();
        options.EnableFrontChannelLogout.Should().BeFalse();
    }

    [Test]
    public void AddBeaconOidcAuthentication_EnabledWithoutATenantDecision_FailsStartup()
    {
        var services = new ServiceCollection();
        var configuration = OidcConfiguration(allowedTenant: null);

        var register = () => services.AddBeaconOidcAuthentication(configuration);

        register.Should().Throw<InvalidOperationException>().WithMessage("*AllowedTenants*");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FrontChannelSignOut_IsServedOnlyOnOptIn(bool enabled)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddAuthentication().AddCookie(CookieAuthenticationDefaults.AuthenticationScheme);
        services.AddBeaconOidcAuthentication(OidcConfiguration(TenantId, frontChannelLogout: enabled));
        using var provider = services.BuildServiceProvider();

        var oidc = provider
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OpenIdConnectDefaults.AuthenticationScheme);

        oidc.RemoteSignOutPath.HasValue.Should().Be(enabled);
        if (enabled)
        {
            oidc.RemoteSignOutPath.Value.Should().Be("/signout-oidc");
        }
    }

    [Test]
    public async Task FrontChannelSignOutPath_ByDefault_DoesNotEndTheSession()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Services.AddDataProtection();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, x => x.Cookie.Name = "Beacon.Auth");
        builder.Services.AddBeaconOidcAuthentication(OidcConfiguration(TenantId));
        await using var app = builder.Build();
        app.UseAuthentication();
        await app.StartAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "/signout-oidc");
        request.Headers.Add("Cookie", "Beacon.Auth=session-cookie-value");
        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Headers.Contains("Set-Cookie").Should().BeFalse("nothing clears the session cookie");
    }

    private static OidcAuthenticationOptions Options(Action<OidcAuthenticationOptions>? configure = null)
    {
        var options = new OidcAuthenticationOptions
        {
            Enabled = true,
            Authority = TenantIssuer,
            ClientId = "beacon-client",
            ClientSecret = "unused-test-value",
            AllowedTenants = [TenantId]
        };
        configure?.Invoke(options);

        return options;
    }

    private static ClaimsPrincipal Principal(string? tenantId = TenantId, params Claim[] extra)
    {
        var claims = new List<Claim>
        {
            new("sub", "subject-1"),
            new("iss", TenantIssuer)
        };
        if (tenantId != null)
        {
            claims.Add(new Claim("tid", tenantId));
        }

        claims.AddRange(extra);

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "oidc"));
    }

    private static IConfiguration OidcConfiguration(string? allowedTenant, bool frontChannelLogout = false)
    {
        var values = new Dictionary<string, string?>
        {
            ["Beacon:Authentication:Oidc:Enabled"] = "true",
            ["Beacon:Authentication:Oidc:Authority"] = "https://login.example.test/",
            ["Beacon:Authentication:Oidc:ClientId"] = "beacon-client",
            ["Beacon:Authentication:Oidc:ClientSecret"] = "unused-test-value",
            ["Beacon:Authentication:Oidc:EnableFrontChannelLogout"] = frontChannelLogout.ToString()
        };
        if (allowedTenant != null)
        {
            values["Beacon:Authentication:Oidc:AllowedTenants:0"] = allowedTenant;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
