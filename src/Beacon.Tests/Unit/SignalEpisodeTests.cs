using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.MCP.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// Episodes (SC3-SC5): <see cref="McpSignalService.RecordSignalAsync"/> links a <c>query</c> or <c>dry_run</c> signal to the
/// <c>ask</c> signal it followed — the one its <c>signal_id</c> names when that is an ask of the same project and caller,
/// else the same caller's newest ask on the same project and data source within 15 minutes — and flags an edited re-run.
/// The stored signals sit behind a mocked <c>DbSet</c> backed by the async-queryable doubles (no database, §4.7), so the
/// service's own filters decide what matches.
/// </summary>
[TestFixture]
public class SignalEpisodeTests
{
    private const int SavedId = 900;
    private const int ProjectId = 7;
    private const int OtherProjectId = 8;
    private const int DataSourceId = 11;
    private const int OtherDataSourceId = 12;
    private const int UserId = 42;
    private const int OtherUserId = 43;
    private const string CallerHash = "c0ffee";
    private const string OtherCallerHash = "decaf0";
    private const string AskSql = "SELECT id FROM orders";
    private const string EditedSql = "SELECT id, total FROM orders";

    private static readonly DateTimeOffset Now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task ExplicitSignalId_OfOwnAsk_IsLinked()
    {
        // The named ask is older than the window and on another data source; a newer implicit candidate exists too.
        var stored = new[]
        {
            Ask(20, dataSourceId: OtherDataSourceId, minutesAgo: 120),
            Ask(30, minutesAgo: 1)
        };

        var (result, context) = await RecordAsync(Query(requestedParent: 20), stored);

        result.Should().Be(SavedId);
        context.Added.Should().ContainSingle().Which.ParentSignalId.Should().Be(20, "the named ask of the caller's own project wins");
    }

    [TestCase("query")]
    [TestCase("dry_run")]
    public async Task ExplicitSignalId_OfOwnAsk_IsLinkedForBothSqlTools(string tool)
    {
        var (_, context) = await RecordAsync(Query(tool: tool, requestedParent: 20), [Ask(20)]);

        context.Added.Should().ContainSingle().Which.ParentSignalId.Should().Be(20);
    }

    [Test]
    public async Task ExplicitSignalId_OfAnotherProject_IsIgnored_AndFallsBackToTheImplicitLink()
    {
        var stored = new[]
        {
            Ask(50, projectId: OtherProjectId, minutesAgo: 1),
            Ask(20, minutesAgo: 3)
        };

        var (result, context) = await RecordAsync(Query(requestedParent: 50), stored);

        result.Should().Be(SavedId, "an id outside the caller's project changes nothing the caller can observe");
        context.Added.Should().ContainSingle().Which.ParentSignalId.Should().Be(20, "the request is ignored and the implicit rule applies");
    }

    [Test]
    public async Task ExplicitSignalId_OfAnotherProject_WithoutImplicitCandidate_LinksNothing()
    {
        var (result, context) = await RecordAsync(Query(requestedParent: 50), [Ask(50, projectId: OtherProjectId)]);

        result.Should().Be(SavedId);
        var saved = context.Added.Should().ContainSingle().Subject;
        saved.ParentSignalId.Should().BeNull("the requested id is never stored unverified");
        saved.IsEditedRerun.Should().BeFalse();
    }

    // An API key or session caller is matched by user id; a mapped caller by its hash, whatever user the other ask has.
    [TestCase(null, OtherUserId, null)]
    [TestCase(CallerHash, UserId, OtherCallerHash)]
    public async Task ExplicitSignalId_OfAnotherCaller_IsIgnored(string? callerHash, int askUserId, string? askCallerHash)
    {
        var stored = new[] { Ask(20, userId: askUserId, callerHash: askCallerHash, dataSourceId: OtherDataSourceId) };

        var (result, context) = await RecordAsync(Query(callerHash: callerHash, requestedParent: 20), stored);

        result.Should().Be(SavedId);
        context.Added.Should().ContainSingle().Which.ParentSignalId.Should().BeNull("another caller's ask is never a parent");
    }

