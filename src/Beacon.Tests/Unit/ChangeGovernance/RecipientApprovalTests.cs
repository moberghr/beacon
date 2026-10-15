using System.Text.Json;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Recipients;
using Beacon.Core.Handlers.Subscriptions;
using Beacon.Core.Models;
using Beacon.Core.Models.Approvals;
using Beacon.Core.Models.Recipients;
using Beacon.Core.Models.Subscriptions;
using Beacon.Core.Notifications;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using Beacon.Tests.Unit.Notifications;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ChangeGovernance;

/// <summary>
/// Recipients under the change approval workflow: a recipient created, or whose destination changes, receives nothing
/// until a second person approves exactly where it now delivers; a recipient stored before keeps its destination approved
/// and delivers as before; a save that leaves the destination as it is submits nothing, one that puts the approved
/// destination back needs no approval and withdraws the requester's own pending change, and one made while someone else's
/// change waits is refused. What a reviewer is shown is masked; no secret is stored in a request.
/// </summary>
[TestFixture]
public class RecipientApprovalTests
{
    private const string Ada = "ext-ada";
    private const string Secret = "T0KEN-PATH-9f8e";
    private const string Destination = "https://hooks.example.com/services/" + Secret;
    private const string Moved = "https://hooks.example.com/services/elsewhere-" + Secret;

    // After the encryption key was replaced: hashes are written under a new key, and those written under the tests' key
    // (ChangeApprovalStore.HookHash) verify as a previous key's.
    private static readonly RecipientDestinationHasher ReplacedKeyHasher = new(
        Convert.ToBase64String(Enumerable.Range(0, 32).Select(x => (byte)((x * 53) + 7)).ToArray()),
        [TestEncryptionKey.Value]);

    [Test]
    public async Task Create_UnderTheWorkflow_HoldsTheRecipientUntilApproved_AndShowsTheReviewerOnlyTheMask()
    {
        var store = new ChangeApprovalStore();

        var created = await CreateHandler(store, TestChangeApprovals.On()).Handle(Create(Destination), CancellationToken.None);
        store.Commit();

        var recipient = store.Recipients.Should().ContainSingle().Subject;
        recipient.Id.Should().Be(created.Id);
        recipient.ApprovedDestinationHash.Should().Be(ChangeApprovalHashes.NothingApproved);
        var request = store.Requests.Should().ContainSingle().Subject;
        request.Should().BeEquivalentTo(
            new { Kind = ChangeApprovalKind.RecipientDestination, ResourceId = recipient.Id, RequestedByUserId = Ada, ChangeSummary = "Recipient created", PreviousJson = (string?)null },
            x => x.ExcludingMissingMembers());
        request.DefinitionHash.Should().Be(ChangeApprovalStore.HookHash(Destination));
        request.ProposedJson.Should().NotContain(Secret).And.Contain("example.com");
        request.PayloadHash.Should().NotBe(request.DefinitionHash, "the hash a reviewer names covers only what they are shown");
        (await Deliveries(store).HeldAsync([recipient.Id], CancellationToken.None)).Should().Equal(recipient.Id);
        store.Offline.Commits.Should().Be(1);

        await Approvals(store).ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None);

