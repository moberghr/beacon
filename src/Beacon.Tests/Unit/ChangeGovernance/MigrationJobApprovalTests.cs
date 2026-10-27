using System.Text.Json;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.DataMigration;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Models.Approvals;
using Beacon.Core.Models.DataMigration;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.Core.HostData;

namespace Beacon.Tests.Unit.ChangeGovernance;

/// <summary>
/// Migration jobs under the change approval workflow: a job created while the workflow covers them is stored with nothing
/// approved and a pending request (its creator the requester), in one commit; no run of it starts until a second person
/// approves exactly its definition, and a refused run is recorded as failed; a job stored before (no approved hash) runs as
/// before; with the workflow or its switch off nothing changes.
/// </summary>
[TestFixture]
public class MigrationJobApprovalTests
{
    private const string Ada = "ext-ada";

    [Test]
    public async Task Create_UnderTheWorkflow_StoresTheJobAwaitingApproval_WithAPendingRequest_InOneCommit()
    {
        var store = new ChangeApprovalStore();
        store.User(7, Ada, "Ada Lovelace");

        var response = await Service(store, TestChangeApprovals.On()).CreateMigrationJob(Request("SELECT id FROM orders"), CancellationToken.None);
        store.Commit();

        response.Success.Should().BeTrue();
        response.AwaitingApproval.Should().BeTrue("the creator is told the job does not run until approved");
        var job = store.Jobs.Should().ContainSingle().Subject;
        job.Id.Should().Be(response.MigrationJobId);
        job.ApprovedDefinitionHash.Should().Be(ChangeApprovalHashes.NothingApproved);
        var request = store.Requests.Should().ContainSingle().Subject;
        request.Should().BeEquivalentTo(
            new
            {
                Kind = ChangeApprovalKind.MigrationJob,
                ResourceId = job.Id,
                ResourceName = "copy orders",
                Status = ApprovalStatus.Pending,
                DefinitionHash = MigrationJobDefinition.Of(job).Hash,
                RequestedByUserId = Ada,
                RequestedByUserName = "Ada Lovelace",
                PreviousJson = (string?)null,
                ChangeSummary = "Migration job created"
            },
            x => x.ExcludingMissingMembers());
        request.PayloadHash.Should().Be(ChangeApprovalHashes.Sha256(request.ProposedJson));
        JsonSerializer.Deserialize<MigrationJobDefinitionView>(request.ProposedJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!
            .QueryText.Should().Be("SELECT id FROM orders");
        store.Offline.Commits.Should().Be(1, "the job and its request commit together");
        store.SaveCount.Should().Be(2, "the job's save gives it the id its request names");
    }

    [TestCase(false, true)]
    [TestCase(true, false)]
    public async Task Create_WithTheWorkflowOrItsSwitchOff_StoresAJobNobodyGoverns(bool enabled, bool covers)
    {
        var store = new ChangeApprovalStore();
        var configuration = new BeaconConfiguration { ApprovalWorkflow = new ApprovalWorkflowOptions { Enabled = enabled, CoversMigrationJobs = covers } };

        var response = await Service(store, configuration).CreateMigrationJob(Request("SELECT 1"), CancellationToken.None);
        store.Commit();

        response.Success.Should().BeTrue();
        response.AwaitingApproval.Should().BeFalse();
        store.Jobs.Should().ContainSingle().Which.ApprovedDefinitionHash.Should().BeNull();
        store.Requests.Should().BeEmpty();
        store.Offline.TransactionIsolationLevels.Should().BeEmpty("nothing changes without the workflow");
        store.SaveCount.Should().Be(1);
    }

    [Test]
    public async Task Create_ByACallerBeaconCannotName_IsRefused_AndWritesNothing()
    {
        var store = new ChangeApprovalStore();

        var act = () => Service(store, TestChangeApprovals.On(), TestChangeApprovals.Caller(null)).CreateMigrationJob(Request("SELECT 1"), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(ChangeApprovalSubmitter.UnidentifiedRequesterMessage);
        store.Saved.Should().BeEmpty();
    }

    [Test]
    public async Task Run_OfAJobAwaitingApproval_IsRefused_AndRecordedAsAFailedRun()
    {
        var store = new ChangeApprovalStore();
        var job = store.AddJob(approvedDefinitionHash: ChangeApprovalHashes.NothingApproved);

        var response = await Service(store, TestChangeApprovals.On()).ExecuteMigrationJob(new ExecuteMigrationJobRequest(job.Id), CancellationToken.None);

        response.Status.Should().Be(MigrationStatus.Failed);
        response.ErrorMessage.Should().Be(MigrationService.NotApprovedMessage);
        store.Saved.OfType<MigrationExecutionHistory>().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { MigrationJobId = job.Id, Status = MigrationStatus.Failed, ErrorMessage = MigrationService.NotApprovedMessage }, x => x.ExcludingMissingMembers());
    }

    // A job changed after its approval (outside the workflow) no longer holds the approved definition: it does not run.
    [Test]
    public async Task Run_OfAJobWhoseDefinitionChangedAfterApproval_IsRefused()
    {
        var store = new ChangeApprovalStore();
        var job = store.AddJob("SELECT id FROM orders");
        job.ApprovedDefinitionHash = MigrationJobDefinition.Of(job).Hash;
        job.Mode = MigrationMode.Truncate;

        var response = await Service(store, TestChangeApprovals.On()).ExecuteMigrationJob(new ExecuteMigrationJobRequest(job.Id), CancellationToken.None);

        response.ErrorMessage.Should().Be(MigrationService.NotApprovedMessage);
    }

    // Past the approval check, the run meets the next one: the target here is not a migration write target.
    [Test]
    public async Task Run_OfAnApprovedJob_PassesTheApprovalCheck()
    {
        var store = new ChangeApprovalStore();
        var job = store.AddJob();
        job.ApprovedDefinitionHash = MigrationJobDefinition.Of(job).Hash;

        var response = await Service(store, TestChangeApprovals.On()).ExecuteMigrationJob(new ExecuteMigrationJobRequest(job.Id), CancellationToken.None);

        response.ErrorMessage.Should().NotBe(MigrationService.NotApprovedMessage).And.Be(MigrationWriteTargets.DestinationRefusal(store.DataSources[2]));
    }

    [Test]
    public async Task Run_AfterApproval_PassesTheApprovalCheck()
    {
        var store = new ChangeApprovalStore();
        var job = store.AddJob(approvedDefinitionHash: ChangeApprovalHashes.NothingApproved);
        var definition = MigrationJobDefinition.Of(job);
        var request = store.AddRequest(ChangeApprovalKind.MigrationJob, job.Id, definition.Hash, "ext-ben");

        await Approvals(store).ApproveAsync(request.Id, null, request.PayloadHash, CancellationToken.None);
        var response = await Service(store, TestChangeApprovals.On()).ExecuteMigrationJob(new ExecuteMigrationJobRequest(job.Id), CancellationToken.None);

        job.ApprovedDefinitionHash.Should().Be(definition.Hash);
        response.ErrorMessage.Should().NotBe(MigrationService.NotApprovedMessage);
    }

    // Grandfathered: a job stored before Beacon reviewed migration jobs carries no approved hash and runs as before.
    [Test]
    public async Task Run_OfAJobStoredBeforeTheWorkflowGovernedIt_PassesTheApprovalCheck()
    {
        var store = new ChangeApprovalStore();
        var job = store.AddJob(approvedDefinitionHash: null);

        var response = await Service(store, TestChangeApprovals.On()).ExecuteMigrationJob(new ExecuteMigrationJobRequest(job.Id), CancellationToken.None);

        response.ErrorMessage.Should().NotBe(MigrationService.NotApprovedMessage);
    }

    [TestCase(false, true)]
    [TestCase(true, false)]
    public async Task Run_WithTheWorkflowOrItsSwitchOff_IgnoresTheApprovalState(bool enabled, bool covers)
    {
        var store = new ChangeApprovalStore();
        var job = store.AddJob(approvedDefinitionHash: ChangeApprovalHashes.NothingApproved);
        var configuration = new BeaconConfiguration { ApprovalWorkflow = new ApprovalWorkflowOptions { Enabled = enabled, CoversMigrationJobs = covers } };

        var response = await Service(store, configuration).ExecuteMigrationJob(new ExecuteMigrationJobRequest(job.Id), CancellationToken.None);

        response.ErrorMessage.Should().NotBe(MigrationService.NotApprovedMessage);
    }

    [Test]
    public async Task TheJobPages_MarkAJobAwaitingApproval_WithItsPendingRequest()
    {
        var store = new ChangeApprovalStore();
        var awaiting = store.AddJob(approvedDefinitionHash: ChangeApprovalHashes.NothingApproved);
        var approved = store.AddJob();
        approved.ApprovedDefinitionHash = MigrationJobDefinition.Of(approved).Hash;
        var grandfathered = store.AddJob();
        var request = store.AddRequest(ChangeApprovalKind.MigrationJob, awaiting.Id, MigrationJobDefinition.Of(awaiting).Hash, "ext-ben");
        await using var context = await store.Factory().CreateDbContextAsync(CancellationToken.None);

        var state = await TestChangeApprovals.MigrationJobs(TestChangeApprovals.On()).AwaitingAsync(context, [awaiting.Id, approved.Id, grandfathered.Id], CancellationToken.None);
        var off = await TestChangeApprovals.MigrationJobs().AwaitingAsync(context, [awaiting.Id], CancellationToken.None);

        state.Should().ContainSingle().Which.Should().BeEquivalentTo(new KeyValuePair<int, AwaitingApproval>(awaiting.Id, new AwaitingApproval(request.Id, null)));
        off.Should().BeEmpty("with the workflow off nothing waits");
    }

    // What a run does is the definition an approval covers: each part of it changes the hash.
    [TestCase("DataSourceId")]
    [TestCase("QueryText")]
    [TestCase("DestinationDataSourceId")]
    [TestCase("DestinationTable")]
    [TestCase("Mode")]
    [TestCase("TransformationScript")]
    public void TheDefinitionHash_ChangesWithEachPartOfWhatARunDoes(string part)
    {
        var job = new ChangeApprovalStore().AddJob("SELECT id FROM orders");
        var approved = MigrationJobDefinition.Of(job).Hash;

        Change(job, part);

        MigrationJobDefinition.Of(job).Hash.Should().NotBe(approved, $"a run reads its {part}");
    }

    // The name, description and the settings no run reads leave the approval as it is.
    [TestCase("Name")]
    [TestCase("Description")]
    [TestCase("Schedule")]
    [TestCase("IsEnabled")]
    [TestCase("MaxRetries")]
    [TestCase("TimeoutMinutes")]
    [TestCase("ValidateBeforeExecution")]
    public void TheDefinitionHash_IgnoresWhatNoRunReads(string part)
    {
        var job = new ChangeApprovalStore().AddJob("SELECT id FROM orders");
        var approved = MigrationJobDefinition.Of(job).Hash;

        Change(job, part);

        MigrationJobDefinition.Of(job).Hash.Should().Be(approved, $"no run reads its {part}");
    }

    private static void Change(MigrationJob job, string part)
    {
        switch (part)
        {
            case "DataSourceId":
                job.DataSourceId = ChangeApprovalStore.Ledger;
                break;
            case "QueryText":
                job.QueryText += " ";
                break;
            case "DestinationDataSourceId":
                job.DestinationDataSourceId = ChangeApprovalStore.Ledger;
                break;
            case "DestinationTable":
                job.DestinationTable = "orders_copy";
                break;
            case "Mode":
                job.Mode = MigrationMode.Upsert;
                break;
            case "TransformationScript":
                job.TransformationScript = "@result";
                break;
            case "Name":
                job.Name = "renamed";
                break;
            case "Description":
                job.Description = "another description";
                break;
            case "Schedule":
                job.Schedule = "0 6 * * *";
                break;
            case "IsEnabled":
                job.IsEnabled = !job.IsEnabled;
                break;
            case "MaxRetries":
                job.MaxRetries += 1;
                break;
            case "TimeoutMinutes":
                job.TimeoutMinutes += 1;
                break;
            case "ValidateBeforeExecution":
                job.ValidateBeforeExecution = !job.ValidateBeforeExecution;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(part), part, null);
        }
    }

