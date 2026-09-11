using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.McpEval;
using Beacon.Core.Models;
using Beacon.Core.Services.Retention;
using Beacon.MCP.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// SC1 — end-to-end proof of the content lock across the three write-path braces (§1.7/§9.5/R12): a locked
/// project's <c>ask</c>-style signal (<see cref="McpSignalService"/>), <c>query</c> audit row
/// (<see cref="McpAuditService"/>) and <c>feedback</c> verdict (<see cref="RecordQueryFeedbackHandler"/>) all
/// persist structure only — reflected against <see cref="McpRetentionDenyList"/> so a future unclassified
/// column would fail this test too. Every real service runs over the async-queryable / change-tracking
/// doubles used elsewhere in this suite — no database, no forbidden <c>UseInMemoryDatabase</c> (§4.7). The
/// control case (lock off) proves the same flow keeps content when the project does not lock it.
/// </summary>
[TestFixture]
public class RetentionLockIntegrationTests
{
    private const int ProjectId = 11;
    private const int DataSourceId = 7;
    private const string Question = "What is our total revenue this year?";
    private const string GeneratedSql = "SELECT sum(revenue) FROM orders";
    private const string SchemaError = "column \"revenue\" does not exist";
    private const string ExecutionError = "permission denied for relation orders";
    private const string CorrectedSql = "SELECT sum(total) FROM orders";
    private const string FeedbackCorrectedSql = "SELECT sum(total) FROM orders WHERE region = 'EU'";
    private const string FeedbackNote = "forgot the region filter";

