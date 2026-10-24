using System.Text.Json;
using Beacon.Core.Authorization;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Approvals;
using Beacon.Core.Models;
using Beacon.Core.Notifications;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ChangeGovernance;

/// <summary>
/// The SQL the change requests of migration jobs, Custom SQL rules and recipients send, on PostgreSQL and SQL Server,
/// captured without a database: a decision takes the resource's row first (a no-op update), reads the resource under that
/// lock, decides by a conditional update from Pending and writes the approved hash; a missed decision commits nothing; a
/// submission reads the resource's pending requests and supersedes by a conditional update; the delivery gate, the job
/// pages, the contract detail and the list read by kind, resource and status.
/// </summary>
[TestFixture]
public class ChangeApprovalTranslationTests
{
    private const string Payload = "payload-hash";

    private static readonly RecipientSecretProtector Protector = new(new EncryptionService(TestEncryptionKey.Value));

    private static readonly MigrationJobDefinition Job = new(3, "SELECT 1", 5, "orders", MigrationMode.Insert, null);

    private static readonly string RuleConfig = JsonSerializer.Serialize(new { sql = "SELECT 1 AS passed" });

    [TestCase(false)]
    [TestCase(true)]
    public async Task Approve_AMigrationJob_TakesItsRowFirst_DecidesFromPending_AndWritesTheApprovedHash_Translates(bool sqlServer)
    {
        var capture = Decision(sqlServer, ChangeApprovalKind.MigrationJob, Job.Hash)
            .ThenRowsAffected(1)
            .ThenRow("copy", 3, "SELECT 1", 5, "orders", (int)MigrationMode.Insert, DBNull.Value)
            .ThenRowsAffected(1)
            .ThenRowsAffected(1);

        await Service(capture).ApproveAsync(90, null, Payload, CancellationToken.None);

        capture.Commands.Should().HaveCount(6);
        capture.Commits.Should().Be(1);
        if (sqlServer)
        {
            capture.Commands[2].Should().Be("UPDATE [m]\nSET [m].[ApprovedDefinitionHash] = [m].[ApprovedDefinitionHash]\nFROM [beacon].[MigrationJobs] AS [m]\nWHERE [m].[ArchivedTime] IS NULL AND [m].[Id] = @resourceId");
            ShouldDecideFromPending(capture.Commands[4], sqlServer);
            capture.Commands[5].Should().Contain("SET [m].[ApprovedDefinitionHash] = @").And.Contain("FROM [beacon].[MigrationJobs]").And.EndWith("[m].[Id] = @request_ResourceId");
            return;
        }

        capture.Commands[2].Should().Be("UPDATE beacon.migration_jobs AS m\nSET approved_definition_hash = m.approved_definition_hash\nWHERE m.archived_time IS NULL AND m.id = @resourceId");
        capture.Commands[3].Should().Contain("FROM beacon.migration_jobs").And.Contain("m.query_text").And.Contain("m.transformation_script");
        ShouldDecideFromPending(capture.Commands[4], sqlServer);
        capture.Commands[5].Should().StartWith("UPDATE beacon.migration_jobs").And.Contain("SET approved_definition_hash = @").And.EndWith("m.id = @request_ResourceId");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Approve_CustomSqlRules_WritesEachWaitingRulesHash_Translates(bool sqlServer)
    {
        var ruleHash = CustomSqlRuleDefinition.Hash(3, RuleConfig);
        var capture = Decision(sqlServer, ChangeApprovalKind.CustomSqlRules, CustomSqlRuleDefinition.SetHash([ruleHash]))
            .ThenRowsAffected(1)
            .ThenRows(["orders", 3, 5, 11, RuleConfig, ChangeApprovalHashes.NothingApproved])
            .ThenRowsAffected(1)
            .ThenRowsAffected(1);

        await Service(capture).ApproveAsync(90, null, Payload, CancellationToken.None);

        capture.Commands.Should().HaveCount(6);
        capture.Commits.Should().Be(1);
        capture.CommandParameters[5].Values.Should().Contain(ruleHash).And.Contain(11).And.Contain(5);
        if (sqlServer)
        {
            capture.Commands[2].Should().Contain("SET [d].[IsEnabled] = [d].[IsEnabled]").And.Contain("FROM [beacon].[DataContracts]");
            capture.Commands[3].Should().Contain("[d0].[RuleType] = 8 AND [d0].[ApprovedSqlHash] IS NOT NULL");
            capture.Commands[5].Should().Contain("SET [d].[ApprovedSqlHash] = @").And.Contain("FROM [beacon].[DataContractRules]").And.Contain("[d].[DataContractId] = @");
            return;
        }

        capture.Commands[2].Should().Be("UPDATE beacon.data_contracts AS d\nSET is_enabled = d.is_enabled\nWHERE d.archived_time IS NULL AND d.id = @resourceId");
        capture.Commands[3].Should().Contain("d0.rule_type = 8 AND d0.approved_sql_hash IS NOT NULL").And.Contain("LEFT JOIN");
        capture.Commands[5].Should().StartWith("UPDATE beacon.data_contract_rules").And.Contain("SET approved_sql_hash = @").And.Contain("d.data_contract_id = @");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Approve_ARecipient_ReadsItsStoredDestinationUnderTheLock_Translates(bool sqlServer)
    {
        const string destination = "https://hooks.example.com/in";
        var capture = Decision(sqlServer, ChangeApprovalKind.RecipientDestination, RecipientDestinationDefinition.Hash(TestChangeApprovals.Hasher, NotificationType.Webhook, destination, null))
            .ThenRowsAffected(1)
            .ThenRow("hook", (int)NotificationType.Webhook, Protector.Protect(destination), DBNull.Value)
            .ThenRowsAffected(1)
            .ThenRowsAffected(1);

        await Service(capture).ApproveAsync(90, null, Payload, CancellationToken.None);

        capture.Commands.Should().HaveCount(6);
        capture.Commits.Should().Be(1);
        if (sqlServer)
        {
            capture.Commands[2].Should().Contain("SET [r].[ApprovedDestinationHash] = [r].[ApprovedDestinationHash]").And.Contain("FROM [beacon].[Recipients]");
            capture.Commands[5].Should().Contain("SET [r].[ApprovedDestinationHash] = @").And.EndWith("[r].[Id] = @request_ResourceId");
            return;
        }

        capture.Commands[2].Should().Be("UPDATE beacon.recipients AS r\nSET approved_destination_hash = r.approved_destination_hash\nWHERE r.archived_time IS NULL AND r.id = @resourceId");
        capture.Commands[5].Should().StartWith("UPDATE beacon.recipients").And.Contain("SET approved_destination_hash = @");
    }

    // A data source's connection: probed before the decision, then its row taken by a no-op update of its approved hash and
    // the full source read under that lock (the definition reader hashes its stored connection); a source gone by then is
    // not decided.
    [TestCase(false)]
    [TestCase(true)]
    public async Task Approve_ADataSourceConnection_TakesItsRowFirst_ThenReadsTheSource_Translates(bool sqlServer)
    {
        var capture = Decision(sqlServer, ChangeApprovalKind.DataSourceConnection, "v1:proposed")
            .ThenRowsAffected(1)
            .ThenNoRows();
        var probe = TestChangeApprovals.Probe();

        var act = () => Service(capture, probe).ApproveAsync(90, null, Payload, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(ChangeApprovalService.ResourceGoneMessage);
        probe.Probed.Should().Equal(5);
        capture.Commands.Should().HaveCount(4);
        capture.Commits.Should().Be(0);
        if (sqlServer)
        {
            capture.Commands[2].Should().Be("UPDATE [d]\nSET [d].[ApprovedTargetHash] = [d].[ApprovedTargetHash]\nFROM [beacon].[DataSources] AS [d]\nWHERE [d].[ArchivedTime] IS NULL AND [d].[Id] = @dataSourceId");
            capture.Commands[3].Should().Contain("FROM [beacon].[DataSources] AS [d]").And.Contain("[d].[EncryptedConnectionData]").And.Contain("[d].[ArchivedTime] IS NULL AND [d].[Id] = @resourceId");
            return;
        }

        capture.Commands[2].Should().Be("UPDATE beacon.data_sources AS d\nSET approved_target_hash = d.approved_target_hash\nWHERE d.archived_time IS NULL AND d.id = @dataSourceId");
        capture.Commands[3].Should().Contain("FROM beacon.data_sources AS d").And.Contain("d.encrypted_connection_data").And.Contain("d.archived_time IS NULL AND d.id = @resourceId");
    }

    // An Approver who is no live Admin lists every kind but the LLM connection's and the Admin role's grants, filtered in the
    // SQL; a live Admin every one, once the LLM connection's requests past their expiry are superseded (the list's first
    // statement).
    [TestCase(false, false, "c.kind NOT IN (5, 6)")]
    [TestCase(false, true, null)]
    [TestCase(true, false, "[c].[Kind] NOT IN (5, 6)")]
    [TestCase(true, true, null)]
    public async Task TheApproversList_LeavesOutTheLlmConnectionsAndAdminGrantsUnlessALiveAdminReads_Translates(bool sqlServer, bool isLiveAdmin, string? filter)
    {
        var capture = Capture(sqlServer).AnsweringCountsWithOne();
        var scope = ChangeApprovalScope.Approver with { IsLiveAdmin = isLiveAdmin };

        await Service(capture).GetPendingAsync(new GetChangeApprovalsQuery(), null, scope, CancellationToken.None);

        if (filter == null)
        {
            capture.Commands.Should().HaveCount(3);
            capture.Commands[0].Should().StartWith("UPDATE").And.MatchRegex(@"(?i)kind\]? = 5");
            capture.Commands.Skip(1).Should().OnlyContain(x => !x.Contains("<> 5", StringComparison.Ordinal) && !x.Contains("NOT IN", StringComparison.Ordinal) && x.StartsWith("SELECT", StringComparison.Ordinal));
            return;
        }

        capture.Commands.Should().HaveCount(2);
        capture.Commands.Should().OnlyContain(x => x.Contains(filter, StringComparison.Ordinal));
    }

    // The LLM connection's requests that waited past their expiry: superseded from Pending by one conditional update that
    // wipes what they staged; a list of another kind does not run it.
    [TestCase(false, "UPDATE beacon.change_approval_requests AS c", "encrypted_payload = NULL", "c.kind = 5", "c.status = 1", "c.created_time < @")]
    [TestCase(true, "UPDATE [c]", "[c].[EncryptedPayload] = NULL", "[c].[Kind] = 5", "[c].[Status] = 1", "[c].[CreatedTime] < @")]
    public async Task TheLlmConnectionsExpiry_SupersedesAndWipesWhatWaitedTooLong_Translates(
        bool sqlServer,
        string update,
        string wipe,
        string kind,
        string pending,
        string expired)
    {
        var capture = Capture(sqlServer).ThenRowsAffected(2).AnsweringCountsWithOne();
        await using var context = await capture.Factory().CreateDbContextAsync(CancellationToken.None);

        var count = await TestChangeApprovals.Llm().ExpireAsync(context, CancellationToken.None);
        await Service(capture).GetPendingAsync(new GetChangeApprovalsQuery(), ChangeApprovalKind.MigrationJob, ChangeApprovalScope.Approver with { IsLiveAdmin = true }, CancellationToken.None);

        count.Should().Be(2);
        capture.Commands[0].Should().StartWith(update).And.Contain(wipe).And.Contain(kind).And.Contain(pending).And.Contain(expired);
        capture.Commands.Skip(1).Should().OnlyContain(x => x.StartsWith("SELECT", StringComparison.Ordinal), "a list of migration jobs expires nothing");
    }

    // A decision on one LLM connection request first supersedes it alone when it waited past its expiry (409), and a stale
    // one is superseded alone after the rollback; both wipe what it staged.
    [TestCase(false, "c.id = @requestId", "c.kind = 5", "c.created_time < @", "encrypted_payload = NULL")]
    [TestCase(true, "[c].[Id] = @requestId", "[c].[Kind] = 5", "[c].[CreatedTime] < @", "[c].[EncryptedPayload] = NULL")]
    public async Task TheLlmConnectionRequestsOwnExpiryAndSupersede_Translate(bool sqlServer, string byId, string kind, string expired, string wipe)
    {
        var capture = Capture(sqlServer).ThenRowsAffected(1).ThenRowsAffected(1);
        await using var context = await capture.Factory().CreateDbContextAsync(CancellationToken.None);
        var llm = TestChangeApprovals.Llm();

        var refuse = () => llm.RefuseIfExpiredAsync(context, 90, CancellationToken.None);
        await refuse.Should().ThrowAsync<BeaconConflictException>().WithMessage(LlmConnectionApproval.ExpiredMessage);
        await llm.SupersedeStaleAsync(context, 90, CancellationToken.None);

        capture.Commands.Should().HaveCount(2);
        capture.Commands[0].Should().Contain(byId).And.Contain(kind).And.Contain(expired).And.Contain(wipe).And.MatchRegex(@"(?i)status\]? = 1");
        capture.Commands[1].Should().Contain(byId).And.Contain(kind).And.Contain(wipe).And.NotContain(expired);
    }

    // The re-encryption run's read of the pending LLM connection requests: only while a previous key is configured, and
    // never more than the hash, the shown view and the staged connection of each.
    [TestCase(false, "c.kind = 5", "c.status = 1", "c.encrypted_payload")]
    [TestCase(true, "[c].[Kind] = 5", "[c].[Status] = 1", "[c].[EncryptedPayload]")]
    public async Task TheLlmConnectionsKeyDependencyRead_Translates(bool sqlServer, string kind, string pending, string payload)
    {
        var capture = Capture(sqlServer).ThenNoRows();
        await using var context = await capture.Factory().CreateDbContextAsync(CancellationToken.None);

        var withoutPreviousKey = await TestChangeApprovals.Llm().KeyDependenciesAsync(context, CancellationToken.None);
        var withPreviousKey = await TestChangeApprovals.Llm(hasher: new ConnectionDefinitionHasher(TestTransport.DefinitionKey, ["an earlier key"]))
            .KeyDependenciesAsync(context, CancellationToken.None);

        withoutPreviousKey.Should().BeEmpty();
        withPreviousKey.Should().BeEmpty();
        capture.Commands.Should().ContainSingle().Which.Should().Contain(kind).And.Contain(pending).And.Contain(payload).And.NotContain("review_comment").And.NotContain("ReviewComment");
    }

    // The detail selects the columns it shows: a staged LLM connection's encrypted values are never read with it.
    [TestCase(false, "encrypted_payload", "c.proposed_json")]
    [TestCase(true, "EncryptedPayload", "[c].[ProposedJson]")]
    public async Task TheDetail_NeverReadsTheEncryptedPayload_Translates(bool sqlServer, string payload, string proposed)
    {
        var capture = Capture(sqlServer).ThenNoRows();

        var detail = await Service(capture).GetDetailAsync(90, ChangeApprovalScope.Approver with { IsLiveAdmin = true }, CancellationToken.None);

        detail.Should().BeNull();
        capture.Commands.Should().ContainSingle().Which.Should().Contain(proposed).And.NotContain(payload);
    }

    // An Approver who is no live Admin opens no LLM connection request or Admin role grant by id: the detail's own SQL leaves
    // them out.
    [TestCase(false, "c.kind NOT IN (5, 6)")]
    [TestCase(true, "[c].[Kind] NOT IN (5, 6)")]
    public async Task TheDetail_ForAnApproverWhoIsNoLiveAdmin_LeavesOutTheLlmConnectionsAndAdminGrants_Translates(bool sqlServer, string filter)
    {
        var capture = Capture(sqlServer).ThenNoRows();

        var detail = await Service(capture).GetDetailAsync(90, ChangeApprovalScope.Approver, CancellationToken.None);

        detail.Should().BeNull();
        capture.Commands.Should().ContainSingle().Which.Should().Contain(filter);
    }

    // A decision that matches no row (decided or superseded since it was read) answers 409: nothing is committed and the
    // approved hash is not written.
    [TestCase(false)]
    [TestCase(true)]
    public async Task Approve_WhenTheDecisionMatchesNoRow_Is409AndCommitsNothing(bool sqlServer)
    {
        var capture = Decision(sqlServer, ChangeApprovalKind.MigrationJob, Job.Hash)
            .ThenRowsAffected(1)
            .ThenRow("copy", 3, "SELECT 1", 5, "orders", (int)MigrationMode.Insert, DBNull.Value)
            .ThenRowsAffected(0);

        var act = () => Service(capture).ApproveAsync(90, null, Payload, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(QueryApprovalService.AlreadyDecidedMessage);
        capture.Commands.Should().HaveCount(5, "the decision is the last statement");
        capture.Commits.Should().Be(0);
    }

    // A resource whose row the lock does not find (archived or deleted) is not decided at all.
    [TestCase(false)]
    [TestCase(true)]
    public async Task Approve_WhenTheResourceRowIsGone_Is409BeforeAnyDecision(bool sqlServer)
    {
        var capture = Decision(sqlServer, ChangeApprovalKind.MigrationJob, Job.Hash).ThenRowsAffected(0);

        var act = () => Service(capture).ApproveAsync(90, null, Payload, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(ChangeApprovalService.ResourceGoneMessage);
        capture.Commands.Should().HaveCount(3);
        capture.Commits.Should().Be(0);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Reject_TakesTheRowFirst_AndDecidesFromPending_Translates(bool sqlServer)
    {
        var capture = Decision(sqlServer, ChangeApprovalKind.RecipientDestination, "d")
            .ThenRowsAffected(1)
            .ThenRowsAffected(1);

        await Service(capture).RejectAsync(90, "no", CancellationToken.None);

        capture.Commands.Should().HaveCount(4);
        capture.Commits.Should().Be(1);
        ShouldDecideFromPending(capture.Commands[3], sqlServer);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Reject_WhenTheDecisionMatchesNoRow_Is409AndCommitsNothing(bool sqlServer)
    {
        var capture = Decision(sqlServer, ChangeApprovalKind.RecipientDestination, "d")
            .ThenRowsAffected(1)
            .ThenRowsAffected(0);

        var act = () => Service(capture).RejectAsync(90, null, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(QueryApprovalService.AlreadyDecidedMessage);
        capture.Commits.Should().Be(0);
    }

    // A submission reads the resource's pending requests, supersedes its requester's own one that proposes something else
    // (a conditional update from Pending), and adds its request.
    [TestCase(false, "c.kind = @proposal_Kind", "c.status = 1", "UPDATE beacon.change_approval_requests", "SET status = @p", "c.id = ANY (@supersededIds)")]
    [TestCase(true, "[c].[Kind] = @proposal_Kind", "[c].[Status] = 1", "FROM [beacon].[ChangeApprovalRequests]", "SET [c].[Status] = @p", "[c].[Id] = @supersededIds")]
    public async Task Submit_SupersedesTheRequestersOwnPendingRequest_Translates(
        bool sqlServer,
        string kind,
        string pending,
        string updateTable,
        string superseded,
        string byId)
    {
        var capture = Capture(sqlServer)
            .ThenRow(77, "ext-ada", "old")
            .ThenRowsAffected(1)
            .ThenNoRows();
        await using var context = await capture.Factory().CreateDbContextAsync(CancellationToken.None);

        var submission = await TestChangeApprovals.Submitter(TestChangeApprovals.On())
            .SubmitAsync(context, new ChangeRequester("ext-ada", null), Proposal(), CancellationToken.None);

        submission.Superseded.Should().ContainSingle().Which.Id.Should().Be(77);
        submission.Request.Should().NotBeNull();
        capture.Commands.Should().HaveCount(3);
        capture.Commands[0].Should().Contain(kind).And.Contain(pending);
        capture.Commands[1].Should().Contain(updateTable).And.Contain(superseded).And.Contain(byId).And.EndWith(pending);
        capture.CommandParameters[1].Values.Should().Contain((int)ApprovalStatus.Superseded);
        capture.CommandParameters[1].Values.SelectMany(x => x as IEnumerable<int> ?? (x is int id ? [id] : [])).Should().Contain(77);
    }

    // Another person's pending request refuses a change of the definition before anything is written.
    [TestCase(false)]
    [TestCase(true)]
    public async Task Submit_WhileAnotherPersonsRequestWaits_Is409AfterOneRead(bool sqlServer)
    {
        var capture = Capture(sqlServer).ThenRow(77, "ext-ben", "old");
        await using var context = await capture.Factory().CreateDbContextAsync(CancellationToken.None);

        var act = () => TestChangeApprovals.Submitter(TestChangeApprovals.On())
            .SubmitAsync(context, new ChangeRequester("ext-ada", null), Proposal(), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>();
        capture.Commands.Should().ContainSingle();
    }

    [TestCase(false, "UPDATE beacon.recipients AS r\nSET approved_destination_hash = r.approved_destination_hash\nWHERE r.archived_time IS NULL AND r.id = @recipientId", "UPDATE beacon.data_contracts AS d\nSET is_enabled = d.is_enabled\nWHERE d.archived_time IS NULL AND d.id = @contractId")]
    [TestCase(true, "UPDATE [r]\nSET [r].[ApprovedDestinationHash] = [r].[ApprovedDestinationHash]\nFROM [beacon].[Recipients] AS [r]\nWHERE [r].[ArchivedTime] IS NULL AND [r].[Id] = @recipientId", "UPDATE [d]\nSET [d].[IsEnabled] = [d].[IsEnabled]\nFROM [beacon].[DataContracts] AS [d]\nWHERE [d].[ArchivedTime] IS NULL AND [d].[Id] = @contractId")]
    public async Task TheRowLocksASubmissionTakes_Translate(bool sqlServer, string recipient, string contract)
    {
        var capture = Capture(sqlServer).ThenRowsAffected(1).ThenRowsAffected(1);
        await using var context = await capture.Factory().CreateDbContextAsync(CancellationToken.None);

        await RecipientChangeApproval.LockAsync(context, 5, CancellationToken.None);
        await CustomSqlChangeApproval.LockAsync(context, 6, CancellationToken.None);

        capture.Commands.Should().Equal(recipient, contract);
    }

    [TestCase(false, "r.id = ANY (@recipientIds)", "r.approved_destination_hash IS NOT NULL")]
    [TestCase(true, "[r].[Id] IN (", "[r].[ApprovedDestinationHash] IS NOT NULL")]
    public async Task TheDeliveryGate_ReadsOnlyGovernedRecipients_Translates(bool sqlServer, string byId, string governed)
    {
        var capture = Capture(sqlServer).ThenNoRows();

        var held = await TestChangeApprovals.Deliveries(capture.Factory(), Protector, TestChangeApprovals.On()).HeldAsync([5, 6], CancellationToken.None);

        held.Should().BeEmpty();
        capture.Commands.Should().ContainSingle().Which.Should().Contain(byId).And.Contain(governed);
    }

    [TestCase(false, "m.approved_definition_hash IS NOT NULL", "c.kind = 1", "c.status = 1")]
    [TestCase(true, "[m].[ApprovedDefinitionHash] IS NOT NULL", "[c].[Kind] = 1", "[c].[Status] = 1")]
    public async Task TheJobPagesState_ReadsGovernedJobsThenTheirPendingRequests_Translates(bool sqlServer, string governed, string kind, string pending)
    {
        var capture = Capture(sqlServer)
            .ThenRow(5, ChangeApprovalHashes.NothingApproved, 3, "SELECT 1", 5, "orders", (int)MigrationMode.Insert, DBNull.Value)
            .ThenRow(5, 90);
        await using var context = await capture.Factory().CreateDbContextAsync(CancellationToken.None);

        var awaiting = await TestChangeApprovals.MigrationJobs(TestChangeApprovals.On()).AwaitingAsync(context, [5], CancellationToken.None);

        awaiting.Should().ContainSingle().Which.Should().BeEquivalentTo(new KeyValuePair<int, AwaitingApproval>(5, new AwaitingApproval(90, null)));
        capture.Commands[0].Should().Contain(governed);
        capture.Commands[1].Should().Contain(kind).And.Contain(pending);
    }

    // The recipients the subscription detail marks as waiting: the governed recipients, then the pending requests of those
    // held (no decided request is read for one that has a pending request).
    [TestCase(false, "c.kind = 3", "c.status = 1", "c.resource_id = ANY (@held)")]
    [TestCase(true, "[c].[Kind] = 3", "[c].[Status] = 1", "[c].[ResourceId] = @held")]
    public async Task TheRecipientsWaitingState_ReadsGovernedRecipientsThenThePendingRequestsOfTheHeldOnes_Translates(bool sqlServer, string kind, string pending, string held)
    {
        var capture = Capture(sqlServer)
            .ThenRow(5, (int)NotificationType.Webhook, Protector.Protect("https://hooks.example.com/b"), DBNull.Value, RecipientDestinationDefinition.Hash(TestChangeApprovals.Hasher, NotificationType.Webhook, "https://hooks.example.com/a", null))
            .ThenRow(5, 90);

        var awaiting = await TestChangeApprovals.Deliveries(capture.Factory(), Protector, TestChangeApprovals.On()).AwaitingAsync([5, 6], CancellationToken.None);

        awaiting.Should().ContainSingle().Which.Should().BeEquivalentTo(new KeyValuePair<int, AwaitingApproval>(5, new AwaitingApproval(90, null)));
        capture.Commands.Should().HaveCount(2);
        capture.Commands[1].Should().Contain(kind).And.Contain(pending).And.Contain(held);
    }

    [TestCase(false, "c.status = 1 AND c.kind = @__kind")]
    [TestCase(true, "[c].[Status] = 1 AND [c].[Kind] = @__kind")]
    public async Task TheList_FiltersByStatusAndKind_Translates(bool sqlServer, string filter)
    {
        var capture = Capture(sqlServer).AnsweringCountsWithOne();

        await Service(capture).GetPendingAsync(new GetChangeApprovalsQuery { Kind = ChangeApprovalKind.MigrationJob }, ChangeApprovalKind.MigrationJob, ChangeApprovalScope.Approver, CancellationToken.None);

        capture.Commands.Should().HaveCount(2, "the count, then the page");
        capture.Commands.Should().OnlyContain(x => x.Replace("@kind", "@__kind").Contains(filter));
        capture.Commands[1].Should().Contain(sqlServer ? "ORDER BY [c].[CreatedTime] DESC" : "ORDER BY c.created_time DESC");
    }

    // The contract detail's extra reads under the workflow, mirrored: the governed Custom SQL rules and the pending request.
    [Test]
    public void TheContractDetailsApprovalReads_Translate_OnBothProviders()
    {
        foreach (BeaconContext context in new BeaconContext[] { NpgsqlTestContext.Create(), SqlServerTestContext.Create() })
        {
            using (context)
            {
                var rules = context.DataContractRules
                    .Where(x => x.DataContractId == 5)
                    .Where(x => x.RuleType == DataContractRuleType.CustomSql)
                    .Where(x => x.ApprovedSqlHash != null)
                    .Select(x =>
                        new
                        {
                            x.Id,
                            x.Configuration,
                            x.ApprovedSqlHash
                        })
                    .ToQueryString();
                var request = context.ChangeApprovalRequests
                    .Where(x => x.Kind == ChangeApprovalKind.CustomSqlRules)
                    .Where(x => x.ResourceId == 5)
                    .Where(x => x.Status == ApprovalStatus.Pending)
                    .OrderByDescending(x => x.Id)
                    .Select(x => (int?)x.Id)
                    .ToQueryString();

                rules.Should().MatchRegex(@"(?i)rule_?type\]? = 8").And.MatchRegex(@"(?i)approved_?sql_?hash\]? IS NOT NULL");
                request.Should().MatchRegex(@"(?i)kind\]? = 2").And.MatchRegex(@"(?i)status\]? = 1").And.Contain("DESC");
            }
        }
    }

    // The last decision a waiting resource shows: its decided (or superseded) requests, by kind and resource.
    [TestCase(false, "c.kind = @kind", "c.status <> 1")]
    [TestCase(true, "[c].[Kind] = @kind", "[c].[Status] <> 1")]
    public async Task TheLastDecisionRead_ByKindResourceAndDecidedStatus_Translates(bool sqlServer, string kind, string decided)
    {
        var capture = Capture(sqlServer).ThenRow(5, 77, (int)ApprovalStatus.Rejected, "No.", DateTime.UtcNow);
        await using var context = await capture.Factory().CreateDbContextAsync(CancellationToken.None);

        var outcomes = await ChangeApprovalOutcomes.LastDecidedAsync(context, ChangeApprovalKind.RecipientDestination, [5], CancellationToken.None);

        outcomes[5].Should().BeEquivalentTo(new { RequestId = 77, Status = ApprovalStatus.Rejected, Comment = "No." }, x => x.ExcludingMissingMembers());
        capture.Commands.Should().ContainSingle().Which.Should().Contain(kind).And.Contain(decided);
    }

    // A save made while the workflow does not review a resource: its pending requests that no longer match are read, then
    // superseded by a conditional update from Pending.
    [TestCase(false, "c.definition_hash <> @", "UPDATE beacon.change_approval_requests")]
    [TestCase(true, "[c].[DefinitionHash] <> @", "UPDATE [c]")]
    public async Task SupersedingOrphanedRequests_ReadsThenUpdatesFromPending_Translates(bool sqlServer, string other, string update)
    {
        var capture = Capture(sqlServer)
            .ThenRow(90, "ext-ben")
            .ThenRowsAffected(1);
        await using var context = await capture.Factory().CreateDbContextAsync(CancellationToken.None);

        var submission = await TestChangeApprovals.Submitter().SupersedeOrphanedAsync(context, ChangeApprovalKind.CustomSqlRules, 5, "kept", CancellationToken.None);

        submission.Superseded.Should().ContainSingle().Which.Should().Be(new SupersededChange(90, "ext-ben"));
        capture.Commands[0].Should().Contain(other).And.MatchRegex(@"(?i)status\]? = 1");
        capture.Commands[1].Should().StartWith(update).And.MatchRegex(@"(?i)status\]? = 1$");
    }

    // The startup count of released Custom SQL rules reads each governed rule's contract data source through the join.
    [Test]
    public void TheReleasedRulesCount_Translates_OnBothProviders()
    {
        foreach (BeaconContext context in new BeaconContext[] { NpgsqlTestContext.Create(), SqlServerTestContext.Create() })
        {
            using (context)
            {
                var sql = context.DataContractRules
                    .Where(x => x.RuleType == DataContractRuleType.CustomSql)
                    .Where(x => x.IsEnabled)
                    .Where(x => x.ApprovedSqlHash != null)
                    .Select(x =>
                        new
                        {
                            x.ApprovedSqlHash,
                            x.Configuration,
                            x.DataContract.DataSourceId
                        })
                    .ToQueryString();

                sql.Should().MatchRegex(@"(?is)JOIN .*data_?contracts").And.MatchRegex(@"(?i)approved_?sql_?hash\]? IS NOT NULL");
            }
        }
    }

    // The trigger's baseline, mirrored from the subscription run: the last run that was not skipped.
    [Test]
    public void TheTriggerBaselineRead_LeavesOutSkippedRuns_Translates_OnBothProviders()
    {
        foreach (BeaconContext context in new BeaconContext[] { NpgsqlTestContext.Create(), SqlServerTestContext.Create() })
        {
            using (context)
            {
                var sql = context.QueryExecutionHistory
                    .Where(x => x.SubscriptionId == 5)
                    .Where(x => x.NotificationStatus != NotificationStatus.Skipped)
                    .OrderByDescending(x => x.CreatedTime)
                    .Select(x =>
                        new
                        {
                            x.ResultCount
                        })
                    .ToQueryString();

                sql.Should().MatchRegex(@"(?i)notification_?status\]? <> 8").And.Contain("DESC");
            }
        }
    }

    private static ChangeProposal Proposal() =>
        new(ChangeApprovalKind.RecipientDestination, 5, "hook", "old", "new", NeedsApproval: true, new { destination = "masked" }, null, "Destination changed");

    // A decision read up to its transaction: pending request 90 of resource 5 of the kind, requested by ext-ben, with the
    // definition hash given, and the reviewer's label.
    // A grant of the Admin role: the user's row taken by a no-op update of its security stamp, then, under that lock, whether
    // the user (not archived) holds the Admin role (or is a super admin), its security stamp and the Admin role's id; a user who holds it is not
    // decided (409) and nothing is committed.
    [TestCase(false)]
    [TestCase(true)]
    public async Task Approve_AnAdminRoleGrant_TakesTheUsersRowFirst_ThenReadsWhetherItHoldsTheRole_Translates(bool sqlServer)
    {
        var capture = Decision(sqlServer, ChangeApprovalKind.AdminRoleGrant, AdminRoleGrantApproval.Hash(5, 1, "stamp"))
            .ThenRowsAffected(1)
            .ThenRow(5, "Cid", "stamp", true)
            .ThenRow(1);

        var act = () => Service(capture).ApproveAsync(90, null, Payload, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(AdminRoleGrantApproval.AlreadyAdminMessage);
        capture.Commands.Should().HaveCount(5);
        capture.Commits.Should().Be(0);
        if (sqlServer)
        {
            capture.Commands[2].Should().Be("UPDATE [u]\nSET [u].[SecurityStamp] = [u].[SecurityStamp]\nFROM [beacon].[Users] AS [u]\nWHERE [u].[ArchivedTime] IS NULL AND [u].[Id] = @userId");
            capture.Commands[3].Should().Contain("FROM [beacon].[Users] AS [u]").And.Contain("[u].[IsSuperAdmin]").And.Contain("[beacon].[UserRoles]").And.Contain("N'Admin'").And.Contain("[u].[ArchivedTime] IS NULL");
            capture.Commands[4].Should().Contain("FROM [beacon].[Roles]").And.Contain("N'Admin'");
            return;
        }

        capture.Commands[2].Should().Be("UPDATE beacon.users AS u\nSET security_stamp = u.security_stamp\nWHERE u.archived_time IS NULL AND u.id = @userId");
        capture.Commands[3].Should().Contain("FROM beacon.users AS u").And.Contain("u.is_super_admin").And.Contain("beacon.user_roles").And.Contain("'Admin'").And.Contain("u.archived_time IS NULL");
        capture.Commands[4].Should().Contain("FROM beacon.roles").And.Contain("'Admin'");
    }

    // A grant's submission reads whether a pending request already proposes it, by kind, user, status and definition.
    [TestCase(false, "c.kind = 6", "c.resource_id = @userId", "c.status = 1", "c.definition_hash = @hash")]
    [TestCase(true, "[c].[Kind] = 6", "[c].[ResourceId] = @userId", "[c].[Status] = 1", "[c].[DefinitionHash] = @hash")]
    public async Task SubmittingAnAdminRoleGrant_ReadsThePendingGrantOfTheUser_Translates(bool sqlServer, string kind, string user, string pending, string hash)
    {
        var capture = Capture(sqlServer).ThenScalar(true);
        await using var context = await capture.Factory().CreateDbContextAsync(CancellationToken.None);
        var grants = new AdminRoleGrantApproval(TestChangeApprovals.Caller("ext-ada"), TestChangeApprovals.On(), NullLogger<AdminRoleGrantApproval>.Instance);

        var submission = await grants.SubmitAsync(context, new ChangeRequester("ext-ada", null), new BeaconUser { Id = 5, SecurityStamp = "stamp" }, 1, CancellationToken.None);

        submission.Request.Should().BeNull("a pending request already proposes the grant");
        capture.Commands.Should().ContainSingle().Which.Should().Contain(kind).And.Contain(user).And.Contain(pending).And.Contain(hash);
    }

    // A new security stamp supersedes the user's pending grants: read by kind, user and status, then marked by a conditional
    // update that touches pending requests only.
    [TestCase(false, "c.kind = 6", "c.resource_id = @userId", "c.status = 1", "SET status = @p")]
    [TestCase(true, "[c].[Kind] = 6", "[c].[ResourceId] = @userId", "[c].[Status] = 1", "SET [c].[Status] = @p")]
    public async Task SupersedingTheAdminRoleGrantsOfAUser_Translates(bool sqlServer, string kind, string user, string pending, string superseded)
    {
        var capture = Capture(sqlServer)
            .ThenRow(7, "ext-ben")
            .ThenRowsAffected(1);
        await using var context = await capture.Factory().CreateDbContextAsync(CancellationToken.None);
        var grants = new AdminRoleGrantApproval(TestChangeApprovals.Caller("ext-ada"), TestChangeApprovals.On(), NullLogger<AdminRoleGrantApproval>.Instance);

        var submission = await grants.SupersedePendingAsync(context, 5, CancellationToken.None);

        submission.Superseded.Should().ContainSingle().Which.Should().Be(new SupersededChange(7, "ext-ben"));
        capture.Commands.Should().HaveCount(2);
        capture.Commands[0].Should().Contain(kind).And.Contain(user).And.Contain(pending);
        capture.Commands[1].Should().StartWith("UPDATE").And.Contain(superseded).And.EndWith(pending);
    }

    private static SqlCapture Decision(bool sqlServer, ChangeApprovalKind kind, string definitionHash)
    {
        return Capture(sqlServer)
            .ThenRow((int)kind, 5, (int)ApprovalStatus.Pending, "ext-ben", definitionHash, Payload, "{}", DBNull.Value)
            .ThenNoRows();
    }

    private static void ShouldDecideFromPending(string sql, bool sqlServer)
    {
        if (sqlServer)
        {
            sql.Should().StartWith("UPDATE [c]").And.Contain("FROM [beacon].[ChangeApprovalRequests]").And.EndWith("AND [c].[Status] = 1");
            return;
        }

        sql.Should().StartWith("UPDATE beacon.change_approval_requests").And.EndWith("AND c.status = 1");
    }

    private static ChangeApprovalService Service(SqlCapture capture) => Service(capture, TestChangeApprovals.NoProbe);

    private static ChangeApprovalService Service(SqlCapture capture, IHeldDataSourceProbe probe) =>
        new(
            capture.Factory(),
            TestChangeApprovals.Caller("ext-ada"),
            Mock.Of<IBeaconUserContext>(),
            Protector,
            TestChangeApprovals.Hasher,
            TestChangeApprovals.On(),
            new RecordingSecurityAudit(),
            TestPermissions.Admin,
            TestChangeApprovals.ReviewerUnchanged,
            TestChangeApprovals.NoDefinitions,
            probe,
            TestChangeApprovals.Llm(),
            NullLogger<ChangeApprovalService>.Instance);

    private static SqlCapture Capture(bool sqlServer) => sqlServer ? SqlCapture.OnSqlServer() : new SqlCapture();
}