        recipient.ApprovedDestinationHash.Should().Be(request.DefinitionHash);
        (await Deliveries(store).HeldAsync([recipient.Id], CancellationToken.None)).Should().BeEmpty();
    }

    [TestCase(false, true)]
    [TestCase(true, false)]
    public async Task Create_WithTheWorkflowOrItsSwitchOff_StoresARecipientNobodyGoverns(bool enabled, bool covers)
    {
        var store = new ChangeApprovalStore();
        var configuration = new BeaconConfiguration { ApprovalWorkflow = new ApprovalWorkflowOptions { Enabled = enabled, CoversRecipientDestinations = covers } };

        await CreateHandler(store, configuration).Handle(Create(Destination), CancellationToken.None);
        store.Commit();

        store.Recipients.Should().ContainSingle().Which.ApprovedDestinationHash.Should().BeNull();
        store.Requests.Should().BeEmpty();
        store.Offline.TransactionIsolationLevels.Should().BeEmpty();
    }

    // Grandfathered: a recipient stored before keeps delivering to its destination; a new destination waits for approval.
    [Test]
    public async Task Update_OfAGrandfatheredRecipientsDestination_KeepsTheOldOneApproved_AndHoldsTheNewOne()
    {
        var store = new ChangeApprovalStore();
        var recipient = store.AddRecipient(Destination);

        await UpdateHandler(store, TestChangeApprovals.On()).Handle(Update(recipient, Moved), CancellationToken.None);
        store.Commit();

        recipient.ApprovedDestinationHash.Should().Be(ChangeApprovalStore.HookHash(Destination));
        var request = store.Requests.Should().ContainSingle().Subject;
        request.DefinitionHash.Should().Be(ChangeApprovalStore.HookHash(Moved));
        request.PreviousJson.Should().NotBeNull().And.NotContain(Secret);
        request.ProposedJson.Should().NotContain(Secret);
        request.ChangeSummary.Should().Be("Destination or header values changed (the change is in a part the mask hides)");
        (await Deliveries(store).HeldAsync([recipient.Id], CancellationToken.None)).Should().Equal(recipient.Id);
        store.Offline.Commits.Should().Be(1);
    }

    [Test]
    public async Task Update_ThatLeavesTheDestinationAsItIs_SubmitsNothing()
    {
        var store = new ChangeApprovalStore();
        var grandfathered = store.AddRecipient(Destination);
        var approved = store.AddRecipient(Destination, ChangeApprovalStore.HookHash(Destination));

        await UpdateHandler(store, TestChangeApprovals.On()).Handle(Update(grandfathered, null, "renamed"), CancellationToken.None);
        await UpdateHandler(store, TestChangeApprovals.On()).Handle(Update(approved, null, "renamed too"), CancellationToken.None);
        store.Commit();

        grandfathered.Name.Should().Be("renamed");
        grandfathered.ApprovedDestinationHash.Should().BeNull("an unchanged recipient stays as it was");
        approved.ApprovedDestinationHash.Should().Be(ChangeApprovalStore.HookHash(Destination));
        store.Requests.Should().BeEmpty();
    }

    // Putting the approved destination back needs no approval: the requester's own pending change is withdrawn, and told.
    [Test]
    public async Task Update_BackToTheApprovedDestination_SupersedesTheRequestersOwnPendingChange()
    {
        var store = new ChangeApprovalStore();
        var recipient = store.AddRecipient(Moved, ChangeApprovalStore.HookHash(Destination));
        var pending = store.AddRequest(ChangeApprovalKind.RecipientDestination, recipient.Id, ChangeApprovalStore.HookHash(Moved), Ada);
        var notifier = new Mock<IApprovalNotifier>();

        await UpdateHandler(store, TestChangeApprovals.On(), notifier.Object).Handle(Update(recipient, Destination), CancellationToken.None);
        store.Commit();

        pending.Status.Should().Be(ApprovalStatus.Superseded);
        store.Requests.Should().ContainSingle("nothing new waits");
        (await Deliveries(store).HeldAsync([recipient.Id], CancellationToken.None)).Should().BeEmpty();
        notifier.Verify(x => x.ApprovalUpdatedAsync(pending.Id, "superseded", null, Ada, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Update_OfTheRequestersOwnPendingChange_SupersedesIt_WithTheNewOne()
    {
        var store = new ChangeApprovalStore();
        var recipient = store.AddRecipient(Moved, ChangeApprovalStore.HookHash(Destination));
        var pending = store.AddRequest(ChangeApprovalKind.RecipientDestination, recipient.Id, ChangeApprovalStore.HookHash(Moved), Ada);
        const string third = "https://hooks.example.com/services/third";

        await UpdateHandler(store, TestChangeApprovals.On()).Handle(Update(recipient, third), CancellationToken.None);
        store.Commit();

        pending.Status.Should().Be(ApprovalStatus.Superseded);
        store.Requests.Should().ContainSingle(x => x.Status == ApprovalStatus.Pending)
            .Which.DefinitionHash.Should().Be(ChangeApprovalStore.HookHash(third));
        recipient.ApprovedDestinationHash.Should().Be(ChangeApprovalStore.HookHash(Destination), "the approved destination stays approved");
    }

    [Test]
    public async Task Update_WhileAnotherPersonsChangeWaits_IsRefused_AndCommitsNothing()
    {
        var store = new ChangeApprovalStore();
        var recipient = store.AddRecipient(Moved, ChangeApprovalStore.HookHash(Destination));
        var pending = store.AddRequest(ChangeApprovalKind.RecipientDestination, recipient.Id, ChangeApprovalStore.HookHash(Moved), "ext-ben");

        var act = () => UpdateHandler(store, TestChangeApprovals.On()).Handle(Update(recipient, Destination), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(ChangeApprovalSubmitter.OtherPendingMessage(ChangeApprovalKind.RecipientDestination));
        pending.Status.Should().Be(ApprovalStatus.Pending);
        store.Offline.Commits.Should().Be(0);
    }

    // A rejected change leaves the recipient held; saving it again as it is submits it again.
    [Test]
    public async Task Update_AfterARejection_SubmitsTheSameDestinationAgain()
    {
        var store = new ChangeApprovalStore();
        var recipient = store.AddRecipient(Moved, ChangeApprovalStore.HookHash(Destination));
        store.AddRequest(ChangeApprovalKind.RecipientDestination, recipient.Id, ChangeApprovalStore.HookHash(Moved), Ada, ApprovalStatus.Rejected);

        await UpdateHandler(store, TestChangeApprovals.On()).Handle(Update(recipient, null), CancellationToken.None);
        store.Commit();

        store.Requests.Should().ContainSingle(x => x.Status == ApprovalStatus.Pending)
            .Which.DefinitionHash.Should().Be(ChangeApprovalStore.HookHash(Moved));
    }

    // Without the workflow nothing is reviewed: a governed recipient whose destination changes is no longer governed.
    [Test]
    public async Task Update_WithTheWorkflowOff_OfAGovernedRecipientsDestination_StopsGoverningIt()
    {
        var store = new ChangeApprovalStore();
        var recipient = store.AddRecipient(Destination, ChangeApprovalStore.HookHash(Destination));

        await UpdateHandler(store, new BeaconConfiguration()).Handle(Update(recipient, Moved), CancellationToken.None);

        recipient.ApprovedDestinationHash.Should().BeNull();
        store.Requests.Should().BeEmpty();
        store.Offline.TransactionIsolationLevels.Should().BeEmpty();
    }

    [Test]
    public async Task Update_ByACallerBeaconCannotName_IsRefused_AndWritesNothing()
    {
        var store = new ChangeApprovalStore();
        var recipient = store.AddRecipient(Destination);

        var act = () => UpdateHandler(store, TestChangeApprovals.On(), caller: TestChangeApprovals.Caller(null)).Handle(Update(recipient, Moved), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(ChangeApprovalSubmitter.UnidentifiedRequesterMessage);
        store.Protector.Unprotect(recipient.Destination).Should().Be(Destination);
        recipient.ApprovedDestinationHash.Should().BeNull();
        store.Saved.Should().BeEmpty();
        store.SaveCount.Should().Be(0);
        store.Offline.TransactionIsolationLevels.Should().BeEmpty("the caller is refused before a unit of work starts");
    }

    // Renaming a recipient whose change waits leaves where it delivers as it is: no conflict with the requester's change,
    // which stays the one request.
    [Test]
    public async Task Update_ThatOnlyRenamesARecipientAwaitingAnotherPersonsChange_IsNotRefused_AndAddsNoRequest()
    {
        var store = new ChangeApprovalStore();
        var recipient = store.AddRecipient(Moved, ChangeApprovalStore.HookHash(Destination));
        var pending = store.AddRequest(ChangeApprovalKind.RecipientDestination, recipient.Id, ChangeApprovalStore.HookHash(Moved), Ada);

        await UpdateHandler(store, TestChangeApprovals.On(), caller: TestChangeApprovals.Caller("ext-ben")).Handle(Update(recipient, null, "renamed"), CancellationToken.None);
        store.Commit();

        recipient.Name.Should().Be("renamed");
        store.Protector.Unprotect(recipient.Destination).Should().Be(Moved);
        recipient.ApprovedDestinationHash.Should().Be(ChangeApprovalStore.HookHash(Destination));
        pending.Status.Should().Be(ApprovalStatus.Pending);
        store.Requests.Should().ContainSingle();
        store.Offline.Commits.Should().Be(1);
    }

    // After a key change, a pending request's hash written under the previous key still names the change it proposes, as
    // the delivery check verifies it: another person's save that leaves where the recipient delivers as it is keeps that
    // request and its requester, and adds none.
    [Test]
    public async Task AfterAKeyChange_AnotherPersonsSaveThatLeavesTheDestination_KeepsThePendingRequestAndItsRequester()
    {
        var store = new ChangeApprovalStore();
        var recipient = store.AddRecipient(Moved, ChangeApprovalStore.HookHash(Destination));
        var pending = store.AddRequest(ChangeApprovalKind.RecipientDestination, recipient.Id, ChangeApprovalStore.HookHash(Moved), Ada);
        var notifier = new Mock<IApprovalNotifier>();

        await ReplacedKeyUpdateHandler(store, TestChangeApprovals.Caller("ext-ben"), notifier.Object).Handle(Update(recipient, null, "renamed"), CancellationToken.None);
        store.Commit();

        recipient.Name.Should().Be("renamed");
        recipient.ApprovedDestinationHash.Should().Be(ChangeApprovalStore.HookHash(Destination));
        pending.Should().BeEquivalentTo(
            new { Status = ApprovalStatus.Pending, RequestedByUserId = Ada, DefinitionHash = ChangeApprovalStore.HookHash(Moved) },
            x => x.ExcludingMissingMembers());
        store.Requests.Should().ContainSingle();
        notifier.VerifyNoOtherCalls();
        store.Offline.Commits.Should().Be(1);
    }

    [Test]
    public async Task AfterAKeyChange_AnotherPersonsChangeOfTheDestination_IsRefused_AndKeepsThePendingRequest()
    {
        var store = new ChangeApprovalStore();
        var recipient = store.AddRecipient(Moved, ChangeApprovalStore.HookHash(Destination));
        var pending = store.AddRequest(ChangeApprovalKind.RecipientDestination, recipient.Id, ChangeApprovalStore.HookHash(Moved), Ada);

        var act = () => ReplacedKeyUpdateHandler(store, TestChangeApprovals.Caller("ext-ben")).Handle(Update(recipient, Destination), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(ChangeApprovalSubmitter.OtherPendingMessage(ChangeApprovalKind.RecipientDestination));
        pending.Should().BeEquivalentTo(new { Status = ApprovalStatus.Pending, RequestedByUserId = Ada }, x => x.ExcludingMissingMembers());
        store.Offline.Commits.Should().Be(0);
    }

    [Test]
    public async Task AfterAKeyChange_TheRequestersOwnNewChange_SupersedesTheirPendingOne()
    {
        var store = new ChangeApprovalStore();
        var recipient = store.AddRecipient(Moved, ChangeApprovalStore.HookHash(Destination));
        var pending = store.AddRequest(ChangeApprovalKind.RecipientDestination, recipient.Id, ChangeApprovalStore.HookHash(Moved), Ada);
        const string third = "https://hooks.example.com/services/third";

        await ReplacedKeyUpdateHandler(store, TestChangeApprovals.Caller(Ada)).Handle(Update(recipient, third), CancellationToken.None);
        store.Commit();

        pending.Status.Should().Be(ApprovalStatus.Superseded);
        store.Requests.Should().ContainSingle(x => x.Status == ApprovalStatus.Pending)
            .Which.Should().BeEquivalentTo(
                new { RequestedByUserId = Ada, DefinitionHash = RecipientDestinationDefinition.Hash(ReplacedKeyHasher, NotificationType.Webhook, third, null) },
                x => x.ExcludingMissingMembers());
    }

    // No save supersedes another person's pending request: one whose hash no configured key verifies (its key is no longer
    // configured) does not propose where the recipient delivers, and a save that leaves the destination as it is, which
    // would supersede it, is refused instead.
    [Test]
    public async Task ASaveThatWouldSupersedeAnotherPersonsPendingRequest_IsRefused_EvenWhenItLeavesTheDestinationAsItIs()
    {
        var store = new ChangeApprovalStore();
        var recipient = store.AddRecipient(Moved, ChangeApprovalStore.HookHash(Destination));
        var unverifiable = RecipientDestinationDefinition.Hash(ReplacedKeyHasher, NotificationType.Webhook, Moved, null);
        var pending = store.AddRequest(ChangeApprovalKind.RecipientDestination, recipient.Id, unverifiable, Ada);

        var act = () => UpdateHandler(store, TestChangeApprovals.On(), caller: TestChangeApprovals.Caller("ext-ben")).Handle(Update(recipient, null, "renamed"), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(ChangeApprovalSubmitter.OtherPendingMessage(ChangeApprovalKind.RecipientDestination));
        pending.Should().BeEquivalentTo(new { Status = ApprovalStatus.Pending, RequestedByUserId = Ada, DefinitionHash = unverifiable }, x => x.ExcludingMissingMembers());
        store.Requests.Should().ContainSingle();
        store.Offline.Commits.Should().Be(0);
    }

    // A grandfathered recipient whose stored destination cannot be read has no destination to keep approved: the new one
    // waits with nothing approved, and the reviewer is told the old one could not be read.
    [Test]
    public async Task Update_OfAGrandfatheredRecipientWhoseStoredDestinationCannotBeRead_HoldsTheNewOneWithNothingApproved()
    {
        var store = new ChangeApprovalStore();
        var recipient = store.AddRecipient(Destination);
        recipient.Destination = new RecipientSecretProtector(new EncryptionService("another-key-for-this-test")).Protect(Destination);

        await UpdateHandler(store, TestChangeApprovals.On()).Handle(Update(recipient, Moved), CancellationToken.None);
        store.Commit();

        recipient.ApprovedDestinationHash.Should().Be(ChangeApprovalHashes.NothingApproved);
        store.Protector.Unprotect(recipient.Destination).Should().Be(Moved);
        var request = store.Requests.Should().ContainSingle().Subject;
        request.DefinitionHash.Should().Be(ChangeApprovalStore.HookHash(Moved));
        View(request.PreviousJson!).Should().BeEquivalentTo(new { Unreadable = true, Destination = RecipientSecrets.Mask, Fingerprint = (string?)null }, x => x.ExcludingMissingMembers());
        (request.PreviousJson + request.ProposedJson).Should().NotContain(Secret);
        (await Deliveries(store).HeldAsync([recipient.Id], CancellationToken.None)).Should().Equal(recipient.Id);
    }

    // The requester's own pending change is theirs under any case of their id, as identity providers may change it.
    [Test]
    public async Task Update_WhenTheOwnPendingChangeRecordsTheIdInAnotherCase_SupersedesIt_InsteadOfRefusing()
    {
        var store = new ChangeApprovalStore();
        var recipient = store.AddRecipient(Moved, ChangeApprovalStore.HookHash(Destination));
        var pending = store.AddRequest(ChangeApprovalKind.RecipientDestination, recipient.Id, ChangeApprovalStore.HookHash(Moved), "EXT-ADA");
        const string third = "https://hooks.example.com/services/third";

        await UpdateHandler(store, TestChangeApprovals.On()).Handle(Update(recipient, third), CancellationToken.None);
        store.Commit();

        pending.Status.Should().Be(ApprovalStatus.Superseded);
        store.Requests.Should().ContainSingle(x => x.Status == ApprovalStatus.Pending)
            .Which.Should().BeEquivalentTo(new { RequestedByUserId = Ada, DefinitionHash = ChangeApprovalStore.HookHash(third) }, x => x.ExcludingMissingMembers());
    }

    // A host that allows self-approval takes a change from a caller Beacon cannot name: its request records no requester,
    // and a named reviewer approves it.
    [Test]
    public async Task Create_ByANamelessCaller_WhenTheHostAllowsSelfApproval_RecordsNoRequester_AndANamedReviewerApproves()
    {
        var store = new ChangeApprovalStore();
        var selfApproval = TestChangeApprovals.On(allowSelfApproval: true);
        var create = new CreateRecipientHandler(store.Factory(), Editor(store), TestChangeApprovals.Recipients(store.Protector, selfApproval, TestChangeApprovals.Caller(null)));

        var created = await create.Handle(Create(Destination), CancellationToken.None);
        store.Commit();

        var recipient = store.Recipients.Should().ContainSingle().Subject;
        recipient.Id.Should().Be(created.Id);
        recipient.ApprovedDestinationHash.Should().Be(ChangeApprovalHashes.NothingApproved);
        var request = store.Requests.Should().ContainSingle().Subject;
        request.Should().BeEquivalentTo(new { Status = ApprovalStatus.Pending, RequestedByUserId = (string?)null }, x => x.ExcludingMissingMembers());

        var decision = await Approvals(store, selfApproval).ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None);

        decision.Should().Be(new ChangeApprovalDecision(request.Id, ApprovalStatus.Approved, null, "ext-cid"));
        recipient.ApprovedDestinationHash.Should().Be(ChangeApprovalStore.HookHash(Destination));
        (await Deliveries(store).HeldAsync([recipient.Id], CancellationToken.None)).Should().BeEmpty();
    }

    // A request without a recorded requester is nobody's own: it holds up every change of where the recipient delivers
    // until it is decided.
    [Test]
    public async Task ARequestWithoutARecordedRequester_HoldsUpEveryOtherChange_UntilItIsDecided()
    {
        var store = new ChangeApprovalStore();
        var recipient = store.AddRecipient(Moved, ChangeApprovalStore.HookHash(Destination));
        var pending = store.AddRequest(ChangeApprovalKind.RecipientDestination, recipient.Id, ChangeApprovalStore.HookHash(Moved), requestedBy: null);

        var act = () => UpdateHandler(store, TestChangeApprovals.On(allowSelfApproval: true)).Handle(Update(recipient, Destination), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(ChangeApprovalSubmitter.OtherPendingMessage(ChangeApprovalKind.RecipientDestination));
        pending.Status.Should().Be(ApprovalStatus.Pending);
        store.SaveCount.Should().Be(0);
        store.Offline.Commits.Should().Be(0);
    }

    [Test]
    public async Task Held_AreOnlyGovernedRecipientsNotOnTheirApprovedDestination_AndNoneWithoutTheWorkflow()
    {
        var store = new ChangeApprovalStore();
        var grandfathered = store.AddRecipient(Destination);
        var approved = store.AddRecipient(Destination, ChangeApprovalStore.HookHash(Destination));
        var awaiting = store.AddRecipient(Moved, ChangeApprovalStore.HookHash(Destination));
        var unreadable = store.AddRecipient(Destination, ChangeApprovalStore.HookHash(Destination));
        unreadable.Destination = new RecipientSecretProtector(new EncryptionService("another-key-for-this-test")).Protect(Destination);
        int[] ids = [grandfathered.Id, approved.Id, awaiting.Id, unreadable.Id];

        var held = await Deliveries(store).HeldAsync(ids, CancellationToken.None);
        var off = await TestChangeApprovals.Deliveries(store.Factory(), store.Protector).HeldAsync(ids, CancellationToken.None);

        held.Should().BeEquivalentTo([awaiting.Id, unreadable.Id]);
        off.Should().BeEmpty();
    }

    // Each recipient deliveries skip comes with the pending request of its change, or else the last decision on it; the
    // subscription detail shows the same for its recipients, and nothing for one that delivers.
    [Test]
    public async Task TheRecipientsDeliveriesSkip_ShowThePendingRequestOrElseTheLastDecision_OnTheSubscriptionDetail()
    {
        var store = new ChangeApprovalStore();
        var awaiting = store.AddRecipient(Moved, ChangeApprovalStore.HookHash(Destination));
        var pending = store.AddRequest(ChangeApprovalKind.RecipientDestination, awaiting.Id, ChangeApprovalStore.HookHash(Moved), "ext-ben");
        var rejected = store.AddRecipient(Moved, ChangeApprovalStore.HookHash(Destination));
        var rejection = store.AddRequest(ChangeApprovalKind.RecipientDestination, rejected.Id, ChangeApprovalStore.HookHash(Moved), "ext-ben", ApprovalStatus.Rejected);
        rejection.ReviewComment = "Not this host.";
        rejection.ReviewedAt = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
        var delivering = store.AddRecipient(Destination, ChangeApprovalStore.HookHash(Destination));
        store.AddRequest(ChangeApprovalKind.RecipientDestination, delivering.Id, ChangeApprovalStore.HookHash(Destination), "ext-ben", ApprovalStatus.Approved);
        var subscriptions = new Mock<ISubscriptionService>();
        subscriptions
            .Setup(x => x.GetSubscriptionDetails(5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubscriptionDetailsData
            {
                SubscriptionId = 5,
                QueryName = "daily",
                Status = "Active",
                Recipients = [Attached(awaiting), Attached(rejected), Attached(delivering)]
            });

        var detail = await new GetSubscriptionDetailHandler(subscriptions.Object, Deliveries(store)).Handle(new GetSubscriptionDetailQuery(5), CancellationToken.None);

        detail.Detail.Recipients.Select(x => (x.Id, x.AwaitingApproval, x.PendingApprovalRequestId)).Should().Equal(
            (awaiting.Id, true, pending.Id),
            (rejected.Id, true, (int?)null),
            (delivering.Id, false, (int?)null));
        detail.Detail.Recipients[0].LastApprovalDecision.Should().BeNull("a pending request is shown instead");
        detail.Detail.Recipients[1].LastApprovalDecision.Should().BeEquivalentTo(
            new ChangeApprovalOutcome { RequestId = rejection.Id, Status = ApprovalStatus.Rejected, Comment = "Not this host.", DecidedAt = rejection.ReviewedAt });
        detail.Detail.Recipients[2].LastApprovalDecision.Should().BeNull("a recipient that delivers waits for nothing");
    }

    // Without the workflow nothing waits and nothing is read (the factory refuses any context).
    [Test]
    public async Task TheRecipientsDeliveriesSkip_WithTheWorkflowOff_AreNoneAndReadNothing()
    {
        var awaiting = await TestChangeApprovals.Deliveries().AwaitingAsync([5, 6], CancellationToken.None);

        awaiting.Should().BeEmpty();
    }

    // Headers count: a changed header value is a new definition; their order is not.
    [Test]
    public void TheDefinitionHash_CoversHeaderValues_NotTheirOrder()
    {
        var first = ChangeApprovalStore.HookHash(Destination, """{"Authorization":"Bearer a","X-Team":"ops"}""");

        ChangeApprovalStore.HookHash(Destination, """{"X-Team":"ops","Authorization":"Bearer a"}""").Should().Be(first);
        ChangeApprovalStore.HookHash(Destination, """{"Authorization":"Bearer b","X-Team":"ops"}""").Should().NotBe(first);
        ChangeApprovalStore.HookHash(Destination, "{}").Should().Be(ChangeApprovalStore.HookHash(Destination), "no headers and an empty object deliver alike");
        RecipientDestinationDefinition.Hash(TestChangeApprovals.Hasher, NotificationType.Slack, Destination, null).Should().NotBe(ChangeApprovalStore.HookHash(Destination));
    }

    [Test]
    public async Task TheDetail_ShowsWhereItDeliversBeforeAndAfter_Masked()
    {
        var store = new ChangeApprovalStore();
        var recipient = store.AddRecipient(Destination);
        await UpdateHandler(store, TestChangeApprovals.On()).Handle(Update(recipient, "https://other.example.org/in/" + Secret), CancellationToken.None);
        store.Commit();
        var request = store.Requests.Single();

        var detail = await Approvals(store).GetDetailAsync(request.Id, ChangeApprovalScope.Approver, CancellationToken.None);

        // What identifies where it delivers — the host — with every path segment masked.
        detail!.PreviousRecipientDestination.Should().BeEquivalentTo(new { NotificationType = NotificationType.Webhook, Destination = "https://hooks.example.com/********/********" }, x => x.ExcludingMissingMembers());
        detail.ProposedRecipientDestination.Should().BeEquivalentTo(new { NotificationType = NotificationType.Webhook, Destination = "https://other.example.org/********/********" }, x => x.ExcludingMissingMembers());
        detail.ChangeSummary.Should().Be("Destination changed");
        detail.MayApprove.Should().BeTrue();
        JsonSerializer.Serialize(detail).Should().NotContain(Secret).And.NotContain(request.DefinitionHash);
    }

    private static RecipientData Attached(Recipient recipient) =>
        new()
        {
            RecipientId = recipient.Id,
            Name = recipient.Name,
            NotificationType = recipient.NotificationType
        };

    private static CreateRecipientCommand Create(string destination) =>
        new("ops hook", null, destination, (int)NotificationType.Webhook, null, null);

    private static UpdateRecipientCommand Update(Recipient recipient, string? destination, string? name = null) =>
        new(recipient.Id, name ?? recipient.Name, null, destination, (int)NotificationType.Webhook, null, null);

    private static RecipientSecretEditor Editor(ChangeApprovalStore store) => new(NotificationTestKit.Policy(), store.Protector);

    private static CreateRecipientHandler CreateHandler(ChangeApprovalStore store, BeaconConfiguration configuration) =>
        new(store.Factory(), Editor(store), TestChangeApprovals.Recipients(store.Protector, configuration, TestChangeApprovals.Caller(Ada)));

    private static UpdateRecipientHandler UpdateHandler(
        ChangeApprovalStore store,
        BeaconConfiguration configuration,
        IApprovalNotifier? notifier = null,
        IBeaconActorAccessor? caller = null) =>
        new(
            store.Factory(),
            Editor(store),
            TestProjectAccess.Evaluator(TestProjectAccess.Unrestricted),
            TestProjectAccess.Actors(),
            TestPermissions.Admin,
            TestChangeApprovals.Recipients(store.Protector, configuration, caller ?? TestChangeApprovals.Caller(Ada), notifier),
            NullLogger<UpdateRecipientHandler>.Instance);

    private static UpdateRecipientHandler ReplacedKeyUpdateHandler(ChangeApprovalStore store, IBeaconActorAccessor caller, IApprovalNotifier? notifier = null) =>
        new(
            store.Factory(),
            Editor(store),
            TestProjectAccess.Evaluator(TestProjectAccess.Unrestricted),
            TestProjectAccess.Actors(),
            TestPermissions.Admin,
            new RecipientChangeApproval(TestChangeApprovals.Submitter(TestChangeApprovals.On(), caller, notifier), store.Protector, ReplacedKeyHasher),
            NullLogger<UpdateRecipientHandler>.Instance);

    private static RecipientDeliveryApproval Deliveries(ChangeApprovalStore store) =>
        TestChangeApprovals.Deliveries(store.Factory(), store.Protector, TestChangeApprovals.On());

    private static ChangeApprovalService Approvals(ChangeApprovalStore store, BeaconConfiguration? configuration = null) =>
        new(
            store.Factory(),
            TestChangeApprovals.Caller("ext-cid"),
            Mock.Of<IBeaconUserContext>(),
            store.Protector,
            TestChangeApprovals.Hasher,
            configuration ?? TestChangeApprovals.On(),
            NullLogger<ChangeApprovalService>.Instance);

    private static RecipientDestinationView View(string json) =>
        JsonSerializer.Deserialize<RecipientDestinationView>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
}