    [Test]
    public async Task Locked_PersistsStructureOnly_NoPromotion()
    {
        var settings = LockedSettings();
        var mediator = new Mock<ISender>(MockBehavior.Strict);

        var signal = await RecordSignalAsync(settings);
        var auditLog = await RecordAuditAsync(settings);
        var feedbackSignal = await RecordFeedbackAsync(settings, mediator);

        AssertContentLockCompliant(signal);
        AssertContentLockCompliant(auditLog);
        AssertContentLockCompliant(feedbackSignal);

        McpContentRedactor.IsStructuralAuditParameters(auditLog.Parameters).Should().BeTrue(
            "under the lock Parameters must be the structural JSON form");
        using (var document = JsonDocument.Parse(auditLog.Parameters!))
        {
            document.RootElement.GetProperty("tool").GetString().Should().Be("query");
            document.RootElement.GetProperty("tables").EnumerateArray().Select(x => x.GetString())
                .Should().BeEquivalentTo(["orders"]);
        }

        feedbackSignal.UserCorrectedSql.Should().BeNull();
        feedbackSignal.FeedbackNote.Should().BeNull();

        mediator.Verify(
            x => x.Send(It.IsAny<PromoteSignalToGoldenCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task Unlocked_RetainsContent_PromotesOnce()
    {
        var settings = UnlockedSettings();
        var mediator = new Mock<ISender>();
        mediator
            .Setup(x => x.Send(It.IsAny<PromoteSignalToGoldenCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PromoteSignalToGoldenResult(500));

        var signal = await RecordSignalAsync(settings);
        var auditLog = await RecordAuditAsync(settings);
        var feedbackSignal = await RecordFeedbackAsync(settings, mediator);

        signal.Question.Should().Be(Question);
        signal.GeneratedSql.Should().Be(GeneratedSql);
        signal.CorrectedSql.Should().Be(CorrectedSql);
        signal.SchemaValidationError.Should().Be(SchemaError);
        signal.ExecutionError.Should().Be(ExecutionError);

        auditLog.Parameters.Should().Be(GeneratedSql);
        auditLog.ErrorMessage.Should().Be(ExecutionError);

        feedbackSignal.UserCorrectedSql.Should().Be(FeedbackCorrectedSql);
        feedbackSignal.FeedbackNote.Should().Be(FeedbackNote);

        mediator.Verify(
            x => x.Send(It.IsAny<PromoteSignalToGoldenCommand>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static McpSettingsData LockedSettings() =>
        new() { EnableLearning = true, RetainQueryContent = false, AllowExplicitFeedbackContent = true };

    private static McpSettingsData UnlockedSettings() =>
        new() { EnableLearning = true, RetainQueryContent = true, AllowExplicitFeedbackContent = true };

    private static async Task<McpQuerySignal> RecordSignalAsync(McpSettingsData settings)
    {
        var captured = new List<McpQuerySignal>();
        var context = new SignalCapturingContext(captured);
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(context);

        var settingsProvider = SettingsProviderMock.Create(settings);
        var service = new McpSignalService(factory.Object, settingsProvider.Object, NullLogger<McpSignalService>.Instance);

        var signal = new McpSignalBuilder()
            .SetTool("ask")
            .SetQuestion(Question)
            .SetProjectId(ProjectId)
            .SetDataSourceId(DataSourceId)
            .SetUserId(1)
            .SetGeneratedSql(GeneratedSql, ["orders"])
            .SetSchemaValidationFailed(SchemaError)
            .SetExecutionFailed(ExecutionError)
            .SetRetry(CorrectedSql, true)
            .SetResult(null, 42, false)
            .Build();

        var id = await service.RecordSignalAsync(signal, CancellationToken.None);

        id.Should().NotBeNull("learning is enabled, so the signal must persist regardless of the lock");
        captured.Should().ContainSingle("exactly one audit row must exist whether or not the lock is on (§1.7)");
        return captured[0];
    }

    private static async Task<McpAuditLog> RecordAuditAsync(McpSettingsData settings)
    {
        var captured = new List<McpAuditLog>();
        var context = new AuditCapturingContext(captured);
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(context);

        var settingsProvider = SettingsProviderMock.Create(settings);
        var service = new McpAuditService(factory.Object, settingsProvider.Object, NullLogger<McpAuditService>.Instance);

        await service.LogToolCallAsync(
            sessionId: null,
            userId: 1,
            tool: "query",
            parameters: GeneratedSql,
            dataSourceId: DataSourceId,
            projectId: ProjectId,
            executionTimeMs: 42,
            resultRowCount: null,
            errorMessage: ExecutionError,
            tables: ["orders"],
            ct: CancellationToken.None);

        captured.Should().ContainSingle("exactly one audit row must exist whether or not the lock is on (§1.7)");
        return captured[0];
    }

    private static async Task<McpQuerySignal> RecordFeedbackAsync(McpSettingsData settings, Mock<ISender> mediator)
    {
        // The rated signal is whatever the signal path already persisted for this project: under the lock it was
        // written redacted (RecordSignalAsync above proves that), so the row the feedback handler loads carries no
        // content to begin with. Re-redacting an OLDER row written before the lock was switched on is deliberately
        // out of scope (spec "Out of scope"); the interceptor belt covers a modified row in production.
        var locked = !settings.RetainQueryContent;
        var seededSignal = new McpQuerySignal
        {
            Id = 900,
            ProjectId = ProjectId,
            DataSourceId = DataSourceId,
            Tool = "ask",
            Question = locked ? string.Empty : Question,
            GeneratedSql = locked ? null : GeneratedSql
        };

        var factory = BuildFeedbackFactory([seededSignal], []);

        var policy = new Mock<IContentRetentionPolicy>();
        policy
            .Setup(x => x.ResolveAsync(ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ContentRetentionDecision.From(settings));

        var handler = new RecordQueryFeedbackHandler(factory, mediator.Object, policy.Object, NullLogger<RecordQueryFeedbackHandler>.Instance);

        await handler.Handle(
            new RecordQueryFeedbackCommand(seededSignal.Id, McpUserVerdict.Correct, FeedbackCorrectedSql, FeedbackNote),
            CancellationToken.None);

        return seededSignal;
    }

    private static void AssertContentLockCompliant(object entity)
    {
        var type = entity.GetType();
        var rules = McpRetentionDenyList.RulesFor(type).ToDictionary(x => x.Property, x => x.Kind);

        foreach (var property in McpRetentionDenyList.StringProperties(type))
        {
            rules.Should().ContainKey(property.Name, $"{type.Name}.{property.Name} must be classified in the registry");
            var value = (string?)property.GetValue(entity);

            switch (rules[property.Name])
            {
                case RetentionKind.Structural:
                    // Structure, configuration or documentation — any value is allowed under the lock.
                    break;
                case RetentionKind.ErrorClass:
                    (value == null || McpContentRedactor.ErrorClasses.Contains(value)).Should().BeTrue(
                        $"{type.Name}.{property.Name} must be null or an error class under the lock, was '{value}'");
                    break;
                case RetentionKind.Content:
                    // McpAuditLog.Parameters is the one Content property whose redacted form is not
                    // null/"" but the structural JSON wrapper (spec Architecture: "the structural JSON
                    // form is the redacted value; the belt leaves a value that already passes
                    // IsStructuralAuditParameters").
                    var isRedactedAuditParameters = type == typeof(McpAuditLog)
                        && property.Name == nameof(McpAuditLog.Parameters)
                        && McpContentRedactor.IsStructuralAuditParameters(value);
                    (value == null || value.Length == 0 || isRedactedAuditParameters).Should().BeTrue(
                        $"{type.Name}.{property.Name} must be null, empty, or the structural JSON form under the lock, was '{value}'");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(entity), $"Unhandled retention kind for {type.Name}.{property.Name}");
            }
        }
    }

    private static IDbContextFactory<BeaconContext> BuildFeedbackFactory(
        List<McpQuerySignal> signals, List<McpEvalCase> cases)
    {
        var context = new FeedbackContext(BuildDbSet(signals).Object, BuildDbSet(cases).Object);
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(context);
        return factory.Object;
    }

    private static Mock<DbSet<T>> BuildDbSet<T>(IEnumerable<T> data) where T : class
    {
        var queryable = data.AsQueryable();
        var set = new Mock<DbSet<T>>();
        set.As<IAsyncEnumerable<T>>()
            .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
            .Returns(() => new TestAsyncEnumerator<T>(data.GetEnumerator()));
        set.As<IQueryable<T>>().Setup(x => x.Provider).Returns(new TestAsyncQueryProvider<T>(queryable.Provider));
        set.As<IQueryable<T>>().Setup(x => x.Expression).Returns(queryable.Expression);
        set.As<IQueryable<T>>().Setup(x => x.ElementType).Returns(queryable.ElementType);
        set.As<IQueryable<T>>().Setup(x => x.GetEnumerator()).Returns(() => data.GetEnumerator());
        return set;
    }

    /// <summary>Captures every <see cref="McpQuerySignal"/> added through the real service — the redaction
    /// (or lack of it) happens on the object before <c>Add</c>, so the captured instance is the final word.</summary>
    private sealed class SignalCapturingContext : BeaconContext
    {
        private static readonly DbContextOptions<SignalCapturingContext> Options =
            new DbContextOptionsBuilder<SignalCapturingContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly Mock<DbSet<McpQuerySignal>> _set = new();

        public SignalCapturingContext(List<McpQuerySignal> signals) : base(Options, "beacon")
        {
            _set.Setup(x => x.Add(It.IsAny<McpQuerySignal>())).Callback<McpQuerySignal>(signals.Add);
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class =>
            typeof(TEntity) == typeof(McpQuerySignal)
                ? (DbSet<TEntity>)(object)_set.Object
                : base.Set<TEntity>();

        public override int SaveChanges() => 0;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    /// <summary>Captures every <see cref="McpAuditLog"/> added through the real audit service.</summary>
    private sealed class AuditCapturingContext : BeaconContext
    {
        private static readonly DbContextOptions<AuditCapturingContext> Options =
            new DbContextOptionsBuilder<AuditCapturingContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly Mock<DbSet<McpAuditLog>> _set = new();

        public AuditCapturingContext(List<McpAuditLog> logs) : base(Options, "beacon")
        {
            _set.Setup(x => x.Add(It.IsAny<McpAuditLog>())).Callback<McpAuditLog>(logs.Add);
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class =>
            typeof(TEntity) == typeof(McpAuditLog)
                ? (DbSet<TEntity>)(object)_set.Object
                : base.Set<TEntity>();

        public override int SaveChanges() => 0;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    /// <summary>A <see cref="BeaconContext"/> whose query-signal and eval-case sets resolve to the supplied
    /// mocked sets — mirrors <c>RecordQueryFeedbackHandlerTests.FeedbackHandlerContext</c>.</summary>
    private sealed class FeedbackContext : BeaconContext
    {
        private static readonly DbContextOptions<FeedbackContext> Options =
            new DbContextOptionsBuilder<FeedbackContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly DbSet<McpQuerySignal> _signals;
        private readonly DbSet<McpEvalCase> _cases;

        public FeedbackContext(DbSet<McpQuerySignal> signals, DbSet<McpEvalCase> cases)
            : base(Options, "beacon")
        {
            _signals = signals;
            _cases = cases;
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(McpQuerySignal))
            {
                return (DbSet<TEntity>)(object)_signals;
            }

            if (typeof(TEntity) == typeof(McpEvalCase))
            {
                return (DbSet<TEntity>)(object)_cases;
            }

            return base.Set<TEntity>();
        }

        public override int SaveChanges() => 0;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }
}