    private static CreateMigrationJobRequest Request(string sql) =>
        new()
        {
            Name = "copy orders",
            Description = "nightly copy",
            DataSourceId = ChangeApprovalStore.Warehouse,
            QueryText = sql,
            DestinationDataSourceId = ChangeApprovalStore.Target,
            DestinationTable = "orders"
        };

    private static MigrationService Service(ChangeApprovalStore store, BeaconConfiguration configuration, IBeaconActorAccessor? caller = null) =>
        new(
            store.Factory(),
            Mock.Of<IDataSourceConnectionResolver>(),
            Mock.Of<IQueryService>(MockBehavior.Strict),
            new QueryExecutionPreviewService(new RecordingStoredQueryPreview { TemporaryAnswer = _ => throw new InvalidOperationException("The test reads no migration source.") }, TestProjectAccess.Authorizer(TestProjectAccess.Unrestricted)),
            Mock.Of<IBeaconUserContext>(),
            MigrationTestWriters.All(),
            NullLogger<MigrationService>.Instance,
            Options.Create(new MigrationOptions { Enabled = true }),
            TestChangeApprovals.Submitter(configuration, caller ?? TestChangeApprovals.Caller(Ada)), TestProjectAccess.Actors(), new RecordingSecurityAudit(), TimeProvider.System);

    private static ChangeApprovalService Approvals(ChangeApprovalStore store) =>
        new(
            store.Factory(),
            TestChangeApprovals.Caller("ext-cid"),
            Mock.Of<IBeaconUserContext>(),
            store.Protector,
            TestChangeApprovals.Hasher,
            TestChangeApprovals.On(),
            new RecordingSecurityAudit(),
            TestPermissions.Admin,
            TestChangeApprovals.ReviewerUnchanged,
            TestChangeApprovals.NoDefinitions,
            TestChangeApprovals.NoProbe,
            TestChangeApprovals.Llm(),
            NullLogger<ChangeApprovalService>.Instance);
}
