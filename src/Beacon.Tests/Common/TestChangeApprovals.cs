using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data;
using Beacon.Core.Notifications;
using Beacon.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Beacon.Tests.Common;

/// <summary>
/// The change approval components (migration jobs, Custom SQL rules, recipient destinations) as the host wires them, for
/// tests: with the default configuration the workflow is off, so they change nothing a test without them expects.
/// </summary>
internal static class TestChangeApprovals
{
    /// <summary>The workflow on, covering every kind (the defaults), self-approval off.</summary>
    public static BeaconConfiguration On(bool allowSelfApproval = false)
    {
        return new BeaconConfiguration { ApprovalWorkflow = new ApprovalWorkflowOptions { Enabled = true, AllowSelfApproval = allowSelfApproval } };
    }

    /// <summary>A signed-in, stored user as the caller: <paramref name="externalId"/>, <c>Users.Id</c> <paramref name="beaconUserId"/>.</summary>
    public static IBeaconActorAccessor Caller(string? externalId, int beaconUserId = 9, IReadOnlyList<string>? sessionIds = null, bool isAdmin = false)
    {
        var actor = externalId == null
            ? BeaconActor.Nobody
            : new BeaconActor(externalId, isAdmin)
            {
                Kind = BeaconPrincipalKind.User,
                BeaconUserId = beaconUserId,
                IsActiveUser = true,
                SessionIds = sessionIds ?? []
            };
        var accessor = new Mock<IBeaconActorAccessor>();
        accessor
            .Setup(x => x.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(actor);

        return accessor.Object;
    }

    public static ChangeApprovalSubmitter Submitter(
        BeaconConfiguration? configuration = null,
        IBeaconActorAccessor? caller = null,
        IApprovalNotifier? notifier = null)
    {
        return new ChangeApprovalSubmitter(
            caller ?? Caller(null),
            Mock.Of<IBeaconUserContext>(),
            configuration ?? new BeaconConfiguration(),
            NullLogger<ChangeApprovalSubmitter>.Instance,
            notifier);
    }

    /// <summary>The keyed hasher of where recipients deliver, under the test encryption key.</summary>
    public static RecipientDestinationHasher Hasher { get; } = new(TestEncryptionKey.Value);

    public static RecipientChangeApproval Recipients(
        RecipientSecretProtector protector,
        BeaconConfiguration? configuration = null,
        IBeaconActorAccessor? caller = null,
        IApprovalNotifier? notifier = null)
    {
        return new RecipientChangeApproval(Submitter(configuration, caller, notifier), protector, Hasher);
    }

    public static CustomSqlChangeApproval CustomSql(BeaconConfiguration? configuration = null, IBeaconActorAccessor? caller = null, IApprovalNotifier? notifier = null)
    {
        return new CustomSqlChangeApproval(Submitter(configuration, caller, notifier));
    }

    /// <summary>The delivery gate; with the workflow off (the default) it holds nothing and reads nothing.</summary>
    public static RecipientDeliveryApproval Deliveries(
        IDbContextFactory<BeaconContext>? factory = null,
        RecipientSecretProtector? protector = null,
        BeaconConfiguration? configuration = null,
        RecipientDestinationHasher? hasher = null)
    {
        return new RecipientDeliveryApproval(
            factory ?? Mock.Of<IDbContextFactory<BeaconContext>>(MockBehavior.Strict),
            protector ?? new RecipientSecretProtector(new EncryptionService(TestEncryptionKey.Value)),
            hasher ?? Hasher,
            configuration ?? new BeaconConfiguration());
    }

    public static MigrationJobApprovalState MigrationJobs(BeaconConfiguration? configuration = null)
    {
        return new MigrationJobApprovalState(configuration ?? new BeaconConfiguration());
    }
}
