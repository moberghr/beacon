using System.Reflection;
using System.Text.Json;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Approvals;
using Beacon.Core.Handlers.Recipients;
using Beacon.Core.Models;
using Beacon.Core.Models.Approvals;
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
/// What a reviewer of a recipient change is shown: what identifies where it delivers — the full host and path, Slack's
/// workspace and channel ids, a Teams workflow, a Jira site, email addresses — with every credential masked (user info,
/// query values, token-like segments, a token or signature), header names with a keyed fingerprint of each value, and a
/// fingerprint of the whole definition; the recipients page shows the same fingerprints to those who may change it. A
/// change whose before and after look the same is flagged, and approving it needs a comment. A request whose proposal
/// cannot be read cannot be approved. No API model carries a stored hash.
/// </summary>
[TestFixture]
public class RecipientReviewViewTests
{
    private const string Ada = "ext-ada";
    private const string Secret = "T0KEN-PATH-9f8e";
    private const string Destination = "https://hooks.example.com/services/" + Secret;
    private const string Moved = "https://hooks.example.com/services/elsewhere-" + Secret;

    public static IEnumerable<TestCaseData> Destinations()
    {
        yield return new TestCaseData(NotificationType.Webhook, "https://user:pw@hooks.example.com:8443/api/v2/alerts/Abc123Def456?token=s3cr3t&team=ops", "https://********@hooks.example.com:8443/api/v2/alerts/********?token=********&team=********")
            .SetName("Identifying_Webhook_KeepsHostAndPath_MasksUserInfoTokenSegmentsAndQueryValues");

        // Assembled from parts: the source never holds a whole provider-shaped webhook.
        var slack = string.Concat("https://hooks.", "slack.com/services/", "T0123ABCD", "/", "B0456EFGH", "/", "token-", "value");
        yield return new TestCaseData(NotificationType.Slack, slack, string.Concat("https://hooks.", "slack.com/services/T0123ABCD/B0456EFGH/********"))
            .SetName("Identifying_Slack_KeepsWorkspaceAndChannel_MasksTheToken");

        yield return new TestCaseData(
                NotificationType.Teams,
                "https://prod-12.westeurope.logic.azure.com:443/workflows/0123456789abcdef0123456789abcdef/triggers/manual/paths/invoke?api-version=2016-06-01&sig=AbCdEf",
                "https://prod-12.westeurope.logic.azure.com/workflows/0123456789abcdef0123456789abcdef/triggers/manual/paths/invoke?api-version=********&sig=********")
            .SetName("Identifying_TeamsWorkflow_KeepsTheWorkflowPath_MasksItsSignature");
        yield return new TestCaseData(
                NotificationType.Teams,
                "https://contoso.webhook.office.com/webhookb2/aaaa-bbbb@cccc-dddd/IncomingWebhook/s3cretpart/eeee",
                "https://contoso.webhook.office.com/webhookb2/aaaa-bbbb@cccc-dddd/IncomingWebhook/********/eeee")
            .SetName("Identifying_LegacyTeamsConnector_MasksItsSecretSegment");
        yield return new TestCaseData(NotificationType.Jira, "acme;OPS;bot@example.com;tok", "acme;OPS;bot@example.com;********")
            .SetName("Identifying_Jira_KeepsSiteProjectAndEmail");
        yield return new TestCaseData(NotificationType.Email, "ops@example.com", "ops@example.com")
            .SetName("Identifying_Email_ShowsTheAddresses");
    }

    [TestCaseSource(nameof(Destinations))]
    public void TheView_ShowsWhatIdentifiesTheDestination_AndMasksEveryCredential(NotificationType type, string destination, string shown)
    {
        RecipientDestinationDefinition.Identifying(type, destination).Should().Be(shown);
    }

