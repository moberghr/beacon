using Beacon.Core;
using Beacon.Core.Adapters;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Notifications;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using Beacon.Tests.Unit.Notifications;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ChangeGovernance;

/// <summary>
/// The delivery gate under the change approval workflow: the send itself (the one path to the adapters) checks the very
/// copy it sends — type, decrypted destination and headers — against the recipient's approved hash read at that moment,
/// archived recipients included, so a change or an archive between loading and sending delivers nothing unapproved, and a
/// recipient that no longer exists receives nothing. An address no stored recipient holds is not governed. The approved
/// hash is keyed (HMAC under a key derived from the encryption key): it differs under another key, a hash written under a
/// previous key still matches, and one whose key was removed, or a copy that cannot be read, holds the recipient.
/// </summary>
[TestFixture]
public class RecipientDeliveryGateTests
{
    private const string Approved = "https://hooks.example.com/services/approved";
    private const string Elsewhere = "https://hooks.example.com/services/elsewhere";

    // Another encryption key that passes the startup checks, for the key-rotation cases.
    private static readonly string OtherKey = Convert.ToBase64String(Enumerable.Range(0, 32)
        .Select(x => (byte)((x * 53) + 7))
        .ToArray());

    // Swapped mid-run: the stored row was put back to its approved destination, while the run sends the copy it loaded.
    [Test]
    public async Task Send_OfACopyThatIsNotTheApprovedDestination_IsRefused_ThoughTheRowNowHoldsTheApprovedOne()
    {
        var store = new ChangeApprovalStore();
        var recipient = store.AddRecipient(Approved, ChangeApprovalStore.HookHash(Approved));
        var adapter = new RecordingAdapter();

        var act = () => Service(store, adapter).SendNotification(Delivery(store, Elsewhere, recipient.Id), null, CancellationToken.None);

        (await act.Should().ThrowAsync<NotificationDeliveryException>()).Which.Message.Should().Be(NotificationFailureReasons.AwaitingApproval);
        adapter.Received.Should().BeEmpty();
    }

    // Archived mid-run: the approved hash is still read (archived recipients included), so the unapproved copy is refused.
    [Test]
    public async Task Send_ToARecipientArchivedMidRun_OfAnUnapprovedCopy_IsRefused()
    {
        var store = new ChangeApprovalStore();
        var recipient = store.AddRecipient(Elsewhere, ChangeApprovalStore.HookHash(Approved));
        recipient.Archive();
        var adapter = new RecordingAdapter();

        var act = () => Service(store, adapter).SendNotification(Delivery(store, Elsewhere, recipient.Id), null, CancellationToken.None);

        await act.Should().ThrowAsync<NotificationDeliveryException>().WithMessage(NotificationFailureReasons.AwaitingApproval);
        adapter.Received.Should().BeEmpty();
    }

    [Test]
    public async Task Send_ToARecipientThatNoLongerExists_IsRefused()
    {
        var store = new ChangeApprovalStore();
        var adapter = new RecordingAdapter();

        var act = () => Service(store, adapter).SendNotification(Delivery(store, Approved, recipientId: 404), null, CancellationToken.None);

        await act.Should().ThrowAsync<NotificationDeliveryException>().WithMessage(NotificationFailureReasons.AwaitingApproval);
        adapter.Received.Should().BeEmpty();
    }

    [Test]
    public async Task Send_OfTheApprovedCopy_AndToARecipientNobodyGoverns_IsDelivered()
    {
        var store = new ChangeApprovalStore();
        var approved = store.AddRecipient(Approved, ChangeApprovalStore.HookHash(Approved));
        var grandfathered = store.AddRecipient(Elsewhere);
        var adapter = new RecordingAdapter();

        await Service(store, adapter).SendNotification(Delivery(store, Approved, approved.Id), null, CancellationToken.None);
        await Service(store, adapter).SendNotification(Delivery(store, Elsewhere, grandfathered.Id), null, CancellationToken.None);

        adapter.Received.Select(x => x.RecipientDestination).Should().Equal(Approved, Elsewhere);
    }

    // By design: an address no stored recipient holds (a host's own call without a recipient id) is not a governed
    // recipient's, so the gate does not read anything for it.
    [Test]
    public async Task Send_ToAnAddressNoStoredRecipientHolds_IsNotGated()
    {
        var protector = new ChangeApprovalStore().Protector;
        var adapter = new RecordingAdapter();
        var service = Service(Mock.Of<IDbContextFactory<BeaconContext>>(MockBehavior.Strict), protector, adapter, TestChangeApprovals.On());

        await service.SendNotification(Delivery(protector, Elsewhere, recipientId: null), null, CancellationToken.None);

        adapter.Received.Should().ContainSingle().Which.RecipientDestination.Should().Be(Elsewhere);
    }

