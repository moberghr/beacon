using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using FluentAssertions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ChangeGovernance;

/// <summary>
/// A save changes what runs when any part of the query's executable content differs — the step set, the steps' order,
/// names, data sources, SQL and parameters (name, type, placeholder), and the final query — and such a save is versioned
/// (with the approval workflow on, submitted). Descriptions and the query's own name change nothing that runs. With the
/// workflow on, stored SQL that is not its active version's is submitted when saved; the step mutators that bypass all
/// this are refused.
/// </summary>
[TestFixture]
public class QueryChangeDetectionTests
{
    private static IEnumerable<TestCaseData> Changes()
    {
        yield return Case("the SQL", x => x.Steps[0].SqlValue = "SELECT 2");
        yield return Case("a step name", x => x.Steps[0].Name = "Totals");
        yield return Case("a data source", x => x.Steps[0].DataSourceId = GovernanceStore.LedgerId);
        yield return Case("an added parameter", x => x.Steps[0].Parameters.Add(new QueryStepParameterData { Name = "n", Type = ParameterType.Number, Placeholder = "{n}" }));
        yield return Case("a removed step", x => x.Steps.Clear());
        yield return Case("an added step", x => x.Steps.Add(new QueryStepData { StepOrder = 2, Name = "Step 2", SqlValue = "SELECT 2", DataSourceId = GovernanceStore.DataSourceId }));
        yield return Case("the final query", x => x.FinalQuery = "SELECT * FROM @result1");
    }

    private static IEnumerable<TestCaseData> NonChanges()
    {
        yield return Case("nothing", _ => { });
        yield return Case("a missing final query made empty", x => x.FinalQuery = string.Empty);
        yield return Case("a step description", x => x.Steps[0].Description = "Orders of the day");
        yield return Case("the query's name", x => x.Name = "Daily orders");
        yield return Case("the query's description", x => x.Description = "For the morning report");
    }

    private static IEnumerable<TestCaseData> ParameterChanges()
    {
        yield return Case("its name", x => x.Steps[0].Parameters[0].Name = "m");
        yield return Case("its type", x => x.Steps[0].Parameters[0].Type = ParameterType.String);
        yield return Case("its placeholder", x => x.Steps[0].Parameters[0].Placeholder = "{m}");
        yield return Case("its removal", x => x.Steps[0].Parameters.Clear());
    }

