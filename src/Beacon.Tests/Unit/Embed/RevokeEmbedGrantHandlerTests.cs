using Beacon.Core.Authorization;
using Beacon.Core.Handlers.Embed;
using Beacon.Core.SecurityAudit;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Embed;

/// <summary>
/// Revoking an embed grant is one conditional update (only while it is unrevoked) and one event with ids only; revoking a
/// revoked grant, or losing a race to another revoke, changes and records nothing. The request needs no project access:
/// a SecurityAdmin revokes any grant.
/// </summary>
[TestFixture]
public class RevokeEmbedGrantHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task AnActiveGrant_IsRevokedOnce_AndRecorded()
    {
        var capture = new SqlCapture().ThenRow(4, DBNull.Value).ThenRowsAffected(1);
        var audit = new RecordingSecurityAudit(RecordingSecurityAudit.User(3, "ext-ada"));

        var result = await Handler(capture, audit).Handle(new RevokeEmbedGrantCommand(9), CancellationToken.None);

        result.Should().Be(new RevokeEmbedGrantResult(9, Now.UtcDateTime, Revoked: true));
        capture.Commands.Should().HaveCount(2);
        capture.Commands[1].Should().StartWith("UPDATE beacon.embed_grants")
            .And.Contain("revoked_time IS NULL")
            .And.Contain("revoked_by_user_id")
            .And.Contain("modified_by_user_id");
        capture.CommandParameters[1].Values.Should().Contain("ext-ada").And.Contain(Now.UtcDateTime);
        var revoked = audit.Single("ext-ada");
        revoked.Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.EmbedGrantRevoked, ResourceType = SecurityAuditResources.EmbedGrant, ResourceId = "9", ActorUserId = (int?)3 },
            x => x.ExcludingMissingMembers());
        audit.Ways.Should().Equal("saved");
    }

    [Test]
    public async Task ARevokedGrant_StaysAsItWas()
    {
        var revokedTime = Now.UtcDateTime.AddDays(-1);
        var capture = new SqlCapture().ThenRow(4, revokedTime);
        var audit = new RecordingSecurityAudit();

        var result = await Handler(capture, audit).Handle(new RevokeEmbedGrantCommand(9), CancellationToken.None);

        result.Should().Be(new RevokeEmbedGrantResult(9, revokedTime, Revoked: false));
        capture.Commands.Should().ContainSingle("nothing is written");
        audit.Events.Should().BeEmpty();
    }

    [Test]
    public async Task ARevokeThatLosesTheRace_RecordsNothing()
    {
        var otherTime = Now.UtcDateTime.AddSeconds(-1);
        var capture = new SqlCapture().ThenRow(4, DBNull.Value).ThenRowsAffected(0).ThenRow(otherTime);
        var audit = new RecordingSecurityAudit();

        var result = await Handler(capture, audit).Handle(new RevokeEmbedGrantCommand(9), CancellationToken.None);

        result.Should().Be(new RevokeEmbedGrantResult(9, otherTime, Revoked: false));
        audit.Events.Should().BeEmpty();
    }

    [Test]
    public async Task AnUnknownGrant_IsRefused()
    {
        var act = () => Handler(new SqlCapture().ThenNoRows(), new RecordingSecurityAudit()).Handle(new RevokeEmbedGrantCommand(9), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not found*");
    }

    [Test]
    public void TheRequest_IsExemptFromProjectChecks_AndGatedBySecurityAdmin()
    {
        var exemption = (ProjectScopeExemptAttribute?)Attribute.GetCustomAttribute(typeof(RevokeEmbedGrantCommand), typeof(ProjectScopeExemptAttribute));

        exemption.Should().NotBeNull();
        exemption!.Policy.Should().Be(BeaconPermissions.SecurityAdminPolicy);
    }

    private static RevokeEmbedGrantHandler Handler(SqlCapture capture, RecordingSecurityAudit audit) =>
        new(
            capture.Factory(),
            TestProjectAccess.Actors(3, "ext-ada"),
            audit,
            new FakeTimeProvider(Now),
            NullLogger<RevokeEmbedGrantHandler>.Instance);
}
