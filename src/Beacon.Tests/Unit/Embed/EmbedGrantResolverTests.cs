using System.Security.Cryptography;
using Beacon.Core.Data.Entities;
using Beacon.Core.Services.Embed;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Embed;

/// <summary>
/// Resolving an embed credential to its live grant: a grant secret by its hash, a token by its signature, lifetime and
/// audience and then its grant, a public id for the embed page. Revoked, expired and superseded grants, unknown or malformed
/// credentials and tokens signed with another key or for another audience are refused, each with its own code.
/// </summary>
[TestFixture]
public class EmbedGrantResolverTests
{
    [Test]
    public async Task ALiveGrant_ResolvesBySecret_ByToken_AndByPublicId()
    {
        var kit = new EmbedTestKit();
        var (grant, secret) = kit.Grant();
        var token = await kit.TokenAsync(grant, branch: "12");

        var bySecret = await kit.Resolver().ResolveSecretAsync(secret, CancellationToken.None);
        var byToken = await kit.Resolver().ResolveTokenAsync(token, CancellationToken.None);
        var byPublicId = await kit.Resolver().ResolvePublicIdAsync(grant.PublicId, CancellationToken.None);

        bySecret.IsLive.Should().BeTrue();
        bySecret.Grant!.Id.Should().Be(grant.Id);
        bySecret.Grant.FixedParameters.Should().Equal(new Dictionary<string, string> { ["region"] = "EU" });
        bySecret.Grant.LockedParameterNames.Should().Equal("branch");
        bySecret.Grant.AllowedOrigins.Should().Equal(EmbedTestKit.Origin);
        byToken.IsLive.Should().BeTrue();
        byToken.Token!.Parameters.Should().Equal(new Dictionary<string, string> { ["branch"] = "12" });
        byPublicId.IsLive.Should().BeTrue();
    }

    [Test]
    public async Task ARevokedGrant_IsRefused_ForItsSecret_AndForEveryTokenIssuedBefore()
    {
        var kit = new EmbedTestKit();
        var (grant, secret) = kit.Grant();
        var token = await kit.TokenAsync(grant);
        grant.RevokedTime = EmbedTestKit.Now.UtcDateTime;

        (await kit.Resolver().ResolveSecretAsync(secret, CancellationToken.None)).Refusal.Should().Be(EmbedRefusalCodes.Revoked);
        (await kit.Resolver().ResolveTokenAsync(token, CancellationToken.None)).Should().BeEquivalentTo(new { Refusal = EmbedRefusalCodes.Revoked, GrantId = (int?)grant.Id, Grant = (EmbedGrantState?)null });
        (await kit.Resolver().ResolvePublicIdAsync(grant.PublicId, CancellationToken.None)).IsLive.Should().BeFalse();
    }

    [Test]
    public async Task AnExpiredGrant_IsRefused()
    {
        var kit = new EmbedTestKit();
        var (_, secret) = kit.Grant(expiresInDays: 1);
        kit.Clock.Advance(TimeSpan.FromDays(1));

        (await kit.Resolver().ResolveSecretAsync(secret, CancellationToken.None)).Refusal.Should().Be(EmbedRefusalCodes.GrantExpired);
    }

    [Test]
    public async Task AGrantWhoseVersionWasSuperseded_IsRefused()
    {
        var kit = new EmbedTestKit();
        var (grant, secret) = kit.Grant();
        var token = await kit.TokenAsync(grant);
        kit.Supersede();

        (await kit.Resolver().ResolveSecretAsync(secret, CancellationToken.None)).Refusal.Should().Be(EmbedRefusalCodes.Superseded);
        (await kit.Resolver().ResolveTokenAsync(token, CancellationToken.None)).Refusal.Should().Be(EmbedRefusalCodes.Superseded);
    }

    [Test]
    public async Task AVersionWhoseApprovalWasWithdrawn_SupersedesTheGrant()
    {
        var kit = new EmbedTestKit();
        var (_, secret) = kit.Grant();
        kit.Data.Store.ListFor<QueryApprovalRequest>().Clear();

        (await kit.Resolver().ResolveSecretAsync(secret, CancellationToken.None)).Refusal.Should().Be(EmbedRefusalCodes.Superseded);
    }

    [Test]
    public async Task AnExpiredToken_IsRefused_AsExpired()
    {
        var kit = new EmbedTestKit();
        var (grant, _) = kit.Grant();
        var token = await kit.TokenAsync(grant, ttlSeconds: 60);
        kit.Clock.Advance(TimeSpan.FromSeconds(60));

        (await kit.Resolver().ResolveTokenAsync(token, CancellationToken.None)).Refusal.Should().Be(EmbedRefusalCodes.Expired);
    }

    [Test]
    public async Task ATokenSignedWithAnotherKey_IsRefused_AsAnInvalidSignature()
    {
        var kit = new EmbedTestKit();
        var (grant, _) = kit.Grant();
        var other = new EmbedOptions { Enabled = true, SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
        var foreign = await new EmbedTokenCodec(Options.Create(other), kit.Clock)
            .MintAsync(grant.PublicId, new Dictionary<string, string> { ["branch"] = "7" }, TimeSpan.FromMinutes(5));

        (await kit.Resolver().ResolveTokenAsync(foreign.Token, CancellationToken.None)).Refusal.Should().Be(EmbedRefusalCodes.InvalidSignature);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("bge_short")]
    [TestCase("Bearer sk-sem_abc")]
    public async Task AMalformedSecret_IsRefused_BeforeAnyLookup(string? secret)
    {
        var kit = new EmbedTestKit();
        kit.Grant();

        (await kit.Resolver().ResolveSecretAsync(secret, CancellationToken.None)).Refusal.Should().Be(EmbedRefusalCodes.Unknown);
    }

    [Test]
    public async Task AnUnknownSecret_OrPublicId_IsRefused()
    {
        var kit = new EmbedTestKit();
        kit.Grant();
        var (unknown, _) = EmbedGrantSecret.Create();

        (await kit.Resolver().ResolveSecretAsync(unknown, CancellationToken.None)).Refusal.Should().Be(EmbedRefusalCodes.Unknown);
        (await kit.Resolver().ResolvePublicIdAsync(EmbedGrantSecret.NewPublicId(), CancellationToken.None)).Refusal.Should().Be(EmbedRefusalCodes.Unknown);
        (await kit.Resolver().ResolvePublicIdAsync("../../etc", CancellationToken.None)).Refusal.Should().Be(EmbedRefusalCodes.Unknown);
    }

    [Test]
    public async Task WhileEmbeddingIsOff_NothingResolves()
    {
        var kit = new EmbedTestKit();
        var (grant, secret) = kit.Grant();
        var token = await kit.TokenAsync(grant);
        kit.Options.Enabled = false;

        (await kit.Resolver().ResolveSecretAsync(secret, CancellationToken.None)).IsLive.Should().BeFalse();
        (await kit.Resolver().ResolveTokenAsync(token, CancellationToken.None)).IsLive.Should().BeFalse();
        (await kit.Resolver().ResolveIdAsync(grant.Id, CancellationToken.None)).IsLive.Should().BeFalse();
    }
}