    [Test]
    public async Task Send_WithTheWorkflowOff_ReadsNothing_AndDelivers()
    {
        var protector = new ChangeApprovalStore().Protector;
        var adapter = new RecordingAdapter();
        var service = Service(Mock.Of<IDbContextFactory<BeaconContext>>(MockBehavior.Strict), protector, adapter, new BeaconConfiguration());

        await service.SendNotification(Delivery(protector, Elsewhere, recipientId: 7), null, CancellationToken.None);

        adapter.Received.Should().ContainSingle();
    }

    // The run's check reads the same way: the copy it loaded, the approved hash as it is now, archived included, and a
    // recipient that is gone held.
    [Test]
    public async Task HeldCopies_AreTheCopiesThatAreNotTheApprovedDestination_ReadNow()
    {
        var store = new ChangeApprovalStore();
        var swapped = store.AddRecipient(Approved, ChangeApprovalStore.HookHash(Approved));
        var archived = store.AddRecipient(Elsewhere, ChangeApprovalStore.HookHash(Approved));
        archived.Archive();
        var approved = store.AddRecipient(Approved, ChangeApprovalStore.HookHash(Approved));
        var grandfathered = store.AddRecipient(Elsewhere);
        RecipientCopy[] copies =
        [
            Copy(store, swapped.Id, Elsewhere),
            Copy(store, archived.Id, Elsewhere),
            Copy(store, approved.Id, Approved),
            Copy(store, grandfathered.Id, Elsewhere),
            Copy(store, 404, Approved)
        ];

        var held = await TestChangeApprovals.Deliveries(store.Factory(), store.Protector, TestChangeApprovals.On()).HeldCopiesAsync(copies, CancellationToken.None);
        var off = await TestChangeApprovals.Deliveries(store.Factory(), store.Protector).HeldCopiesAsync(copies, CancellationToken.None);

        held.Should().BeEquivalentTo([swapped.Id, archived.Id, 404]);
        off.Should().BeEmpty();
    }

    // The approved hash is keyed: someone who reads it cannot test a guessed destination against it.
    [Test]
    public void TheHash_IsKeyed_VersionedAndFitsItsColumn()
    {
        var current = new RecipientDestinationHasher(TestEncryptionKey.Value);
        var other = new RecipientDestinationHasher(OtherKey);
        var canonical = RecipientDestinationDefinition.Canonical(NotificationType.Webhook, Approved, null);

        var hash = current.Hash(canonical);

        hash.Should().StartWith(RecipientDestinationHasher.Prefix).And.HaveLength(46).And.NotBe(other.Hash(canonical));
        hash.Length.Should().BeLessThanOrEqualTo(64, "it is stored in the 64-character approved hash column");
        current.Matches(hash, canonical).Should().BeTrue();
        other.Matches(hash, canonical).Should().BeFalse();
        current.Matches(ChangeApprovalHashes.Sha256(canonical), canonical).Should().BeFalse("an unkeyed hash is never accepted");
        current.Matches(hash, RecipientDestinationDefinition.Canonical(NotificationType.Webhook, Elsewhere, null)).Should().BeFalse();
    }

    // Replacing the key holds no governed recipient: a hash written under a key now listed as previous still matches,
    // at the send and in the run; once that key is removed it no longer does, and the recipient is held.
    [Test]
    public async Task AnApprovedHashUnderAPreviousKey_StillDelivers_AndUnderARemovedKeyIsHeld()
    {
        var store = new ChangeApprovalStore();
        var underOld = new RecipientDestinationHasher(OtherKey);
        var recipient = store.AddRecipient(Approved, RecipientDestinationDefinition.Hash(underOld, NotificationType.Webhook, Approved, null));
        var rotated = new RecipientDestinationHasher(TestEncryptionKey.Value, [OtherKey]);
        var removed = new RecipientDestinationHasher(TestEncryptionKey.Value);

        var rotatedGate = TestChangeApprovals.Deliveries(store.Factory(), store.Protector, TestChangeApprovals.On(), rotated);
        var removedGate = TestChangeApprovals.Deliveries(store.Factory(), store.Protector, TestChangeApprovals.On(), removed);

        (await rotatedGate.AllowsAsync(recipient.Id, NotificationType.Webhook, Approved, null, CancellationToken.None)).Should().BeTrue();
        (await rotatedGate.HeldCopiesAsync([Copy(store, recipient.Id, Approved)], CancellationToken.None)).Should().BeEmpty();
        (await removedGate.AllowsAsync(recipient.Id, NotificationType.Webhook, Approved, null, CancellationToken.None)).Should().BeFalse();
        (await removedGate.HeldCopiesAsync([Copy(store, recipient.Id, Approved)], CancellationToken.None)).Should().Equal(recipient.Id);
    }

