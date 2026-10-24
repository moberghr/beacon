using Beacon.Core.Authorization;
using Beacon.Core.Data.Entities;
using Beacon.Core.Handlers.Embed;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services.Embed;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Embed;

/// <summary>
/// Exchanging a grant's secret for an embed token: only the grant's own principal exchanges, and only while the grant is
/// live; the body names exactly the locked parameters, each of its type; the lifetime is the default, or shorter on request,
/// never past the maximum or the grant's own expiry; the exchange is recorded with the lifetime and stamps the grant.
/// </summary>
[TestFixture]
public class IssueEmbedTokenHandlerTests
{
    [Test]
    public async Task AnExchange_IssuesATokenForTheGrant_WithTheLockedValues()
    {
        var kit = new EmbedTestKit();
        var (grant, _) = kit.Grant();
        var audit = new RecordingSecurityAudit(kit.Actor(grant, EmbedCredential.GrantSecret));

        var result = await Handler(kit, grant, audit: audit).Handle(new IssueEmbedTokenCommand(Locked("7"), null), CancellationToken.None);

        var check = await kit.Codec.ValidateAsync(result.Token);
        check.Payload!.GrantPublicId.Should().Be(grant.PublicId);
        check.Payload.Parameters.Should().Equal(Locked("7"));
        result.ExpiresAt.Should().Be(EmbedTestKit.Now.AddSeconds(kit.Options.TokenTtlSeconds));
        result.EmbedPath.Should().Be($"/beacon/embed/{grant.PublicId}#t={result.Token}");
        grant.LastTokenIssuedTime.Should().Be(EmbedTestKit.Now.UtcDateTime);

        var issued = audit.Single(result.Token, "7");
        issued.Action.Should().Be(SecurityAuditActions.EmbedTokenIssued);
        issued.DetailsJson.Should().Be($"{{\"ttlSeconds\":{kit.Options.TokenTtlSeconds}}}");
        audit.AssertStagedEventsSaved();
    }

    [TestCase(60, 60)]
    [TestCase(5000, 900)]
    public async Task TheLifetime_IsTheOneAskedFor_NeverPastTheMaximum(int asked, int expected)
    {
        var kit = new EmbedTestKit();
        var (grant, _) = kit.Grant();

        var result = await Handler(kit, grant).Handle(new IssueEmbedTokenCommand(Locked("7"), asked), CancellationToken.None);

        result.ExpiresAt.Should().Be(EmbedTestKit.Now.AddSeconds(expected));
    }

    [Test]
    public async Task TheLifetime_NeverOutlivesTheGrant()
    {
        var kit = new EmbedTestKit();
        var (grant, _) = kit.Grant(expiresInDays: 1);
        kit.Clock.Advance(TimeSpan.FromDays(1) - TimeSpan.FromSeconds(40));

        var result = await Handler(kit, grant).Handle(new IssueEmbedTokenCommand(Locked("7"), null), CancellationToken.None);

        result.ExpiresAt.Should().Be(grant.ExpiresTime);
    }

    [TestCase(0)]
    [TestCase(-5)]
    public async Task ALifetimeBelowOneSecond_IsRefused(int asked)
    {
        var kit = new EmbedTestKit();
        var (grant, _) = kit.Grant();

        var act = () => Handler(kit, grant).Handle(new IssueEmbedTokenCommand(Locked("7"), asked), CancellationToken.None);

        await act.Should().ThrowAsync<EmbedRequestInvalidException>();
    }

    [TestCase("{}", "*Missing value*branch*")]
    [TestCase("{\"branch\":\"7\",\"region\":\"US\"}", "*'region' is not one the host supplies*")]
    [TestCase("{\"branch\":\"7\",\"other\":\"1\"}", "*'other' is not one the host supplies*")]
    [TestCase("{\"branch\":\"seven\"}", "*must be a number*")]
    public async Task TheLockedValues_AreExactlyTheGrantsLockedParameters_OfTheirTypes(string lockedJson, string expected)
    {
        var kit = new EmbedTestKit();
        var (grant, _) = kit.Grant();
        var locked = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(lockedJson)!;

        var act = () => Handler(kit, grant).Handle(new IssueEmbedTokenCommand(locked, null), CancellationToken.None);

        await act.Should().ThrowAsync<EmbedRequestInvalidException>().WithMessage(expected);
        grant.LastTokenIssuedTime.Should().BeNull();
    }

    [Test]
    public async Task OnlyTheGrantsOwnPrincipal_Exchanges()
    {
        var kit = new EmbedTestKit();
        var (grant, _) = kit.Grant();
        var admin = new BeaconActor(EmbedTestKit.CreatorExternalId, true) { Kind = BeaconPrincipalKind.User, BeaconUserId = EmbedTestKit.CreatorId, IsActiveUser = true };

        var asUser = () => Handler(kit, grant, admin).Handle(new IssueEmbedTokenCommand(Locked("7"), null), CancellationToken.None);
        var asToken = () => Handler(kit, grant, kit.Actor(grant, EmbedCredential.Token)).Handle(new IssueEmbedTokenCommand(Locked("7"), null), CancellationToken.None);
        var withoutCreator = () => Handler(kit, grant, kit.Actor(grant, EmbedCredential.GrantSecret) with { BeaconUserId = null })
            .Handle(new IssueEmbedTokenCommand(Locked("7"), null), CancellationToken.None);

        await asUser.Should().ThrowAsync<EmbedUnavailableException>();
        await asToken.Should().ThrowAsync<EmbedUnavailableException>();
        await withoutCreator.Should().ThrowAsync<EmbedUnavailableException>();
        grant.LastTokenIssuedTime.Should().BeNull();
    }

    [Test]
    public async Task ARevokedOrSupersededGrant_IssuesNothing()
    {
        var kit = new EmbedTestKit();
        var (revoked, _) = kit.Grant(revoked: EmbedTestKit.Now.UtcDateTime);
        var (superseded, _) = kit.Grant();

        var revokedAct = () => Handler(kit, revoked).Handle(new IssueEmbedTokenCommand(Locked("7"), null), CancellationToken.None);
        (await revokedAct.Should().ThrowAsync<EmbedUnavailableException>()).Which.Reason.Should().Be(EmbedRefusalCodes.Revoked);

        kit.Supersede();
        var supersededAct = () => Handler(kit, superseded).Handle(new IssueEmbedTokenCommand(Locked("7"), null), CancellationToken.None);
        (await supersededAct.Should().ThrowAsync<EmbedUnavailableException>()).Which.Reason.Should().Be(EmbedRefusalCodes.Superseded);
    }

    private static Dictionary<string, string> Locked(string branch) => new() { ["branch"] = branch };

    private static IssueEmbedTokenHandler Handler(EmbedTestKit kit, EmbedGrant grant, BeaconActor? caller = null, RecordingSecurityAudit? audit = null)
    {
        var actor = caller ?? kit.Actor(grant, EmbedCredential.GrantSecret);

        return new IssueEmbedTokenHandler(
            kit.Data.Store.Factory().Object,
            RecordingSecurityAudit.Accessor(actor),
            kit.Resolver(),
            kit.Codec,
            kit.EmbedOptions(),
            audit ?? new RecordingSecurityAudit(actor),
            kit.Clock,
            NullLogger<IssueEmbedTokenHandler>.Instance);
    }
}
