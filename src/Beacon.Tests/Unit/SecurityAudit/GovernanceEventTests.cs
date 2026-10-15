using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.Core;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.McpSettings;
using Beacon.Core.Models;
using Beacon.Core.Models.DataMigration;
using Beacon.Core.Models.Queries;
using Beacon.Core.Models.Settings;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using Beacon.Tests.Unit.ChangeGovernance;
using Beacon.Core.Authorization;
using Beacon.Core.HostData;

namespace Beacon.Tests.Unit.SecurityAudit;

/// <summary>
/// The governance events: one per settings save that changes something (the keys it changed and the history rows holding
/// their values, never a value), and the submission of and decision on each change that waits for a second person (query
/// changes and the change approval workflow), naming the request, the resource and the requester by <c>Users.Id</c> — the
/// reviewer is the event's actor — and never a comment, SQL or definition. Each commits with the change it records.
/// </summary>
[TestFixture]
public class GovernanceEventTests
{
    private const string Ada = "ext-ada";
    private const string Ben = "ext-ben";

    [Test]
    public async Task SavingTheGlobalMcpSettings_RecordsTheChangedKeysAndTheirHistoryRows_NeverAValue()
    {
        var audit = new RecordingSecurityAudit(RecordingSecurityAudit.User(3, Ada));
        var settings = new SettingsStore();
        settings.Global.Add(new McpSettings { Id = 1 });
        var handler = new UpdateMcpSettingsHandler(
            settings.Factory(),
            SettingsProviderMock.Create().Object,
            Options.Create(new McpDeploymentOptions()),
            TestPermissions.Admin,
            TestProjectAccess.Actors(3, Ada),
            audit,
            NullLogger<UpdateMcpSettingsHandler>.Instance);
        var data = new McpSettingsData { EnableLearning = false, MaxRowLimit = 250, AskSystemPrompt = "Answer as ops@corp.example would" };

        await handler.Handle(new UpdateMcpSettingsCommand(data), CancellationToken.None);
        await handler.Handle(new UpdateMcpSettingsCommand(data), CancellationToken.None);

        var changed = audit.Single("ops@corp.example", "Answer as", "250");
        changed.Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.McpSettingsChanged, ActorUserId = (int?)3, ResourceType = SecurityAuditResources.Settings, ResourceId = (string?)null },
            x => x.ExcludingMissingMembers());
        var history = settings.History.ToList();
        var historyIds = settings.History.Select(x => x.Id).ToList();
        var digest = SecurityAuditTestKit.Integrity;
        var before = string.Join(",", history.Select(x => $"\"{digest.Digest(x.OldValue)}\""));
        var after = string.Join(",", history.Select(x => $"\"{digest.Digest(x.NewValue)}\""));
        changed.DetailsJson.Should().Be(
            "{\"changedKeys\":[\"Mcp.AskSystemPrompt\",\"Mcp.EnableLearning\",\"Mcp.MaxRowLimit\"]," +
            $"\"historyIds\":[{string.Join(",", historyIds)}]," +
            $"\"beforeDigests\":[{before}],\"afterDigests\":[{after}]}}",
            "each key's value before and after, as keyed digests in the order of the keys, never the value");
        history.Select(x => x.SettingKey).Should().Equal("Mcp.AskSystemPrompt", "Mcp.EnableLearning", "Mcp.MaxRowLimit");
        audit.Ways.Should().Equal("saved");
    }

    [Test]
    public async Task SavingAProjectsMcpOverrides_RecordsTheChangedKeys_InTheProject()
    {
        var audit = new RecordingSecurityAudit(RecordingSecurityAudit.User(3, Ada));
        var settings = new SettingsStore();
        settings.Projects.Add(new Project { Id = 4, Name = "ops" });
        var handler = new UpdateMcpProjectSettingsHandler(
            settings.Factory(),
            SettingsProviderMock.Create().Object,
            Options.Create(new McpDeploymentOptions()),
            TestPermissions.Admin,
            TestProjectAccess.Actors(3, Ada),
            audit,
            NullLogger<UpdateMcpProjectSettingsHandler>.Instance);

        await handler.Handle(new UpdateMcpProjectSettingsCommand(4, new McpProjectSettingsData { RetainQueryContent = false }), CancellationToken.None);

        var changed = audit.Single();
        changed.Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.McpProjectSettingsChanged, ResourceType = SecurityAuditResources.Project, ResourceId = "4", ProjectId = (int?)4 },
            x => x.ExcludingMissingMembers());
        changed.DetailsJson.Should().StartWith("{\"changedKeys\":[\"Mcp.Project.4.RetainQueryContent\"],\"historyIds\":[");
    }

    [Test]
    public async Task SavingTheAdminSettings_RecordsTheChangedKeys_NeverASecret()
    {
        var audit = new RecordingSecurityAudit(RecordingSecurityAudit.User(3, Ada));
        var settings = new SettingsStore();
        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(x => x.Encrypt(It.IsAny<string>())).Returns<string>(x => "enc:" + x.Length);
        var service = new AppSettingsService(settings.Factory(), new MemoryCache(new MemoryCacheOptions()), encryption.Object, new BeaconConfiguration(), audit);

        await service.SaveSettingsAsync(new AppSettingsData { BaseUrl = "https://beacon.corp.example", LlmApiKey = "sk-live-0123456789" }, Ada, CancellationToken.None);

        var changed = audit.Single("sk-live-0123456789", "beacon.corp.example");
        changed.Action.Should().Be(SecurityAuditActions.AdminSettingsChanged);
        changed.DetailsJson.Should().Contain("\"General.BaseUrl\"").And.Contain("\"LLM.ApiKey\"").And.Contain("\"historyIds\":[");
    }

    [Test]
    public async Task SubmittingAQueryChange_RecordsTheRequestItsBaseAndItsRequester_NeverTheSql()
    {
        var audit = new RecordingSecurityAudit(RecordingSecurityAudit.User(3, Ada));
        audit.UserIds[Ada] = 3;
        var store = new GovernanceStore();
        var active = store.Activate(store.AddVersion(1, QueryVersionStatus.Active));
        var earlier = store.AddVersion(2, QueryVersionStatus.PendingApproval, "SELECT 9");
        var earlierRequest = store.AddRequest(earlier, Ada, active.Id);

        await TestQueryServices.VersionService(store.Factory(), audit: audit).SubmitForApprovalAsync(
            GovernanceStore.QueryId,
            GovernanceStore.Edit("SELECT secret_column FROM vault"),
            new QueryChangeSubmission(Ada, null, "UserEdit", "because ops asked", "SQL query modified"),
            CancellationToken.None);

        var submitted = audit.Single("secret_column", "because ops asked", Ada);
        submitted.Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.QueryChangeSubmitted, ActorUserId = (int?)3, ResourceType = SecurityAuditResources.ApprovalRequest },
            x => x.ExcludingMissingMembers());
        submitted.DetailsJson.Should().Be(
            $"{{\"queryId\":{GovernanceStore.QueryId},\"versionId\":0,\"baseVersionId\":{active.Id},\"changeSource\":\"UserEdit\",\"newQuery\":false," +
            $"\"supersededRequestIds\":[{earlierRequest.Id}],\"requesterUserId\":3}}");
        audit.Ways.Should().Equal("saved");
        store.Offline.Commits.Should().Be(1, "the submission and its event commit together");
    }

    [Test]
    public async Task ApprovingAQueryChange_RecordsTheDecisionAndTheRequester_AndAttributesTheQueryToTheReviewer()
    {
        var audit = new RecordingSecurityAudit(RecordingSecurityAudit.User(3, Ada));
        audit.UserIds[Ben] = 8;
        var (store, _, proposal, request) = PendingQueryChange(requestedBy: Ben);

        await TestQueryServices.ApprovalService(store.Factory(), TestProjectAccess.Actors(3, Ada), audit: audit)
            .ApproveAsync(request.Id, "checked with ben@corp.example", Shown(request), CancellationToken.None);

        var approved = audit.Single("ben@corp.example", Ben);
        approved.Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.QueryChangeApproved, ActorUserId = (int?)3, ResourceType = SecurityAuditResources.ApprovalRequest, ResourceId = request.Id.ToString() },
            x => x.ExcludingMissingMembers());
        approved.DetailsJson.Should().Be(
            $"{{\"queryId\":{GovernanceStore.QueryId},\"versionId\":{proposal.Id},\"outcome\":\"Approved\",\"withdrawn\":false,\"requesterUserId\":8}}");
        audit.Ways.Should().Equal("saved");
        store.Query.Should().BeEquivalentTo(new { ModifiedByUserId = Ada }, x => x.ExcludingMissingMembers());
        store.Query.ModifiedTime.Should().NotBeNull();
        store.Offline.Commits.Should().Be(1);
    }

    [Test]
    public async Task ARequesterWithdrawingTheirChange_IsRecordedAsAWithdrawnRejection()
    {
        var audit = new RecordingSecurityAudit(RecordingSecurityAudit.User(3, Ada));
        audit.UserIds[Ada] = 3;
        var (store, _, _, request) = PendingQueryChange(requestedBy: Ada);

        await TestQueryServices.ApprovalService(store.Factory(), TestProjectAccess.Actors(3, Ada), audit: audit)
            .RejectAsync(request.Id, "never mind", CancellationToken.None);

        var rejected = audit.Single("never mind");
        rejected.Action.Should().Be(SecurityAuditActions.QueryChangeRejected);
        rejected.DetailsJson.Should().EndWith("\"outcome\":\"Rejected\",\"withdrawn\":true,\"requesterUserId\":3}");
    }

    [Test]
    public async Task ARefusedQueryDecision_RecordsNothing()
    {
        var audit = new RecordingSecurityAudit(RecordingSecurityAudit.User(3, Ada));
        var (store, _, _, request) = PendingQueryChange(requestedBy: Ada);

        var act = () => TestQueryServices.ApprovalService(store.Factory(), TestProjectAccess.Actors(3, Ada), audit: audit)
            .ApproveAsync(request.Id, null, Shown(request), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>();
        audit.Events.Should().BeEmpty();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task DecidingAGovernedChange_RecordsTheDecisionKindResourceAndRequester(bool approve)
    {
        var store = new ChangeApprovalStore();
        store.User(8, Ben);
        var submissions = new RecordingSecurityAudit(RecordingSecurityAudit.User(8, Ben));
        var created = await Migrations(store, submissions).CreateMigrationJob(
            new CreateMigrationJobRequest { Name = "copy", DataSourceId = ChangeApprovalStore.Warehouse, QueryText = "SELECT 1", DestinationDataSourceId = ChangeApprovalStore.Target, DestinationTable = "t" },
            CancellationToken.None);
        store.Commit();
        var request = store.Requests.Single();
        submissions.Of(SecurityAuditActions.ChangeSubmitted).Should().ContainSingle().Which.DetailsJson.Should().Be(
            $"{{\"kind\":\"MigrationJob\",\"resourceId\":{created.MigrationJobId},\"supersededRequestIds\":[]}}");
        var decisions = new RecordingSecurityAudit(RecordingSecurityAudit.User(9, "ext-cid"));
        decisions.UserIds[Ben] = 8;
        var service = new ChangeApprovalService(
            store.Factory(),
            TestChangeApprovals.Caller("ext-cid"),
            Mock.Of<IBeaconUserContext>(),
            store.Protector,
            TestChangeApprovals.Hasher,
            TestChangeApprovals.On(),
            decisions,
            NullLogger<ChangeApprovalService>.Instance);

        if (approve)
        {
            await service.ApproveAsync(request.Id, "looks right", request.PayloadHash, CancellationToken.None);
        }
        else
        {
            await service.RejectAsync(request.Id, "not now", CancellationToken.None);
        }

        var decided = decisions.Single("looks right", "not now", Ben);
        decided.Should().BeEquivalentTo(
            new
            {
                Action = approve ? SecurityAuditActions.ChangeApproved : SecurityAuditActions.ChangeRejected,
                ActorUserId = (int?)9,
                ResourceType = SecurityAuditResources.ChangeApprovalRequest,
                ResourceId = request.Id.ToString()
            },
            x => x.ExcludingMissingMembers());
        decided.DetailsJson.Should().Be(
            $"{{\"kind\":\"MigrationJob\",\"resourceId\":{created.MigrationJobId},\"outcome\":\"{(approve ? "Approved" : "Rejected")}\",\"requesterUserId\":8}}");
        decisions.Ways.Should().Equal("saved");
    }

    // The query's active version 1, and a pending proposal 2 made on it.
    private static (GovernanceStore Store, QueryVersion Active, QueryVersion Proposal, QueryApprovalRequest Request) PendingQueryChange(string? requestedBy)
    {
        var store = new GovernanceStore();
        var active = store.Activate(store.AddVersion(1, QueryVersionStatus.Active));
        var proposal = store.AddVersion(2, QueryVersionStatus.PendingApproval, "SELECT 2");
        var request = store.AddRequest(proposal, requestedBy, active.Id);

        return (store, active, proposal, request);
    }

    private static string? Shown(QueryApprovalRequest request) =>
        QueryContentHash.OfVersion(request.QueryVersion.ContentHash, request.QueryVersion.StepsJson, request.QueryVersion.FinalQuery);

    private static MigrationService Migrations(ChangeApprovalStore store, ISecurityAuditRecorder audit) =>
        new(
            store.Factory(),
            Mock.Of<IDataSourceConnectionResolver>(),
            Mock.Of<IQueryService>(MockBehavior.Strict),
            Mock.Of<IQueryExecutionPreviewService>(MockBehavior.Strict),
            Mock.Of<IBeaconUserContext>(),
            new MigrationBulkContextFactory([]),
            NullLogger<MigrationService>.Instance,
            Options.Create(new MigrationOptions { Enabled = true }),
            TestChangeApprovals.Submitter(TestChangeApprovals.On(), TestChangeApprovals.Caller(Ben, 8)),
            TestChangeApprovals.Caller(Ben, 8),
            audit,
            TimeProvider.System);

    // Settings rows and their history behind a recording context (§4.7: no database); saves give history rows ids.
    private sealed class SettingsStore
    {
        private int _nextId = 900;

        public List<McpSettings> Global { get; } = [];

        public List<McpProjectSettings> ProjectRows { get; } = [];

        public List<Project> Projects { get; } = [];

        public List<AppSetting> AppSettings { get; } = [];

        public List<object> Saved { get; } = [];

        public IReadOnlyList<AppSettingHistory> History => Saved.OfType<AppSettingHistory>().ToList();

        public IDbContextFactory<BeaconContext> Factory()
        {
            var sets = new Dictionary<Type, object>
            {
                [typeof(McpSettings)] = RecordingBeaconContext.MemorySet(Global, Saved),
                [typeof(McpProjectSettings)] = RecordingBeaconContext.MemorySet(ProjectRows, Saved),
                [typeof(Project)] = RecordingBeaconContext.MemorySet(Projects, Saved),
                [typeof(AppSetting)] = RecordingBeaconContext.MemorySet(AppSettings, Saved),
                [typeof(AppSettingHistory)] = RecordingBeaconContext.MemorySet(new List<AppSettingHistory>(), Saved)
            };
            var factory = new Mock<IDbContextFactory<BeaconContext>>();
            factory
                .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new RecordingBeaconContext(sets, Saved, onSave: GiveIds));

            return factory.Object;
        }

        private void GiveIds()
        {
            foreach (var entity in Saved.OfType<BaseEntity>().Where(x => x.Id == 0))
            {
                entity.Id = _nextId++;
            }
        }
    }
}
