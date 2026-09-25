using Beacon.Core.SavedQueries;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Queries;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
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

    private static SetQueryMcpToolHandler Handler(SavedQueryTestData data) =>
        new(data.Store.Factory().Object, NullLogger<SetQueryMcpToolHandler>.Instance);
}
