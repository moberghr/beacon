using System.Security.Claims;
using Beacon.Core.Authorization;
using Beacon.Core.Data.Entities;
using Beacon.Core.Mcp;
using Beacon.Core.Services;
using Beacon.Core.Services.Embed;
using Beacon.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Embed;

/// <summary>
/// How the request's actor reads an embed principal: before anything takes it for a user or a session, as an embed caller
/// for its grant that acts for the grant's creator (their live stored user, never an Admin, naming no user) in the grant's
/// one project. A revoked or expired grant, a creator who is no longer active (also when their external id now belongs to a
/// user created after the grant), and a principal that only claims to be embed get no user, which the evaluator gives nothing; the embed principal carries no NameIdentifier, name or role claim.
/// </summary>
[TestFixture]
public class EmbedActorTests
{
    [TestCase(true)]
    [TestCase(false)]
    public async Task AnEmbedPrincipal_ActsForTheGrantsCreator_InTheGrantsOneProject(bool token)
    {
        var credential = token ? EmbedCredential.Token : EmbedCredential.GrantSecret;
        var kit = new EmbedTestKit();
        var (grant, _) = kit.Grant();

        var actor = await Resolve(kit, EmbedPrincipal.Create(grant.Id, credential));

        actor.Kind.Should().Be(BeaconPrincipalKind.Embed);
        actor.EmbedGrantId.Should().Be(grant.Id);
        actor.EmbedCredential.Should().Be(credential);
        actor.BeaconUserId.Should().Be(EmbedTestKit.CreatorId);
        actor.IsActiveUser.Should().BeTrue();
        actor.AllowedProjectIds.Should().Equal(EmbedTestKit.ProjectId);
        actor.UserId.Should().BeNull("an embed caller names no user, so no owner rule takes it for the creator");
        actor.IsAdmin.Should().BeFalse();
        actor.IsLiveAdmin.Should().BeFalse();
    }

    [Test]
    public async Task AnAdminCreator_StaysNoAdmin_AsAnEmbedCaller()
    {
        var kit = new EmbedTestKit();
        kit.Creator.IsSuperAdmin = true;
        var (grant, _) = kit.Grant();

        var actor = await Resolve(kit, EmbedPrincipal.Create(grant.Id, EmbedCredential.Token));

        actor.IsAdmin.Should().BeFalse();
        actor.IsLiveAdmin.Should().BeFalse();
        (await TestProjectAccess.RealEvaluator(actor, kit.Data.Store.Factory().Object).GetScopeAsync(actor, CancellationToken.None))
            .IsUnrestricted.Should().BeFalse("never the Admin bypass");
    }

    [Test]
    public async Task ARevokedOrExpiredGrant_OrAnInactiveCreator_ActsForNobody()
    {
        var kit = new EmbedTestKit();
        var (revoked, _) = kit.Grant(revoked: EmbedTestKit.Now.UtcDateTime);
        var (expiring, _) = kit.Grant(expiresInDays: 1);
        var (live, _) = kit.Grant();

        (await Resolve(kit, EmbedPrincipal.Create(revoked.Id, EmbedCredential.Token))).BeaconUserId.Should().BeNull();
        kit.Clock.Advance(TimeSpan.FromDays(2));
        (await Resolve(kit, EmbedPrincipal.Create(expiring.Id, EmbedCredential.Token))).BeaconUserId.Should().BeNull();
        kit.Creator.IsEnabled = false;
        var disabled = await Resolve(kit, EmbedPrincipal.Create(live.Id, EmbedCredential.Token));
        disabled.BeaconUserId.Should().BeNull();
        disabled.Kind.Should().Be(BeaconPrincipalKind.Embed);

        var scope = await TestProjectAccess.RealEvaluator(disabled, kit.Data.Store.Factory().Object).GetScopeAsync(disabled, CancellationToken.None);
        scope.Allows(EmbedTestKit.ProjectId, ProjectAction.Read).Should().BeFalse();
    }

    [Test]
    public async Task AnArchivedCreatorsGrant_StaysDead_WhenTheirExternalIdIsGivenToANewUser()
    {
        var kit = new EmbedTestKit();
        var (grant, _) = kit.Grant();
        kit.Creator.ArchivedTime = EmbedTestKit.Now.UtcDateTime.AddDays(1);
        kit.Data.Store.ListFor<BeaconUser>().Add(new BeaconUser
        {
            Id = 40,
            ExternalId = EmbedTestKit.CreatorExternalId,
            UserName = "ada-again",
            IsEnabled = true,
            CreatedTime = EmbedTestKit.Now.UtcDateTime.AddDays(2),
            UserRoles = [new BeaconUserRole { UserId = 40, Role = new BeaconRole { Name = "Editor", Level = RoleService.RoleLevels.Editor } }]
        });

        var actor = await Resolve(kit, EmbedPrincipal.Create(grant.Id, EmbedCredential.Token));

        actor.Kind.Should().Be(BeaconPrincipalKind.Embed);
        actor.BeaconUserId.Should().BeNull("the user now holding the id was created after the grant");
        var scope = await TestProjectAccess.RealEvaluator(actor, kit.Data.Store.Factory().Object).GetScopeAsync(actor, CancellationToken.None);
        scope.Allows(EmbedTestKit.ProjectId, ProjectAction.Read).Should().BeFalse();
    }

    [Test]
    public async Task APrincipalThatOnlyClaimsToBeEmbed_IsNobody_NeverASession()
    {
        var kit = new EmbedTestKit();
        var (grant, _) = kit.Grant();
        var forged = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, EmbedTestKit.CreatorExternalId),
                new Claim(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.EmbedTokenAuthMethod),
                new Claim(McpCallerClaimTypes.EmbedGrantId, grant.Id.ToString())
            ],
            "Cookies"));

        var actor = await Resolve(kit, forged);

        actor.Should().Be(BeaconActor.Nobody);
    }

    [Test]
    public void TheEmbedPrincipal_CarriesNoNameIdentifierNameOrRole()
    {
        var principal = EmbedPrincipal.Create(9, EmbedCredential.Token);

        principal.FindFirst(ClaimTypes.NameIdentifier).Should().BeNull();
        principal.FindFirst(ClaimTypes.Name).Should().BeNull();
        principal.FindFirst(ClaimTypes.Role).Should().BeNull();
        principal.FindFirst(BeaconClaims.UserId).Should().BeNull();
        principal.Claims.Select(x => x.Type).Should().BeEquivalentTo([McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.EmbedGrantId]);
        McpCallerClaimTypes.Reserved.Should().Contain(McpCallerClaimTypes.EmbedGrantId, "a bearer token never sets it for itself");
    }

    private static Task<BeaconActor> Resolve(EmbedTestKit kit, ClaimsPrincipal principal)
    {
        return BeaconActorAccessor.ResolveAsync(principal, kit.Data.Store.Factory().Object, CancellationToken.None, beaconManagesUsers: true, kit.Clock);
    }
}
