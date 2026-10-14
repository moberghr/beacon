using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Models;
using Beacon.Core.SavedQueries;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Queries;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.Core.SecurityAudit;
using Beacon.Tests.Common;
using static Beacon.Tests.Unit.SavedQueryTools.SavedQueryTestData;

namespace Beacon.Tests.Unit.SavedQueryTools;

[TestFixture]
public class SetQueryMcpToolHandlerTests
{
    [Test]
    public async Task Enabling_WithAValidUniqueNameAndAnApprovedVersion_PersistsTheTool()
    {
        var data = Seed();
        var query = data.Query(1, null, [Step(1, 10, "SELECT {n}", Parameter("n", ParameterType.Number))], enabled: false);

        var result = await Handler(data).Handle(new SetQueryMcpToolCommand(1, true, " loan_book ", "  Monthly loan book. ", "7"), CancellationToken.None);

        query.McpToolEnabled.Should().BeTrue();
        query.McpToolName.Should().Be("loan_book");
        query.McpToolDescription.Should().Be("Monthly loan book.");
        result.ToolName.Should().Be("q_loan_book");
        result.RunnableVersionNumber.Should().Be(1);
        result.Issue.Should().BeNull();
        data.Store.SaveCount.Should().Be(1);
    }

    [Test]
    public async Task ExposingOrWithdrawingATool_IsRecorded_AndAttributedToTheCaller()
    {
        var data = Seed();
        var query = data.Query(1, null, [Step(1, 10, "SELECT {n}", Parameter("n", ParameterType.Number))], enabled: false);
        var audit = new RecordingSecurityAudit(RecordingSecurityAudit.User(3, "ext-ada"));
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero));

        await Handler(data, audit: audit, timeProvider: time).Handle(new SetQueryMcpToolCommand(1, true, "loan_book", "Monthly loan book.", null), CancellationToken.None);

        var exposed = audit.Single("Monthly loan book");
        exposed.Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.QueryMcpToolChanged, ResourceType = SecurityAuditResources.Query, ResourceId = "1", ActorUserId = (int?)3 },
            x => x.ExcludingMissingMembers());
        exposed.DetailsJson.Should().Be("{\"enabled\":true,\"wasEnabled\":false,\"tool\":\"q_loan_book\",\"versionNumber\":1}");
        query.ModifiedByUserId.Should().Be("ext-ada");
        query.ModifiedTime.Should().Be(time.GetUtcNow().UtcDateTime);
    }

    [TestCase("ab")]
    [TestCase("Loan_Book")]
    [TestCase("loan-book")]
    [TestCase("9lives")]
    public async Task AnInvalidName_IsRejected(string name)
    {
        var data = Seed();
        data.Query(1, null, [Step(1, 10, "SELECT 1")], enabled: false);

        var act = () => Handler(data).Handle(new SetQueryMcpToolCommand(1, true, name, null, null), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*lowercase letters*");
        data.Store.SaveCount.Should().Be(0);
    }

    [Test]
    public async Task EnablingWithoutAName_IsRejected()
    {
        var data = Seed();
        data.Query(1, null, [Step(1, 10, "SELECT 1")], enabled: false);

        var act = () => Handler(data).Handle(new SetQueryMcpToolCommand(1, true, "  ", null, null), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*name is required*");
    }

    [Test]
    public async Task ATooLongDescription_IsRejected()
    {
        var data = Seed();
        data.Query(1, null, [Step(1, 10, "SELECT 1")], enabled: false);

        var act = () => Handler(data).Handle(new SetQueryMcpToolCommand(1, true, "loan_book", new string('d', 1001), null), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*1000*");
    }

    [Test]
    public async Task ANameAnotherQueryUses_IsRejected()
    {
        var data = Seed();
        data.Query(1, null, [Step(1, 10, "SELECT 1")], enabled: false);
        data.Query(2, "loan_book", [Step(1, 10, "SELECT 2")]);

        var act = () => Handler(data).Handle(new SetQueryMcpToolCommand(1, true, "loan_book", null, null), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already used by query #2*");
        data.Store.SaveCount.Should().Be(0);
    }

    [Test]
    public async Task ResavingTheSameQuery_DoesNotCollideWithItself()
    {
        var data = Seed();
        data.Query(1, "loan_book", [Step(1, 10, "SELECT 1")]);

        var result = await Handler(data).Handle(new SetQueryMcpToolCommand(1, true, "loan_book", "Updated.", null), CancellationToken.None);

        result.Description.Should().Be("Updated.");
    }

    [Test]
    public async Task EnablingAQueryWithoutAnApprovedVersion_IsRejected()
    {
        var data = Seed();
        data.Query(1, null, [Step(1, 10, "SELECT 1")], approval: null, enabled: false);

        var act = () => Handler(data).Handle(new SetQueryMcpToolCommand(1, true, "loan_book", null, null), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no approved active version*");
    }

    [Test]
    public async Task EnablingAQueryWithAPendingVersionOnly_IsRejected()
    {
        var data = Seed();
        data.Query(1, null, [Step(1, 10, "SELECT 1")], versionStatus: QueryVersionStatus.PendingApproval, approval: ApprovalStatus.Pending, enabled: false);

        var act = () => Handler(data).Handle(new SetQueryMcpToolCommand(1, true, "loan_book", null, null), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no approved active version*");
    }

    [Test]
    public async Task EnablingAVersionWithAReservedParameter_IsRejected()
    {
        var data = Seed();
        data.Query(1, null, [Step(1, 10, "SELECT {project_id}", Parameter("project_id", ParameterType.Number))], enabled: false);

        var act = () => Handler(data).Handle(new SetQueryMcpToolCommand(1, true, "loan_book", null, null), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*reserved*");
    }

    [Test]
    public async Task Disabling_IsAllowedWithoutARunnableVersion_AndReportsTheIssue()
    {
        var data = Seed();
        var query = data.Query(1, "loan_book", [Step(1, 10, "SELECT 1")], approval: null);

        var result = await Handler(data).Handle(new SetQueryMcpToolCommand(1, false, "loan_book", null, null), CancellationToken.None);

        query.McpToolEnabled.Should().BeFalse();
        query.McpToolName.Should().Be("loan_book", "the name stays reserved while it is set");
        result.RunnableVersionNumber.Should().BeNull();
        result.Issue.Should().Be(SavedQueryRunnableVersion.NoRunnableVersionIssue);
    }

    [Test]
    public async Task Disabling_WithANameAnotherQueryUses_Succeeds_AndKeepsTheStoredName()
    {
        var data = Seed();
        var query = data.Query(1, "loan_book_v1", [Step(1, 10, "SELECT 1")]);
        data.Query(2, "loan_book", [Step(1, 10, "SELECT 2")]);

        var result = await Handler(data).Handle(new SetQueryMcpToolCommand(1, false, "loan_book", null, null), CancellationToken.None);

        query.McpToolEnabled.Should().BeFalse();
        query.McpToolName.Should().Be("loan_book_v1", "a disable never takes over another query's name");
        result.Enabled.Should().BeFalse();
    }

    [Test]
    public async Task ALostRaceOnTheUniqueName_IsTheSameBusinessError_NotA500()
    {
        var data = Seed();
        data.Query(1, null, [Step(1, 10, "SELECT {n}", Parameter("n", ParameterType.Number))], enabled: false);
        data.Store.FailNextSave = new Microsoft.EntityFrameworkCore.DbUpdateException(
            "insert",
            new Npgsql.PostgresException("duplicate key", "ERROR", "ERROR", "23505"));

        var act = () => Handler(data).Handle(new SetQueryMcpToolCommand(1, true, "loan_book", null, null), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("The tool name 'loan_book' is already used by another query.");
    }

    [Test]
    public async Task AQueryNoSingleProjectCanSee_IsReportedAsNotVisible()
    {
        var data = Seed();
        data.Project(2, "B", data.DataSource(20, "ledger"));
        data.Query(1, null, [Step(1, 10, "SELECT 1"), Step(2, 20, "SELECT 2")], enabled: false);

        var result = await Handler(data).Handle(new SetQueryMcpToolCommand(1, true, "loan_book", null, null), CancellationToken.None);

        result.Enabled.Should().BeTrue("visibility can change when a data source is added, so it is reported, not blocking");
        result.Issue.Should().Be(SavedQueryRunnableVersion.NotVisibleInAnyProjectIssue, "project A has only source 10 and project B only source 20");
    }

    [Test]
    public async Task AnUnknownQuery_IsRejected()
    {
        var data = Seed();

        var act = () => Handler(data).Handle(new SetQueryMcpToolCommand(99, false, null, null, null), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*#99 not found*");
    }

    private static SavedQueryTestData Seed()
    {
        var data = new SavedQueryTestData();
        data.Project(1, "A", data.DataSource(10, "warehouse"));

        return data;
    }

    [Test]
    public async Task ACallerWhoCannotReadTheQuery_CannotPublishIt()
    {
        var data = Seed();
        var query = data.Query(1, null, [Step(1, 10, "SELECT {n}", Parameter("n", ParameterType.Number))], enabled: false);
        var authorizer = TestProjectAccess.Authorizer(ProjectAccessScope.None, new MissingResolver(), new SqlCapture().Factory());

        var act = () => new SetQueryMcpToolHandler(data.Store.Factory().Object, authorizer, TestProjectAccess.Actors(), new BeaconConfiguration(), new RecordingSecurityAudit(), TimeProvider.System, NullLogger<SetQueryMcpToolHandler>.Instance)
            .Handle(new SetQueryMcpToolCommand(1, true, "loan_book", null, null), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconNotFoundException>();
        query.McpToolEnabled.Should().BeFalse();
        data.Store.SaveCount.Should().Be(0);
    }

    // Publishing is a third pair of eyes: neither the requester nor the approver of the version the tool runs enables it.
    [TestCase("ext-ada", null)]
    [TestCase(null, "ext-ada")]
    public async Task Enabling_ByTheRequesterOrApproverOfTheActiveVersion_IsRefused(string? requestedBy, string? reviewedBy)
    {
        var data = Seed();
        var query = data.Query(1, null, [Step(1, 10, "SELECT {n}", Parameter("n", ParameterType.Number))], enabled: false);
        var approval = data.Store.ListFor<QueryApprovalRequest>().Single();
        approval.RequestedByUserId = requestedBy ?? "ext-ben";
        approval.ReviewedByUserId = reviewedBy ?? "ext-ben";

        var act = () => Handler(data).Handle(new SetQueryMcpToolCommand(1, true, "loan_book", null, null), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>();
        query.McpToolEnabled.Should().BeFalse();
        data.Store.SaveCount.Should().Be(0);
    }

    [Test]
    public async Task Enabling_BySomeoneOtherThanTheRequesterAndApprover_Succeeds()
    {
        var data = Seed();
        var query = data.Query(1, null, [Step(1, 10, "SELECT {n}", Parameter("n", ParameterType.Number))], enabled: false);
        var approval = data.Store.ListFor<QueryApprovalRequest>().Single();
        approval.RequestedByUserId = "ext-ben";
        approval.ReviewedByUserId = "ext-cid";

        await Handler(data).Handle(new SetQueryMcpToolCommand(1, true, "loan_book", null, null), CancellationToken.None);

        query.McpToolEnabled.Should().BeTrue();
    }

    [Test]
    public async Task Enabling_ByTheRequester_WhenTheHostAllowsSelfApproval_Succeeds()
    {
        var data = Seed();
        var query = data.Query(1, null, [Step(1, 10, "SELECT {n}", Parameter("n", ParameterType.Number))], enabled: false);
        data.Store.ListFor<QueryApprovalRequest>().Single().RequestedByUserId = "ext-ada";
        var selfApproval = new BeaconConfiguration { ApprovalWorkflow = new ApprovalWorkflowOptions { Enabled = true, AllowSelfApproval = true } };

        await Handler(data, selfApproval).Handle(new SetQueryMcpToolCommand(1, true, "loan_book", null, null), CancellationToken.None);

        query.McpToolEnabled.Should().BeTrue();
    }

    // A caller Beacon cannot name (an API key without an active owner) could be the requester or the approver: it publishes
    // nothing while self-approval is off.
    [Test]
    public async Task Enabling_ByACallerWithoutAUserId_IsRefused()
    {
        var data = Seed();
        var query = data.Query(1, null, [Step(1, 10, "SELECT {n}", Parameter("n", ParameterType.Number))], enabled: false);
        var approval = data.Store.ListFor<QueryApprovalRequest>().Single();
        approval.RequestedByUserId = "ext-ben";
        approval.ReviewedByUserId = "ext-cid";
        var nobody = new Mock<IBeaconActorAccessor>();
        nobody.Setup(x => x.GetCurrentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(BeaconActor.Nobody);

        var act = () => Handler(data, actors: nobody.Object).Handle(new SetQueryMcpToolCommand(1, true, "loan_book", null, null), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>();
        query.McpToolEnabled.Should().BeFalse();
        data.Store.SaveCount.Should().Be(0);
    }

    // Strict mode: the version a tool runs must be approved for exactly its content; an approval recorded before Beacon kept
    // hashes names the version but not what it approved.
    [TestCase(false, true)]
    [TestCase(true, false)]
    public async Task Enabling_InStrictMode_NeedsAnApprovalOfTheVersionsHash(bool approvedForItsHash, bool refused)
    {
        var data = Seed();
        var query = data.Query(1, null, [Step(1, 10, "SELECT {n}", Parameter("n", ParameterType.Number))], enabled: false);
        query.ActiveVersion!.ContentHash = new string('a', 64);
        var approval = data.Store.ListFor<QueryApprovalRequest>().Single();
        approval.RequestedByUserId = "ext-ben";
        approval.ReviewedByUserId = "ext-cid";
        approval.ApprovedContentHash = approvedForItsHash ? query.ActiveVersion.ContentHash : null;
        var strict = new BeaconConfiguration { ApprovalWorkflow = new ApprovalWorkflowOptions { Enabled = true, RequireApprovedVersionToRun = true } };

        var act = () => Handler(data, strict).Handle(new SetQueryMcpToolCommand(1, true, "loan_book", null, null), CancellationToken.None);

        if (refused)
        {
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*{SavedQueryRunnableVersion.NoRunnableVersionIssue}");
            query.McpToolEnabled.Should().BeFalse();
            return;
        }

        await act.Should().NotThrowAsync();
        query.McpToolEnabled.Should().BeTrue();
    }

    // With the approval workflow on, the requester is refused under any id their request presents: here the numeric
    // Users.Id an earlier version recorded for the caller's API key.
    [Test]
    public async Task Enabling_ByTheRequesterUnderAnotherOfTheirIds_IsRefused()
    {
        var data = Seed();
        var query = data.Query(1, null, [Step(1, 10, "SELECT {n}", Parameter("n", ParameterType.Number))], enabled: false);
        var approval = data.Store.ListFor<QueryApprovalRequest>().Single();
        approval.RequestedByUserId = "3";
        approval.ReviewedByUserId = "ext-cid";

        var act = () => Handler(data).Handle(new SetQueryMcpToolCommand(1, true, "loan_book", null, null), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>();
        query.McpToolEnabled.Should().BeFalse();
    }

    // With the workflow off nobody approves a change, so there is no second pair of eyes to keep: the requester and approver
    // check applies only while the workflow is on, and whoever may publish enables the tool of a version approved earlier.
    [TestCase("ext-ada")]
    [TestCase("3")]
    public async Task Enabling_WithTheWorkflowOff_IsNotCheckedAgainstTheRequesterOrApprover(string requestedBy)
    {
        var data = Seed();
        var query = data.Query(1, null, [Step(1, 10, "SELECT {n}", Parameter("n", ParameterType.Number))], enabled: false);
        var approval = data.Store.ListFor<QueryApprovalRequest>().Single();
        approval.RequestedByUserId = requestedBy;
        approval.ReviewedByUserId = "ext-ada";

        await Handler(data, new BeaconConfiguration()).Handle(new SetQueryMcpToolCommand(1, true, "loan_book", null, null), CancellationToken.None);

        query.McpToolEnabled.Should().BeTrue();
    }

    [Test]
    public async Task Disabling_ByTheRequester_IsAlwaysAllowed()
    {
        var data = Seed();
        var query = data.Query(1, "loan_book", [Step(1, 10, "SELECT 1")]);
        data.Store.ListFor<QueryApprovalRequest>().Single().RequestedByUserId = "ext-ada";

        await Handler(data).Handle(new SetQueryMcpToolCommand(1, false, "loan_book", null, null), CancellationToken.None);

        query.McpToolEnabled.Should().BeFalse();
    }

    // The caller is "ext-ada", a stored user, unless actors says otherwise.
    private static SetQueryMcpToolHandler Handler(
        SavedQueryTestData data,
        BeaconConfiguration? configuration = null,
        IBeaconActorAccessor? actors = null,
        ISecurityAuditRecorder? audit = null,
        TimeProvider? timeProvider = null) =>
        new(
            data.Store.Factory().Object,
            TestProjectAccess.Authorizer(TestProjectAccess.Unrestricted),
            actors ?? TestProjectAccess.Actors(),
            configuration ?? new BeaconConfiguration { ApprovalWorkflow = new ApprovalWorkflowOptions { Enabled = true } },
            audit ?? new RecordingSecurityAudit(),
            timeProvider ?? TimeProvider.System,
            NullLogger<SetQueryMcpToolHandler>.Instance);

    // Resolves every resource into a project the caller does not read.
    private sealed class MissingResolver : IProjectResourceResolver
    {
        public Task<ResolvedProjectResource> ResolveAsync(BeaconContext context, ProjectResource resource, CancellationToken cancellationToken) =>
            Task.FromResult(ResolvedProjectResource.InProject(77));
    }
}
