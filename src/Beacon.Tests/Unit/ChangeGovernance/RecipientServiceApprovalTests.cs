using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Recipients;
using Beacon.Core.Models;
using Beacon.Core.Models.Recipients;
using Beacon.Core.Notifications;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using Beacon.Tests.Unit.Notifications;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ChangeGovernance;

/// <summary>
/// The host-facing recipient service under the change approval workflow, as the REST handlers: a recipient it creates
/// receives nothing until a second person approves where it delivers, the recipient and its request committed together; a
/// recipient stored before keeps delivering to its approved destination while a new one waits; a change made while
/// someone else's waits comes back unsuccessful with nothing committed; a caller Beacon cannot name is refused before
/// anything is written.
/// </summary>
[TestFixture]
public class RecipientServiceApprovalTests
{
    private const string Ada = "ext-ada";
    private const string Destination = "https://hooks.example.com/services/T0KEN-PATH-9f8e";
    private const string Moved = "https://hooks.example.com/services/elsewhere-T0KEN-PATH-9f8e";

    [Test]
    public async Task Create_UnderTheWorkflow_HoldsTheRecipient_WithAPendingRequest_InOneCommit()
    {
        var store = new ChangeApprovalStore();

        var response = await Service(store).CreateRecipient(Data(Destination), CancellationToken.None);
        store.Commit();

        response.Success.Should().BeTrue();
        response.Message.Should().Be(CreateRecipientResult.SubmittedForApprovalMessage);
        var recipient = store.Recipients.Should().ContainSingle().Subject;
        recipient.ApprovedDestinationHash.Should().Be(ChangeApprovalHashes.NothingApproved);
        store.Requests.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new
            {
                Kind = ChangeApprovalKind.RecipientDestination,
                ResourceId = recipient.Id,
                Status = ApprovalStatus.Pending,
                DefinitionHash = ChangeApprovalStore.HookHash(Destination),
                RequestedByUserId = Ada,
                ChangeSummary = "Recipient created"
            },
            x => x.ExcludingMissingMembers());
        (await Deliveries(store).HeldAsync([recipient.Id], CancellationToken.None)).Should().Equal(recipient.Id);
        store.Offline.Commits.Should().Be(1, "the recipient and its request commit together");
        store.SaveCount.Should().Be(2, "the recipient's save gives it the id its request names");
    }

    // Grandfathered: the destination it delivered to before stays approved; the new one waits.
    [Test]
    public async Task Update_OfAGrandfatheredRecipientsDestination_KeepsTheOldOneApproved_AndHoldsTheNewOne()
    {
        var store = new ChangeApprovalStore();
        var recipient = store.AddRecipient(Destination);

        var response = await Service(store).UpdateRecipient(Data(Moved, recipient), CancellationToken.None);
        store.Commit();

        response.Success.Should().BeTrue();
        recipient.ApprovedDestinationHash.Should().Be(ChangeApprovalStore.HookHash(Destination));
        store.Protector.Unprotect(recipient.Destination).Should().Be(Moved);
        store.Requests.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { ResourceId = recipient.Id, Status = ApprovalStatus.Pending, DefinitionHash = ChangeApprovalStore.HookHash(Moved), RequestedByUserId = Ada },
            x => x.ExcludingMissingMembers());
        (await Deliveries(store).HeldAsync([recipient.Id], CancellationToken.None)).Should().Equal(recipient.Id);
        store.Offline.Commits.Should().Be(1);
    }

    [Test]
    public async Task Update_WhileAnotherPersonsChangeWaits_IsUnsuccessful_AndCommitsNothing()
    {
        var store = new ChangeApprovalStore();
        var recipient = store.AddRecipient(Moved, ChangeApprovalStore.HookHash(Destination));
        var pending = store.AddRequest(ChangeApprovalKind.RecipientDestination, recipient.Id, ChangeApprovalStore.HookHash(Moved), "ext-ben");

        var response = await Service(store).UpdateRecipient(Data(Destination, recipient), CancellationToken.None);

        response.Success.Should().BeFalse();
        response.Message.Should().Be(ChangeApprovalSubmitter.OtherPendingMessage(ChangeApprovalKind.RecipientDestination));
        pending.Status.Should().Be(ApprovalStatus.Pending);
        store.Requests.Should().ContainSingle();
        recipient.ApprovedDestinationHash.Should().Be(ChangeApprovalStore.HookHash(Destination));
        store.SaveCount.Should().Be(0);
        store.Offline.Commits.Should().Be(0);
    }

    [Test]
    public async Task ACallerBeaconCannotName_IsRefused_AndNothingIsWritten()
    {
        var store = new ChangeApprovalStore();
        var recipient = store.AddRecipient(Destination);
        var service = Service(store, TestChangeApprovals.Caller(null));

        var create = () => service.CreateRecipient(Data(Moved), CancellationToken.None);
        var update = () => service.UpdateRecipient(Data(Moved, recipient), CancellationToken.None);

        await create.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(ChangeApprovalSubmitter.UnidentifiedRequesterMessage);
        await update.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(ChangeApprovalSubmitter.UnidentifiedRequesterMessage);
        store.Saved.Should().BeEmpty();
        store.SaveCount.Should().Be(0);
        store.Offline.TransactionIsolationLevels.Should().BeEmpty();
        store.Recipients.Should().ContainSingle();
        store.Protector.Unprotect(recipient.Destination).Should().Be(Destination);
        recipient.ApprovedDestinationHash.Should().BeNull();
    }

    private static RecipientData Data(string destination, Recipient? recipient = null) =>
        new()
        {
            RecipientId = recipient?.Id,
            Name = recipient?.Name ?? "ops hook",
            Destination = destination,
            NotificationType = NotificationType.Webhook
        };

    private static RecipientService Service(ChangeApprovalStore store, IBeaconActorAccessor? caller = null) =>
        new(
            store.Factory(),
            new RecipientSecretEditor(NotificationTestKit.Policy(), store.Protector),
            TestChangeApprovals.Recipients(store.Protector, TestChangeApprovals.On(), caller ?? TestChangeApprovals.Caller(Ada)));

    private static RecipientDeliveryApproval Deliveries(ChangeApprovalStore store) =>
        TestChangeApprovals.Deliveries(store.Factory(), store.Protector, TestChangeApprovals.On());
}