    // Header names are shown with a keyed fingerprint of each value: a changed value changes its fingerprint (and the
    // definition's), never the other header's; nothing of a value is shown, and another key gives other fingerprints.
    [Test]
    public void TheView_NamesHeaders_WithAKeyedFingerprintOfEachValue_NeverTheValue()
    {
        var protector = new ChangeApprovalStore().Protector;
        var before = View(protector, TestChangeApprovals.Hasher, """{"X-Team":"ops","Authorization":"Bearer s3cr3t-value"}""");
        var after = View(protector, TestChangeApprovals.Hasher, """{"X-Team":"ops","Authorization":"Bearer an0ther-value"}""");
        var otherKey = View(protector, new RecipientDestinationHasher(Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i * 3 + 1)).ToArray())), """{"X-Team":"ops","Authorization":"Bearer s3cr3t-value"}""");

        before.HeaderNames.Should().Equal("Authorization", "X-Team");
        before.Headers.Select(x => x.Name).Should().Equal("Authorization", "X-Team");
        before.Headers.Should().OnlyContain(x => x.Fingerprint.Length == RecipientDestinationHasher.FingerprintLength);
        before.Fingerprint.Should().HaveLength(RecipientDestinationHasher.FingerprintLength);
        after.Headers[0].Fingerprint.Should().NotBe(before.Headers[0].Fingerprint);
        after.Headers[1].Fingerprint.Should().Be(before.Headers[1].Fingerprint);
        after.Fingerprint.Should().NotBe(before.Fingerprint);
        otherKey.Headers[0].Fingerprint.Should().NotBe(before.Headers[0].Fingerprint, "fingerprints are keyed");
        JsonSerializer.Serialize(before).Should().NotContain("s3cr3t").And.NotContain(Secret);
        RecipientDestinationDefinition.ChangeHiddenByMask(before, after).Should().BeTrue("the views look the same while the definition differs");
        RecipientDestinationDefinition.ChangeHiddenByMask(before, before).Should().BeFalse();
    }

    // A change only a masked part carries: the reviewer is told, and approving it needs a comment saying how it was checked.
    [Test]
    public async Task AChangeOnlyInAMaskedPart_IsFlagged_AndApprovingItNeedsAComment()
    {
        var store = new ChangeApprovalStore();
        var recipient = store.AddRecipient(Destination, ChangeApprovalStore.HookHash(Destination));
        await UpdateHandler(store).Handle(new UpdateRecipientCommand(recipient.Id, recipient.Name, null, Moved, (int)NotificationType.Webhook, null, null), CancellationToken.None);
        store.Commit();
        var request = store.Requests.Single();

        var detail = await Approvals(store).GetDetailAsync(request.Id, ChangeApprovalScope.Approver, CancellationToken.None);
        var withoutComment = () => Approvals(store).ApproveAsync(request.Id, " ", request.PayloadHash, CancellationToken.None);

        detail!.Should().BeEquivalentTo(new { ChangeHiddenByMask = true, CommentRequiredToApprove = true, MayApprove = true }, x => x.ExcludingMissingMembers());
        detail.PreviousRecipientDestination!.Destination.Should().Be(detail.ProposedRecipientDestination!.Destination);
        detail.PreviousRecipientDestination.Fingerprint.Should().NotBe(detail.ProposedRecipientDestination.Fingerprint);
        await withoutComment.Should().ThrowAsync<InvalidOperationException>().WithMessage(ChangeApprovalService.CommentRequiredMessage);
        request.Status.Should().Be(ApprovalStatus.Pending);
        recipient.ApprovedDestinationHash.Should().Be(ChangeApprovalStore.HookHash(Destination));

        await Approvals(store).ApproveAsync(request.Id, "Fingerprints compared with the requester by phone.", request.PayloadHash, CancellationToken.None);

        request.Status.Should().Be(ApprovalStatus.Approved);
        recipient.ApprovedDestinationHash.Should().Be(ChangeApprovalStore.HookHash(Moved));
    }

    // A change the reviewer can see needs no comment.
    [Test]
    public async Task AChangeOfTheShownDestination_IsNotFlagged_AndIsApprovedWithoutAComment()
    {
        var store = new ChangeApprovalStore();
        var recipient = store.AddRecipient(Destination, ChangeApprovalStore.HookHash(Destination));
        await UpdateHandler(store).Handle(new UpdateRecipientCommand(recipient.Id, recipient.Name, null, "https://other.example.org/in/" + Secret, (int)NotificationType.Webhook, null, null), CancellationToken.None);
        store.Commit();
        var request = store.Requests.Single();

        var detail = await Approvals(store).GetDetailAsync(request.Id, ChangeApprovalScope.Approver, CancellationToken.None);
        await Approvals(store).ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None);

        detail!.ChangeHiddenByMask.Should().BeFalse();
        detail.CommentRequiredToApprove.Should().BeFalse();
        request.Status.Should().Be(ApprovalStatus.Approved);
    }

    // Nothing is approved that the reviewer could not be shown: the detail says why, and the server refuses it too.
    [Test]
    public async Task ARequestWhoseProposalCannotBeRead_CannotBeApproved()
    {
        var store = new ChangeApprovalStore();
        var job = store.AddJob(approvedDefinitionHash: ChangeApprovalHashes.NothingApproved);
        var request = store.AddRequest(ChangeApprovalKind.MigrationJob, job.Id, MigrationJobDefinition.Of(job).Hash, "ext-ben", proposedJson: "not a view");

        var detail = await Approvals(store).GetDetailAsync(request.Id, ChangeApprovalScope.Approver, CancellationToken.None);
        var approve = () => Approvals(store).ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None);

        detail!.Should().BeEquivalentTo(new { MayApprove = false, ApproveBlockedReason = ChangeApprovalService.UnreadableProposalMessage, ProposedMigrationJob = (object?)null }, x => x.ExcludingMissingMembers());
        await approve.Should().ThrowAsync<BeaconConflictException>().WithMessage(ChangeApprovalService.UnreadableProposalMessage);
        request.Status.Should().Be(ApprovalStatus.Pending);
        job.ApprovedDefinitionHash.Should().Be(ChangeApprovalHashes.NothingApproved);
    }

    // Those who may change recipients see the fingerprints a reviewer is shown, so both can compare them; nobody else does.
    [Test]
    public async Task TheRecipientsPage_ShowsThoseWhoChangeRecipients_TheFingerprintsAReviewerSees()
    {
        var store = new ChangeApprovalStore();
        var created = await CreateHandler(store).Handle(
            new CreateRecipientCommand("ops hook", null, Destination, (int)NotificationType.Webhook, """{"Authorization":"Bearer s3cr3t-value"}""", null),
            CancellationToken.None);
        store.Commit();
        var proposed = (await Approvals(store).GetDetailAsync(store.Requests.Single().Id, ChangeApprovalScope.Approver, CancellationToken.None))!.ProposedRecipientDestination!;

        var editor = (await RecipientsHandler(store, TestPermissions.Admin).Handle(new GetRecipientsQuery(), CancellationToken.None)).Items.Single();
        var reader = (await RecipientsHandler(store, TestPermissions.None).Handle(new GetRecipientsQuery(), CancellationToken.None)).Items.Single();

        created.Should().BeEquivalentTo(new { AwaitingApproval = true, Message = CreateRecipientResult.SubmittedForApprovalMessage }, x => x.ExcludingMissingMembers());
        editor.DefinitionFingerprint.Should().NotBeNull().And.Be(proposed.Fingerprint);
        editor.HeaderFingerprints.Should().BeEquivalentTo(proposed.Headers);
        reader.DefinitionFingerprint.Should().BeNull();
        reader.HeaderFingerprints.Should().BeNull();
        JsonSerializer.Serialize(editor).Should().NotContain("s3cr3t");
    }

    // No API model carries a stored hash: neither the approved hash of a governed resource nor a request's definition hash.
    [Test]
    public void NoApiModel_HasAPropertyForAStoredHash()
    {
        string[] stored = ["ApprovedDestinationHash", "ApprovedSqlHash", "ApprovedDefinitionHash", "DefinitionHash"];

        var exposing = typeof(ChangeApprovalService).Assembly.GetExportedTypes()
            .Where(x => x.Namespace?.StartsWith("Beacon.Core.Data.Entities", StringComparison.Ordinal) != true)
            .SelectMany(x => x.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(y => $"{x.Name}.{y.Name}"))
            .Where(x => stored.Any(y => x.EndsWith("." + y, StringComparison.Ordinal)))
            .ToList();

        exposing.Should().BeEmpty();
    }

    // And their values never appear in what the recipient list, the request list or a request's detail returns.
    [Test]
    public async Task NoResponse_CarriesAStoredHashValue()
    {
        var store = new ChangeApprovalStore();
        var recipient = store.AddRecipient(Destination, ChangeApprovalStore.HookHash(Destination));
        await UpdateHandler(store).Handle(new UpdateRecipientCommand(recipient.Id, recipient.Name, null, "https://other.example.org/in/" + Secret, (int)NotificationType.Webhook, null, null), CancellationToken.None);
        store.Commit();
        var request = store.Requests.Single();
        string[] hashes = [recipient.ApprovedDestinationHash!, request.DefinitionHash];

        var responses = new object?[]
        {
            await RecipientsHandler(store, TestPermissions.Admin).Handle(new GetRecipientsQuery(), CancellationToken.None),
            await Approvals(store).GetPendingAsync(new GetChangeApprovalsQuery(), null, ChangeApprovalScope.Approver, CancellationToken.None),
            await Approvals(store).GetDetailAsync(request.Id, ChangeApprovalScope.Approver, CancellationToken.None)
        };

        foreach (var json in responses.Select(x => JsonSerializer.Serialize(x)))
        {
            foreach (var hash in hashes)
            {
                json.Should().NotContain(hash);
            }
        }
    }

    private static RecipientDestinationView View(RecipientSecretProtector protector, RecipientDestinationHasher hasher, string headersJson) =>
        RecipientDestinationDefinition.ViewOfStored(protector, hasher, NotificationType.Webhook, protector.Protect(Destination), protector.Protect(headersJson));

    private static RecipientSecretEditor Editor(ChangeApprovalStore store) => new(NotificationTestKit.Policy(), store.Protector);

    private static CreateRecipientHandler CreateHandler(ChangeApprovalStore store) =>
        new(store.Factory(), Editor(store), TestChangeApprovals.Recipients(store.Protector, TestChangeApprovals.On(), TestChangeApprovals.Caller(Ada)));

    private static UpdateRecipientHandler UpdateHandler(ChangeApprovalStore store) =>
        new(
            store.Factory(),
            Editor(store),
            TestProjectAccess.Evaluator(TestProjectAccess.Unrestricted),
            TestProjectAccess.Actors(),
            TestPermissions.Admin,
            TestChangeApprovals.Recipients(store.Protector, TestChangeApprovals.On(), TestChangeApprovals.Caller(Ada)),
            NullLogger<UpdateRecipientHandler>.Instance);

    private static GetRecipientsHandler RecipientsHandler(ChangeApprovalStore store, IBeaconPermissionAccessor permissions) =>
        new(
            store.Factory(),
            permissions,
            Editor(store),
            TestChangeApprovals.Deliveries(store.Factory(), store.Protector, TestChangeApprovals.On()),
            store.Protector,
            TestChangeApprovals.Hasher);

    private static ChangeApprovalService Approvals(ChangeApprovalStore store) =>
        new(
            store.Factory(),
            TestChangeApprovals.Caller("ext-cid"),
            Mock.Of<IBeaconUserContext>(),
            store.Protector,
            TestChangeApprovals.Hasher,
            TestChangeApprovals.On(),
            NullLogger<ChangeApprovalService>.Instance);
}
