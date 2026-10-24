using Beacon.Core.Handlers.DataQuality.GetDataQualityOverview;
using Beacon.Core.Handlers.McpEval;
using Beacon.Core.Handlers.McpGlossary;
using Beacon.Core.Handlers.QueryFolders;
using Beacon.Core.Helpers;
using Beacon.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// The glossary, golden case and eval result lists page in SQL on both providers: a count, then one page sorted by the
/// request (or the list's default) and ended on the item id. Each list keeps its unpaged route, now bounded.
/// </summary>
[TestFixture]
public class PlatformListPagingTests
{
    [TestCase(false, "m.project_id = @", "LIMIT @", "ORDER BY m.term, m.id")]
    [TestCase(true, "[m].[ProjectId] = @", "FETCH NEXT @", "ORDER BY [m].[Term], [m].[Id]")]
    public async Task TheGlossaryPage_CountsThenPagesSortedByTerm_Translates(bool sqlServer, string project, string paging, string order)
    {
        var capture = Capture(sqlServer).AnsweringCountsWithOne();

        var page = await new ListGlossaryTermsHandler(capture.Factory())
            .Handle(new ListGlossaryTermsQuery { ProjectId = 4, Page = 2, PageSize = 10 }, CancellationToken.None);

        capture.Commands.Should().HaveCount(2, "the count, then the page");
        capture.Commands.Should().OnlyContain(x => x.Contains(project));
        capture.Commands[1].Should().Contain(order).And.Contain(paging);
        capture.CommandParameters[1].Values.Should().Contain(20, "page 2 of 10 rows starts at row 20");
        page.TotalCount.Should().Be(1);
    }

    [Test]
    public async Task TheGlossaryPage_SearchesTheTerm_AndIncludesInactiveOnRequest()
    {
        var capture = Capture(false).AnsweringCountsWithOne();

        await new ListGlossaryTermsHandler(capture.Factory())
            .Handle(new ListGlossaryTermsQuery { ProjectId = 4, IncludeInactive = true, Search = "rev" }, CancellationToken.None);

        capture.Commands[0].Should().Contain("m.term LIKE @").And.NotContain("m.is_active");
    }

    [TestCase(false, "ORDER BY m.created_time DESC, m.id", "LIMIT @")]
    [TestCase(true, "ORDER BY [m].[CreatedTime] DESC, [m].[Id]", "FETCH NEXT @")]
    public async Task TheGoldenCasePage_CountsThenPagesNewestFirst_Translates(bool sqlServer, string order, string paging)
    {
        var capture = Capture(sqlServer).AnsweringCountsWithOne();

        await new ListGoldenCasesHandler(capture.Factory())
            .Handle(new ListGoldenCasesQuery { ProjectId = 4 }, CancellationToken.None);

        capture.Commands.Should().HaveCount(2, "the count, then the page; an empty page reads no data source dialects");
        capture.Commands[1].Should().Contain(order).And.Contain(paging);
    }

    [TestCase(false, "m.eval_run_id = @")]
    [TestCase(true, "[m].[EvalRunId] = @")]
    public async Task TheEvalResultPage_CountsThenPagesBySortedColumn_Translates(bool sqlServer, string run)
    {
        var capture = Capture(sqlServer).AnsweringCountsWithOne();

        await new ListEvalResultsHandler(capture.Factory())
            .Handle(new ListEvalResultsQuery { RunId = 7, Sort = "-executionTimeMs" }, CancellationToken.None);

        capture.Commands.Should().HaveCount(2);
        capture.Commands.Should().OnlyContain(x => x.Contains(run));
        capture.Commands[1].Should().Contain(sqlServer ? "ORDER BY [m].[ExecutionTimeMs] DESC, [m].[Id]" : "ORDER BY m.execution_time_ms DESC, m.id");
    }

