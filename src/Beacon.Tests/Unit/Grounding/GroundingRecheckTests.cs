using Beacon.Api;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.McpEval;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Grounding;

/// <summary>
/// Grounding stored before Beacon checked it, surfaced for an Admin and never rewritten: the recheck runs today's checks
/// over a project's active golden cases and approved learned patterns still in use and lists what they refuse, by id with
/// the reasons (one batch: each data source read once), changing nothing; the startup note counts the active golden cases
/// with no recorded promoter and, with user management on, names the live projects that learn while no enabled user is
/// their ProjectAdmin. Counts and ids only reach the log. In-memory sets, no database (§4.7).
/// </summary>
[TestFixture]
public class GroundingRecheckTests
{
    private const int ProjectId = 1;
    private const int OtherProjectId = 2;
    private const int DataSourceId = TestGroundingSqlChecks.DataSourceId;

    [Test]
    public async Task TheRecheck_ListsWhatTodaysChecksRefuse_ByIdWithTheReasons_AndChangesNothing()
    {
        var store = new Store();
        store.Cases.Add(Case(5, "How many orders?", "SELECT id FROM orders -- the ids"));
        store.Cases.Add(Case(6, "Which orders are open?", "SELECT secret_column FROM orders"));
        store.Cases.Add(Case(7, "How many ‮orders?", "SELECT id FROM orders"));
        store.Cases.Add(Case(8, "", "SELECT id FROM orders"));
        var inactive = Case(9, "Old question", "DELETE FROM orders");
        inactive.IsActive = false;
        store.Cases.Add(inactive);
        var otherProject = Case(10, "Their question", "DELETE FROM orders");
        otherProject.ProjectId = OtherProjectId;
        store.Cases.Add(otherProject);
        store.Patterns.Add(Pattern(21, McpPatternStatus.Approved, "orders last week", "SELECT id FROM orders"));
        store.Patterns.Add(Pattern(22, McpPatternStatus.AutoApproved, "orders last week", "SELECT id /* never closed FROM orders"));
        store.Patterns.Add(Pattern(23, McpPatternStatus.Approved, "orders​ last week", null));
        store.Patterns.Add(Pattern(24, McpPatternStatus.NeedsEvidence, "orders last week", "DELETE FROM orders"));
        store.Patterns.Add(Pattern(25, McpPatternStatus.Rejected, "orders last week", "DELETE FROM orders"));
        var superseded = Pattern(26, McpPatternStatus.Approved, "orders last week", "DELETE FROM orders");
        superseded.SupersededAt = DateTime.UtcNow;
        store.Patterns.Add(superseded);
        var pending = store.Patterns.Single(x => x.Id == 22);

        var result = await Handler(store).Handle(new RecheckGroundingQuery(ProjectId), CancellationToken.None);

        result.Should().BeEquivalentTo(new { ProjectId, GoldenCasesChecked = 4, PatternsChecked = 3 }, x => x.ExcludingMissingMembers());
        result.Refused.Select(x => (x.Kind, x.Id)).Should().Equal(
            (GroundingRecheckKind.GoldenCase, 6),
            (GroundingRecheckKind.GoldenCase, 7),
            (GroundingRecheckKind.GoldenCase, 8),
            (GroundingRecheckKind.LearnedPattern, 22),
            (GroundingRecheckKind.LearnedPattern, 23));
        result.Refused[0].Should().BeEquivalentTo(
            new { DataSourceId, Question = "Which orders are open?", Sql = "SELECT secret_column FROM orders", QuestionRefusal = (string?)null, PromotedByUserId = (int?)null, ContentHash = (string?)null },
            x => x.ExcludingMissingMembers());
        result.Refused[0].SqlRefusal.Should().NotBeNullOrEmpty();
        result.Refused[1].Should().BeEquivalentTo(new { QuestionRefusal = RecheckGroundingHandler.HiddenQuestionRefusal, SqlRefusal = (string?)null }, x => x.ExcludingMissingMembers());
        result.Refused[2].QuestionRefusal.Should().Be(RecheckGroundingHandler.EmptyQuestionRefusal);
        result.Refused[3].Should().BeEquivalentTo(
            new
            {
                SqlRefusal = GroundingSqlCheck.CommentRefusal,
                QuestionRefusal = (string?)null,
                PatternStatus = (McpPatternStatus?)McpPatternStatus.AutoApproved,
                ContentHash = GroundingContentHash.OfPattern(pending.PatternContent, pending.ExampleQuestion, pending.ExampleSql)
            },
            x => x.ExcludingMissingMembers());
        result.Refused[4].Should().BeEquivalentTo(new { QuestionRefusal = RecheckGroundingHandler.HiddenQuestionRefusal, SqlRefusal = (string?)null }, x => x.ExcludingMissingMembers());
        store.Cases.Single(x => x.Id == 5).GoldSql.Should().Be("SELECT id FROM orders -- the ids", "nothing stored is rewritten");
        store.Cases.Should().OnlyContain(x => x.Id == 9 ? !x.IsActive : x.IsActive);
        pending.Status.Should().Be(McpPatternStatus.AutoApproved);
        store.Saved.Should().BeEmpty();
    }