    [TestCase("query")]
    [TestCase("dry_run")]
    public async Task ExplicitSignalId_OfNonAskSignal_IsIgnored(string storedTool)
    {
        var stored = new[] { Ask(20, tool: storedTool) };

        var (result, context) = await RecordAsync(Query(requestedParent: 20), stored);

        result.Should().Be(SavedId);
        context.Added.Should().ContainSingle().Which.ParentSignalId.Should().BeNull("only an ask signal can be a parent");
    }

    [Test]
    public async Task Implicit_LinksTheNewestAskOfTheSameCallerProjectAndDataSource()
    {
        var stored = new[]
        {
            Ask(10, minutesAgo: 3),
            Ask(12, minutesAgo: 1),
            Ask(13, dataSourceId: OtherDataSourceId, minutesAgo: 0),
            Ask(14, projectId: OtherProjectId, minutesAgo: 0),
            Ask(15, userId: OtherUserId, minutesAgo: 0),
            Ask(16, tool: "query", minutesAgo: 0)
        };

        var (_, context) = await RecordAsync(Query(), stored);

        context.Added.Should().ContainSingle().Which.ParentSignalId.Should().Be(12);
    }

    [TestCase(14, true)]
    [TestCase(15, true)]
    [TestCase(16, false)]
    public async Task Implicit_LinksOnlyAnAskWithinFifteenMinutes(int minutesAgo, bool linked)
    {
        var (_, context) = await RecordAsync(Query(), [Ask(20, minutesAgo: minutesAgo)]);

        context.Added.Should().ContainSingle().Which.ParentSignalId.Should().Be(linked ? 20 : null);
    }

    [Test]
    public async Task Implicit_AskOnAnotherDataSource_IsNotLinked()
    {
        var (_, context) = await RecordAsync(Query(), [Ask(20, dataSourceId: OtherDataSourceId)]);

        context.Added.Should().ContainSingle().Which.ParentSignalId.Should().BeNull();
    }

    [Test]
    public async Task SignalWithoutDataSource_HasNoImplicitLink_ButKeepsAnExplicitOne()
    {
        var (_, implicitContext) = await RecordAsync(Query(dataSourceId: null), [Ask(20)]);
        var (_, explicitContext) = await RecordAsync(Query(dataSourceId: null, requestedParent: 20), [Ask(20)]);

        implicitContext.Added.Should().ContainSingle().Which.ParentSignalId.Should().BeNull();
        explicitContext.Added.Should().ContainSingle().Which.ParentSignalId.Should().Be(20);
    }

    [Test]
    public async Task MappedCaller_IsMatchedByCallerHash()
    {
        var stored = new[]
        {
            Ask(21, callerHash: OtherCallerHash, minutesAgo: 1),
            Ask(20, callerHash: CallerHash, minutesAgo: 3)
        };

        var (_, context) = await RecordAsync(Query(callerHash: CallerHash), stored);

        var saved = context.Added.Should().ContainSingle().Subject;
        saved.CallerHash.Should().Be(CallerHash, "the caller hash is stored on the signal (SC6)");
        saved.ParentSignalId.Should().Be(20, "a mapped caller is the same caller only under the same hash, whatever its user id");
    }

    [Test]
    public async Task ApiKeyOrSessionCaller_IsMatchedByUserId()
    {
        var stored = new[]
        {
            Ask(21, userId: OtherUserId, minutesAgo: 1),
            Ask(20, userId: UserId, minutesAgo: 3)
        };

        var (_, context) = await RecordAsync(Query(), stored);

        var saved = context.Added.Should().ContainSingle().Subject;
        saved.CallerHash.Should().BeNull();
        saved.ParentSignalId.Should().Be(20);
    }

    [Test]
    public async Task CallerWithoutHashOrUser_LinksNothing_AndRunsNoLookup()
    {
        var stored = new[] { Ask(20, userId: null) };

        var (result, context) = await RecordAsync(Query(userId: null, requestedParent: 20), stored);

        result.Should().Be(SavedId, "the signal is still recorded");
        context.Added.Should().ContainSingle().Which.ParentSignalId.Should().BeNull();
        context.QueryRootReads.Should().Be(0, "a signal without a caller identity runs no parent lookup");
    }

