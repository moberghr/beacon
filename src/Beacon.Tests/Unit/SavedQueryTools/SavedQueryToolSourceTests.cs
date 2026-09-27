using Beacon.Core.Data.Enums;
using Beacon.Core.SavedQueries;
using FluentAssertions;
using NUnit.Framework;
using static Beacon.Tests.Unit.SavedQueryTools.SavedQueryTestData;

namespace Beacon.Tests.Unit.SavedQueryTools;

/// <summary>
/// Which version runs (only an approved active one) and who sees the tool (projects containing every data source the
/// version reads, hard-filtered on the caller's projects — §1.12).
/// </summary>
[TestFixture]
public class SavedQueryToolSourceTests
{
    private const int ProjectA = 1;
    private const int ProjectB = 2;

    [Test]
    public async Task AnApprovedActiveVersion_IsCallable()
    {
        var data = new SavedQueryTestData();
        var shared = data.DataSource(10, "warehouse");
        data.Project(ProjectA, "A", shared);
        var query = data.Query(1, "loan_book", [Step(1, 10, "SELECT 1")]);

        var tools = await Source(data).GetToolsAsync([ProjectA], CancellationToken.None);

        tools.Should().ContainSingle();
        tools[0].ToolName.Should().Be("q_loan_book");
        tools[0].QueryVersionId.Should().Be(query.ActiveVersionId!.Value);
        tools[0].ProjectIds.Should().Equal(ProjectA);
    }

    [Test]
    public async Task APendingEdit_KeepsRunningTheApprovedVersion()
    {
        var data = new SavedQueryTestData();
        data.Project(ProjectA, "A", data.DataSource(10, "warehouse"));
        var query = data.Query(1, "loan_book", [Step(1, 10, "SELECT 'approved'")]);
        data.AddVersion(query, 2, QueryVersionStatus.PendingApproval, [Step(1, 10, "SELECT 'draft'")], approval: ApprovalStatus.Pending);

        var tools = await Source(data).GetToolsAsync([ProjectA], CancellationToken.None);

        tools.Should().ContainSingle().Which.Steps[0].SqlValue.Should().Be("SELECT 'approved'");
        tools[0].VersionNumber.Should().Be(1);
    }

    [TestCase(ApprovalStatus.Pending)]
    [TestCase(ApprovalStatus.Rejected)]
    public async Task AnActiveVersionWhoseApprovalIsNotApproved_IsNotCallable(ApprovalStatus status)
    {
        var data = new SavedQueryTestData();
        data.Project(ProjectA, "A", data.DataSource(10, "warehouse"));
        data.Query(1, "loan_book", [Step(1, 10, "SELECT 1")], approval: status);

        (await Source(data).GetToolsAsync([ProjectA], CancellationToken.None)).Should().BeEmpty();
    }

    [Test]
    public async Task AnActiveVersionThatWentLiveWithoutReview_IsNotCallable()
    {
        var data = new SavedQueryTestData();
        data.Project(ProjectA, "A", data.DataSource(10, "warehouse"));
        data.Query(1, "loan_book", [Step(1, 10, "SELECT 1")], approval: null);

        (await Source(data).GetToolsAsync([ProjectA], CancellationToken.None)).Should().BeEmpty();
    }

    [Test]
    public async Task AnApprovalOfAnOlderVersion_DoesNotMakeANewerActiveVersionCallable()
    {
        var data = new SavedQueryTestData();
        data.Project(ProjectA, "A", data.DataSource(10, "warehouse"));
        var query = data.Query(1, "loan_book", [Step(1, 10, "SELECT 'v1'")]);
        query.ActiveVersion!.Status = QueryVersionStatus.Archived;
        var restored = data.AddVersion(query, 2, QueryVersionStatus.Active, [Step(1, 10, "SELECT 'v2'")]);
        query.ActiveVersionId = restored.Id;
        query.ActiveVersion = restored;

        (await Source(data).GetToolsAsync([ProjectA], CancellationToken.None)).Should().BeEmpty();
    }