    [Test]
    public async Task TheRecheck_ReadsEachDataSourceAndItsCatalogOnce_AndLogsCountsAndIdsOnly()
    {
        var store = new Store();
        store.Cases.Add(Case(5, "How many orders?", "SELECT secret_column FROM orders"));
        store.Cases.Add(Case(6, "Which orders are open?", "SELECT status FROM orders"));
        store.Patterns.Add(Pattern(21, McpPatternStatus.Approved, "orders last week", "SELECT total FROM orders"));
        var reads = 0;
        using var logs = new LogRecorder();

        var result = await new RecheckGroundingHandler(store.Factory, TestGroundingSqlChecks.Real(onRead: () => reads++), logs.For<RecheckGroundingHandler>())
            .Handle(new RecheckGroundingQuery(ProjectId), CancellationToken.None);

        result.Refused.Should().ContainSingle().Which.Id.Should().Be(5);
        reads.Should().Be(2, "the data source and its catalog, once for the whole recheck");
        logs.Entries.Should().ContainSingle().Which.Message.Should().Be(
            "Grounding recheck of project 1: 1 of 2 active golden cases and 1 approved learned patterns refused (golden cases 5; patterns )");
        logs.Contains("secret_column").Should().BeFalse();
        logs.Contains("orders").Should().BeFalse("no question or SQL reaches the log");
    }