    [Test]
    public async Task SignalWithoutProject_LinksNothing_AndRunsNoLookup()
    {
        var (_, context) = await RecordAsync(Query(projectId: null, requestedParent: 20), [Ask(20)]);

        context.Added.Should().ContainSingle().Which.ParentSignalId.Should().BeNull();
        context.QueryRootReads.Should().Be(0);
    }

    [Test]
    public async Task AskSignal_NeverGetsAParent_EvenWhenOneIsRequested()
    {
        var signal = Query(tool: "ask", requestedParent: 20);
        signal.IsEditedRerun = true;

        var (_, context) = await RecordAsync(signal, [Ask(20)]);

        var saved = context.Added.Should().ContainSingle().Subject;
        saved.ParentSignalId.Should().BeNull();
        saved.IsEditedRerun.Should().BeFalse();
        context.QueryRootReads.Should().Be(0);
    }

    // SC5: edited only when both digests exist and differ.
    [TestCase(AskSql, AskSql, false)]
    [TestCase(AskSql, EditedSql, true)]
    [TestCase(null, EditedSql, false)]
    [TestCase(AskSql, null, false)]
    public async Task EditedFlag_ComparesTheCandidateSqlDigests(string? askSql, string? querySql, bool edited)
    {
        var stored = new[] { Ask(20, candidateSql: askSql) };

        var (_, context) = await RecordAsync(Query(sql: querySql), stored);

        var saved = context.Added.Should().ContainSingle().Subject;
        saved.ParentSignalId.Should().Be(20);
        saved.IsEditedRerun.Should().Be(edited);
    }

    [Test]
    public async Task ContentLock_KeepsTheLink_ButNeverTheEditedFlag()
    {
        var settings = new McpSettingsData { EnableLearning = true, RetainQueryContent = false };

        var (_, context) = await RecordAsync(Query(sql: EditedSql), [Ask(20)], settings);

        var saved = context.Added.Should().ContainSingle().Subject;
        saved.ParentSignalId.Should().Be(20, "the link is resolved after redaction and holds no content");
        saved.IsEditedRerun.Should().BeFalse("redaction removed the digest, so the edit cannot be told");
        saved.CandidateSqlHash.Should().BeNull();
    }

