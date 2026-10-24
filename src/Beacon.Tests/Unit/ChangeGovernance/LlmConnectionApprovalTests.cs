using System.Text.Json;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.AdminSettings;
using Beacon.Core.Models;
using Beacon.Core.Models.Approvals;
using Beacon.Core.Models.Settings;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ChangeGovernance;

/// <summary>
/// A change of the stored LLM connection under the approval workflow: an Admin's save that changes the provider, endpoint,
/// region or authentication mode (or stores a first provider over the host's configuration) is staged, encrypted, while
/// the stored connection and models stay in use and the other settings are saved with the request in one unit of work; a
/// change of credentials or models alone applies at once. A live Admin other than the requester approves: the staged
/// connection is written with its history attributed to the requester and an admin-settings event, the settings cache is
/// cleared and the LLM configuration updated, and the staged values are wiped. They are wiped too when the request is
/// rejected, superseded, found stale or expired. The reviewer never sees the endpoint's path, a credential or the payload.
/// </summary>
[TestFixture]
public class LlmConnectionApprovalTests
{
    private const string Ada = "ext-ada";
    private const string Ben = "ext-ben";
    private const string StoredEndpoint = "https://api.openai.example/v1";
    private const string ProposedEndpoint = "https://llm.example/openai/deployments/beacon?api-version=2024";
    private const string StoredKey = "stored-key-value";
    private const string ProposedKey = "proposed-key-value";

    private static readonly EncryptionService Encryption = new(TestEncryptionKey.Value);

