using Beacon.Api;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.McpEval;
using Beacon.Core.Handlers.McpLearning;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Grounding;

/// <summary>
/// The SQL the grounding-integrity reads send, on PostgreSQL and SQL Server, captured without a database: the golden
/// candidates (Correct verdicts with retained content and no golden case promoted from them, the asker's and the rater's
/// names, newest first, paged), the golden list with who promoted and edited each case and their names, the pattern and
/// patch lists with who reviewed, applied or rejected each, the data source read the
/// grounding SQL check takes its dialect from, the live projects linking data sources (who may curate shared grounding),
/// the case a signal was already promoted to, a documentation patch resolved with its target, the grounding recheck's reads,
/// and the grounding startup note's counts.
/// </summary>
[TestFixture]
public class GroundingTranslationTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task TheGoldenCandidates_Translates(bool sqlServer)
    {
        var capture = Capture(sqlServer).AnsweringCountsWithOne();

        await new GetGoldenCandidatesHandler(capture.Factory()).Handle(new GetGoldenCandidatesQuery { ProjectId = 4 }, CancellationToken.None);

        capture.Commands.Should().HaveCount(2, "the count, then the page");
        var page = capture.Commands[1];
        if (sqlServer)
        {
            capture.Commands.Should().OnlyContain(x => x.Contains("[m].[UserVerdict] = 1")
                && x.Contains("[m].[ProjectId] IS NOT NULL")
                && x.Contains("[m].[DataSourceId] IS NOT NULL")
                && x.Contains("[m].[Question] <> N''")
                && x.Contains("([m].[UserCorrectedSql] IS NOT NULL OR [m].[CorrectedSql] IS NOT NULL OR [m].[GeneratedSql] IS NOT NULL)")
                && x.Contains("NOT EXISTS (")
                && x.Contains("FROM [beacon].[McpEvalCases] AS [m0]")
                && x.Contains("[m0].[SourceSignalId] = [m].[Id]")
                && x.Contains("[m].[ProjectId] = @"));
            page.Should().Contain("COALESCE([m].[UserCorrectedSql], [m].[CorrectedSql], [m].[GeneratedSql]) AS [Sql]")
                .And.Contain("FROM [beacon].[Users] AS [u]").And.Contain("[u].[Id] = [m].[UserId]")
                .And.Contain("FROM [beacon].[Users] AS [u0]").And.Contain("[u0].[Id] = [m].[FeedbackByUserId]");
            page.Should().Contain("ORDER BY [m].[CreatedTime] DESC, [m].[Id]").And.Contain("OFFSET @");
            return;
        }

        capture.Commands.Should().OnlyContain(x => x.Contains("m.user_verdict = 1")
            && x.Contains("m.project_id IS NOT NULL")
            && x.Contains("m.data_source_id IS NOT NULL")
            && x.Contains("m.question <> ''")
            && x.Contains("(m.user_corrected_sql IS NOT NULL OR m.corrected_sql IS NOT NULL OR m.generated_sql IS NOT NULL)")
            && x.Contains("NOT EXISTS (")
            && x.Contains("FROM beacon.mcp_eval_cases AS m0")
            && x.Contains("m0.source_signal_id = m.id")
            && x.Contains("m.project_id = @"));
        page.Should().Contain("COALESCE(m.user_corrected_sql, m.corrected_sql, m.generated_sql) AS \"Sql\"")
            .And.Contain("FROM beacon.users AS u").And.Contain("u.id = m.user_id")
            .And.Contain("FROM beacon.users AS u0").And.Contain("u0.id = m.feedback_by_user_id");
        page.Should().Contain("ORDER BY m.created_time DESC, m.id").And.Contain("LIMIT @");
    }

    [TestCase(false, "m.promoted_by_user_id AS \"PromotedByUserId\"", "m.modified_by_user_id AS \"ModifiedByUserId\"", "u.id = m.promoted_by_user_id", "u0.id = m.modified_by_user_id")]
    [TestCase(true, "[m].[PromotedByUserId]", "[m].[ModifiedByUserId]", "[u].[Id] = [m].[PromotedByUserId]", "[u0].[Id] = [m].[ModifiedByUserId]")]
    public async Task TheGoldenList_ReadsWhoPromotedAndWhoEdited_WithTheirNames_Translates(bool sqlServer, string promotedBy, string modifiedBy, string promoter, string editor)
    {
        var capture = Capture(sqlServer).ThenNoRows();

        await new GetGoldenCasesHandler(capture.Factory()).Handle(new GetGoldenCasesQuery { ProjectId = 4 }, CancellationToken.None);

        capture.Commands.Should().ContainSingle().Which.Should()
            .Contain(promotedBy).And.Contain(modifiedBy).And.Contain(promoter).And.Contain(editor);
    }

    [TestCase(false, "FROM beacon.users AS u", "u.id = m.reviewed_by_user_id", "m.reviewed_at AS \"ReviewedAt\"")]
    [TestCase(true, "FROM [beacon].[Users] AS [u]", "[u].[Id] = [m].[ReviewedByUserId]", "[m].[ReviewedAt]")]
    public async Task ThePatternList_NamesItsReviewer_Translates(bool sqlServer, string users, string reviewer, string reviewedAt)
    {
        var capture = Capture(sqlServer).AnsweringCountsWithOne();

        await new GetLearnedPatternsHandler(capture.Factory(), TestProjectAccess.Evaluator(TestProjectAccess.Unrestricted))
            .Handle(new GetLearnedPatternsQuery(), CancellationToken.None);

        capture.Commands.Should().HaveCount(2, "the count, then the page");
        capture.Commands[1].Should().Contain(users).And.Contain(reviewer).And.Contain(reviewedAt);
    }

    [TestCase(false, "FROM beacon.users AS u", "u.id = m.applied_by_user_id")]
    [TestCase(true, "FROM [beacon].[Users] AS [u]", "[u].[Id] = [m].[AppliedByUserId]")]
    public async Task ThePatchList_NamesWhoAppliedOrRejected_Translates(bool sqlServer, string users, string decider)
    {
        var capture = Capture(sqlServer).AnsweringCountsWithOne();

        await new GetDocumentationPatchesHandler(capture.Factory(), TestProjectAccess.Evaluator(TestProjectAccess.Unrestricted), TestPermissions.None)
            .Handle(new GetDocumentationPatchesQuery(), CancellationToken.None);

        capture.Commands.Should().HaveCount(2, "the count, then the page; an empty page reads no linking projects");
        capture.Commands[1].Should().Contain(users).And.Contain(decider);
    }

    // With a host's own save check, the grounding check reads the data source's dialect, and whether Beacon holds a catalog
    // for it, in one command; the host's check judges the comment-free SQL.
    [TestCase(false, "SELECT d.data_source_type AS \"DataSourceType\", d.database_engine_type AS \"DatabaseEngineType\", EXISTS (", "FROM beacon.database_metadata AS d0", "d0.data_source_id = d.id", "d.id = @dataSourceId", "LIMIT 1")]
    [TestCase(true, "SELECT TOP(1) [d].[DataSourceType], [d].[DatabaseEngineType], CASE", "FROM [beacon].[DatabaseMetadata] AS [d0]", "[d0].[DataSourceId] = [d].[Id]", "[d].[Id] = @dataSourceId", "[d].[ArchivedTime] IS NULL")]
    public async Task TheCheck_ReadsTheSourcesDialectAndWhetherItHasACatalog_InOneRead_Translates(
        bool sqlServer, string select, string catalog, string catalogOfTheSource, string from, string tail)
    {
        var capture = Capture(sqlServer).ThenRow((int)DataSourceType.Database, (int)DatabaseEngineType.PostgreSQL, true);
        var saveCheck = new Mock<IStepSqlSaveCheck>();

        var result = await new GroundingSqlCheck(capture.Factory(), saveCheck.Object).CheckAsync(7, "SELECT 1 -- remark", CancellationToken.None);

        result.Sql.Should().Be("SELECT 1");
        result.Dialect.Should().Be("PostgreSQL");
        capture.Commands.Should().ContainSingle().Which.Should()
            .Contain(select).And.Contain(catalog).And.Contain(catalogOfTheSource).And.Contain(from).And.Contain(tail);
        saveCheck.Verify(x => x.RejectionAsync(7, "SELECT 1", It.Is<IReadOnlyCollection<string>>(y => y.Count == 0), It.IsAny<CancellationToken>()), Times.Once);
    }

    // A data source Beacon holds no catalog for refuses grounding SQL even where the host's check would pass it.
    [TestCase(false)]
    [TestCase(true)]
    public async Task TheCheck_OfASourceWithoutACatalog_Refuses(bool sqlServer)
    {
        var capture = Capture(sqlServer).ThenRow((int)DataSourceType.Database, (int)DatabaseEngineType.PostgreSQL, false);

        var result = await new GroundingSqlCheck(capture.Factory(), new Mock<IStepSqlSaveCheck>().Object).CheckAsync(7, "SELECT 1", CancellationToken.None);

        result.Refusal.Should().Be(GroundingSqlCheck.NoCatalogRefusal);
    }

    [TestCase(false, "FROM beacon.project_data_sources AS p", "p.data_source_id = ANY (@dataSourceIds)", "archived_time IS NULL")]
    [TestCase(true, "FROM [beacon].[ProjectDataSources] AS [p]", "[p].[DataSourceId] IN (@dataSourceIds1, @dataSourceIds2)", "[ArchivedTime] IS NULL")]
    public async Task TheLiveProjectsLinkingSources_AreOneRead_Translates(bool sqlServer, string from, string sources, string live)
    {
        var capture = Capture(sqlServer).ThenRows([10, 1], [10, 2], [11, 1]);
        await using var context = await capture.Factory().CreateDbContextAsync();

        var linking = await LinkingProjects.OfAsync(context, [10, 11], CancellationToken.None);

        linking[10].Should().Equal(1, 2);
        linking[11].Should().Equal(1);
        capture.Commands.Should().ContainSingle().Which.Should().Contain(from).And.Contain(sources).And.Contain(live);
    }

    // A promotion claims its signal first — inside the transaction that creates the case — with one conditional update:
    // unpromoted, still rated correct, still holding the reviewed SQL by its digest; only then is the case inserted. (The
    // insert's answer cannot be scripted offline: it is captured, and the transaction does not commit.)
    [TestCase(false)]
    [TestCase(true)]
    public async Task APromotion_ClaimsItsSignalFirst_InTheTransactionThatCreatesTheCase_Translates(bool sqlServer)
    {
        var capture = Capture(sqlServer)
            .ThenRow(9, 4, 7, "ask", "How many orders?", (int)McpUserVerdict.Correct, "SELECT id FROM orders", GroundingContentHash.OfSql("SELECT id FROM orders"), DBNull.Value, DBNull.Value, DBNull.Value)
            .ThenScalar(true)
            .ThenRowsAffected(1);
        var handler = PromoteHandler(capture);

        var act = () => handler.Handle(new PromoteSignalToGoldenCommand(9, GroundingContentHash.OfSql("SELECT id FROM orders")), CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
        var timeline = capture.Timeline;
        timeline.Should().HaveCount(5);
        timeline[2].Should().Be(SqlCapture.TransactionBegun);
        AssertTheClaim(timeline[3], sqlServer);
        capture.CommandParameters[2].Values.Should().Contain(GroundingContentHash.OfSql("SELECT id FROM orders"), "the claim compares the reviewed SQL's digest");
        timeline[4].Should().Contain("INSERT INTO").And.Contain(sqlServer ? "[McpEvalCases]" : "mcp_eval_cases");
        capture.Commits.Should().Be(0, "a claim without its case never commits");
    }

    // A signal recorded before Beacon kept the digest has it stored first — outside the claim's transaction, only while it
    // still holds no digest and the SQL read — and the claim compares the digest all the same.
    [TestCase(false)]
    [TestCase(true)]
    public async Task APromotionOfASignalWithoutADigest_StoresItFirst_ThenClaimsByIt_Translates(bool sqlServer)
    {
        var capture = Capture(sqlServer)
            .ThenRow(9, 4, 7, "ask", "How many orders?", (int)McpUserVerdict.Correct, "SELECT id FROM orders", DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value)
            .ThenScalar(true)
            .ThenRowsAffected(1)
            .ThenRowsAffected(1);
        var handler = PromoteHandler(capture);

        var act = () => handler.Handle(new PromoteSignalToGoldenCommand(9, GroundingContentHash.OfSql("SELECT id FROM orders")), CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
        var timeline = capture.Timeline;
        timeline.Should().HaveCount(6);
        var digest = timeline[2];
        if (sqlServer)
        {
            digest.Should().Contain("SET [m].[CandidateSqlHash] = @").And.Contain("[m].[CandidateSqlHash] IS NULL")
                .And.Contain("COALESCE([m].[UserCorrectedSql], [m].[CorrectedSql], [m].[GeneratedSql]) = @");
        }
        else
        {
            digest.Should().Contain("SET candidate_sql_hash = @").And.Contain("m.candidate_sql_hash IS NULL")
                .And.Contain("COALESCE(m.user_corrected_sql, m.corrected_sql, m.generated_sql) = @");
        }

        capture.CommandParameters[2].Values.Should().Contain(GroundingContentHash.OfSql("SELECT id FROM orders"))
            .And.Contain("SELECT id FROM orders");
        timeline[3].Should().Be(SqlCapture.TransactionBegun);
        AssertTheClaim(timeline[4], sqlServer);
    }

    // On a case-insensitive SQL Server, text equality would let a concurrent change of the SQL's case or its trailing spaces
    // pass the claim; the claim compares fixed-length lowercase hex digests instead, which differ for either change.
    [TestCase(false, "SELECT id FROM Orders")]
    [TestCase(true, "SELECT id FROM Orders")]
    [TestCase(false, "SELECT id FROM orders ")]
    [TestCase(true, "SELECT id FROM orders ")]
    public async Task TheClaim_ComparesTheReviewedDigest_WhichACaseOrTrailingSpaceChangeDoesNotMatch_Translates(bool sqlServer, string changed)
    {
        const string reviewed = "SELECT id FROM orders";
        var capture = Capture(sqlServer)
            .ThenRow(9, 4, 7, "ask", "How many orders?", (int)McpUserVerdict.Correct, reviewed, GroundingContentHash.OfSql(reviewed), DBNull.Value, DBNull.Value, DBNull.Value)
            .ThenScalar(true)
            .ThenRowsAffected(0)
            .ThenRow(9, 4, 7, "ask", "How many orders?", (int)McpUserVerdict.Correct, changed, GroundingContentHash.OfSql(changed), DBNull.Value, DBNull.Value, DBNull.Value);
        var handler = PromoteHandler(capture);

        var act = () => handler.Handle(new PromoteSignalToGoldenCommand(9, GroundingContentHash.OfSql(reviewed)), CancellationToken.None);

        (await act.Should().ThrowAsync<BeaconConflictException>()).Which.Message.Should().Be(PromoteSignalToGoldenHandler.ChangedSinceReviewedMessage);
        AssertTheClaim(capture.Timeline[3], sqlServer);
        var compared = capture.CommandParameters[2].Values.OfType<string>().ToList();
        compared.Should().ContainSingle().Which.Should().Be(GroundingContentHash.OfSql(reviewed))
            .And.MatchRegex("^[0-9a-f]{64}$")
            .And.NotBe(GroundingContentHash.OfSql(changed));
        GroundingContentHash.OfSql(changed).Should().NotBeEquivalentTo(GroundingContentHash.OfSql(reviewed), "the digests differ ignoring case too");
    }

    // A promotion that claims nothing — another one committed its claim first — reads the signal again and answers with
    // the winner's case; nothing is inserted and nothing commits.
    [TestCase(false)]
    [TestCase(true)]
    public async Task APromotionThatClaimsNothing_ReadsTheSignalAgain_AndAnswersWithTheWinnersCase_Translates(bool sqlServer)
    {
        var capture = Capture(sqlServer)
            .ThenRow(9, 4, 7, "ask", "How many orders?", (int)McpUserVerdict.Correct, "SELECT id FROM orders", GroundingContentHash.OfSql("SELECT id FROM orders"), DBNull.Value, DBNull.Value, DBNull.Value)
            .ThenScalar(true)
            .ThenRowsAffected(0)
            .ThenRow(9, 4, 7, "ask", "How many orders?", (int)McpUserVerdict.Correct, "SELECT id FROM orders", GroundingContentHash.OfSql("SELECT id FROM orders"), new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc), 12, 12);
        var handler = PromoteHandler(capture);

        var result = await handler.Handle(new PromoteSignalToGoldenCommand(9, GroundingContentHash.OfSql("SELECT id FROM orders")), CancellationToken.None);

        result.Should().BeEquivalentTo(new { EvalCaseId = 12, AlreadyPromoted = true });
        var timeline = capture.Timeline;
        timeline.Should().HaveCount(5);
        timeline[2].Should().Be(SqlCapture.TransactionBegun);
        AssertTheClaim(timeline[3], sqlServer);
        timeline[4].Should().Be(timeline[0], "the signal is read again as it was read first");
        timeline.Should().NotContain(x => x.Contains("INSERT"));
        capture.Commits.Should().Be(0);
    }

    // The read a promotion decides on: the signal's verdict, tool, the SQL a promotion starts from, its claim, and the
    // first case promoted from it before claims were recorded — one command.
    [TestCase(false, "COALESCE(m.user_corrected_sql, m.corrected_sql, m.generated_sql)", "m.promoted_at", "m.promoted_eval_case_id", "m0.source_signal_id = m.id")]
    [TestCase(true, "COALESCE([m].[UserCorrectedSql], [m].[CorrectedSql], [m].[GeneratedSql])", "[m].[PromotedAt]", "[m].[PromotedEvalCaseId]", "[m0].[SourceSignalId] = [m].[Id]")]
    public async Task APromotedSignal_AnswersWithItsCase_InOneRead_Translates(bool sqlServer, string sql, string promotedAt, string promotedCase, string firstCase)
    {
        var capture = Capture(sqlServer)
            .ThenRow(9, 4, 7, "ask", "How many orders?", (int)McpUserVerdict.Correct, "SELECT id FROM orders", DBNull.Value, new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc), 12, DBNull.Value);
        var handler = PromoteHandler(capture);

        var result = await handler.Handle(new PromoteSignalToGoldenCommand(9, "any"), CancellationToken.None);

        result.Should().BeEquivalentTo(new { EvalCaseId = 12, AlreadyPromoted = true });
        capture.Commands.Should().ContainSingle().Which.Should()
            .Contain(sql).And.Contain(promotedAt).And.Contain(promotedCase).And.Contain(firstCase);
    }

    // The claim compares the reviewed SQL by its digest, never by text equality (which a case-insensitive collation weakens).
    private static void AssertTheClaim(string claim, bool sqlServer)
    {
        if (sqlServer)
        {
            claim.Should().Contain("UPDATE [m]").And.Contain("SET [m].[PromotedAt] = @")
                .And.Contain("[m].[Id] = @").And.Contain("[m].[PromotedAt] IS NULL").And.Contain("[m].[UserVerdict] = 1")
                .And.Contain("[m].[CandidateSqlHash] = @").And.NotContain("COALESCE");
            return;
        }

        claim.Should().Contain("UPDATE beacon.mcp_query_signals AS m").And.Contain("SET promoted_at = @")
            .And.Contain("m.id = @").And.Contain("m.promoted_at IS NULL").And.Contain("m.user_verdict = 1")
            .And.Contain("m.candidate_sql_hash = @").And.NotContain("COALESCE");
    }

    private static PromoteSignalToGoldenHandler PromoteHandler(SqlCapture capture)
    {
        return new PromoteSignalToGoldenHandler(
            capture.Factory(),
            TestGroundingSqlChecks.PassThrough().Object,
            TestProjectAccess.Actors(),
            new RecordingSecurityAudit(),
            NullLogger<PromoteSignalToGoldenHandler>.Instance);
    }

    [TestCase(false, "SELECT m.project_id AS \"ProjectId\", m.data_source_id AS \"DataSourceId\", m.target_type AS \"TargetType\"\nFROM beacon.mcp_documentation_patches AS m\nWHERE m.id = @id")]
    [TestCase(true, "SELECT TOP(1) [m].[ProjectId], [m].[DataSourceId], [m].[TargetType]\nFROM [beacon].[McpDocumentationPatches] AS [m]\nWHERE [m].[Id] = @id")]
    public async Task APatchResolvesWithItsTarget_Translates(bool sqlServer, string read)
    {
        var capture = Capture(sqlServer).ThenRow(1, 10, (int)McpDocPatchTarget.DocumentationSection);
        await using var context = await capture.Factory().CreateDbContextAsync();

        var resolved = await new ProjectResourceResolver().ResolveAsync(context, ProjectResource.DocumentationPatch(8), CancellationToken.None);

        resolved.ProjectId.Should().Be(1);
        resolved.SharedDataSourceIds.Should().BeEmpty("a section patch writes no shared state");
        capture.Commands.Should().ContainSingle().Which.Should().Contain(read);
    }

    // The grounding recheck reads the project's existence, then its active golden cases and its approved and auto-approved
    // learned patterns still in use, each in id order.
    [TestCase(false, "SELECT EXISTS (", "FROM beacon.projects AS p", "m.project_id = @request_ProjectId AND m.is_active ORDER BY m.id", "m.project_id = @request_ProjectId AND m.status IN (1, 3) AND m.superseded_at IS NULL ORDER BY m.id")]
    [TestCase(true, "WHEN EXISTS (", "FROM [beacon].[Projects] AS [p]", "[m].[ProjectId] = @request_ProjectId AND [m].[IsActive] = CAST(1 AS bit) ORDER BY [m].[Id]", "[m].[ProjectId] = @request_ProjectId AND [m].[Status] IN (1, 3) AND [m].[SupersededAt] IS NULL ORDER BY [m].[Id]")]
    public async Task TheRecheck_ReadsActiveCasesAndApprovedPatternsInUse_Translates(bool sqlServer, string exists, string projects, string cases, string patterns)
    {
        var capture = Capture(sqlServer).ThenRow(true).ThenNoRows().ThenNoRows();

        var result = await new RecheckGroundingHandler(capture.Factory(), TestGroundingSqlChecks.PassThrough().Object, new RecordingSecurityAudit(), NullLogger<RecheckGroundingHandler>.Instance)
            .Handle(new RecheckGroundingQuery(4), CancellationToken.None);

        result.Refused.Should().BeEmpty();
        capture.Commands.Should().HaveCount(3);
        capture.Commands[0].Should().Contain(exists).And.Contain(projects);
        capture.Commands[1].ReplaceLineEndings(" ").Should().Contain(cases);
        capture.Commands[2].ReplaceLineEndings(" ").Should().Contain(patterns);
    }

    // The startup note's two reads: the projects of active golden cases without a promoter, and the live projects without
    // a ProjectAdmin member whose user is enabled and not archived.
    [TestCase(false, "WHERE m.is_active AND m.promoted_by_user_id IS NULL", "p0.role = 3", "u.archived_time IS NULL AND u.id = p0.user_id AND u.is_enabled", "p.archived_time IS NULL AND NOT EXISTS (")]
    [TestCase(true, "WHERE [m].[IsActive] = CAST(1 AS bit) AND [m].[PromotedByUserId] IS NULL", "[p0].[Role] = 3", "[u].[ArchivedTime] IS NULL AND [u].[Id] = [p0].[UserId] AND [u].[IsEnabled] = CAST(1 AS bit)", "[p].[ArchivedTime] IS NULL AND NOT EXISTS (")]
    public async Task TheGroundingStartupNote_Translates(bool sqlServer, string unattributed, string projectAdmin, string enabledUser, string liveProjects)
    {
        var capture = Capture(sqlServer).ThenRows([1], [3]).ThenRows([2]);
        var configuration = new BeaconConfiguration();
        configuration.UserManagement.Enabled = true;
        var services = new Mock<IServiceProvider>();
        services.Setup(x => x.GetService(typeof(IDbContextFactory<BeaconContext>))).Returns(capture.Factory());
        services.Setup(x => x.GetService(typeof(IMcpSettingsProvider))).Returns(SettingsProviderMock.Create().Object);
        using var logs = new LogRecorder();

        await new GroundingStartupNote(logs.For<GroundingStartupNote>(), configuration).NoteAsync(services.Object, CancellationToken.None);

        capture.Commands.Should().HaveCount(2);
        capture.Commands[0].Should().Contain(unattributed);
        capture.Commands[1].Should().Contain(projectAdmin).And.Contain(enabledUser).And.Contain(liveProjects);
        logs.Entries.Select(x => x.Message).Should().Contain(x => x.StartsWith("2 active golden cases record no promoting user (projects 1,3)", StringComparison.Ordinal))
            .And.Contain(x => x.StartsWith("1 live projects learn from MCP feedback and have no enabled ProjectAdmin member (projects 2)", StringComparison.Ordinal));
    }

    private static SqlCapture Capture(bool sqlServer) => sqlServer ? SqlCapture.OnSqlServer() : new SqlCapture();
}