    [Test]
    public async Task FailingLookup_StillSavesTheSignal_Unlinked()
    {
        var logs = new LogRecorder();
        var failure = new InvalidOperationException($"lookup failed near {EditedSql}");

        var (result, context) = await RecordAsync(Query(sql: EditedSql, requestedParent: 20), [Ask(20)], logs: logs, lookupFailure: failure);

        result.Should().Be(SavedId, "a failed lookup never costs the signal");
        context.SaveCount.Should().Be(1);
        context.Added.Should().ContainSingle().Which.ParentSignalId.Should().BeNull();
        logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning);
        logs.Entries.Should().NotContain(x => x.Level == LogLevel.Error);
        logs.Entries.Should().OnlyContain(x => x.Exception == null, "failures are logged as codes, never as the exception (§1.11)");
        logs.Contains(EditedSql).Should().BeFalse("no SQL reaches the log (§1.11)");
    }

    [Test]
    public async Task CallerCancellation_DuringLookup_FollowsTheSavePath()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var logs = new LogRecorder();

        var (result, context) = await RecordAsync(
            Query(),
            [Ask(20)],
            logs: logs,
            lookupFailure: new OperationCanceledException(cancellation.Token),
            cancellationToken: cancellation.Token);

        result.Should().BeNull("the caller's cancellation is handled as the save path handles it");
        context.Added.Should().BeEmpty();
        logs.Entries.Should().NotContain(x => x.Level == LogLevel.Warning, "a cancellation is not a failed lookup");
        logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Error);
    }

    private static McpQuerySignal Ask(
        int id,
        int projectId = ProjectId,
        int? dataSourceId = DataSourceId,
        int? userId = UserId,
        string? callerHash = null,
        int minutesAgo = 1,
        string tool = "ask",
        string? candidateSql = AskSql)
    {
        return new McpQuerySignal
        {
            Id = id,
            Tool = tool,
            Question = "how many orders?",
            ProjectId = projectId,
            DataSourceId = dataSourceId,
            UserId = userId,
            CallerHash = callerHash,
            CreatedTime = Now.UtcDateTime.AddMinutes(-minutesAgo),
            CandidateSqlHash = candidateSql == null ? null : GroundingContentHash.OfSql(candidateSql)
        };
    }

    // Built through the builder as the query and dry_run tools build it: the requested parent travels on ParentSignalId.
    private static McpQuerySignal Query(
        string tool = "query",
        int? projectId = ProjectId,
        int? dataSourceId = DataSourceId,
        int? userId = UserId,
        string? callerHash = null,
        int? requestedParent = null,
        string? sql = AskSql)
    {
        var builder = new McpSignalBuilder()
            .SetTool(tool)
            .SetQuestion(sql ?? "")
            .SetProjectId(projectId)
            .SetDataSourceId(dataSourceId)
            .SetUserId(userId)
            .SetCallerHash(callerHash)
            .SetRequestedParent(requestedParent);
        if (sql != null)
        {
            builder.SetGeneratedSql(sql);
        }

        return builder.Build();
    }

    private static async Task<(int? Result, EpisodeContext Context)> RecordAsync(
        McpQuerySignal signal,
        IEnumerable<McpQuerySignal> stored,
        McpSettingsData? settings = null,
        LogRecorder? logs = null,
        Exception? lookupFailure = null,
        CancellationToken cancellationToken = default)
    {
        var context = new EpisodeContext(stored, lookupFailure);
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(context);
        var settingsProvider = SettingsProviderMock.Create(settings ?? new McpSettingsData { EnableLearning = true, RetainQueryContent = true });
        var service = new McpSignalService(
            factory.Object,
            settingsProvider.Object,
            (logs ?? new LogRecorder()).For<McpSignalService>(),
            new FakeTimeProvider(Now));

        var result = await service.RecordSignalAsync(signal, cancellationToken);

        return (result, context);
    }

    /// <summary>
    /// A <see cref="BeaconContext"/> whose <c>McpQuerySignals</c> set reads the stored signals through the async-queryable
    /// doubles, counts the queries composed on it, records added signals apart from the stored ones and fakes the save.
    /// </summary>
    private sealed class EpisodeContext : BeaconContext
    {
        private static readonly DbContextOptions<EpisodeContext> Options =
            new DbContextOptionsBuilder<EpisodeContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly Mock<DbSet<McpQuerySignal>> _set = new();

        public EpisodeContext(IEnumerable<McpQuerySignal> stored, Exception? lookupFailure) : base(Options, "beacon")
        {
            var data = stored
                .ToList()
                .AsQueryable();
            _set.As<IAsyncEnumerable<McpQuerySignal>>()
                .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
                .Returns(() => new TestAsyncEnumerator<McpQuerySignal>(data.GetEnumerator()));
            _set.As<IQueryable<McpQuerySignal>>()
                .Setup(x => x.Provider)
                .Returns(() =>
                {
                    QueryRootReads++;
                    if (lookupFailure != null)
                    {
                        throw lookupFailure;
                    }

                    return new TestAsyncQueryProvider<McpQuerySignal>(data.Provider);
                });
            _set.As<IQueryable<McpQuerySignal>>().Setup(x => x.Expression).Returns(data.Expression);
            _set.As<IQueryable<McpQuerySignal>>().Setup(x => x.ElementType).Returns(data.ElementType);
            _set.As<IQueryable<McpQuerySignal>>().Setup(x => x.GetEnumerator()).Returns(() => data.GetEnumerator());
            _set.Setup(x => x.Add(It.IsAny<McpQuerySignal>()))
                .Callback<McpQuerySignal>(x =>
                {
                    x.Id = SavedId;
                    Added.Add(x);
                });
        }

        public List<McpQuerySignal> Added { get; } = [];

        public int QueryRootReads { get; private set; }

        public int SaveCount { get; private set; }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class =>
            typeof(TEntity) == typeof(McpQuerySignal)
                ? (DbSet<TEntity>)(object)_set.Object
                : base.Set<TEntity>();

        public override int SaveChanges() => ++SaveCount;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return Task.FromResult(1);
        }
    }
}
