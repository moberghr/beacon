using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.McpEval;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// Part B coverage for <see cref="RecordQueryFeedbackHandler"/>: the human correctness feedback loop.
/// A <see cref="McpUserVerdict.Correct"/> verdict on a project- and data-source-scoped signal that carries
/// SQL auto-promotes it into a golden case exactly once (idempotent per source signal); any other verdict,
/// or a signal missing its scope, records the verdict without promoting. Exercises the real handler against
/// a mocked <see cref="BeaconContext"/> backed by the async-queryable doubles (no DB, no forbidden
/// <c>UseInMemoryDatabase</c> — §4.7); the promotion is observed through a mocked <see cref="ISender"/>.
/// </summary>
[TestFixture]
public class RecordQueryFeedbackHandlerTests
{
    private const int ProjectId = 3;
    private const int DataSourceId = 7;

    [Test]
    public async Task Correct_PromotesGolden_Idempotent()
    {
        var signal = NewSignal(1, ProjectId, DataSourceId, generatedSql: "SELECT 1");

        var mediator = new Mock<ISender>();
        mediator
            .Setup(x => x.Send(It.IsAny<PromoteSignalToGoldenCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PromoteSignalToGoldenResult(99));

        var handler = new RecordQueryFeedbackHandler(
            BuildFactory(new List<McpQuerySignal> { signal }, new List<McpEvalCase>()), mediator.Object);

        await handler.Handle(
            new RecordQueryFeedbackCommand(1, McpUserVerdict.Correct, CorrectedSql: "SELECT 2", Note: "human fix"),
            CancellationToken.None);

        // The verdict fields are written onto the signal before any promotion.
        signal.UserVerdict.Should().Be(McpUserVerdict.Correct);
        signal.UserCorrectedSql.Should().Be("SELECT 2");
        signal.FeedbackNote.Should().Be("human fix");

        // A Correct verdict on a scoped signal with SQL promotes exactly once, forwarding the note.
        mediator.Verify(
            x => x.Send(
                It.Is<PromoteSignalToGoldenCommand>(c => c.SignalId == 1 && c.Notes == "human fix"),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Second call once a golden case already exists for the signal: the idempotency guard short-circuits.
        var promotedCase = new McpEvalCase
        {
            Id = 50,
            ProjectId = ProjectId,
            DataSourceId = DataSourceId,
            Question = signal.Question,
            GoldSql = "SELECT 2",
            SourceSignalId = 1
        };
        var idempotentHandler = new RecordQueryFeedbackHandler(
            BuildFactory(new List<McpQuerySignal> { signal }, new List<McpEvalCase> { promotedCase }), mediator.Object);

        await idempotentHandler.Handle(
            new RecordQueryFeedbackCommand(1, McpUserVerdict.Correct, CorrectedSql: "SELECT 2", Note: "human fix"),
            CancellationToken.None);

        // Still exactly one promotion in total — the second call did NOT re-promote.
        mediator.Verify(
            x => x.Send(
                It.Is<PromoteSignalToGoldenCommand>(c => c.SignalId == 1),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task Incorrect_NoPromotion()
    {
        var signal = NewSignal(1, ProjectId, DataSourceId, generatedSql: "SELECT 1");

        var mediator = new Mock<ISender>();

        var handler = new RecordQueryFeedbackHandler(
            BuildFactory(new List<McpQuerySignal> { signal }, new List<McpEvalCase>()), mediator.Object);

        await handler.Handle(
            new RecordQueryFeedbackCommand(1, McpUserVerdict.Incorrect, Note: "wrong join"),
            CancellationToken.None);

        signal.UserVerdict.Should().Be(McpUserVerdict.Incorrect, "the verdict is still recorded");
        signal.FeedbackNote.Should().Be("wrong join");

        mediator.Verify(
            x => x.Send(It.IsAny<PromoteSignalToGoldenCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task UnknownSignal_Throws()
    {
        var mediator = new Mock<ISender>();

        var handler = new RecordQueryFeedbackHandler(
            BuildFactory(new List<McpQuerySignal>(), new List<McpEvalCase>()), mediator.Object);

        var act = async () => await handler.Handle(
            new RecordQueryFeedbackCommand(404, McpUserVerdict.Correct),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not found*");

        mediator.Verify(
            x => x.Send(It.IsAny<PromoteSignalToGoldenCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task KnowledgeSignal_NoPromotion()
    {
        // A knowledge-intent signal has no data source, so it cannot seed a golden case.
        var signal = NewSignal(1, ProjectId, dataSourceId: null, generatedSql: "SELECT 1");

        var mediator = new Mock<ISender>();

        var handler = new RecordQueryFeedbackHandler(
            BuildFactory(new List<McpQuerySignal> { signal }, new List<McpEvalCase>()), mediator.Object);

        await handler.Handle(
            new RecordQueryFeedbackCommand(1, McpUserVerdict.Correct, Note: "correct but no data source"),
            CancellationToken.None);

        signal.UserVerdict.Should().Be(McpUserVerdict.Correct, "the verdict is still recorded");
        signal.FeedbackNote.Should().Be("correct but no data source");

        mediator.Verify(
            x => x.Send(It.IsAny<PromoteSignalToGoldenCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static McpQuerySignal NewSignal(int id, int? projectId, int? dataSourceId, string? generatedSql) =>
        new()
        {
            Id = id,
            ProjectId = projectId,
            DataSourceId = dataSourceId,
            Tool = "ask",
            Question = "What is our total revenue this year?",
            GeneratedSql = generatedSql
        };

    private static IDbContextFactory<BeaconContext> BuildFactory(
        List<McpQuerySignal> signals, List<McpEvalCase> cases)
    {
        var context = new FeedbackHandlerContext(BuildDbSet(signals).Object, BuildDbSet(cases).Object);
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);
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

    /// <summary>
    /// A <see cref="BeaconContext"/> whose query-signal and eval-case sets resolve to the supplied mocked
    /// sets; save operations are no-ops (the handler's one unit of work is verified via the mocked sets).
    /// </summary>
    private sealed class FeedbackHandlerContext : BeaconContext
    {
        private static readonly DbContextOptions<FeedbackHandlerContext> Options =
            new DbContextOptionsBuilder<FeedbackHandlerContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly DbSet<McpQuerySignal> _signals;
        private readonly DbSet<McpEvalCase> _cases;

        public FeedbackHandlerContext(DbSet<McpQuerySignal> signals, DbSet<McpEvalCase> cases)
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
