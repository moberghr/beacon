using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.Api.Authentication;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Mcp;
using Beacon.Core.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.Authorization;

/// <summary>
/// What a host is told at startup about functional permissions: what grants them (stored roles, or on a host without
/// Beacon user management the Admin role claim, and other role claims only on opt-in), the role names, identity-provider
/// mapping and separation of duties; and, with separation of duties on, how many active users' stored roles already
/// conflict. An API key refused by a route that requires a permission is told that keys hold none.
/// </summary>
[TestFixture]
public class BeaconPermissionStartupNoteTests
{
    [TestCase(true, false, "stored roles")]
    [TestCase(false, false, "the Admin role claim only")]
    [TestCase(false, true, "the session's role claims (HonourHostRoleClaims)")]
    public void TheStartupNote_NamesWhatGrantsFunctionalPermissions(bool userManagement, bool honourHostRoleClaims, string source)
    {
        var logs = new LogRecorder();
        var configuration = new Beacon.Core.BeaconConfiguration();
        configuration.UserManagement.Enabled = userManagement;
        var mapping = new RoleMappingOptions
        {
            Roles = new FunctionalRoleNames { HonourHostRoleClaims = honourHostRoleClaims, Auditor = "Compliance Reviewer" },
            SeparationOfDuties = new SeparationOfDutiesOptions { Enabled = true }
        };

        new BeaconPermissionStartupNote(Options.Create(mapping), logs.For<BeaconPermissionStartupNote>(), configuration).Configure(_ => { });

        var note = logs.Entries.Should().ContainSingle().Subject;
        note.Level.Should().Be(LogLevel.Information);
        note.Message.Should().Contain(source).And.Contain("Auditor=Compliance Reviewer").And.Contain("separation of duties True");
    }

    [Test]
    public async Task UsersWhoseStoredRolesConflict_AreCounted_OnlyActiveAndOnlyThroughFunctionalRoles()
    {
        var users = new List<BeaconUser>
        {
            WithRoles(new BeaconUser { Id = 1, ExternalId = "a", UserName = "a", IsEnabled = true }, Functional(BeaconPermissions.UserAdmin), Functional(BeaconPermissions.Auditor)),
            WithRoles(new BeaconUser { Id = 2, ExternalId = "b", UserName = "b", IsEnabled = false }, Functional(BeaconPermissions.UserAdmin), Functional(BeaconPermissions.Auditor)),
            WithRoles(new BeaconUser { Id = 3, ExternalId = "c", UserName = "c", IsEnabled = true }, Functional(BeaconPermissions.UserAdmin), new BeaconRole { Name = BeaconPermissions.Auditor, Level = 0, IsSystemRole = false }),
            WithRoles(new BeaconUser { Id = 4, ExternalId = "d", UserName = "d", IsEnabled = true }, Functional(BeaconPermissions.UserAdmin), Functional(BeaconPermissions.DataSourceAdmin))
        };
        var userRoles = users
            .SelectMany(x => x.UserRoles)
            .ToList();
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(
                new Dictionary<Type, object> { [typeof(BeaconUserRole)] = RecordingBeaconContext.MemorySet(userRoles, []) },
                []));

        var count = await SeparationOfDutiesReport.CountUsersWithConflictsAsync(factory.Object, new RoleMappingOptions(), CancellationToken.None);

        count.Should().Be(1, "a disabled user, a role that only has a functional role's name and a pair that does not conflict count for nothing");
    }

    [Test]
    public void AnApiKey_RefusedByARouteThatRequiresAPermission_IsToldKeysHoldNone()
    {
        var key = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.ApiKeyAuthMethod)],
            McpCallerClaimTypes.ApiKeyAuthenticationType));
        var session = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity("Cookies"));

        var keyDetail = AuthServiceExtensions.ApiKeyRefusalDetail(Context(key, required: true));
        var sessionDetail = AuthServiceExtensions.ApiKeyRefusalDetail(Context(session, required: true));
        var admittedOnly = AuthServiceExtensions.ApiKeyRefusalDetail(Context(key, required: false));

        keyDetail.Should().Be("API keys cannot use administrative routes; sign in as a user with the UserAdmin permission.");
        sessionDetail.Should().BeNull("every other refusal keeps the plain body");
        admittedOnly.Should().BeNull();
    }

    private static DefaultHttpContext Context(System.Security.Claims.ClaimsPrincipal user, bool required)
    {
        var context = new DefaultHttpContext { User = user };
        context.SetEndpoint(new Endpoint(
            null,
            new EndpointMetadataCollection(new BeaconFunctionalPermissionMetadata([BeaconPermissions.UserAdmin], required)),
            "route"));

        return context;
    }

    private static BeaconRole Functional(string permission)
    {
        return new BeaconRole { Name = permission, Level = RoleService.RoleLevels.Functional, IsSystemRole = true };
    }

    private static BeaconUser WithRoles(BeaconUser user, params BeaconRole[] roles)
    {
        user.UserRoles = roles
            .Select(x => new BeaconUserRole { UserId = user.Id, User = user, Role = x })
            .ToList();

        return user;
    }
}
