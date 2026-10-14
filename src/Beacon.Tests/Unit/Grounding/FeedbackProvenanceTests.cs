using Beacon.AI.Services.Learning;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.McpEval;
using Beacon.Core.Mcp;
using Beacon.Core.Services;
using Beacon.Core.Services.Retention;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Grounding;

/// <summary>
/// Where a verdict came from is recorded apart from who it is attributed to: <see cref="McpQuerySignal.FeedbackFromCurator"/>
/// is true only for a verdict an interactive session recorded while holding Curate in the signal's project — a project admin
/// member or an Admin, signed in — and only such a verdict weighs in mining (the preferred example and the confidence
/// bonus). An API key or an MCP caller of a ProjectAdmin or an Admin records a verdict attributed to that user, which weighs
/// nothing; so does an Editor's. The caller's scope is the one the real evaluator gives (no database — §4.7). The deployment:
/// open project 1 linking data source 7; user 7 is its ProjectAdmin, user 8 an Editor, user 9 an Admin.
/// </summary>
[TestFixture]
public class FeedbackProvenanceTests
{
    private const int ProjectId = 1;
    private const int DataSourceId = TestGroundingSqlChecks.DataSourceId;
    private const int ProjectAdminUserId = 7;
    private const int EditorUserId = 8;
    private const int AdminUserId = 9;

    private static IEnumerable<TestCaseData> Callers()
    {
        yield return new TestCaseData(Session(ProjectAdminUserId), true).SetArgDisplayNames("a signed-in ProjectAdmin");
        yield return new TestCaseData(Session(AdminUserId) with { IsLiveAdmin = true }, true).SetArgDisplayNames("a signed-in Admin");
        yield return new TestCaseData(Session(EditorUserId), false).SetArgDisplayNames("a signed-in Editor");
        yield return new TestCaseData(ApiKeyOf(ProjectAdminUserId), false).SetArgDisplayNames("an API key of a ProjectAdmin");
        yield return new TestCaseData(ApiKeyOf(AdminUserId) with { IsLiveAdmin = true }, false).SetArgDisplayNames("an API key of an Admin");
        yield return new TestCaseData(McpCallerOf(ProjectAdminUserId), false).SetArgDisplayNames("an MCP caller of a ProjectAdmin");
        yield return new TestCaseData(McpCallerOf(AdminUserId) with { IsLiveAdmin = true }, false).SetArgDisplayNames("an MCP caller of an Admin");
    }

    [TestCaseSource(nameof(Callers))]
    public async Task AVerdict_IsACuratorsOnlyFromAnInteractiveSessionHoldingCurate_AndAttributedToItsUserEitherWay(BeaconActor caller, bool fromCurator)
    {
        var store = new Store(Signal(1, askedBy: caller.BeaconUserId));

        await Handler(store, caller).Handle(new RecordQueryFeedbackCommand(1, McpUserVerdict.Correct), CancellationToken.None);

        store.Signals.Single().Should().BeEquivalentTo(
            new { UserVerdict = McpUserVerdict.Correct, FeedbackByUserId = caller.BeaconUserId, FeedbackFromCurator = fromCurator },
            x => x.ExcludingMissingMembers());
    }

    // The verdict then weighs in mining only as a curator's: the answer becomes its pattern's preferred example and the
    // pattern earns the confidence bonus, or neither.
    [TestCaseSource(nameof(Callers))]
    public async Task AVerdict_WeighsInMining_OnlyAsACuratorsVerdict(BeaconActor caller, bool fromCurator)
    {
        var store = new Store(Signal(1, askedBy: 99, question: "orders by region"), Signal(2, askedBy: caller.BeaconUserId, question: "the rated question"), Signal(3, askedBy: 99, question: "orders by week"));
        await Handler(store, caller).Handle(new RecordQueryFeedbackCommand(2, McpUserVerdict.Correct), CancellationToken.None);
        var saved = new List<object>();
        var context = new RecordingBeaconContext(
            new Dictionary<Type, object> { [typeof(McpLearnedPattern)] = RecordingBeaconContext.MemorySet(new List<McpLearnedPattern>(), saved) },
            saved);

        await Aggregation().DetectCommonQueriesAsync(context, ProjectId, DataSourceId, store.Signals, new McpLearningAggregationService.ExtractionStats(), CancellationToken.None);

        var pattern = saved.OfType<McpLearnedPattern>().Should().ContainSingle().Subject;
        pattern.Confidence.Should().BeApproximately(fromCurator ? 0.9 : 0.7, 0.0001);
        pattern.ExampleQuestion.Should().Be(fromCurator ? "the rated question" : "orders by region");
    }

    // Provenance is an interactive session's whatever scope a caller is given: an API key, an MCP caller or the owner of
    // scheduled work never records a curator's verdict, even with a scope that curates.
    [Test]
    public async Task ANonInteractiveCaller_NeverRecordsACuratorsVerdict_EvenWithAScopeThatCurates()
    {
        foreach (var kind in new[] { BeaconPrincipalKind.ApiKey, BeaconPrincipalKind.McpCaller, BeaconPrincipalKind.Owner, BeaconPrincipalKind.System })
        {
            var caller = Session(ProjectAdminUserId) with { Kind = kind };
            var store = new Store(Signal(1, askedBy: ProjectAdminUserId));

            await Handler(store, caller, TestProjectAccess.Evaluator(TestProjectAccess.Unrestricted)).Handle(new RecordQueryFeedbackCommand(1, McpUserVerdict.Correct), CancellationToken.None);

            store.Signals.Single().Should().BeEquivalentTo(
                new { FeedbackByUserId = (int?)ProjectAdminUserId, FeedbackFromCurator = false },
                x => x.ExcludingMissingMembers(),
                $"a {kind} caller is not an interactive session");
        }
    }

