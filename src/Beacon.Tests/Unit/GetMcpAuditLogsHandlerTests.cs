using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Handlers.McpAudit;
using Beacon.Core.Telemetry;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// SC10 — <c>GetMcpAuditLogsHandler</c> for GET /beacon/api/mcp/audit: a bad range or bad paging is rejected
/// before any query runs (a strict context factory proves this — a validation gap would surface as a Moq
/// exception, not just a missed assertion). <c>InvalidOperationException</c> maps to HTTP 400 via
/// <c>ApiExceptionMiddleware</c>. The happy path pages over async-queryable doubles (§4.7 — no DB) and emits
/// exactly one structural 9103 event; offset-less dates are normalised to UTC before the query binds them.
/// </summary>
[TestFixture]
public class GetMcpAuditLogsHandlerTests
{
    private const string CallerHash = "c0ffee00c0ffee00c0ffee00c0ffee00";

    private static readonly DateTime From = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task ToEarlierThanFrom_Throws()
    {
        var act = () => HandleAsync(From, From.AddDays(-1), page: 1, pageSize: 100);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task RangeOver93Days_Throws()
    {
        var act = () => HandleAsync(From, From.AddDays(94), page: 1, pageSize: 100);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task PageLessThanOne_Throws()
    {
        var act = () => HandleAsync(From, From.AddDays(1), page: 0, pageSize: 100);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [TestCase(0)]
    [TestCase(501)]
    public async Task PageSizeOutsideOneToFiveHundred_Throws(int pageSize)
    {
        var act = () => HandleAsync(From, From.AddDays(1), page: 1, pageSize: pageSize);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task PageOffsetOverflow_Throws()
    {
        // (Page-1)*PageSize computed as int would wrap; computed in long it plainly exceeds int.MaxValue.
        var act = () => HandleAsync(From, From.AddDays(1), page: int.MaxValue, pageSize: 500);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task ToolFilter_LogsPresenceOnly_NeverTheValue()
    {
        var logs = new List<(EventId EventId, string Message, IReadOnlyList<KeyValuePair<string, object?>> State)>();
        var request = Query(From, From.AddDays(30), page: 1, pageSize: 100) with { Tool = "evil\r\nFAKE" };

        await HandleOverDoublesAsync(request, logs);

        var read = logs.Should().ContainSingle(x => x.EventId.Id == 9103).Subject;
        read.State.Should().Contain(new KeyValuePair<string, object?>("HasToolFilter", true));
        read.State.Select(x => x.Key).Should().NotContain("Tool");
        read.State.Should().NotContain(x => x.Value is string && ((string)x.Value!).Contains("evil", StringComparison.OrdinalIgnoreCase));
        read.Message.Should().NotContain("evil").And.NotContain("FAKE");
    }

    [Test]
    public async Task RangeOfExactly93Days_IsAccepted()
    {
        var result = await HandleOverDoublesAsync(Query(From, From.AddDays(93), page: 1, pageSize: 100), []);

        result.TotalCount.Should().Be(6);
    }

    [TestCase(1)]
    [TestCase(500)]
    public async Task PageSizeAtTheBounds_IsAccepted(int pageSize)
    {
        var result = await HandleOverDoublesAsync(Query(From, From.AddDays(30), page: 1, pageSize: pageSize), []);

        result.PageSize.Should().Be(pageSize);
        result.Items.Should().HaveCount(Math.Min(pageSize, 6));
    }

    [Test]
    public async Task SecondPage_ReturnsNewestFirstWithIdTieBreak_AndLogsOneStructural9103()
    {
        var logs = new List<(EventId EventId, string Message, IReadOnlyList<KeyValuePair<string, object?>> State)>();
        var request = Query(From, From.AddDays(30), page: 2, pageSize: 2) with { CallerHash = CallerHash, RequestedByUserId = 17 };

        var result = await HandleOverDoublesAsync(request, logs);

        // Newest first: 5 (Jan 6), 4 (Jan 5), then 3 and 2 share Jan 4 and break by Id descending, then 1.
        result.Items.Select(x => x.Id).Should().Equal(3, 2);
        result.TotalCount.Should().Be(5, "the row with another caller hash is filtered out");
        result.Page.Should().Be(2);
        result.PageSize.Should().Be(2);

        var read = logs.Should().ContainSingle(x => x.EventId.Id == 9103).Subject;
        logs.Should().ContainSingle();
        read.State.Should().Contain(new KeyValuePair<string, object?>("RequestedByUserId", 17));
        read.State.Should().Contain(new KeyValuePair<string, object?>("ReturnedCount", 2));
        read.State.Should().Contain(new KeyValuePair<string, object?>("HasCallerHashFilter", true));
        read.State.Should().NotContain(x => x.Value is string && ((string)x.Value!).Contains(CallerHash));
        read.Message.Should().NotContain(CallerHash);
    }

    [TestCase(DateTimeKind.Unspecified)]
    [TestCase(DateTimeKind.Local)]
    public void ToUtc_NormalisesNonUtcKinds(DateTimeKind kind)
    {
        var value = new DateTime(2026, 1, 1, 8, 0, 0, kind);

        var normalised = GetMcpAuditLogsHandler.ToUtc(value);

        normalised.Kind.Should().Be(DateTimeKind.Utc);
        normalised.Should().Be(kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    [TestCase(DateTimeKind.Unspecified)]
    [TestCase(DateTimeKind.Local)]
    public async Task NonUtcDates_AreNormalisedBeforeTheQueryRuns(DateTimeKind kind)
    {
        var logs = new List<(EventId EventId, string Message, IReadOnlyList<KeyValuePair<string, object?>> State)>();
        var from = DateTime.SpecifyKind(From, kind);

        var result = await HandleOverDoublesAsync(Query(from, from.AddDays(30), page: 1, pageSize: 100), logs);

        result.TotalCount.Should().Be(6);
        var state = logs.Should().ContainSingle(x => x.EventId.Id == 9103).Subject.State;
        state.Single(x => x.Key == "From").Value.Should().BeOfType<DateTime>()
            .Which.Kind.Should().Be(DateTimeKind.Utc, "the handler binds UTC values, which Npgsql timestamptz accepts");
        state.Single(x => x.Key == "To").Value.Should().BeOfType<DateTime>()
            .Which.Kind.Should().Be(DateTimeKind.Utc);
    }

    private static GetMcpAuditLogsQuery Query(DateTime from, DateTime to, int page, int pageSize) =>
        new(from, to, null, null, null, null, page, pageSize, null);

    private static Task<GetMcpAuditLogsResult> HandleAsync(DateTime from, DateTime to, int page, int pageSize)
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>(MockBehavior.Strict);
        var handler = new GetMcpAuditLogsHandler(factory.Object, NullLoggerFactory.Instance);

        return handler.Handle(Query(from, to, page, pageSize), CancellationToken.None);
    }

    private static async Task<GetMcpAuditLogsResult> HandleOverDoublesAsync(GetMcpAuditLogsQuery request,
        List<(EventId EventId, string Message, IReadOnlyList<KeyValuePair<string, object?>> State)> logs)
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AuditLogContext());
        var handler = new GetMcpAuditLogsHandler(factory.Object, new LoggerFactory([new StateLoggerProvider(logs)]));

        return await handler.Handle(request, CancellationToken.None);
    }

    private sealed class AuditLogContext : BeaconContext
    {
        private static readonly DbContextOptions<AuditLogContext> Options =
            new DbContextOptionsBuilder<AuditLogContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        public AuditLogContext() : base(Options, "beacon")
        {
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(McpAuditLog))
            {
                return (DbSet<TEntity>)(object)BuildSet(Seed());
            }

            return base.Set<TEntity>();
        }

        private static List<McpAuditLog> Seed() =>
        [
            Row(1, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), CallerHash),
            Row(2, new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc), CallerHash),
            Row(3, new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc), CallerHash),
            Row(4, new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc), CallerHash),
            Row(5, new DateTime(2026, 1, 6, 0, 0, 0, DateTimeKind.Utc), CallerHash),
            Row(6, new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc), "another-caller")
        ];

        private static McpAuditLog Row(int id, DateTime createdTime, string callerHash) =>
            new()
            {
                Id = id,
                CreatedTime = createdTime,
                Tool = "project_query",
                CallerHash = callerHash
            };

        private static DbSet<T> BuildSet<T>(List<T> data) where T : class
        {
            var queryable = data.AsQueryable();
            var set = new Mock<DbSet<T>>();
            set.As<IAsyncEnumerable<T>>()
                .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
                .Returns(() => new TestAsyncEnumerator<T>(queryable.GetEnumerator()));
            set.As<IQueryable<T>>()
                .Setup(x => x.Provider)
                .Returns(new TestAsyncQueryProvider<T>(queryable.Provider));
            set.As<IQueryable<T>>().Setup(x => x.Expression).Returns(queryable.Expression);
            set.As<IQueryable<T>>().Setup(x => x.ElementType).Returns(queryable.ElementType);
            set.As<IQueryable<T>>().Setup(x => x.GetEnumerator()).Returns(() => queryable.GetEnumerator());
            return set.Object;
        }
    }

    private sealed class StateLoggerProvider(
        List<(EventId EventId, string Message, IReadOnlyList<KeyValuePair<string, object?>> State)> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) =>
            categoryName == BeaconTelemetry.AuditLogCategory ? new StateLogger(logs) : NullLogger.Instance;

        public void Dispose()
        {
        }
    }

    private sealed class StateLogger(
        List<(EventId EventId, string Message, IReadOnlyList<KeyValuePair<string, object?>> State)> logs) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            logs.Add((eventId, formatter(state, exception),
                state as IReadOnlyList<KeyValuePair<string, object?>> ?? []));
    }
}