    [Test]
    public async Task TheRecheck_OfAMissingProject_IsNotFound()
    {
        var store = new Store();

        var act = () => Handler(store).Handle(new RecheckGroundingQuery(99), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconNotFoundException>().WithMessage("Project 99 not found.");
    }

    [Test]
    public async Task TheRecheck_OfAProjectWhoseGroundingPasses_RefusesNothing()
    {
        var store = new Store();
        store.Cases.Add(Case(5, "How many orders?", "SELECT id FROM orders WHERE status = 'a--b'"));
        store.Patterns.Add(Pattern(21, McpPatternStatus.Approved, null, null));

        var result = await Handler(store).Handle(new RecheckGroundingQuery(ProjectId), CancellationToken.None);

        result.Should().BeEquivalentTo(new { GoldenCasesChecked = 1, PatternsChecked = 1 }, x => x.ExcludingMissingMembers());
        result.Refused.Should().BeEmpty();
    }

    [Test]
    public async Task TheStartupNote_CountsActiveGoldenCasesWithoutAPromoter_AndSaysHowToReviewThem()
    {
        var store = new Store();
        store.Cases.Add(Case(5, "q", "SELECT 1"));
        store.Cases.Add(Case(6, "q", "SELECT 1"));
        var otherProject = Case(7, "q", "SELECT 1");
        otherProject.ProjectId = OtherProjectId;
        store.Cases.Add(otherProject);
        var promoted = Case(8, "q", "SELECT 1");
        promoted.PromotedByUserId = 4;
        store.Cases.Add(promoted);
        var inactive = Case(9, "q", "SELECT 1");
        inactive.IsActive = false;
        store.Cases.Add(inactive);
        using var logs = new LogRecorder();

        await Note(logs, userManagement: false).NoteAsync(Services(store, SettingsProviderMock.Create().Object), CancellationToken.None);

        logs.Entries.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Level = LogLevel.Warning }, x => x.ExcludingMissingMembers());
        logs.Entries[0].Message.Should().StartWith("3 active golden cases record no promoting user (projects 1,2)")
            .And.Contain("Golden examples")
            .And.Contain("GET /beacon/api/eval/grounding/recheck");
    }

    // A project that learns from feedback while no enabled user is its ProjectAdmin is curated by Admins alone: named, by id.
    [Test]
    public async Task TheStartupNote_NamesLearningProjectsWithoutAnEnabledProjectAdmin()
    {
        var store = new Store();
        store.Projects.AddRange([new Project { Id = 3, Name = "three" }, new Project { Id = 4, Name = "four" }]);
        store.Users.AddRange([new BeaconUser { Id = 7, ExternalId = "ext-7", UserName = "ada" }, new BeaconUser { Id = 8, ExternalId = "ext-8", UserName = "ben", IsEnabled = false }]);
        store.Members.Add(new ProjectMember { ProjectId = ProjectId, UserId = 7, Role = ProjectRole.ProjectAdmin });
        store.Members.Add(new ProjectMember { ProjectId = OtherProjectId, UserId = 8, Role = ProjectRole.ProjectAdmin });
        store.Members.Add(new ProjectMember { ProjectId = 3, UserId = 7, Role = ProjectRole.Editor });
        var settings = SettingsProviderMock.Create(
            new McpSettingsData { EnableLearning = true },
            projectSettings: new Dictionary<int, McpSettingsData> { [4] = new() { EnableLearning = false } });
        using var logs = new LogRecorder();

        await Note(logs, userManagement: true).NoteAsync(Services(store, settings.Object), CancellationToken.None);

        logs.Entries.Should().ContainSingle().Which.Message.Should().StartWith(
            "2 live projects learn from MCP feedback and have no enabled ProjectAdmin member (projects 2,3)",
            "project 1 has one; project 2's is disabled; project 3 has only an Editor; project 4 does not learn");
    }

    [TestCase(false, true)]
    [TestCase(true, false)]
    public async Task TheStartupNote_SaysNothingAboutCurators_WithoutUserManagementOrLearning(bool userManagement, bool learning)
    {
        var store = new Store();
        using var logs = new LogRecorder();

        await Note(logs, userManagement).NoteAsync(
            Services(store, SettingsProviderMock.Create(new McpSettingsData { EnableLearning = learning }).Object),
            CancellationToken.None);

        logs.Entries.Should().BeEmpty();
    }

    [Test]
    public async Task TheStartupNote_NotesACountThatCannotBeRead_AndGoesOn()
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no database"));
        var services = new Mock<IServiceProvider>();
        services.Setup(x => x.GetService(typeof(IDbContextFactory<BeaconContext>))).Returns(factory.Object);
        services.Setup(x => x.GetService(typeof(IMcpSettingsProvider))).Returns(SettingsProviderMock.Create().Object);
        using var logs = new LogRecorder();

        await Note(logs, userManagement: true).NoteAsync(services.Object, CancellationToken.None);

        logs.Entries.Select(x => (x.Level, x.Message)).Should().Equal(
            (LogLevel.Information, "The golden cases without a recorded promoter could not be counted at startup (InvalidOperationException)"),
            (LogLevel.Information, "The learning projects without a project admin could not be counted at startup (InvalidOperationException)"));
    }

    private static RecheckGroundingHandler Handler(Store store)
    {
        return new RecheckGroundingHandler(store.Factory, TestGroundingSqlChecks.Real(), new LogRecorder().For<RecheckGroundingHandler>());
    }

    private static GroundingStartupNote Note(LogRecorder logs, bool userManagement)
    {
        var configuration = new BeaconConfiguration();
        configuration.UserManagement.Enabled = userManagement;

        return new GroundingStartupNote(logs.For<GroundingStartupNote>(), configuration);
    }

    private static IServiceProvider Services(Store store, IMcpSettingsProvider settings)
    {
        var services = new Mock<IServiceProvider>();
        services.Setup(x => x.GetService(typeof(IDbContextFactory<BeaconContext>))).Returns(store.Factory);
        services.Setup(x => x.GetService(typeof(IMcpSettingsProvider))).Returns(settings);

        return services.Object;
    }

    private static McpEvalCase Case(int id, string question, string goldSql)
    {
        return new McpEvalCase
        {
            Id = id,
            ProjectId = ProjectId,
            DataSourceId = DataSourceId,
            Question = question,
            GoldSql = goldSql,
            IsActive = true
        };
    }

    private static McpLearnedPattern Pattern(int id, McpPatternStatus status, string? question, string? sql)
    {
        return new McpLearnedPattern
        {
            Id = id,
            ProjectId = ProjectId,
            DataSourceId = DataSourceId,
            SchemaName = "public",
            TableName = "orders",
            PatternType = McpPatternType.CommonQuery,
            PatternContent = "Common query pattern for public.orders",
            ExampleQuestion = question,
            ExampleSql = sql,
            Status = status
        };
    }

    /// <summary>Projects 1 and 2 and the cases, patterns, users and memberships a test adds; whatever a context adds lands in <see cref="Saved"/>.</summary>
    private sealed class Store
    {
        public Store()
        {
            Projects.AddRange([new Project { Id = ProjectId, Name = "one" }, new Project { Id = OtherProjectId, Name = "two" }]);
            var factory = new Mock<IDbContextFactory<BeaconContext>>();
            factory
                .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new RecordingBeaconContext(
                    new Dictionary<Type, object>
                    {
                        [typeof(Project)] = RecordingBeaconContext.MemorySet(Projects, Saved),
                        [typeof(McpEvalCase)] = RecordingBeaconContext.MemorySet(Cases, Saved),
                        [typeof(McpLearnedPattern)] = RecordingBeaconContext.MemorySet(Patterns, Saved),
                        [typeof(ProjectMember)] = RecordingBeaconContext.MemorySet(Members, Saved),
                        [typeof(BeaconUser)] = RecordingBeaconContext.MemorySet(Users, Saved)
                    },
                    Saved));
            Factory = factory.Object;
        }

        public List<Project> Projects { get; } = [];

        public List<McpEvalCase> Cases { get; } = [];

        public List<McpLearnedPattern> Patterns { get; } = [];

        public List<ProjectMember> Members { get; } = [];

        public List<BeaconUser> Users { get; } = [];

        public List<object> Saved { get; } = [];

        public IDbContextFactory<BeaconContext> Factory { get; }
    }
}
