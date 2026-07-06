using System.Security.Claims;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using Beacon.Core.Authorization;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;
using Beacon.SampleProject.Services;

namespace Beacon.Tests.Unit;

/// <summary>
/// Guards the privilege-escalation fix in <see cref="SampleClaimsTransformation"/>: the role is
/// resolved from the Beacon user store (super-admin flag / role levels) — never inferred from the
/// username string — so self-registration as "admin" cannot grant the Admin role.
/// </summary>
[TestFixture]
public class SampleClaimsTransformationTests
{
    private Mock<IUserManagementService> _userService = null!;
    private SampleClaimsTransformation _transformation = null!;

    [SetUp]
    public void SetUp()
    {
        _userService = new Mock<IUserManagementService>();
        _transformation = new SampleClaimsTransformation(_userService.Object);
    }

    private static ClaimsPrincipal AuthenticatedPrincipal(string username, params Claim[] extraClaims)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, username) };
        claims.AddRange(extraClaims);

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test"));
    }

    private void SetupUser(string username, BeaconUserData? user)
    {
        _userService
            .Setup(x => x.GetUserByUserNameAsync(username, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
    }

    [Test]
    public async Task TransformAsync_SuperAdminUser_GetsAdminRoleClaim()
    {
        SetupUser("alice", new BeaconUserData
        {
            UserName = "alice",
            IsEnabled = true,
            IsSuperAdmin = true
        });

        var result = await _transformation.TransformAsync(AuthenticatedPrincipal("alice"));

        result.FindFirst(BeaconClaims.Role)!.Value.Should().Be(RoleService.RoleNames.Admin);
    }

    [Test]
    public async Task TransformAsync_EditorLevelUser_GetsEditorRoleClaim()
    {
        SetupUser("bob", new BeaconUserData
        {
            UserName = "bob",
            IsEnabled = true,
            IsSuperAdmin = false,
            Roles = new List<BeaconRoleData>
            {
                new() { Name = RoleService.RoleNames.Editor, Level = RoleService.RoleLevels.Editor }
            }
        });

        var result = await _transformation.TransformAsync(AuthenticatedPrincipal("bob"));

        result.FindFirst(BeaconClaims.Role)!.Value.Should().Be(RoleService.RoleNames.Editor);
    }

    [Test]
    public async Task TransformAsync_UnknownUsername_LeavesPrincipalWithoutRoleClaim()
    {
        SetupUser("ghost", null);

        var result = await _transformation.TransformAsync(AuthenticatedPrincipal("ghost"));

        result.HasClaim(x => x.Type == BeaconClaims.Role).Should().BeFalse();
    }

    [Test]
    public async Task TransformAsync_DisabledUser_LeavesPrincipalWithoutRoleClaim()
    {
        SetupUser("dave", new BeaconUserData
        {
            UserName = "dave",
            IsEnabled = false,
            IsSuperAdmin = true
        });

        var result = await _transformation.TransformAsync(AuthenticatedPrincipal("dave"));

        result.HasClaim(x => x.Type == BeaconClaims.Role).Should().BeFalse();
    }

    [Test]
    public async Task TransformAsync_ExistingRoleClaim_IsNotOverwrittenOrDuplicated()
    {
        var principal = AuthenticatedPrincipal("carol", new Claim(BeaconClaims.Role, RoleService.RoleNames.Viewer));

        var result = await _transformation.TransformAsync(principal);

        // Existing role claim short-circuits — the user store is never consulted, and no duplicate is added.
        result.FindAll(BeaconClaims.Role).Should().ContainSingle()
            .Which.Value.Should().Be(RoleService.RoleNames.Viewer);
        _userService.Verify(x => x.GetUserByUserNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
