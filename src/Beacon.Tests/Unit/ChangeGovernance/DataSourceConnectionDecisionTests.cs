using System.Text.Json;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.HostData;
using Beacon.Core.Models;
using Beacon.Core.Models.Approvals;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services;
using Beacon.Core.Services.Metadata;
using Beacon.Core.Services.Providers;
using Beacon.Core.Worker;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ChangeGovernance;

/// <summary>
/// Deciding a data source's connection: an approval probes the server once before anything is decided (a server that is
/// not the declared engine refuses it with 409, and the request stays pending), records the proposed definition's hash
/// under the current key, and reads the source's catalog after the commit (a database source's re-scan is enqueued, an
/// API source created held imports its endpoints). A source whose definition changed since the request is stale; one whose
/// secrets alone changed, or whose project links changed, is not. A connection shown only by its fingerprint needs a
/// comment. A source deleted while the approval runs is gone. A change of the LLM connection is decided by a live Admin
/// only, its requester included.
/// </summary>
[TestFixture]
public class DataSourceConnectionDecisionTests
{
    private const string Ada = "ext-ada";
    private const string Ben = "ext-ben";
    private const string Connection = "Host=db.example.com;Database=sales;Username=reader;Password=pw-one;SSL Mode=VerifyFull";
    private const string Rotated = "Host=db.example.com;Database=sales;Username=reader;Password=pw-two;SSL Mode=VerifyFull";
    private const string OtherDatabase = "Host=db.example.com;Database=ledger;Username=reader;Password=pw-one;SSL Mode=VerifyFull";
    private const string ApiConnection = """{"baseUrl":"https://api.example.com/v1","openApiSpecUrl":"https://api.example.com/openapi.json"}""";

    private static readonly EncryptionService Encryption = new(TestEncryptionKey.Value);

    [Test]
    public async Task Approve_ProbesTheServerOnce_RecordsTheProposedDefinition_AndRefreshesTheCatalogAfterTheCommit()
    {
        var store = new ChangeApprovalStore();
        var source = Stored(store, Connection, DataSourceApprovalDefinition.Held);
        var request = Pending(store, source, Connection);
        var verifier = TestEngineVerifier.Passing;
        var scheduler = new Mock<IBeaconScheduler>();
        scheduler.Setup(x => x.EnqueueMetadataRefresh(source.Id)).ReturnsAsync("job-1");
        var dataSources = DataSources(store, verifier, scheduler.Object);

        await Approvals(store, dataSources).ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None);

