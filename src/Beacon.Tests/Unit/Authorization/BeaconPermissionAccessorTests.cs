using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Entities;
using Beacon.Core.Mcp;
using Beacon.Core.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.Authorization;

/// <summary>
/// The caller's functional permissions, read live: a stored user holds what its roles in the user store grant now, under
/// the configured role names; an Admin holds them all; permissions mapped from identity-provider app roles count only
/// while the mapping is on; an API key holds none, whatever its owner holds; and a session without a stored user holds
/// what its role claims grant on a host without Beacon user management only.
/// </summary>
[TestFixture]
public class BeaconPermissionAccessorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private static readonly ConditionalWeakTable<LogRecorder, BeaconPermissionLog> SharedLogs = new();

    [Test]
    public async Task AStoredUser_HoldsThePermissionsItsRolesGrantNow()
    {
        var world = new ProjectAccessWorld();
        world.FunctionalUser(3, "ext-ana", RoleService.RoleLevels.Viewer, BeaconPermissions.Auditor, BeaconPermissions.Approver);

        var permissions = await PermissionsAsync(world, Session("ext-ana"));

        permissions.IsAdmin.Should().BeFalse();
        permissions.Permissions.Should().BeEquivalentTo([BeaconPermissions.Auditor, BeaconPermissions.Approver]);
        permissions.Holds(BeaconPermissions.UserAdmin).Should().BeFalse();
    }

    [Test]
    public async Task ARoleClaimTheSessionCarries_GrantsNothing_WhenTheStoredUserNoLongerHoldsTheRole()
    {
        var world = new ProjectAccessWorld();
        world.User(3, "ext-ana");

        var permissions = await PermissionsAsync(world, Session("ext-ana", BeaconPermissions.UserAdmin));

        permissions.Should().Be(BeaconPermissionSet.None);
    }

    [Test]
    public async Task AnAdmin_HoldsEveryPermission()
    {
        var world = new ProjectAccessWorld();
        world.Users.Add(new BeaconUser { Id = 1, ExternalId = "ext-root", UserName = "root", IsEnabled = true, IsSuperAdmin = true });

        var permissions = await PermissionsAsync(world, Session("ext-root"));

        permissions.IsAdmin.Should().BeTrue();
        BeaconPermissions.All.Should().OnlyContain(x => permissions.Holds(x));
    }

    [Test]
    public async Task APermission_IsHeldThroughTheConfiguredRoleName()
    {
        var world = new ProjectAccessWorld();
        var user = world.User(3, "ext-ana");
        user.UserRoles.Add(new BeaconUserRole { UserId = 3, Role = new BeaconRole { Name = "Change Board", Level = RoleService.RoleLevels.Functional } });
        user.UserRoles.Add(new BeaconUserRole { UserId = 3, Role = new BeaconRole { Name = BeaconPermissions.Approver, Level = RoleService.RoleLevels.Functional } });
        var mapping = new RoleMappingOptions { Roles = new FunctionalRoleNames { Approver = "Change Board" } };

        var permissions = await PermissionsAsync(world, Session("ext-ana"), mapping);

        permissions.Permissions.Should().Equal(BeaconPermissions.Approver);
    }

    [Test]
    public async Task IdentityProviderPermissions_CountOnlyWhileTheMappingIsOn()
    {
        var world = new ProjectAccessWorld();
        world.User(3, "ext-ana");
        var session = IdpSession("ext-ana", Now, BeaconPermissions.Auditor);
        var on = IdpMapping();

        var whileOn = await PermissionsAsync(world, session, on);
        var whileOff = await PermissionsAsync(world, session, new RoleMappingOptions());

        whileOn.Permissions.Should().Equal(BeaconPermissions.Auditor);
        whileOff.Should().Be(BeaconPermissionSet.None);
    }

    [TestCase(59, true)]
    [TestCase(60, true)]
    [TestCase(61, false)]
    [TestCase(60 * 24 * 7, false)]
    public async Task IdentityProviderPermissions_CountOnlyWithinTheirLifetimeAfterTheSignIn(int minutesAgo, bool honoured)
    {
        var world = new ProjectAccessWorld();
        world.User(3, "ext-ana");
        var logs = new LogRecorder();
        var session = IdpSession("ext-ana", Now.AddMinutes(-minutesAgo), BeaconPermissions.Auditor);

        var permissions = await PermissionsAsync(world, session, IdpMapping(), logs);
        var again = await PermissionsAsync(world, session, IdpMapping(), logs);

        permissions.Holds(BeaconPermissions.Auditor).Should().Be(honoured, "IdpRoles:ClaimLifetimeMinutes defaults to 60");
        again.Permissions.Should().BeEquivalentTo(permissions.Permissions);
        logs.Entries.Count(x => x.Message.Contains("no longer honoured", StringComparison.Ordinal)).Should().Be(honoured ? 0 : 1, "dropped permissions are noted once per sign-in");
        logs.Entries.Should().NotContain(x => x.Message.Contains("ext-ana", StringComparison.Ordinal));
    }

    [Test]
    public async Task IdentityProviderPermissions_FromAnyOtherIssuer_OrWithoutTheirSignInTime_GrantNothing()
    {
        var world = new ProjectAccessWorld();
        world.User(3, "ext-ana");
        var foreign = Session("ext-ana");
        ((ClaimsIdentity)foreign.Identity!).AddClaims(
        [
            new Claim(BeaconClaims.IdpPermission, BeaconPermissions.UserAdmin),
            new Claim(BeaconClaims.IdpPermissionsIssuedAt, Now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))
        ]);
        var unstamped = Session("ext-ana");
        ((ClaimsIdentity)unstamped.Identity!).AddClaim(new Claim(BeaconClaims.IdpPermission, BeaconPermissions.UserAdmin, ClaimValueTypes.String, BeaconClaims.Issuer));

        (await PermissionsAsync(world, foreign, IdpMapping())).Should().Be(BeaconPermissionSet.None, "only Beacon's sign-in issues these claims");
        (await PermissionsAsync(world, unstamped, IdpMapping())).Should().Be(BeaconPermissionSet.None, "a mapped permission without its sign-in time has no lifetime left");
    }

    [Test]
    public async Task ARoleWithAFunctionalRolesName_ThatIsNoFunctionalRole_GrantsNothing()
    {
        var world = new ProjectAccessWorld();
        var user = world.User(3, "ext-ana");
        user.UserRoles.Add(new BeaconUserRole { UserId = 3, Role = new BeaconRole { Name = BeaconPermissions.UserAdmin, Level = RoleService.RoleLevels.Functional, IsSystemRole = false } });
        user.UserRoles.Add(new BeaconUserRole { UserId = 3, Role = new BeaconRole { Name = BeaconPermissions.Auditor, Level = RoleService.RoleLevels.Editor, IsSystemRole = true } });

        var permissions = await PermissionsAsync(world, Session("ext-ana"));

        permissions.Should().Be(BeaconPermissionSet.None, "only a system role at level 0 is a functional role");
    }

    [Test]
    public async Task UnderSeparationOfDuties_AConflictingPair_GrantsNeitherPermission_HoweverItWasGained()
    {
        var world = new ProjectAccessWorld();
        world.FunctionalUser(3, "ext-ana", RoleService.RoleLevels.Viewer, BeaconPermissions.UserAdmin, BeaconPermissions.DataSourceAdmin);
        var logs = new LogRecorder();
        var separated = IdpMapping();
        separated.SeparationOfDuties.Enabled = true;
        var session = IdpSession("ext-ana", Now, BeaconPermissions.Auditor);

        var on = await PermissionsAsync(world, session, separated, logs);
        var off = await PermissionsAsync(world, session, IdpMapping());

        on.Permissions.Should().Equal(BeaconPermissions.DataSourceAdmin);
        off.Permissions.Should().BeEquivalentTo([BeaconPermissions.UserAdmin, BeaconPermissions.DataSourceAdmin, BeaconPermissions.Auditor]);
        var warning = logs.Entries.Should().ContainSingle(x => x.Message.StartsWith("Separation of duties", StringComparison.Ordinal)).Subject;
        warning.Level.Should().Be(LogLevel.Warning);
        warning.Message.Should().Contain("Auditor+UserAdmin").And.Contain("caller 3");
    }

    [Test]
    public async Task AnApiKey_HoldsNoPermission_WhateverItsOwnerHolds()
    {
        var world = new ProjectAccessWorld();
        world.FunctionalUser(3, "ext-ana", RoleService.RoleLevels.Editor, BeaconPermissions.UserAdmin);
        var key = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "3"),
                new Claim(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.ApiKeyAuthMethod),
                new Claim(McpCallerClaimTypes.Scope, "Execute"),
                new Claim(ClaimTypes.Role, BeaconPermissions.UserAdmin)
            ],
            McpCallerClaimTypes.ApiKeyAuthenticationType));

        var permissions = await PermissionsAsync(world, key);

        permissions.Should().Be(BeaconPermissionSet.None);
    }

    [Test]
    public async Task ASessionWithoutAStoredUser_HoldsWhatItsRoleClaimsGrant_OnlyWithoutUserManagementAndOnlyOnOptIn()
    {
        var withoutUserManagement = new ProjectAccessWorld { UserManagement = false };
        var withUserManagement = new ProjectAccessWorld();
        var hostPrincipal = Session("host-user", BeaconPermissions.Auditor);
        var optedIn = new RoleMappingOptions { Roles = new FunctionalRoleNames { HonourHostRoleClaims = true } };

        var notOptedIn = await PermissionsAsync(withoutUserManagement, hostPrincipal);
        var off = await PermissionsAsync(withoutUserManagement, hostPrincipal, optedIn);
        var on = await PermissionsAsync(withUserManagement, hostPrincipal, optedIn);
        var hostAdmin = await PermissionsAsync(withoutUserManagement, Session("host-admin", RoleService.RoleNames.Admin));

        notOptedIn.Should().Be(BeaconPermissionSet.None, "a host's role claims grant a permission only with Roles:HonourHostRoleClaims");
        off.Permissions.Should().Equal(BeaconPermissions.Auditor);
        on.Should().Be(BeaconPermissionSet.None, "with user management on every caller that may use Beacon is a stored user");
        hostAdmin.IsAdmin.Should().BeTrue("the Admin claim holds every permission without the opt-in");
    }

    [Test]
    public async Task AnAnonymousRequest_HoldsNoPermission()
    {
        var permissions = await PermissionsAsync(new ProjectAccessWorld(), new ClaimsPrincipal(new ClaimsIdentity()));

        permissions.Should().Be(BeaconPermissionSet.None);
    }

    private static async Task<BeaconPermissionSet> PermissionsAsync(
        ProjectAccessWorld world,
        ClaimsPrincipal principal,
        RoleMappingOptions? mapping = null,
        LogRecorder? logs = null)
    {
        var services = new ServiceCollection();
        world.Register(services);
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } });
        services.AddSingleton(Options.Create(mapping ?? new RoleMappingOptions()));
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        services.AddSingleton(SharedLog(logs ?? new LogRecorder()));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IBeaconPermissionAccessor>().GetCurrentAsync(CancellationToken.None);
    }

    // One log helper per recorder, so a note given once is given once across the scopes of a test.
    private static BeaconPermissionLog SharedLog(LogRecorder logs)
    {
        lock (SharedLogs)
        {
            if (!SharedLogs.TryGetValue(logs, out var log))
            {
                log = new BeaconPermissionLog(TimeProvider.System, logs.For<BeaconPermissionLog>());
                SharedLogs.Add(logs, log);
            }

            return log;
        }
    }

    private static RoleMappingOptions IdpMapping()
    {
        return new RoleMappingOptions { IdpRoles = new IdpRoleOptions { Enabled = true, Map = { ["beacon.auditor"] = BeaconPermissions.Auditor } } };
    }

    // A session as Beacon's sign-in leaves it after mapping app roles: the permissions and the time of the sign-in, both
    // issued by Beacon.
    private static ClaimsPrincipal IdpSession(string externalId, DateTimeOffset signedInAt, params string[] permissions)
    {
        var session = Session(externalId);
        var identity = (ClaimsIdentity)session.Identity!;
        identity.AddClaim(new Claim(
            BeaconClaims.IdpPermissionsIssuedAt,
            signedInAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ClaimValueTypes.Integer64,
            BeaconClaims.Issuer));
        foreach (var permission in permissions)
        {
            identity.AddClaim(new Claim(BeaconClaims.IdpPermission, permission, ClaimValueTypes.String, BeaconClaims.Issuer));
        }

        return session;
    }

    private static ClaimsPrincipal Session(string externalId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, externalId) };
        claims.AddRange(roles.Select(x => new Claim(ClaimTypes.Role, x)));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies"));
    }
}
