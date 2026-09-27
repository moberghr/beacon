using Beacon.Core;
using Beacon.Core.Data.Enums;
using Beacon.Core.HostDocs;
using Beacon.Core.SavedQueries;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using static Beacon.Tests.Unit.SavedQueryTools.SavedQueryTestData;

namespace Beacon.Tests.Unit.SavedQueryTools;

/// <summary>The project brief (<c>get_context format=agents_md</c>) lists the project's saved-query tools.</summary>
[TestFixture]
public class SavedQueryToolBriefTests
{
    private const int ProjectId = 1;

    [Test]
    public async Task TheBrief_ListsTheProjectsSavedQueryTools_WithTheirParameters()
    {
        var data = new SavedQueryTestData();
        data.Project(ProjectId, "Netgiro", data.DataSource(10, "warehouse"));
        var source = Source(Tool(
            "loan_book_by_month",
            [Step(1, 10, "SELECT * FROM loans WHERE issued >= {from} AND issued < {to}", Parameter("from", ParameterType.DateTime, "First day, inclusive"), Parameter("to", ParameterType.DateTime))],
            [ProjectId]));

        var brief = await new ProjectBriefService(data.Store.Factory().Object, null, source.Object).BuildAgentsMdAsync(ProjectId, CancellationToken.None);

        brief.Should().Contain("## Saved query tools");
        brief.Should().Contain("- `q_loan_book_by_month(from: date-time, to: date-time)` — Description of loan_book_by_month");
        brief.Should().Contain("  - `from` — First day, inclusive");
        brief.Should().Contain("Call them by name.");
        source.Verify(x => x.GetToolsAsync(It.Is<IReadOnlyCollection<int>>(y => y.SequenceEqual(new[] { ProjectId })), It.IsAny<CancellationToken>()), Times.Once, "the brief asks for this project's tools only (§1.12)");
    }

    [Test]
    public async Task PastTheNamedToolLimit_TheBriefPointsAtTheCatalogTools()
    {
        var data = new SavedQueryTestData();
        data.Project(ProjectId, "Netgiro", data.DataSource(10, "warehouse"));
        var source = Source(
            Tool("loan_book", [Step(1, 10, "SELECT 1")], [ProjectId]),
            Tool("arrears", [Step(1, 10, "SELECT 1")], [ProjectId], queryId: 2));
        var configuration = new BeaconConfiguration { SavedQueryTools = new SavedQueryToolOptions { NamedToolLimit = 1 } };

        var brief = await new ProjectBriefService(data.Store.Factory().Object, null, source.Object, configuration).BuildAgentsMdAsync(ProjectId, CancellationToken.None);

        brief.Should().Contain("`search_saved_queries`").And.Contain("`run_saved_query`");
        brief.Should().Contain("- `loan_book()`");
    }

    [Test]
    public async Task WithoutTools_TheBriefHasNoSavedQuerySection()
    {
        var data = new SavedQueryTestData();
        data.Project(ProjectId, "Netgiro", data.DataSource(10, "warehouse"));

        var brief = await new ProjectBriefService(data.Store.Factory().Object, null, Source().Object).BuildAgentsMdAsync(ProjectId, CancellationToken.None);

        brief.Should().NotContain("Saved query tools");
    }

    private static Mock<ISavedQueryToolSource> Source(params SavedQueryToolDefinition[] tools)
    {
        var source = new Mock<ISavedQueryToolSource>();
        source
            .Setup(x => x.GetToolsAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(tools);

        return source;
    }
}
