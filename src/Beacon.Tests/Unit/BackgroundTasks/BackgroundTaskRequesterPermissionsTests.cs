using Beacon.Core.Authorization;
using Beacon.Core.BackgroundTasks;
using Beacon.Core.Configuration;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.BackgroundTasks;

/// <summary>
/// The job's read of an identity-provider grant recorded at a task's start, over the real EF Core providers without a
/// database (<see cref="SqlCapture"/>, §4.7): one query reads the task (only its own requester's, only an identity-provider
/// grant) with the requester's enabled flag and security stamp, on both providers.
/// </summary>
[TestFixture]
public class BackgroundTaskRequesterPermissionsTests
{
    private const int TaskId = 41;
    private const int UserId = 7;

    [TestCase(false)]
    [TestCase(true)]
    public async Task TheIdentityProviderGrant_IsReadWithTheRequestersStamp_Translates(bool sqlServer)
    {
        var capture = (sqlServer ? SqlCapture.OnSqlServer() : new SqlCapture())
            .ThenNoRows()
            .ThenNoRows();

        var holds = await Permissions(capture, idpRolesEnabled: true).HoldsAsync(BeaconPermissions.MigrationOperator, TaskId, CancellationToken.None);

        holds.Should().BeFalse("no task records a grant");
        capture.Commands.Should().HaveCount(2, "the stored roles, then the task with its requester");
        var read = capture.Commands[1];
        read.Should().Contain(sqlServer ? "[BackgroundTasks]" : "background_tasks");
        read.Should().Contain(sqlServer ? "[SecurityStamp]" : "security_stamp");
        read.Should().Contain(sqlServer ? "[IsEnabled]" : "is_enabled");
        read.Should().Contain($"'{BackgroundTaskPermissionSources.IdentityProvider}'");
        capture.CommandParameters[1].Values.Should().Contain([TaskId, UserId]);
    }

    [Test]
    public async Task WhileIdentityProviderMappingIsOff_NoGrantIsRead()
    {
        var capture = new SqlCapture().ThenNoRows();

        var holds = await Permissions(capture, idpRolesEnabled: false).HoldsAsync(BeaconPermissions.MigrationOperator, TaskId, CancellationToken.None);

        holds.Should().BeFalse();
        capture.Commands.Should().ContainSingle("only the stored roles are read");
    }

    [Test]
    public void TheStampHash_IsStable_AndNeverTheStamp()
    {
        var hash = BackgroundTaskRequesterPermissions.StampHash("stamp-1");

        hash.Should().Be(BackgroundTaskRequesterPermissions.StampHash("stamp-1"));
        hash.Should().HaveLength(64).And.NotContain("stamp-1");
        BackgroundTaskRequesterPermissions.StampHash(null).Should().Be(BackgroundTaskRequesterPermissions.StampHash(string.Empty));
        hash.Should().NotBe(BackgroundTaskRequesterPermissions.StampHash("stamp-2"));
    }

    // The job's scope: no request, run as a stored, active user without a stored functional role.
    private static BackgroundTaskRequesterPermissions Permissions(SqlCapture capture, bool idpRolesEnabled)
    {
        var actor = new Mock<IBeaconActorAccessor>();
        actor
            .Setup(x => x.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BeaconActor("ext-idp", false) { Kind = BeaconPrincipalKind.Owner, BeaconUserId = UserId, IsActiveUser = true });
        var mapping = new RoleMappingOptions { IdpRoles = new IdpRoleOptions { Enabled = idpRolesEnabled } };
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));

        return new BackgroundTaskRequesterPermissions(
            TestRequestPrincipal.OffRequest(),
            new Mock<IBeaconPermissionAccessor>(MockBehavior.Strict).Object,
            actor.Object,
            new Mock<IProjectAccessEvaluator>(MockBehavior.Strict).Object,
            capture.Factory(),
            Options.Create(mapping),
            time,
            new BeaconPermissionLog(time, NullLogger<BeaconPermissionLog>.Instance));
    }
}