    [Test]
    public async Task ADraftOnlyQuery_IsNotCallable()
    {
        var data = new SavedQueryTestData();
        data.Project(ProjectA, "A", data.DataSource(10, "warehouse"));
        data.Query(1, "loan_book", [Step(1, 10, "SELECT 1")], versionStatus: QueryVersionStatus.PendingApproval, approval: ApprovalStatus.Pending);

        (await Source(data).GetToolsAsync([ProjectA], CancellationToken.None)).Should().BeEmpty();
    }

    [Test]
    public async Task DisabledOrUnnamedOrBadlyShapedQueries_AreNotListed()
    {
        var data = new SavedQueryTestData();
        data.Project(ProjectA, "A", data.DataSource(10, "warehouse"));
        data.Query(1, "disabled_one", [Step(1, 10, "SELECT 1")], enabled: false);
        data.Query(2, null, [Step(1, 10, "SELECT 1")]);
        data.Query(3, "conflicting", [Step(1, 10, "SELECT {x}", Parameter("x", ParameterType.Number)), Step(2, 10, "SELECT {x}", Parameter("x", ParameterType.String))]);

        (await Source(data).GetToolsAsync([ProjectA], CancellationToken.None)).Should().BeEmpty();
    }

    [Test]
    public async Task ASharedDataSource_ShowsTheToolInEveryProjectThatHasAllOfItsSources()
    {
        var data = new SavedQueryTestData();
        var shared = data.DataSource(10, "shared");
        var onlyA = data.DataSource(11, "only-a");
        data.Project(ProjectA, "A", shared, onlyA);
        data.Project(ProjectB, "B", shared);
        data.Query(1, "shared_only", [Step(1, 10, "SELECT 1")]);
        data.Query(2, "needs_a_too", [Step(1, 10, "SELECT 1"), Step(2, 11, "SELECT 2")], finalQuery: "SELECT * FROM @result1");

        var both = await Source(data).GetToolsAsync([ProjectA, ProjectB], CancellationToken.None);

        both.Single(x => x.McpToolName == "shared_only").ProjectIds.Should().Equal(ProjectA, ProjectB);
        both.Single(x => x.McpToolName == "needs_a_too").ProjectIds.Should().Equal(ProjectA);
    }

    [Test]
    public async Task ACallerOfProjectB_NeverSeesAToolThatNeedsAnAOnlySource()
    {
        var data = new SavedQueryTestData();
        var shared = data.DataSource(10, "shared");
        var onlyA = data.DataSource(11, "only-a");
        data.Project(ProjectA, "A", shared, onlyA);
        data.Project(ProjectB, "B", shared);
        data.Query(1, "a_only", [Step(1, 11, "SELECT 1")]);
        data.Query(2, "mixed", [Step(1, 10, "SELECT 1"), Step(2, 11, "SELECT 2")]);
        data.Query(3, "shared_only", [Step(1, 10, "SELECT 1")]);

        var tools = await Source(data).GetToolsAsync([ProjectB], CancellationToken.None);

        tools.Select(x => x.McpToolName).Should().Equal("shared_only");
        tools[0].ProjectIds.Should().BeEquivalentTo(new[] { ProjectB }, "a project the caller did not pass must never appear");
    }

    [Test]
    public async Task NoProjects_ReturnsNothing()
    {
        var data = new SavedQueryTestData();
        data.Project(ProjectA, "A", data.DataSource(10, "warehouse"));
        data.Query(1, "loan_book", [Step(1, 10, "SELECT 1")]);

        (await Source(data).GetToolsAsync([], CancellationToken.None)).Should().BeEmpty();
    }

    [Test]
    public async Task TheDescriptionFallsBackFromTheToolDescriptionToTheVersionDescription()
    {
        var data = new SavedQueryTestData();
        data.Project(ProjectA, "A", data.DataSource(10, "warehouse"));
        data.Query(1, "with_description", [Step(1, 10, "SELECT 1")], toolDescription: "Monthly loan book.");
        data.Query(2, "without_description", [Step(1, 10, "SELECT 1")]);

        var tools = await Source(data).GetToolsAsync([ProjectA], CancellationToken.None);

        tools.Single(x => x.McpToolName == "with_description").Description.Should().Be("Monthly loan book.");
        tools.Single(x => x.McpToolName == "without_description").Description.Should().Be("Description of query 2");
    }

    private static SavedQueryToolSource Source(SavedQueryTestData data) => new(data.Store.Factory().Object);
}