        verifier.Calls.Should().Be(1, "the held source's engine is checked once, at approval");
        source.ApprovedTargetHash.Should().Be(HashOf(Connection));
        request.Status.Should().Be(ApprovalStatus.Approved);
        store.Offline.Commits.Should().Be(1);
        scheduler.Verify(x => x.EnqueueMetadataRefresh(source.Id), Times.Once);
        Resolver().GetConnectionString(source).Should().Be(Connection);
    }

    [Test]
    public async Task Approve_OfAnApiSourceCreatedHeld_ImportsItsEndpointsAfterTheCommit()
    {
        var store = new ChangeApprovalStore();
        var source = Stored(store, ApiConnection, DataSourceApprovalDefinition.Held, DataSourceType.Api, index: 2);
        var request = Pending(store, source, ApiConnection, DataSourceType.Api);
        var api = new Mock<IDataSourceProvider>();
        api.Setup(x => x.GetMetadataAsync(It.Is<DataSource>(y => y.Id == source.Id), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DataSourceMetadata { Endpoints = [new ApiEndpointMetadata { Method = "GET", Path = "/orders", Tag = "orders" }] });
        var providers = new Mock<IDataSourceProviderFactory>();
        providers.Setup(x => x.GetProvider(DataSourceType.Api)).Returns(api.Object);

        await Approvals(store, DataSources(store, providers: providers.Object)).ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None);

        source.ApprovedTargetHash.Should().Be(HashOf(ApiConnection, DataSourceType.Api));
        store.Saved.OfType<Beacon.Core.Data.Entities.Metadata.DatabaseMetadata>().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { DataSourceId = source.Id, SchemaName = "orders", TableName = "GET /orders" }, x => x.ExcludingMissingMembers());
    }

    [Test]
    public async Task Approve_WhenTheServerIsNotTheDeclaredEngine_Is409_AndTheRequestStaysPending()
    {
        var store = new ChangeApprovalStore();
        var source = Stored(store, Connection, DataSourceApprovalDefinition.Held);
        var request = Pending(store, source, Connection);
        var scheduler = new Mock<IBeaconScheduler>();

        var act = () => Approvals(store, DataSources(store, new MismatchedEngine(), scheduler.Object))
            .ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(MismatchedEngine.Message);
        request.Status.Should().Be(ApprovalStatus.Pending);
        source.ApprovedTargetHash.Should().Be(DataSourceApprovalDefinition.Held);
        store.Offline.TransactionIsolationLevels.Should().BeEmpty("nothing is decided before the server answers");
        scheduler.Verify(x => x.EnqueueMetadataRefresh(It.IsAny<int>()), Times.Never);
    }

    [Test]
    public async Task Approve_WhenTheDefinitionChangedSinceTheRequest_IsStale_AndChangesNothing()
    {
        var store = new ChangeApprovalStore();
        var source = Stored(store, OtherDatabase, HashOf(Rotated));
        var request = Pending(store, source, Connection);
        var probe = TestChangeApprovals.Probe();

        var detail = await Approvals(store, probe).GetDetailAsync(request.Id, ChangeApprovalScope.Approver, CancellationToken.None);
        var act = () => Approvals(store, probe).ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None);

        detail!.IsStale.Should().BeTrue();
        detail.ApproveBlockedReason.Should().Be(ChangeApprovalService.ChangedSinceSubmittedMessage);
        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(ChangeApprovalService.ChangedSinceSubmittedMessage);
        request.Status.Should().Be(ApprovalStatus.Pending);
        source.ApprovedTargetHash.Should().Be(HashOf(Rotated));
        store.Offline.Commits.Should().Be(0);
        probe.Approved.Should().BeEmpty();
    }

    // A rejection decides the request and changes nothing else: the source stays held, its server is never dialled, and
    // what is shown for it says that it still awaits its first approval.
    [Test]
    public async Task Reject_OfAHeldSource_RecordsTheDecision_AndTheSourceStaysHeld()
    {
        var store = new ChangeApprovalStore();
        var source = Stored(store, Connection, DataSourceApprovalDefinition.Held);
        var request = Pending(store, source, Connection);
        var probe = TestChangeApprovals.Probe();

        var decision = await Approvals(store, probe).RejectAsync(request.Id, "Use the reporting login.", CancellationToken.None);

        decision.Status.Should().Be(ApprovalStatus.Rejected);
        request.Should().BeEquivalentTo(new { Status = ApprovalStatus.Rejected, ReviewComment = "Use the reporting login.", ReviewedByUserId = Ada }, x => x.ExcludingMissingMembers());
        source.ApprovedTargetHash.Should().Be(DataSourceApprovalDefinition.Held);
        probe.Probed.Should().BeEmpty();
        probe.Approved.Should().BeEmpty();
        var refused = () => Resolver().GetConnectionString(source);
        refused.Should().Throw<DataSourceAwaitingApprovalException>();
    }

    // The secrets are not part of the definition: a password rotated since the request leaves it approvable.
    [Test]
    public async Task Approve_AfterTheSecretsAloneChanged_RecordsTheDefinition()
    {
        var store = new ChangeApprovalStore();
        var source = Stored(store, Rotated, DataSourceApprovalDefinition.Held);
        var request = Pending(store, source, Connection);
        var probe = TestChangeApprovals.Probe();

        await Approvals(store, probe).ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None);

        source.ApprovedTargetHash.Should().Be(HashOf(Connection)).And.Be(HashOf(Rotated));
        probe.Probed.Should().Equal(source.Id);
        probe.Approved.Should().Equal(source.Id);
    }

    [Test]
    public async Task Approve_WhenTheStoredConnectionCannotBeRead_IsRefused()
    {
        var store = new ChangeApprovalStore();
        var source = Stored(store, Connection, DataSourceApprovalDefinition.Held);
        source.EncryptedConnectionData = new EncryptionService(Convert.ToBase64String(Enumerable.Range(0, 32).Select(x => (byte)(255 - (x * 7))).ToArray()))
            .Encrypt(Connection);
        var request = Pending(store, source, Connection);

        var act = () => Approvals(store, TestChangeApprovals.Probe()).ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(ChangeApprovalService.UnreadableConnectionMessage);
        request.Status.Should().Be(ApprovalStatus.Pending);
        source.ApprovedTargetHash.Should().Be(DataSourceApprovalDefinition.Held);
    }

    // The detail reads the source's project links live; a link made since the request does not make it stale.
    [Test]
    public async Task TheDetail_ShowsTheCurrentProjects_AndALinkMadeSince_DoesNotMakeItStale()
    {
        var store = new ChangeApprovalStore();
        var source = Stored(store, Connection, DataSourceApprovalDefinition.Held);
        var request = Pending(store, source, Connection);
        store.ProjectLinks.Add(Link(source, 21, "Sales"));
        var probe = TestChangeApprovals.Probe();

        var before = await Approvals(store, probe).GetDetailAsync(request.Id, ChangeApprovalScope.Approver, CancellationToken.None);
        store.ProjectLinks.Add(Link(source, 22, "Finance"));
        var after = await Approvals(store, probe).GetDetailAsync(request.Id, ChangeApprovalScope.Approver, CancellationToken.None);
        await Approvals(store, probe).ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None);

        before!.CurrentProjects.Select(x => x.Name).Should().Equal("Sales");
        after!.CurrentProjects.Should().BeEquivalentTo([new ChangeApprovalProject { Id = 22, Name = "Finance" }, new ChangeApprovalProject { Id = 21, Name = "Sales" }]);
        after.IsStale.Should().BeFalse();
        after.MayApprove.Should().BeTrue();
        after.ProposedDataSourceConnection.Should().BeEquivalentTo(new { Host = "db.example.com", Database = "sales", Login = "reader" }, x => x.ExcludingMissingMembers());
        after.PayloadHash.Should().Be(request.PayloadHash);
        request.Status.Should().Be(ApprovalStatus.Approved);
    }

    // A connection its connector cannot show setting by setting is shown by its fingerprint only: approving it needs a
    // comment saying how it was checked.
    [Test]
    public async Task Approve_OfAConnectionShownOnlyByItsFingerprint_NeedsAComment()
    {
        var store = new ChangeApprovalStore();
        const string stored = """{"projectId":"analytics","credentialsJson":"one"}""";
        var source = Stored(store, stored, DataSourceApprovalDefinition.Held, DataSourceType.BigQuery);
        var request = Pending(store, source, stored, DataSourceType.BigQuery);
        var probe = TestChangeApprovals.Probe();

        var detail = await Approvals(store, probe).GetDetailAsync(request.Id, ChangeApprovalScope.Approver, CancellationToken.None);
        var withoutComment = () => Approvals(store, probe).ApproveAsync(request.Id, " ", request.PayloadHash, CancellationToken.None);

        detail!.CommentRequiredToApprove.Should().BeTrue();
        detail.ProposedDataSourceConnection!.SecretOnlyDefinition.Should().BeTrue();
        await withoutComment.Should().ThrowAsync<InvalidOperationException>().WithMessage(ChangeApprovalService.ConnectionCommentRequiredMessage);
        probe.Probed.Should().BeEmpty("the comment rule refuses before the server is probed");
        request.Status.Should().Be(ApprovalStatus.Pending);

        await Approvals(store, probe).ApproveAsync(request.Id, "Compared the fingerprint with the requester.", request.PayloadHash, CancellationToken.None);

        request.Status.Should().Be(ApprovalStatus.Approved);
        source.ApprovedTargetHash.Should().Be(HashOf(stored, DataSourceType.BigQuery));
    }

    // Deleting a source supersedes its requests; an approval that read the request before the delete committed finds the
    // source gone when it takes its row, and decides nothing.
    [Test]
    public async Task Approve_WhileTheSourceIsDeleted_IsResourceGone_AndDecidesNothing()
    {
        var store = new ChangeApprovalStore();
        var source = Stored(store, Connection, DataSourceApprovalDefinition.Held);
        var request = Pending(store, source, Connection);
        var deleting = new DeletingProbe(() => store.DataSources.Remove(source));

        var act = () => Approvals(store, deleting).ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(ChangeApprovalService.ResourceGoneMessage);
        request.Status.Should().Be(ApprovalStatus.Pending, "the delete supersedes it");
        store.Offline.Commits.Should().Be(0);
    }

    // A data source's connection is decided by an Approver; four eyes apply as for every other kind.
    [Test]
    public async Task Approve_ByItsRequester_Is403()
    {
        var store = new ChangeApprovalStore();
        var source = Stored(store, Connection, DataSourceApprovalDefinition.Held);
        var request = Pending(store, source, Connection, requestedBy: Ada);
        var probe = TestChangeApprovals.Probe();

        var act = () => Approvals(store, probe).ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>();
        probe.Probed.Should().BeEmpty();
    }

    // Only a live Admin decides a change of the LLM connection: an Approver who is no Admin is refused approving and
    // rejecting, and so is its requester withdrawing it without being an Admin.
    [TestCase(Ben)]
    [TestCase(Ada)]
    public async Task AnLlmConnectionChange_IsDecidedByALiveAdminOnly(string requestedBy)
    {
        var store = new ChangeApprovalStore();
        var request = store.AddRequest(ChangeApprovalKind.LlmConnection, 0, "v1:llm", requestedBy, proposedJson: Json(new LlmConnectionView { Provider = AiProvider.OpenAI }));
        var approver = Approvals(store, TestChangeApprovals.NoProbe, TestPermissions.Holding(BeaconPermissions.Approver));

        var approve = () => approver.ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None);
        var reject = () => approver.RejectAsync(request.Id, null, CancellationToken.None);

        await approve.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(ChangeApprovalService.LiveAdminRequiredMessage);
        await reject.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(ChangeApprovalService.LiveAdminRequiredMessage);
        request.Status.Should().Be(ApprovalStatus.Pending);
        store.Offline.TransactionIsolationLevels.Should().BeEmpty();
    }

    // Who decides is read again inside the decision's transaction, after the source's lock, from the user store as it is
    // now: a reviewer disabled, archived or stripped of the Approver role while the decision waited for that lock does not
    // decide, and nothing is committed.
    [TestCase("approve", "disabled")]
    [TestCase("approve", "archived")]
    [TestCase("approve", "approver role removed")]
    [TestCase("reject", "disabled")]
    [TestCase("reject", "approver role removed")]
    public async Task ADecision_ByAReviewerRevokedWhileItWaitedForTheLock_IsRefused_AndCommitsNothing(string decision, string revocation)
    {
        var store = new ChangeApprovalStore();
        var ada = store.UserHolding(7, Ada, BeaconPermissions.Approver);
        var source = Stored(store, Connection, DataSourceApprovalDefinition.Held);
        var request = Pending(store, source, Connection);
        var probe = TestChangeApprovals.Probe();
        var reviewers = new RevokingReviewerAuthority(TestChangeApprovals.Reviewers(), () => Revoke(ada, revocation));
        var approvals = Approvals(store, probe, reviewers: reviewers, configuration: ManagingUsers());

        Func<Task> act = decision == "approve"
            ? () => approvals.ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None)
            : () => approvals.RejectAsync(request.Id, null, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(
            revocation == "approver role removed" ? ChangeApprovalService.ApproverRequiredMessage : ChangeApprovalService.ReviewerInactiveMessage);
        reviewers.ReadInsideTheTransaction.Should().BeTrue();
        request.Status.Should().Be(ApprovalStatus.Pending);
        request.ReviewedByUserId.Should().BeNull();
        source.ApprovedTargetHash.Should().Be(DataSourceApprovalDefinition.Held);
        store.Offline.Commits.Should().Be(0);
        probe.Approved.Should().BeEmpty();
    }

    // The same re-read lets a reviewer who still holds what the decision needs decide: an Approver, or an Admin who holds
    // no Approver role.
    [TestCase(BeaconPermissions.Approver)]
    [TestCase(RoleService.RoleNames.Admin)]
    public async Task Approve_ByAReviewerWhoStillHoldsTheRole_IsDecided(string role)
    {
        var store = new ChangeApprovalStore();
        store.UserHolding(7, Ada, role);
        var source = Stored(store, Connection, DataSourceApprovalDefinition.Held);
        var request = Pending(store, source, Connection);
        var reviewers = new RevokingReviewerAuthority(TestChangeApprovals.Reviewers(), () => { });

        await Approvals(store, TestChangeApprovals.Probe(), reviewers: reviewers, configuration: ManagingUsers())
            .ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None);

        reviewers.Reads.Should().Be(1);
        request.Status.Should().Be(ApprovalStatus.Approved);
        store.Offline.Commits.Should().Be(1);
    }

    private static BeaconConfiguration ManagingUsers()
    {
        var configuration = TestChangeApprovals.CoveringConnections();
        configuration.UserManagement.Enabled = true;

        return configuration;
    }

    private static void Revoke(BeaconUser user, string revocation)
    {
        switch (revocation)
        {
            case "disabled":
                user.IsEnabled = false;
                break;
            case "archived":
                user.ArchivedTime = DateTime.UtcNow;
                break;
            default:
                user.UserRoles.Clear();
                break;
        }
    }

    private static string HashOf(string connection, DataSourceType type = DataSourceType.Database)
    {
        var engine = type == DataSourceType.Database ? DatabaseEngineType.PostgreSQL : (DatabaseEngineType?)null;

        return TestTransport.Hasher().Hash(
            ConnectionDefinitionPurpose.DataSourceDefinition,
            DataSourceApprovalDefinition.Canonical(TestTransport.Policy(), type, engine, connection));
    }

    private static DataSource Stored(ChangeApprovalStore store, string connection, string? approvedHash, DataSourceType type = DataSourceType.Database, int index = 0)
    {
        var source = store.DataSources[index];
        source.DataSourceType = type;
        source.DatabaseEngineType = type == DataSourceType.Database ? DatabaseEngineType.PostgreSQL : null;
        source.EncryptedConnectionData = Encryption.Encrypt(connection);
        source.ApprovedTargetHash = approvedHash;

        return source;
    }

    // A pending request of the source proposing the connection, with the view a reviewer approves.
    private static ChangeApprovalRequest Pending(
        ChangeApprovalStore store,
        DataSource source,
        string connection,
        DataSourceType type = DataSourceType.Database,
        string requestedBy = Ben)
    {
        var verdict = TestTransport.Policy().Inspect(type, source.DatabaseEngineType, connection);
        var view = DataSourceApprovalDefinition.View(
            TestTransport.Hasher(),
            source.Name,
            type,
            source.DatabaseEngineType,
            verdict.Target,
            verdict.Definition,
            connection,
            previous: null,
            authenticatesServer: verdict.Verified,
            databaseReadOnlyEnforced: false,
            isTargetChange: false);

        return store.AddRequest(ChangeApprovalKind.DataSourceConnection, source.Id, HashOf(connection, type), requestedBy, proposedJson: Json(view));
    }

    private static ProjectDataSource Link(DataSource source, int projectId, string name) =>
        new() { ProjectId = projectId, DataSourceId = source.Id, Project = new Project { Id = projectId, Name = name } };

    private static string Json(object view) =>
        JsonSerializer.Serialize(view, view.GetType(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    private static IDataSourceConnectionResolver Resolver() =>
        TestTransport.Resolver(Encryption, configuration: TestChangeApprovals.CoveringConnections());

    private static ChangeApprovalService Approvals(
        ChangeApprovalStore store,
        IHeldDataSourceProbe probe,
        IBeaconPermissionAccessor? permissions = null,
        IReviewerAuthority? reviewers = null,
        BeaconConfiguration? configuration = null) =>
        new(
            store.Factory(),
            TestChangeApprovals.Caller(Ada, beaconUserId: 7),
            Mock.Of<IBeaconUserContext>(),
            store.Protector,
            TestChangeApprovals.Hasher,
            configuration ?? TestChangeApprovals.CoveringConnections(),
            new RecordingSecurityAudit(),
            permissions ?? TestPermissions.Holding(BeaconPermissions.Approver),
            reviewers ?? TestChangeApprovals.ReviewerUnchanged,
            (IDataSourceDefinitionReader)Resolver(),
            probe,
            TestChangeApprovals.Llm(),
            NullLogger<ChangeApprovalService>.Instance);

    private static DataSourceService DataSources(
        ChangeApprovalStore store,
        IDatabaseEngineVerifier? verifier = null,
        IBeaconScheduler? scheduler = null,
        IDataSourceProviderFactory? providers = null) =>
        new(
            store.Factory(),
            Encryption,
            providers ?? TestProviderFactory.Accepting(),
            new MemoryCache(new MemoryCacheOptions()),
            Mock.Of<ISchemaGraphService>(),
            new RecordingSecurityAudit(),
            TestTransport.Policy(),
            verifier ?? TestEngineVerifier.Passing,
            TestChangeApprovals.Submitter(TestChangeApprovals.CoveringConnections(), TestChangeApprovals.Caller(Ben)),
            TestTransport.Hasher(),
            NullLogger<DataSourceService>.Instance,
            scheduler);

    // The source is deleted between the request's read and the decision's row lock.
    private sealed class DeletingProbe(Action delete) : IHeldDataSourceProbe
    {
        public Task ProbeAsync(int dataSourceId, CancellationToken cancellationToken)
        {
            delete();

            return Task.CompletedTask;
        }

        public Task ConnectionApprovedAsync(int dataSourceId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // The engine verifier's answer for a server that is clearly another engine than the one declared.
    private sealed class MismatchedEngine : IDatabaseEngineVerifier
    {
        public const string Message = "The server is not PostgreSQL.";

        public Task<DatabaseEngineIdentification> IdentifyAsync(
            int? dataSourceId,
            DatabaseEngineType declared,
            System.Data.Common.DbConnection openConnection,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task EnsureDeclaredEngineAsync(int? dataSourceId, DatabaseEngineType declared, string connectionString, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException(Message);
        }
    }
}