    [Test]
    public async Task ThePageSize_IsClampedToTheMaximum()
    {
        var capture = Capture(false).AnsweringCountsWithOne();

        await new ListEvalResultsHandler(capture.Factory())
            .Handle(new ListEvalResultsQuery { RunId = 7, PageSize = 100_000 }, CancellationToken.None);

        capture.CommandParameters[1].Values.Should().Contain(ListRequest.MaxPageSize);
    }

    [TestCase(false, "LIMIT @")]
    [TestCase(true, "TOP(@")]
    public async Task TheUnpagedLists_ReadOneRowPastTheSafetyCap(bool sqlServer, string limit)
    {
        var capture = Capture(sqlServer).ThenNoRows().ThenNoRows().ThenNoRows();

        await new GetGlossaryTermsHandler(capture.Factory()).Handle(new GetGlossaryTermsQuery { ProjectId = 4 }, CancellationToken.None);
        await new GetEvalResultsHandler(capture.Factory()).Handle(new GetEvalResultsQuery(7), CancellationToken.None);
        await new GetGoldenCasesHandler(capture.Factory()).Handle(new GetGoldenCasesQuery { ProjectId = 4 }, CancellationToken.None);

        capture.Commands.Should().HaveCount(3).And.OnlyContain(x => x.Contains(limit));
        capture.CommandParameters.Should().OnlyContain(x => x.Values.Contains(ListRequest.LegacyUnpagedCap + 1));
    }

    [Test]
    public void ACutUnpagedList_KeepsTheCapAndSaysSo()
    {
        var truncation = new LegacyListTruncation();
        var items = Enumerable.Range(0, ListRequest.LegacyUnpagedCap + 1).ToList();

        var kept = LegacyListTruncation.Apply(items, truncation, null, "test");

        kept.Should().HaveCount(ListRequest.LegacyUnpagedCap);
        truncation.IsTruncated.Should().BeTrue();
    }

    [Test]
    public void AnUncutUnpagedList_IsReturnedWhole_WithoutTheTruncationFlag()
    {
        var truncation = new LegacyListTruncation();
        var items = Enumerable.Range(0, ListRequest.LegacyUnpagedCap).ToList();

        LegacyListTruncation.Apply(items, truncation, null, "test").Should().HaveCount(ListRequest.LegacyUnpagedCap);
        truncation.IsTruncated.Should().BeFalse();
    }

    [Test]
    public async Task TheUnpagedDataQualityOverview_ReadsAllScoresInOneQuery_UpToTheSafetyCap()
    {
        var commands = await new ListHandlerHarness(TestProjectAccess.FastPath).SendAsync(new GetDataQualityOverviewQuery());

        commands.Should().HaveCount(2, "the scores of every source, then the contract counts; never a query per source");
        commands[0].Should().NotContain("data_contracts").And.Contain("LIMIT @");
    }

    [Test]
    public async Task ThePagedDataQualityOverview_ReadsTheTableScoresOfAllItsSourcesInOneQuery()
    {
        var commands = await new ListHandlerHarness(TestProjectAccess.FastPath).SendAsync(new ListDataQualityOverviewQuery { PageSize = 50 });

        commands.Should().HaveCountLessThanOrEqualTo(4, "count, page, contracts and one table score query");
    }

    [Test]
    public void ASearchTerm_IsTrimmedAndCutToTheMaximumLength()
    {
        ListRequest.ClampSearch(new string('a', 500)).Should().HaveLength(ListRequest.MaxSearchLength);
        ListRequest.ClampSearch("  rev ").Should().Be("rev");
        ListRequest.ClampSearch("   ").Should().BeNull();
    }

    [Test]
    public async Task TheFolderPage_CanBeNarrowedToOneFolderById()
    {
        var commands = await new ListHandlerHarness(TestProjectAccess.FastPath).SendAsync(new ListQueryFoldersQuery { FolderId = 5 });

        commands.Should().OnlyContain(x => x.Contains("q.id = @") || x.Contains(".id = @"));
    }

    private static SqlCapture Capture(bool sqlServer) => sqlServer ? SqlCapture.OnSqlServer() : new SqlCapture();
}