    // A copy no configured key decrypts is never approved.
    [Test]
    public async Task ACopyThatCannotBeRead_IsHeld()
    {
        var store = new ChangeApprovalStore();
        var recipient = store.AddRecipient(Approved, ChangeApprovalStore.HookHash(Approved));
        var unreadable = new RecipientCopy(recipient.Id, NotificationType.Webhook, new RecipientSecretProtector(new EncryptionService(OtherKey)).Protect(Approved), null);

        var held = await TestChangeApprovals.Deliveries(store.Factory(), store.Protector, TestChangeApprovals.On()).HeldCopiesAsync([unreadable], CancellationToken.None);

        held.Should().Equal(recipient.Id);
    }

    // The approved hash is read with archived recipients included (no archive filter), on both providers.
    [TestCase(false, "r.archived_time")]
    [TestCase(true, "[r].[ArchivedTime]")]
    public async Task TheApprovedHashRead_IncludesArchivedRecipients_Translates(bool sqlServer, string archiveFilter)
    {
        var capture = (sqlServer ? SqlCapture.OnSqlServer() : new SqlCapture()).ThenNoRows();
        var gate = TestChangeApprovals.Deliveries(capture.Factory(), new ChangeApprovalStore().Protector, TestChangeApprovals.On());

        var allowed = await gate.AllowsAsync(7, NotificationType.Webhook, Approved, null, CancellationToken.None);

        allowed.Should().BeFalse("a recipient that is not found receives nothing");
        capture.Commands.Should().ContainSingle()
            .Which.Should().Contain(sqlServer ? "[r].[ApprovedDestinationHash]" : "r.approved_destination_hash").And.NotContain(archiveFilter);
    }

    private static RecipientCopy Copy(ChangeApprovalStore store, int recipientId, string destination) =>
        new(recipientId, NotificationType.Webhook, store.Protector.Protect(destination), null);

    private static RecipientQueryResult Delivery(ChangeApprovalStore store, string destination, int? recipientId) =>
        Delivery(store.Protector, destination, recipientId);

    private static RecipientQueryResult Delivery(RecipientSecretProtector protector, string destination, int? recipientId) =>
        new()
        {
            RecipientDestination = protector.Protect(destination),
            RecipientNotificationType = NotificationType.Webhook,
            RecipientId = recipientId,
            QueryResult = new QueryResult
            {
                QueryResults = "[]",
                TotalRecords = 1,
                DataSourceName = "warehouse",
                SqlQuery = "SELECT 1",
                SubscriptionName = "daily",
                SubscriptionId = 5
            }
        };

    private static NotificationService Service(ChangeApprovalStore store, RecordingAdapter adapter) =>
        Service(store.Factory(), store.Protector, adapter, TestChangeApprovals.On());

    private static NotificationService Service(
        IDbContextFactory<BeaconContext> factory,
        RecipientSecretProtector protector,
        RecordingAdapter adapter,
        BeaconConfiguration configuration) =>
        new(
            Mock.Of<IDbContextFactory<BeaconContext>>(MockBehavior.Strict),
            new AdapterFactory([adapter]),
            protector,
            NotificationTestKit.Policy(),
            Options.Create(new NotificationChannelOptions()),
            TestChangeApprovals.Deliveries(factory, protector, configuration),
            NullLogger<NotificationService>.Instance);

    private sealed class RecordingAdapter : IAdapter
    {
        public List<RecipientQueryResult> Received { get; } = [];

        public NotificationType NotificationType => NotificationType.Webhook;

        public Task SendNotificationAsync(RecipientQueryResult recipientQueryResult, int? lastNotificationResultCount, CancellationToken cancellationToken = default)
        {
            Received.Add(recipientQueryResult);

            return Task.CompletedTask;
        }
    }
}