    // A later verdict replaces the provenance with its own: a curator's verdict the asker then overrides no longer weighs.
    [Test]
    public async Task ALaterVerdict_RecordsItsOwnProvenance()
    {
        var store = new Store(Signal(1, askedBy: EditorUserId));

        await Handler(store, Session(ProjectAdminUserId)).Handle(new RecordQueryFeedbackCommand(1, McpUserVerdict.Correct), CancellationToken.None);
        var byCurator = store.Signals.Single().FeedbackFromCurator;
        await Handler(store, Session(EditorUserId)).Handle(new RecordQueryFeedbackCommand(1, McpUserVerdict.Correct), CancellationToken.None);

        byCurator.Should().BeTrue();
        store.Signals.Single().Should().BeEquivalentTo(new { FeedbackByUserId = (int?)EditorUserId, FeedbackFromCurator = false }, x => x.ExcludingMissingMembers());
    }

    private static RecordQueryFeedbackHandler Handler(Store store, BeaconActor caller, IProjectAccessEvaluator? access = null)
    {
        var actors = new Mock<IBeaconActorAccessor>();
        actors
            .Setup(x => x.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(caller);
        var policy = new Mock<IContentRetentionPolicy>();
        policy
            .Setup(x => x.ResolveAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentRetentionDecision(true, true));

        return new RecordQueryFeedbackHandler(
            store.Factory,
            TestGroundingSqlChecks.PassThrough().Object,
            policy.Object,
            access ?? TestProjectAccess.RealEvaluator(caller, store.Factory),
            actors.Object,
            NullLogger<RecordQueryFeedbackHandler>.Instance);
    }

    private static McpLearningAggregationService Aggregation()
    {
        return new McpLearningAggregationService(
            Mock.Of<IDbContextFactory<BeaconContext>>(),
            SettingsProviderMock.Create().Object,
            TestGroundingSqlChecks.PassThrough().Object,
            NullLogger<McpLearningAggregationService>.Instance);
    }

    private static BeaconActor Session(int beaconUserId)
    {
        return new BeaconActor($"ext-{beaconUserId}", false)
        {
            Kind = BeaconPrincipalKind.User,
            BeaconUserId = beaconUserId,
            IsActiveUser = true,
            RoleLevel = 2
        };
    }

    private static BeaconActor ApiKeyOf(int beaconUserId)
    {
        return new BeaconActor($"ext-{beaconUserId}", false)
        {
            Kind = BeaconPrincipalKind.ApiKey,
            IsApiKey = true,
            BeaconUserId = beaconUserId,
            IsActiveUser = true,
            ScopeCeiling = McpCallerScope.Execute
        };
    }

    private static BeaconActor McpCallerOf(int beaconUserId)
    {
        return new BeaconActor($"ext-{beaconUserId}", false)
        {
            Kind = BeaconPrincipalKind.McpCaller,
            BeaconUserId = beaconUserId,
            IsActiveUser = true,
            AllowedProjectIds = [ProjectId],
            ScopeCeiling = McpCallerScope.Execute,
            CallerKind = McpCallerKind.User
        };
    }

    private static McpQuerySignal Signal(int id, int? askedBy, string question = "orders last week")
    {
        return new McpQuerySignal
        {
            Id = id,
            ProjectId = ProjectId,
            DataSourceId = DataSourceId,
            UserId = askedBy,
            Tool = "ask",
            Question = question,
            GeneratedSql = "SELECT id FROM public.orders",
            TablesUsed = "[\"public.orders\"]",
            IsSuccessful = true
        };
    }

    /// <summary>The deployment and its signals, served by every context the factory hands out.</summary>
    private sealed class Store
    {
        public Store(params McpQuerySignal[] signals)
        {
            Signals = signals.ToList();
            var project = new Project { Id = ProjectId, Name = "sales" };
            var source = new DataSource { Id = DataSourceId, Name = "warehouse", DataSourceType = DataSourceType.Database, EncryptedConnectionData = "enc" };
            var links = new List<ProjectDataSource> { new() { ProjectId = ProjectId, DataSourceId = DataSourceId, Project = project, DataSource = source } };
            var members = new List<ProjectMember>
            {
                new() { ProjectId = ProjectId, UserId = ProjectAdminUserId, Role = ProjectRole.ProjectAdmin },
                new() { ProjectId = ProjectId, UserId = EditorUserId, Role = ProjectRole.Editor }
            };
            var saved = new List<object>();
            var factory = new Mock<IDbContextFactory<BeaconContext>>();
            factory
                .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new RecordingBeaconContext(
                    new Dictionary<Type, object>
                    {
                        [typeof(Project)] = RecordingBeaconContext.MemorySet([project], saved),
                        [typeof(DataSource)] = RecordingBeaconContext.MemorySet([source], saved),
                        [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet(links, saved),
                        [typeof(ProjectMember)] = RecordingBeaconContext.MemorySet(members, saved),
                        [typeof(McpQuerySignal)] = RecordingBeaconContext.MemorySet(Signals, saved)
                    },
                    saved));
            Factory = factory.Object;
        }

        public List<McpQuerySignal> Signals { get; }

        public IDbContextFactory<BeaconContext> Factory { get; }
    }
}