    [Test]
    public async Task AMixedSave_StagesTheConnection_SavesTheRestWithTheRequest_AndKeepsTheStoredConnectionInUse()
    {
        var harness = new Harness();
        harness.StoreConnection();

        await harness.Handler(Ben).Handle(ChangeOfProvider() with { BaseUrl = "https://beacon.new.example", LlmMonthlyBudget = 42 }, CancellationToken.None);
        harness.Store.Commit();

        var request = harness.Store.Requests.Should().ContainSingle().Subject;
        request.Should().BeEquivalentTo(
            new
            {
                Kind = ChangeApprovalKind.LlmConnection,
                ResourceId = LlmConnectionApproval.ResourceId,
                Status = ApprovalStatus.Pending,
                RequestedByUserId = Ben,
                ChangeSummary = "Provider, Endpoint changed"
            },
            x => x.ExcludingMissingMembers());
        var staged = JsonSerializer.Deserialize<LlmConnectionDefinition>(Encryption.Decrypt(request.EncryptedPayload!), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        staged.Should().BeEquivalentTo(new { Provider = AiProvider.AzureOpenAI, Endpoint = ProposedEndpoint, ApiKey = ProposedKey, Model = "gpt-next" }, x => x.ExcludingMissingMembers());

        var stored = await harness.Settings().GetSettingsAsync(CancellationToken.None);
        stored.Should().BeEquivalentTo(
            new
            {
                LlmProvider = AiProvider.OpenAI,
                LlmEndpoint = StoredEndpoint,
                LlmApiKey = StoredKey,
                LlmModel = "gpt",
                BaseUrl = "https://beacon.new.example",
                LlmMonthlyBudget = 42m
            },
            x => x.ExcludingMissingMembers());
        harness.Store.Offline.Commits.Should().Be(1, "the request and the other settings commit together");
        harness.Audit.Of(SecurityAuditActions.ChangeSubmitted).Should().ContainSingle();
        harness.Audit.Of(SecurityAuditActions.AdminSettingsChanged).Should().ContainSingle();
        harness.History().Select(x => x.SettingKey).Should().NotContain(x => x == "LLM.Provider" || x == "LLM.Endpoint" || x == "LLM.Model" || x == "LLM.ApiKey");
        harness.Updater.Applied.Should().Equal((AiProvider.OpenAI, true));
    }

    [Test]
    public async Task AMixedSave_WhoseAfterCommitFails_StandsAndLogsTheTypeOnly()
    {
        var harness = new Harness();
        harness.StoreConnection();
        harness.Updater.Fails = true;

        await harness.Handler(Ben).Handle(ChangeOfProvider() with { BaseUrl = "https://beacon.new.example" }, CancellationToken.None);
        harness.Store.Commit();

        harness.Store.Requests.Should().ContainSingle().Which.Status.Should().Be(ApprovalStatus.Pending);
        harness.Log.Entries.Should().ContainSingle(x => x.Level == LogLevel.Error)
            .Which.Message.Should().Contain("InvalidOperationException").And.NotContain("could not be created");
    }

    [Test]
    public async Task AMixedSave_RefusedWhileAnotherPersonsChangeWaits_SavesNothing()
    {
        var harness = new Harness();
        harness.StoreConnection();
        await harness.Handler(Ada).Handle(ChangeOfProvider(), CancellationToken.None);
        harness.Store.Commit();

        var act = () => harness.Handler(Ben).Handle(ChangeOfProvider() with { LlmProvider = AiProvider.Claude, BaseUrl = "https://beacon.other.example" }, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(ChangeApprovalSubmitter.OtherPendingMessage(ChangeApprovalKind.LlmConnection));
        harness.Store.Requests.Should().ContainSingle().Which.RequestedByUserId.Should().Be(Ada);
        (await harness.Settings().GetSettingsAsync(CancellationToken.None)).BaseUrl.Should().Be("https://beacon.example");
    }

    [Test]
    public async Task ASecondChangeOfTheRequester_SupersedesTheirOwn_AndWipesWhatItStaged()
    {
        var harness = new Harness();
        harness.StoreConnection();
        await harness.Handler(Ben).Handle(ChangeOfProvider(), CancellationToken.None);
        harness.Store.Commit();
        var first = harness.Store.Requests.Single();

        await harness.Handler(Ben).Handle(ChangeOfProvider() with { LlmProvider = AiProvider.Claude }, CancellationToken.None);
        harness.Store.Commit();

        first.Status.Should().Be(ApprovalStatus.Superseded);
        first.EncryptedPayload.Should().BeNull();
        harness.Store.Requests.Should().HaveCount(2).And.ContainSingle(x => x.Status == ApprovalStatus.Pending);
    }

    // The unique index on the pending LLM connection request refuses the second of two submissions racing each other.
    [Test]
    public async Task ASubmissionLosingTheRaceToTheUniqueIndex_Is409()
    {
        var harness = new Harness { SaveFailure = new DbUpdateException("duplicate", new PostgresException("duplicate", "ERROR", "ERROR", "23505")) };
        harness.StoreConnection();

        var act = () => harness.Handler(Ben).Handle(ChangeOfProvider(), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(ChangeApprovalSubmitter.OtherPendingMessage(ChangeApprovalKind.LlmConnection));
        harness.Store.Offline.Commits.Should().Be(0);
        harness.Updater.Applied.Should().BeEmpty();
    }

    [TestCase("credentials")]
    [TestCase("model")]
    public async Task AChangeOfCredentialsOrModelsAlone_AppliesAtOnce(string change)
    {
        var harness = new Harness();
        harness.StoreConnection();
        var command = change == "credentials"
            ? Save(apiKey: ProposedKey)
            : Save(model: "gpt-next");

        await harness.Handler(Ben).Handle(command, CancellationToken.None);

        harness.Store.Requests.Should().BeEmpty();
        var stored = await harness.Settings().GetSettingsAsync(CancellationToken.None);
        stored.LlmApiKey.Should().Be(change == "credentials" ? ProposedKey : StoredKey);
        stored.LlmModel.Should().Be(change == "model" ? "gpt-next" : "gpt");
    }

    // The region and the authentication mode are governed like the provider and endpoint, each named in the summary.
    [TestCase("region", "Region changed")]
    [TestCase("authMode", "Authentication mode changed")]
    public async Task AChangeOfTheRegionOrTheAuthenticationMode_IsStaged(string change, string summary)
    {
        var harness = new Harness();
        harness.StoreConnection();
        var command = change == "region"
            ? Save() with { LlmRegion = "eu-west-1" }
            : Save() with { LlmBedrockAuthMode = BedrockAuthMode.AccessKey };

        await harness.Handler(Ben).Handle(command, CancellationToken.None);
        harness.Store.Commit();

        harness.Store.Requests.Should().ContainSingle().Which.ChangeSummary.Should().Be(summary);
        harness.Store.Settings.Should().NotContain(x => x.Key == "LLM.Region");
        harness.Store.Settings.Single(x => x.Key == "LLM.BedrockAuthMode").Value.Should().Be(nameof(BedrockAuthMode.IamRole));
    }

    // This process's settings cache still holds the connection another process replaced: a save is judged against the
    // stored connection, so sending the cached one back is a governed change, staged, and the stored one stays.
    [Test]
    public async Task ASaveOfTheCachedConnection_AfterTheStoredOneChanged_IsStaged()
    {
        var harness = new Harness();
        harness.StoreConnection();
        await harness.Settings().GetSettingsAsync(CancellationToken.None);
        harness.ReplaceStoredConnection();

        await harness.Handler(Ben).Handle(Save(), CancellationToken.None);
        harness.Store.Commit();

        harness.Store.Requests.Should().ContainSingle().Which.ChangeSummary.Should().Be("Provider changed");
        harness.Store.Settings.Single(x => x.Key == "LLM.Provider").Value.Should().Be(nameof(AiProvider.AzureOpenAI));
        Encryption.Decrypt(harness.Store.Settings.Single(x => x.Key == "LLM.Endpoint").Value!).Should().Be(ProposedEndpoint);
    }

    [Test]
    public async Task ASecurityAdminSendingTheCachedConnectionBack_AfterTheStoredOneChanged_IsRefused()
    {
        var harness = new Harness();
        harness.StoreConnection();
        await harness.Settings().GetSettingsAsync(CancellationToken.None);
        harness.ReplaceStoredConnection();

        var act = () => harness.Handler(Ben, TestPermissions.Holding(BeaconPermissions.SecurityAdmin)).Handle(Save(model: "gpt-next"), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(LlmConnectionSettings.AdminOnlyMessage);
        harness.Store.Settings.Single(x => x.Key == "LLM.Provider").Value.Should().Be(nameof(AiProvider.AzureOpenAI));
        harness.Store.Requests.Should().BeEmpty();
    }

    [Test]
    public async Task ACallerWhoIsNoAdmin_IsRefusedBeforeAnythingIsStaged()
    {
        var harness = new Harness();
        harness.StoreConnection();

        var act = () => harness.Handler(Ben, TestPermissions.Holding(BeaconPermissions.SecurityAdmin)).Handle(ChangeOfProvider(), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(LlmConnectionSettings.AdminOnlyMessage);
        harness.Store.Requests.Should().BeEmpty();
        harness.Store.Offline.TransactionIsolationLevels.Should().BeEmpty();
    }

    // Nothing stored yet: the host's Beacon:LLM configures the connection, and the first stored provider is governed, its
    // previous connection shown as the host's configuration with no value.
    [Test]
    public async Task TheFirstStoredProvider_OverTheHostsConfiguration_IsGoverned()
    {
        var harness = new Harness();

        await harness.Handler(Ben).Handle(Save(provider: AiProvider.OpenAI, apiKey: ProposedKey), CancellationToken.None);
        harness.Store.Commit();

        var request = harness.Store.Requests.Should().ContainSingle().Subject;
        request.ChangeSummary.Should().Be("Provider, Endpoint, Region, Authentication mode changed");
        JsonSerializer.Deserialize<LlmConnectionView>(request.PreviousJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .Should().BeEquivalentTo(new LlmConnectionView { FromHostConfiguration = true });
        harness.Store.Settings.Should().NotContain(x => x.Key == "LLM.Provider" || x.Key == "LLM.ApiKey");
    }

    // A save made while the workflow does not cover the LLM connection applies at once; a pending request it leaves made
    // from another connection is superseded and its staged values wiped.
    [Test]
    public async Task ASaveWithoutCoverage_AppliesAtOnce_AndSupersedesThePendingRequestsItMakesMoot()
    {
        var harness = new Harness();
        harness.StoreConnection();
        await harness.Handler(Ada).Handle(ChangeOfProvider(), CancellationToken.None);
        harness.Store.Commit();
        var pending = harness.Store.Requests.Single();
        harness.Configuration.ApprovalWorkflow.CoversLlmConnection = false;

        await harness.Handler(Ben).Handle(ChangeOfProvider() with { LlmProvider = AiProvider.Claude }, CancellationToken.None);

        (await harness.Settings().GetSettingsAsync(CancellationToken.None)).LlmProvider.Should().Be(AiProvider.Claude);
        pending.Status.Should().Be(ApprovalStatus.Superseded);
        pending.EncryptedPayload.Should().BeNull();
    }

    [Test]
    public async Task Approve_WritesTheStagedConnection_AsTheRequestersChange_AndAppliesIt()
    {
        var harness = new Harness();
        harness.StoreConnection();
        var request = await harness.SubmittedAsync(Ben);
        harness.Store.Settings.Single(x => x.Key == "LLM.MaxConcurrentRequests").Value = "7";
        harness.Updater.Applied.Clear();
        var submitted = harness.History().Count;
        (await harness.Settings().GetSettingsAsync(CancellationToken.None)).LlmProvider.Should().Be(AiProvider.OpenAI, "the settings cache holds the connection before the approval");

        await harness.Decisions(Ada).ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None);

        request.Status.Should().Be(ApprovalStatus.Approved);
        request.EncryptedPayload.Should().BeNull("an applied request keeps no staged values");
        var stored = await harness.Settings().GetSettingsAsync(CancellationToken.None);
        stored.Should().BeEquivalentTo(
            new { LlmProvider = AiProvider.AzureOpenAI, LlmEndpoint = ProposedEndpoint, LlmApiKey = ProposedKey, LlmModel = "gpt-next", LlmMaxConcurrentRequests = 7 },
            x => x.ExcludingMissingMembers());
        var approval = harness.History().Skip(submitted).ToList();
        approval.Should().OnlyContain(x => x.ChangedByUserId == Ben, "the change is the requester's");
        var applied = approval.Select(x => x.SettingKey).ToList();
        applied.Should().BeEquivalentTo(["LLM.Provider", "LLM.Endpoint", "LLM.ApiKey", "LLM.Model"]);
        applied.Should().NotContain(x => x == "LLM.MaxConcurrentRequests" || x == "LLM.RateLimitsEnabled" || x == "General.BaseUrl", "an approval writes no limit and nothing but the connection");
        harness.Audit.Of(SecurityAuditActions.ChangeApproved).Should().ContainSingle();
        harness.Audit.Of(SecurityAuditActions.AdminSettingsChanged).Should().HaveCount(2, "the submission's save of the other settings, and the approval's write of the connection");
        harness.Updater.Applied.Should().Equal((AiProvider.AzureOpenAI, false));
    }

    // Four eyes hold for the LLM connection too: its requester, a live Admin, approves it only where self-approval is
    // allowed, and may always withdraw it by rejecting it.
    [TestCase(false)]
    [TestCase(true)]
    public async Task Approve_ByItsRequester_FollowsSelfApproval(bool allowSelfApproval)
    {
        var harness = new Harness();
        harness.Configuration.ApprovalWorkflow.AllowSelfApproval = allowSelfApproval;
        harness.StoreConnection();
        var request = await harness.SubmittedAsync(Ben);

        var approve = () => harness.Decisions(Ben).ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None);

        if (allowSelfApproval)
        {
            await approve.Should().NotThrowAsync();
            request.Status.Should().Be(ApprovalStatus.Approved);
            harness.Store.Settings.Single(x => x.Key == "LLM.Provider").Value.Should().Be(nameof(AiProvider.AzureOpenAI));

            return;
        }

        await approve.Should().ThrowAsync<BeaconForbiddenException>();
        request.Status.Should().Be(ApprovalStatus.Pending);
        harness.Store.Settings.Single(x => x.Key == "LLM.Provider").Value.Should().Be(nameof(AiProvider.OpenAI));
    }

    [Test]
    public async Task Reject_ByItsRequester_WithdrawsIt_AndWipesWhatItStaged()
    {
        var harness = new Harness();
        harness.StoreConnection();
        var request = await harness.SubmittedAsync(Ben);

        await harness.Decisions(Ben).RejectAsync(request.Id, "Withdrawn.", CancellationToken.None);

        request.Status.Should().Be(ApprovalStatus.Rejected);
        request.EncryptedPayload.Should().BeNull();
        harness.Store.Settings.Single(x => x.Key == "LLM.Provider").Value.Should().Be(nameof(AiProvider.OpenAI));
    }

    // Who decides is read again inside the decision's transaction, after the LLM connection's lock, from the user store as
    // it is now: an Admin disabled or stripped of the Admin role while the decision waited for that lock does not decide;
    // the request stays pending with what it staged, and the stored connection stays in use.
    [TestCase("approve", "disabled")]
    [TestCase("approve", "admin role removed")]
    [TestCase("reject", "disabled")]
    [TestCase("reject", "admin role removed")]
    public async Task ADecision_ByAnAdminRevokedWhileItWaitedForTheLock_IsRefused_AndChangesNothing(string decision, string revocation)
    {
        var harness = new Harness();
        harness.StoreConnection();
        var request = await harness.SubmittedAsync(Ben);
        var ada = harness.Store.UserHolding(9, Ada, RoleService.RoleNames.Admin);
        var locksBefore = harness.Lock.Acquired;
        var reviewers = new RevokingReviewerAuthority(TestChangeApprovals.Reviewers(), () =>
        {
            harness.Lock.Acquired.Should().Be(locksBefore + 1, "the reviewer is read again once the LLM connection's lock is held");
            if (revocation == "disabled")
            {
                ada.IsEnabled = false;
                return;
            }

            ada.UserRoles.Clear();
        });
        var decisions = harness.Decisions(Ada, reviewers);

        Func<Task> act = decision == "approve"
            ? () => decisions.ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None)
            : () => decisions.RejectAsync(request.Id, null, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(
            revocation == "disabled" ? ChangeApprovalService.ReviewerInactiveMessage : ChangeApprovalService.LiveAdminRequiredMessage);
        reviewers.ReadInsideTheTransaction.Should().BeTrue();
        request.Status.Should().Be(ApprovalStatus.Pending);
        request.EncryptedPayload.Should().NotBeNull("a refused decision wipes nothing it staged");
        harness.Store.Settings.Single(x => x.Key == "LLM.Provider").Value.Should().Be(nameof(AiProvider.OpenAI));
        harness.Audit.Of(SecurityAuditActions.ChangeApproved).Should().BeEmpty();
        harness.Audit.Of(SecurityAuditActions.ChangeRejected).Should().BeEmpty();
    }

    [Test]
    public async Task Approve_ByAnAdminWhoStillIsOne_IsDecided()
    {
        var harness = new Harness();
        harness.StoreConnection();
        var request = await harness.SubmittedAsync(Ben);
        harness.Store.UserHolding(9, Ada, RoleService.RoleNames.Admin);
        var reviewers = new RevokingReviewerAuthority(TestChangeApprovals.Reviewers(), () => { });

        await harness.Decisions(Ada, reviewers).ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None);

        reviewers.Reads.Should().Be(1);
        request.Status.Should().Be(ApprovalStatus.Approved);
        harness.Store.Settings.Single(x => x.Key == "LLM.Provider").Value.Should().Be(nameof(AiProvider.AzureOpenAI));
    }

    // The approval is committed, but this process could not apply it: the approver is told so, not shown a plain success.
    [Test]
    public async Task Approve_ThatCannotBeAppliedInThisProcess_StandsAndTellsTheApprover()
    {
        var harness = new Harness();
        harness.StoreConnection();
        var request = await harness.SubmittedAsync(Ben);
        harness.Updater.Fails = true;

        var act = () => harness.Decisions(Ada).ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(ChangeApprovalService.ApprovedNotAppliedMessage);
        request.Status.Should().Be(ApprovalStatus.Approved);
        request.EncryptedPayload.Should().BeNull();
        harness.Store.Settings.Single(x => x.Key == "LLM.Provider").Value.Should().Be(nameof(AiProvider.AzureOpenAI));
    }

    [Test]
    public async Task Approve_OfAProviderNoLongerInstalled_IsRefused_AndTheRequestStaysPending()
    {
        var harness = new Harness();
        harness.StoreConnection();
        var request = await harness.SubmittedAsync(Ben);
        harness.ApprovalCatalog = Catalog(AiProvider.OpenAI);

        var act = () => harness.Decisions(Ada).ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None);

        await act.Should().ThrowExactlyAsync<InvalidOperationException>().WithMessage(LlmProviderInstallation.NotInstalledMessage(AiProvider.AzureOpenAI));
        request.Status.Should().Be(ApprovalStatus.Pending);
        request.EncryptedPayload.Should().NotBeNull();
        (await harness.Settings().GetSettingsAsync(CancellationToken.None)).LlmProvider.Should().Be(AiProvider.OpenAI);
    }

    [TestCase("absent")]
    [TestCase("unreadable")]
    [TestCase("not the one shown")]
    public async Task Approve_OfAStagedConnectionThatCannotBeRead_Is409(string payload)
    {
        var harness = new Harness();
        harness.StoreConnection();
        var request = await harness.SubmittedAsync(Ben);
        request.EncryptedPayload = payload switch
        {
            "absent" => null,
            "unreadable" => new EncryptionService("a key nobody configured for this test").Encrypt("{}"),
            _ => Encryption.Encrypt(JsonSerializer.Serialize(new LlmConnectionDefinition { Provider = AiProvider.AzureOpenAI, Endpoint = "https://elsewhere.example/x", ApiKey = ProposedKey }))
        };

        var act = () => harness.Decisions(Ada).ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(ChangeApprovalService.UnreadableProposalMessage);
        request.Status.Should().Be(ApprovalStatus.Pending);
        (await harness.Settings().GetSettingsAsync(CancellationToken.None)).LlmProvider.Should().Be(AiProvider.OpenAI);
    }

    // The stored connection changed since the request (here its key, an immediate change): the request was made from
    // another connection, so the approval is refused, and the request is superseded and wiped after the rollback.
    [Test]
    public async Task Approve_AfterTheStoredConnectionChanged_IsStale_AndSupersedesAndWipesTheRequest()
    {
        var harness = new Harness();
        harness.StoreConnection();
        var request = await harness.SubmittedAsync(Ben);
        await harness.Handler(Ben).Handle(Save(apiKey: "rotated-key-value"), CancellationToken.None);

        var act = () => harness.Decisions(Ada).ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(ChangeApprovalService.ChangedSinceSubmittedMessage);
        request.Status.Should().Be(ApprovalStatus.Superseded);
        request.EncryptedPayload.Should().BeNull();
        (await harness.Settings().GetSettingsAsync(CancellationToken.None)).LlmProvider.Should().Be(AiProvider.OpenAI);
    }

    [Test]
    public async Task Reject_WipesWhatTheRequestStaged()
    {
        var harness = new Harness();
        harness.StoreConnection();
        var request = await harness.SubmittedAsync(Ben);

        await harness.Decisions(Ada).RejectAsync(request.Id, "Not this tenant.", CancellationToken.None);

        request.Status.Should().Be(ApprovalStatus.Rejected);
        request.EncryptedPayload.Should().BeNull();
        (await harness.Settings().GetSettingsAsync(CancellationToken.None)).LlmProvider.Should().Be(AiProvider.OpenAI);
    }

    [TestCase("approve")]
    [TestCase("reject")]
    public async Task ADecisionOnARequestPastItsExpiry_Is409_AndWipesIt(string decision)
    {
        var harness = new Harness();
        harness.StoreConnection();
        var request = await harness.SubmittedAsync(Ben);
        request.CreatedTime = DateTime.UtcNow.AddDays(-8);
        var transactions = harness.Store.Offline.TransactionIsolationLevels.Count;

        Func<Task> act = decision == "approve"
            ? () => harness.Decisions(Ada).ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None)
            : () => harness.Decisions(Ada).RejectAsync(request.Id, null, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(LlmConnectionApproval.ExpiredMessage);
        request.Status.Should().Be(ApprovalStatus.Superseded);
        request.EncryptedPayload.Should().BeNull();
        harness.Store.Offline.TransactionIsolationLevels.Should().HaveCount(transactions, "nothing is decided");
    }

    [Test]
    public async Task ARequestPastItsExpiry_IsWipedWhenListedOrRead_AndNoLongerBlocksAnotherPersonsChange()
    {
        var harness = new Harness { Days = 3 };
        harness.StoreConnection();
        var listed = await harness.SubmittedAsync(Ada);
        listed.CreatedTime = DateTime.UtcNow.AddDays(-4);

        var list = await harness.Decisions(Ada).GetPendingAsync(new Beacon.Core.Handlers.Approvals.GetChangeApprovalsQuery(), null, LiveAdmin, CancellationToken.None);

        list.Items.Should().BeEmpty();
        listed.Should().BeEquivalentTo(new { Status = ApprovalStatus.Superseded, EncryptedPayload = (string?)null }, x => x.ExcludingMissingMembers());

        listed.Status = ApprovalStatus.Pending;
        listed.EncryptedPayload = "staged";
        var detail = await harness.Decisions(Ada).GetDetailAsync(listed.Id, LiveAdmin, CancellationToken.None);

        detail!.Status.Should().Be(ApprovalStatus.Superseded);
        detail.MayApprove.Should().BeFalse();
        listed.EncryptedPayload.Should().BeNull();

        listed.Status = ApprovalStatus.Pending;
        listed.EncryptedPayload = "staged";
        await harness.Handler(Ben).Handle(ChangeOfProvider(), CancellationToken.None);

        listed.Should().BeEquivalentTo(new { Status = ApprovalStatus.Superseded, EncryptedPayload = (string?)null }, x => x.ExcludingMissingMembers());
        harness.Store.Saved.OfType<ChangeApprovalRequest>().Should().Contain(x => x.RequestedByUserId == Ben && x.Status == ApprovalStatus.Pending);
    }

    // The reviewer sees the endpoint as scheme and host with a fingerprint, each credential as a fingerprint, and names in
    // the summary: never the endpoint's path or query, a credential, or the staged payload.
    [Test]
    public async Task TheDetail_NeverHoldsTheEndpointsPath_ACredential_OrThePayload()
    {
        var harness = new Harness();
        harness.StoreConnection();
        var request = await harness.SubmittedAsync(Ben);

        var detail = await harness.Decisions(Ada).GetDetailAsync(request.Id, LiveAdmin, CancellationToken.None);
        var list = await harness.Decisions(Ada).GetPendingAsync(new Beacon.Core.Handlers.Approvals.GetChangeApprovalsQuery(), ChangeApprovalKind.LlmConnection, LiveAdmin, CancellationToken.None);

        detail!.ProposedLlmConnection.Should().BeEquivalentTo(
            new { Provider = AiProvider.AzureOpenAI, Endpoint = "https://llm.example", Model = "gpt-next" },
            x => x.ExcludingMissingMembers());
        detail.ProposedLlmConnection!.EndpointFingerprint.Should().HaveLength(ConnectionDefinitionHasher.FingerprintLength);
        detail.ProposedLlmConnection.Credentials.Should().ContainSingle().Which.Name.Should().Be(LlmConnectionDefinition.ApiKeyField);
        detail.PreviousLlmConnection!.Endpoint.Should().Be("https://api.openai.example");
        detail.ChangeSummary.Should().Be("Provider, Endpoint changed");
        detail.MayApprove.Should().BeTrue();
        detail.CommentRequiredToApprove.Should().BeFalse();
        foreach (var shown in new[] { JsonSerializer.Serialize(detail), JsonSerializer.Serialize(list), request.ProposedJson, request.PreviousJson!, request.ChangeSummary! })
        {
            shown.Should().NotContain("deployments").And.NotContain("api-version").And.NotContain("/v1")
                .And.NotContain(StoredKey).And.NotContain(ProposedKey).And.NotContain(request.EncryptedPayload!);
        }
    }

    // A change that lies only in the endpoint's path is shown by fingerprints alone: approving it needs a comment.
    [Test]
    public async Task AChangeOnlyInTheEndpointsPath_IsApprovedWithAComment()
    {
        var harness = new Harness();
        harness.StoreConnection();
        await harness.Handler(Ben).Handle(Save(endpoint: "https://api.openai.example/v2"), CancellationToken.None);
        harness.Store.Commit();
        var request = harness.Store.Requests.Single();

        var detail = await harness.Decisions(Ada).GetDetailAsync(request.Id, LiveAdmin, CancellationToken.None);
        var withoutComment = () => harness.Decisions(Ada).ApproveAsync(request.Id, " ", request.PayloadHash, CancellationToken.None);

        request.ChangeSummary.Should().Be("Endpoint changed");
        detail!.CommentRequiredToApprove.Should().BeTrue();
        await withoutComment.Should().ThrowExactlyAsync<InvalidOperationException>().WithMessage(LlmConnectionApproval.CommentRequiredMessage);

        await harness.Decisions(Ada).ApproveAsync(request.Id, "Compared the endpoint fingerprint with the requester.", request.PayloadHash, CancellationToken.None);

        (await harness.Settings().GetSettingsAsync(CancellationToken.None)).LlmEndpoint.Should().Be("https://api.openai.example/v2");
    }

    // A change of credentials alone, saved while an approval writes a staged connection, waits for the approval's unit of
    // work and is then judged against the connection approved: here it names the earlier provider, so it is staged, and the
    // approved connection stays as approved.
    [Test]
    public async Task ACredentialChangeSavedWhileAnApprovalWrites_WaitsForIt_AndIsJudgedAgainstTheApprovedConnection()
    {
        var harness = new Harness();
        harness.StoreConnection();
        var request = await harness.SubmittedAsync(Ben);
        Task? rotation = null;
        harness.Stager.BeforeStage(AppSettingsParts.ConnectionOnly, async () =>
        {
            rotation = harness.Handler(Ada).Handle(Save(apiKey: "rotated-key-value"), CancellationToken.None);
            await Task.WhenAny(rotation, harness.Lock.Waiting);
        });

        await harness.Decisions(Ada).ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None);
        harness.Lock.Resume();
        await rotation!.WaitAsync(TimeSpan.FromSeconds(10));
        harness.Store.Commit();

        request.Status.Should().Be(ApprovalStatus.Approved);
        var stored = await harness.Settings().GetSettingsAsync(CancellationToken.None);
        stored.Should().BeEquivalentTo(new { LlmProvider = AiProvider.AzureOpenAI, LlmEndpoint = ProposedEndpoint, LlmApiKey = ProposedKey }, x => x.ExcludingMissingMembers());
        harness.Store.Requests.Should().ContainSingle(x => x.Status == ApprovalStatus.Pending)
            .Which.Should().BeEquivalentTo(new { RequestedByUserId = Ada, ChangeSummary = "Provider, Endpoint changed" }, x => x.ExcludingMissingMembers());
        harness.History().Should().NotContain(x => x.ChangedByUserId == Ada && x.SettingKey.StartsWith("LLM.", StringComparison.Ordinal), "the save wrote no part of the connection");
    }

    // An approval decided while a change of credentials alone is being saved waits for the save's unit of work, then finds
    // the stored connection changed since the request: refused, superseded and wiped, and the saved credentials stay.
    [Test]
    public async Task AnApprovalDecidedWhileACredentialChangeIsSaved_WaitsForIt_AndIsRefusedAsStale()
    {
        var harness = new Harness();
        harness.StoreConnection();
        var request = await harness.SubmittedAsync(Ben);
        Task? approval = null;
        harness.Stager.BeforeStage(AppSettingsParts.Everything, async () =>
        {
            approval = harness.Decisions(Ada).ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None);
            await Task.WhenAny(approval, harness.Lock.Waiting);
        });

        await harness.Handler(Ben).Handle(Save(apiKey: "rotated-key-value"), CancellationToken.None);
        harness.Lock.Resume();
        var decided = () => approval!.WaitAsync(TimeSpan.FromSeconds(10));

        await decided.Should().ThrowAsync<BeaconConflictException>().WithMessage(ChangeApprovalService.ChangedSinceSubmittedMessage);
        request.Status.Should().Be(ApprovalStatus.Superseded);
        request.EncryptedPayload.Should().BeNull();
        var stored = await harness.Settings().GetSettingsAsync(CancellationToken.None);
        stored.Should().BeEquivalentTo(new { LlmProvider = AiProvider.OpenAI, LlmEndpoint = StoredEndpoint, LlmApiKey = "rotated-key-value" }, x => x.ExcludingMissingMembers());
    }