    [TestCaseSource(nameof(Changes))]
    public async Task ASave_ThatChangesWhatRuns_IsVersioned(string _, Action<QueryData> edit)
    {
        var store = new GovernanceStore();
        var versions = new Mock<IQueryVersionService>();

        await Service(store, versions.Object).UpdateQuery(Edited(edit), CancellationToken.None);

        versions.Verify(x => x.CreateVersionAsync(GovernanceStore.QueryId, "ext-ada", "UserEdit", null, QueryVersionStatus.Active, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestCaseSource(nameof(Changes))]
    public async Task ASave_ThatChangesWhatRuns_IsSubmittedWithTheWorkflowOn(string _, Action<QueryData> edit)
    {
        var store = new GovernanceStore();
        store.Activate(store.AddVersion(1, QueryVersionStatus.Active));
        var versions = Versions();

        var response = await Service(store, versions.Object, WorkflowOn()).UpdateQuery(Edited(edit), CancellationToken.None);

        response.Message.Should().Be("Changes submitted for approval");
        versions.Verify(
            x => x.SubmitForApprovalAsync(
                GovernanceStore.QueryId,
                It.IsAny<QueryData>(),
                It.Is<QueryChangeSubmission>(y => y.Summary == "SQL query modified" && y.SupersedesOthers),
                It.IsAny<CancellationToken>()),
            Times.Once);
        versions.Verify(x => x.EnsureBaselineVersionAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never, "the submission records the SQL it would replace, in its own transaction");
        store.Query.Steps.Should().ContainSingle().Which.SqlValue.Should().Be("SELECT 1", "nothing goes live before approval");
    }

    // A swap of two steps' orders changes the order they run in.
    [TestCase(true)]
    [TestCase(false)]
    public async Task ASave_ThatSwapsTwoStepsOrders_ChangesWhatRuns(bool workflowOn)
    {
        var store = new GovernanceStore();
        store.Query.Steps.Add(new QueryStep { Id = 12, QueryId = GovernanceStore.QueryId, DataSourceId = GovernanceStore.DataSourceId, DataSource = store.Warehouse, StepOrder = 2, Name = "Step 2", SqlValue = "SELECT 2" });
        store.Activate(store.AddVersion(1, QueryVersionStatus.Active));
        var versions = Versions();
        var swapped = GovernanceStore.Edit("SELECT 1");
        swapped.Steps[0].StepOrder = 2;
        swapped.Steps.Add(new QueryStepData { StepId = 12, StepOrder = 1, Name = "Step 2", SqlValue = "SELECT 2", DataSourceId = GovernanceStore.DataSourceId });

        await Service(store, versions.Object, workflowOn ? WorkflowOn() : null).UpdateQuery(swapped, CancellationToken.None);

        if (workflowOn)
        {
            versions.Verify(x => x.SubmitForApprovalAsync(GovernanceStore.QueryId, It.IsAny<QueryData>(), It.IsAny<QueryChangeSubmission>(), It.IsAny<CancellationToken>()), Times.Once);
            return;
        }

        versions.Verify(x => x.CreateVersionAsync(GovernanceStore.QueryId, "ext-ada", "UserEdit", null, QueryVersionStatus.Active, It.IsAny<CancellationToken>()), Times.Once);
    }

    // Two steps sharing an order run in whichever order the store returns them, and a gap leaves a step's order and place
    // apart: a save whose orders do not run 1, 2, 3, … is refused before anything is written, whatever the workflow.
    [TestCase(true, 2, 0)]
    [TestCase(false, 2, 0)]
    [TestCase(true, 1, 1)]
    [TestCase(false, 1, 3)]
    public async Task ASave_WhoseStepOrdersDoNotRunInSequence_IsRefused(bool workflowOn, int firstOrder, int secondOrder)
    {
        var store = new GovernanceStore();
        store.Activate(store.AddVersion(1, QueryVersionStatus.Active));
        var versions = Versions();
        var edit = GovernanceStore.Edit("SELECT 2");
        edit.Steps[0].StepOrder = firstOrder;
        if (secondOrder > 0)
        {
            edit.Steps.Add(new QueryStepData { StepOrder = secondOrder, Name = "Step 2", SqlValue = "SELECT 3", DataSourceId = GovernanceStore.DataSourceId });
        }

        var act = () => Service(store, versions.Object, workflowOn ? WorkflowOn() : null).UpdateQuery(edit, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(QueryStepOrders.Refusal);
        versions.VerifyNoOtherCalls();
        store.Query.Steps.Should().ContainSingle().Which.SqlValue.Should().Be("SELECT 1");
        store.SaveCount.Should().Be(0);
    }

    // An editor that loaded another version than the active one would overwrite (or submit over) what went live since.
    [TestCase(true)]
    [TestCase(false)]
    public async Task ASave_FromAnEditorThatLoadedAnotherVersion_Is409AndWritesNothing(bool workflowOn)
    {
        var store = new GovernanceStore();
        var loaded = store.AddVersion(1, QueryVersionStatus.Archived);
        store.Activate(store.AddVersion(2, QueryVersionStatus.Active));
        var versions = Versions();
        var edit = GovernanceStore.Edit("SELECT 2");
        edit.BaseVersionId = loaded.Id;

        var act = () => Service(store, versions.Object, workflowOn ? WorkflowOn() : null).UpdateQuery(edit, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(QueryVersionService.StaleEditorMessage);
        versions.VerifyNoOtherCalls();
        store.SaveCount.Should().Be(0);
    }

    // The editor's base travels with the submission, which compares it again under the query's lock.
    [Test]
    public async Task ASave_FromAnEditorOnTheActiveVersion_IsSubmittedWithItsBase()
    {
        var store = new GovernanceStore();
        var active = store.Activate(store.AddVersion(1, QueryVersionStatus.Active));
        var versions = Versions();
        var edit = GovernanceStore.Edit("SELECT 2");
        edit.BaseVersionId = active.Id;

        await Service(store, versions.Object, WorkflowOn()).UpdateQuery(edit, CancellationToken.None);

        versions.Verify(
            x => x.SubmitForApprovalAsync(GovernanceStore.QueryId, It.IsAny<QueryData>(), It.Is<QueryChangeSubmission>(y => y.ExpectedBaseVersionId == active.Id), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestCase(1, "Changes submitted for approval; they replace 1 pending request for this query")]
    [TestCase(2, "Changes submitted for approval; they replace 2 pending requests for this query")]
    public async Task ASubmittedSave_SaysHowManyPendingRequestsItReplaced(int superseded, string message)
    {
        var store = new GovernanceStore();
        store.Activate(store.AddVersion(1, QueryVersionStatus.Active));

        var response = await Service(store, Versions(superseded).Object, WorkflowOn()).UpdateQuery(GovernanceStore.Edit("SELECT 2"), CancellationToken.None);

        response.Message.Should().Be(message);
    }

    [Test]
    public async Task WithTheWorkflowOff_ASaveThatChangesWhatRuns_RecordsTheSqlItReplacesFirst()
    {
        var store = new GovernanceStore();
        var versions = Versions();

        await Service(store, versions.Object).UpdateQuery(GovernanceStore.Edit("SELECT 2"), CancellationToken.None);

        versions.Verify(x => x.EnsureBaselineVersionAsync(GovernanceStore.QueryId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestCaseSource(nameof(NonChanges))]
    public async Task ASave_ThatChangesNothingThatRuns_WritesNoVersion(string _, Action<QueryData> edit)
    {
        var store = new GovernanceStore();
        store.Activate(store.AddVersion(1, QueryVersionStatus.Active));
        var versions = new Mock<IQueryVersionService>();

        var response = await Service(store, versions.Object, WorkflowOn()).UpdateQuery(Edited(edit), CancellationToken.None);

        response.Message.Should().Be("Saved successfully");
        versions.VerifyNoOtherCalls();
    }

    [TestCaseSource(nameof(ParameterChanges))]
    public async Task ASave_ThatChangesAParameter_IsVersioned(string _, Action<QueryData> edit)
    {
        var store = new GovernanceStore();
        store.Query.Steps[0].Parameters.Add(new QueryStepParameter { QueryStepId = GovernanceStore.StepId, Name = "n", Type = ParameterType.Number, Placeholder = "{n}" });
        var versions = new Mock<IQueryVersionService>();
        var query = Edited(x => x.Steps[0].Parameters.Add(new QueryStepParameterData { Name = "n", Type = ParameterType.Number, Placeholder = "{n}" }));
        edit(query);

        await Service(store, versions.Object).UpdateQuery(query, CancellationToken.None);

        versions.Verify(x => x.CreateVersionAsync(GovernanceStore.QueryId, "ext-ada", "UserEdit", null, QueryVersionStatus.Active, It.IsAny<CancellationToken>()), Times.Once);
    }

    // Stored SQL no version reviewed (changed before through a path that bypassed review, or never versioned) goes under
    // review when the query is saved, instead of staying live.
    [TestCase(true)]
    [TestCase(false)]
    public async Task WithTheWorkflowOn_SavingUnreviewedStoredSql_SubmitsIt(bool hasActiveVersion)
    {
        var store = new GovernanceStore();
        if (hasActiveVersion)
        {
            store.Activate(store.AddVersion(1, QueryVersionStatus.Active, "SELECT 0"));
        }

        var versions = Versions();

        var response = await Service(store, versions.Object, WorkflowOn()).UpdateQuery(Edited(x => x.Name = "Daily orders"), CancellationToken.None);

        response.Message.Should().Be("Changes submitted for approval");
        versions.Verify(
            x => x.SubmitForApprovalAsync(
                GovernanceStore.QueryId,
                It.Is<QueryData>(y => y.Name == "Daily orders" && y.Steps.Single().SqlValue == "SELECT 1"),
                It.Is<QueryChangeSubmission>(y => y.Summary == "Stored SQL submitted for review" && y.RequesterUserId == "ext-ada" && !y.SupersedesOthers),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "a save that changes nothing that runs never replaces someone else's pending change");
        store.Query.Name.Should().Be("Orders", "the save waits for approval as a whole");
    }

    // The single-step form older clients send (the SQL, data source and parameters on the query, which the model turns into
    // its one step): a change of the SQL or the data source is submitted with the workflow on, and nothing goes live.
    [TestCase("SELECT 2", GovernanceStore.DataSourceId)]
    [TestCase("SELECT 1", GovernanceStore.LedgerId)]
    public async Task ALegacySingleStepSave_ThatChangesTheSqlOrTheDataSource_IsSubmittedWithTheWorkflowOn(string sql, int dataSourceId)
    {
        var store = new GovernanceStore();
        store.Activate(store.AddVersion(1, QueryVersionStatus.Active));
        var versions = Versions();

        var response = await Service(store, versions.Object, WorkflowOn()).UpdateQuery(LegacyForm(sql, dataSourceId), CancellationToken.None);

        response.Message.Should().Be("Changes submitted for approval");
        versions.Verify(
            x => x.SubmitForApprovalAsync(
                GovernanceStore.QueryId,
                It.Is<QueryData>(y => y.Steps.Single().SqlValue == sql && y.Steps.Single().DataSourceId == dataSourceId),
                It.Is<QueryChangeSubmission>(y => y.Summary == "SQL query modified"),
                It.IsAny<CancellationToken>()),
            Times.Once);
        store.Query.Steps.Should().ContainSingle().Which.Should().BeEquivalentTo(new { SqlValue = "SELECT 1", DataSourceId = GovernanceStore.DataSourceId }, "nothing goes live before approval");
    }

    [Test]
    public async Task ALegacySingleStepSave_OfTheStoredSql_ChangesNothingThatRuns()
    {
        var store = new GovernanceStore();
        store.Activate(store.AddVersion(1, QueryVersionStatus.Active));
        var versions = new Mock<IQueryVersionService>();

        var response = await Service(store, versions.Object, WorkflowOn()).UpdateQuery(LegacyForm("SELECT 1", GovernanceStore.DataSourceId), CancellationToken.None);

        response.Message.Should().Be("Saved successfully");
        versions.VerifyNoOtherCalls();
    }

    [Test]
    public async Task WithTheWorkflowOn_TheStepMutators_AreRefused()
    {
        var store = new GovernanceStore();
#pragma warning disable CS0618 // the obsolete mutators are what is tested
        var service = Service(store, Mock.Of<IQueryVersionService>(), WorkflowOn());
        var step = new QueryStepData { StepOrder = 2, Name = "Step 2", SqlValue = "SELECT 2", DataSourceId = GovernanceStore.DataSourceId };

        var add = () => service.AddQueryStep(GovernanceStore.QueryId, step, CancellationToken.None);
        var update = () => service.UpdateQueryStep(GovernanceStore.QueryId, 1, step, CancellationToken.None);
        var delete = () => service.DeleteQueryStep(GovernanceStore.QueryId, 1, CancellationToken.None);
#pragma warning restore CS0618

        await add.Should().ThrowAsync<BeaconConflictException>().WithMessage(QueryService.StepMutatorRefusal);
        await update.Should().ThrowAsync<BeaconConflictException>().WithMessage(QueryService.StepMutatorRefusal);
        await delete.Should().ThrowAsync<BeaconConflictException>().WithMessage(QueryService.StepMutatorRefusal);
        store.Query.Steps.Should().ContainSingle().Which.SqlValue.Should().Be("SELECT 1");
        store.Saved.Should().BeEmpty();
    }

    [Test]
    public async Task WithTheWorkflowOff_TheStepMutators_StillWork()
    {
        var store = new GovernanceStore();
#pragma warning disable CS0618 // the obsolete mutators are what is tested
        var service = Service(store, Mock.Of<IQueryVersionService>());

        await service.AddQueryStep(GovernanceStore.QueryId, new QueryStepData { StepOrder = 2, Name = "Step 2", SqlValue = "SELECT 2", DataSourceId = GovernanceStore.DataSourceId }, CancellationToken.None);
#pragma warning restore CS0618

        store.Saved.OfType<QueryStep>().Should().ContainSingle().Which.SqlValue.Should().Be("SELECT 2");
    }

    private static TestCaseData Case(string name, Action<QueryData> edit) => new TestCaseData(name, edit).SetArgDisplayNames(name);

    private static QueryData Edited(Action<QueryData> edit)
    {
        var query = GovernanceStore.Edit("SELECT 1");
        edit(query);

        return query;
    }

    private static BeaconConfiguration WorkflowOn() => new() { ApprovalWorkflow = new ApprovalWorkflowOptions { Enabled = true } };

    // The query as an older client saves it: no steps, the SQL and data source set on the query itself.
    private static QueryData LegacyForm(string sql, int dataSourceId) =>
        new()
        {
            QueryId = GovernanceStore.QueryId,
            Name = "Orders",
            SqlValue = sql,
            DataSourceId = dataSourceId,
        };

    // A version service whose submissions answer as having superseded superseded pending requests.
    private static Mock<IQueryVersionService> Versions(int superseded = 0)
    {
        var versions = new Mock<IQueryVersionService>();
        versions
            .Setup(x => x.SubmitForApprovalAsync(It.IsAny<int>(), It.IsAny<QueryData>(), It.IsAny<QueryChangeSubmission>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryVersionChange(new QueryVersion { Id = 500, QueryId = GovernanceStore.QueryId, Name = "Orders", StepsJson = "[]", Status = QueryVersionStatus.PendingApproval }, superseded));

        return versions;
    }

    private static QueryService Service(GovernanceStore store, IQueryVersionService versions, BeaconConfiguration? configuration = null) =>
        TestQueryServices.QueryService(
            store.Factory(),
            versions: versions,
            configuration: configuration,
            userContext: Mock.Of<IBeaconUserContext>(x => x.UserId == "ext-ada" && x.DisplayName == "Ada Lovelace"));
}