    // A save while the workflow does not cover the LLM connection takes the lock too: an approval of a request made while it
    // did never overwrites it.
    [Test]
    public async Task ASaveWithoutCoverage_TakesTheLlmConnectionLock()
    {
        var harness = new Harness();
        harness.StoreConnection();
        harness.Configuration.ApprovalWorkflow.CoversLlmConnection = false;

        await harness.Handler(Ben).Handle(Save(apiKey: "rotated-key-value"), CancellationToken.None);

        harness.Lock.Acquired.Should().Be(1);
        (await harness.Settings().GetSettingsAsync(CancellationToken.None)).LlmApiKey.Should().Be("rotated-key-value");
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task ASaveLeavingTheCredentialsAsTheyAre_AfterAnotherProcessReplacedThem_KeepsTheStoredOnes(bool covered)
    {
        var harness = new Harness();
        harness.StoreConnection();
        harness.Configuration.ApprovalWorkflow.CoversLlmConnection = covered;
        (await harness.Settings().GetSettingsAsync(CancellationToken.None)).LlmApiKey.Should().Be(StoredKey);
        harness.Store.Settings.Single(x => x.Key == "LLM.ApiKey").Value = Encryption.Encrypt("replaced-key-value");

        await harness.Handler(Ben).Handle(Save(model: "gpt-next"), CancellationToken.None);

        Encryption.Decrypt(harness.Store.Settings.Single(x => x.Key == "LLM.ApiKey").Value!).Should().Be("replaced-key-value");
        harness.Store.Settings.Single(x => x.Key == "LLM.Model").Value.Should().Be("gpt-next");
        harness.History().Should().NotContain(x => x.SettingKey == "LLM.ApiKey");
    }

    private static ChangeApprovalScope LiveAdmin => ChangeApprovalScope.Approver with { IsLiveAdmin = true };

    // An Admin's save changing the provider and endpoint, with a new key and model.
    private static UpdateAdminSettingsCommand ChangeOfProvider()
    {
        return Save(provider: AiProvider.AzureOpenAI, endpoint: ProposedEndpoint, apiKey: ProposedKey, model: "gpt-next");
    }

    // A save as the admin settings page sends it: null credentials and endpoint keep the stored ones.
    private static UpdateAdminSettingsCommand Save(
        AiProvider? provider = AiProvider.OpenAI,
        string? endpoint = null,
        string? apiKey = null,
        string model = "gpt")
    {
        return new UpdateAdminSettingsCommand(
            "https://beacon.example",
            provider,
            model,
            null,
            null,
            BedrockAuthMode.IamRole,
            apiKey,
            endpoint,
            null,
            null,
            null,
            50,
            80000,
            1000,
            100m);
    }

    private static AppSetting Row(string key, string value, bool sensitive = false)
    {
        return new AppSetting
        {
            Key = key,
            Value = sensitive ? Encryption.Encrypt(value) : value,
            IsSensitive = sensitive,
            Category = key.StartsWith("General.", StringComparison.Ordinal) ? "General" : "LLM"
        };
    }

    private static ILlmProviderCatalog Catalog(params AiProvider[] installed)
    {
        var catalog = new Mock<ILlmProviderCatalog>();
        catalog.SetupGet(x => x.AiEnabled).Returns(true);
        catalog.SetupGet(x => x.Registered).Returns(installed.Select(x => new LlmProviderInfo(x, true)).ToList());

        return catalog.Object;
    }

    // The workflow covering the LLM connection over one change approval store, with Ben and Ada both Admins.
    private sealed class Harness
    {
        public ChangeApprovalStore Store { get; } = new();

        public BeaconConfiguration Configuration { get; } = new()
        {
            ApprovalWorkflow = new ApprovalWorkflowOptions { Enabled = true, CoversLlmConnection = true }
        };

        public RecordingSecurityAudit Audit { get; } = new();

        public RecordingUpdater Updater { get; } = new();

        public LogRecorder Log { get; } = new();

        public MemoryCache Cache { get; } = new(new MemoryCacheOptions());

        public ILlmProviderCatalog ApprovalCatalog { get; set; } = Catalog(AiProvider.OpenAI, AiProvider.AzureOpenAI, AiProvider.Claude);

        public Exception? SaveFailure { get; init; }

        /// <summary>The LLM connection lock every unit of work over this store takes, held as the database holds it.</summary>
        public SerializingLlmConnectionLock Lock { get; } = new();

        /// <summary>The stager every unit of work over this store writes the settings with.</summary>
        public HookedStager Stager => _stager ??= new HookedStager(Settings());

        private HookedStager? _stager;

        public int Days
        {
            init => Configuration.ApprovalWorkflow.LlmConnectionRequestDays = value;
        }

        public void StoreConnection()
        {
            Store.Settings.AddRange(
            [
                Row("General.BaseUrl", "https://beacon.example"),
                Row("LLM.Provider", nameof(AiProvider.OpenAI)),
                Row("LLM.Endpoint", StoredEndpoint, sensitive: true),
                Row("LLM.ApiKey", StoredKey, sensitive: true),
                Row("LLM.Model", "gpt"),
                Row("LLM.BedrockAuthMode", nameof(BedrockAuthMode.IamRole))
            ]);
        }

        // Another process stored the Azure OpenAI connection, which this process's settings cache does not know.
        public void ReplaceStoredConnection()
        {
            Store.Settings.Single(x => x.Key == "LLM.Provider").Value = nameof(AiProvider.AzureOpenAI);
            Store.Settings.Single(x => x.Key == "LLM.Endpoint").Value = Encryption.Encrypt(ProposedEndpoint);
        }

        public AppSettingsService Settings()
        {
            return new AppSettingsService(Store.Factory(), Cache, Encryption, Configuration, Audit, Updater);
        }

        public UpdateAdminSettingsHandler Handler(string caller, IBeaconPermissionAccessor? permissions = null)
        {
            return new UpdateAdminSettingsHandler(
                Settings(),
                UserContext(caller),
                TestChangeApprovals.Caller(caller, isAdmin: true),
                permissions ?? TestPermissions.Admin,
                Catalog(AiProvider.OpenAI, AiProvider.AzureOpenAI, AiProvider.Claude),
                Approval(caller, Catalog(AiProvider.OpenAI, AiProvider.AzureOpenAI, AiProvider.Claude)),
                NullLogger<UpdateAdminSettingsHandler>.Instance);
        }

        public ChangeApprovalService Decisions(string caller, IReviewerAuthority? reviewers = null)
        {
            return new ChangeApprovalService(
                Store.Factory(),
                TestChangeApprovals.Caller(caller, isAdmin: true),
                UserContext(caller),
                Store.Protector,
                TestChangeApprovals.Hasher,
                Configuration,
                Audit,
                TestPermissions.Admin,
                reviewers ?? TestChangeApprovals.ReviewerUnchanged,
                TestChangeApprovals.NoDefinitions,
                TestChangeApprovals.NoProbe,
                Approval(caller, ApprovalCatalog),
                NullLogger<ChangeApprovalService>.Instance);
        }

        // The change of the provider and endpoint, submitted by requester and committed.
        public async Task<ChangeApprovalRequest> SubmittedAsync(string requester)
        {
            await Handler(requester).Handle(ChangeOfProvider(), CancellationToken.None);
            Store.Commit();

            return Store.Requests.Single(x => x.Status == ApprovalStatus.Pending);
        }

        public List<AppSettingHistory> History()
        {
            return Store.Saved.OfType<AppSettingHistory>().ToList();
        }

        private LlmConnectionApproval Approval(string caller, ILlmProviderCatalog catalog)
        {
            return TestChangeApprovals.Llm(
                Store.Factory(),
                Configuration,
                TestChangeApprovals.Caller(caller, isAdmin: true),
                SaveFailure == null ? Audit : new FailingSave(SaveFailure),
                catalog,
                Updater,
                Settings(),
                cache: Cache,
                stager: Stager,
                connectionLock: Lock,
                logger: Log.For<LlmConnectionApproval>());
        }

        private static IBeaconUserContext UserContext(string caller)
        {
            return Mock.Of<IBeaconUserContext>(x => x.UserId == caller && x.DisplayName == caller);
        }
    }

    // Beacon's own stager, running a hook once, at the first staged save of the parts it names: the point where a unit of
    // work has judged the stored connection and is about to write.
    private sealed class HookedStager(IAppSettingsStager inner) : IAppSettingsStager
    {
        private (AppSettingsParts Parts, Func<Task> Hook)? _beforeStage;

        public void BeforeStage(AppSettingsParts parts, Func<Task> hook)
        {
            _beforeStage = (parts, hook);
        }

        public Task<AppSettingsData> ReadStoredAsync(BeaconContext context, CancellationToken cancellationToken)
        {
            return inner.ReadStoredAsync(context, cancellationToken);
        }

        public async Task<StagedAppSettings> StageAsync(
            BeaconContext context,
            AppSettingsData settings,
            string? changedByUserId,
            AppSettingsParts parts,
            bool saveLimits,
            CancellationToken cancellationToken)
        {
            if (_beforeStage is { } before && before.Parts == parts)
            {
                _beforeStage = null;
                await before.Hook();
            }

            return await inner.StageAsync(context, settings, changedByUserId, parts, saveLimits, cancellationToken);
        }
    }

    // What the LLM configuration was told: the provider applied and whether the limits were.
    private sealed class RecordingUpdater : ILlmConfigurationUpdater
    {
        public List<(AiProvider? Provider, bool ApplyLimits)> Applied { get; } = [];

        public bool Fails { get; set; }

        public void UpdateConfiguration(AppSettingsData settings)
        {
            UpdateConfiguration(settings, applyLimits: true);
        }

        public void UpdateConfiguration(AppSettingsData settings, bool applyLimits)
        {
            if (Fails)
            {
                throw new InvalidOperationException("The provider could not be created.");
            }

            Applied.Add((settings.LlmProvider, applyLimits));
        }
    }

    // A save the database refuses.
    private sealed class FailingSave(Exception failure) : ISecurityAuditRecorder
    {
        public Task StageAsync(BeaconContext context, SecurityAuditEntry entry, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SaveWithEventsAsync(BeaconContext context, Func<IEnumerable<SecurityAuditEntry>> events, CancellationToken cancellationToken) =>
            Task.FromException(failure);

        public Task<T> SaveWithEventsAsync<T>(BeaconContext context, Func<Task<T>> change, Func<T, IEnumerable<SecurityAuditEntry>> events, CancellationToken cancellationToken) =>
            Task.FromException<T>(failure);

        public Task RecordAsync(SecurityAuditEntry entry, CancellationToken cancellationToken) => Task.CompletedTask;

        public string CallerHashOf(string tenantOrIssuer, string subject) => string.Empty;
    }
}
