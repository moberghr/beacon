using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Services.Retention;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// SC9 — the purge deletes only rows older than the retention window; a <c>null</c> window is a no-op (the
/// context factory is never asked for a context). The delete predicate is shared between the purge and this
/// test via <see cref="McpAuditRetentionService.Expired"/>, verified here to translate on Npgsql (§4.3-§4.5,
/// no database). The cutoff comes from the injected <see cref="TimeProvider"/>.
/// </summary>
[TestFixture]
public class McpAuditRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTime ExpectedCutoff = new(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public void ExpiredPredicate_Translates()
    {
        using var context = NpgsqlTestContext.Create();
        var cutoff = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var sql = McpAuditRetentionService.Expired(context.McpAuditLogs, cutoff).ToQueryString();

        sql.Should().Contain("created_time <", "the purge deletes rows whose created_time is strictly older than the cutoff");
    }

    [Test]
    public void CutoffFor_SubtractsTheRetentionWindowInUtc()
    {
        var cutoff = McpAuditRetentionService.CutoffFor(Now, 30);

        cutoff.Should().Be(ExpectedCutoff);
        cutoff.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Test]
    public void CutoffFor_NonUtcOffset_IsNormalisedToUtc()
    {
        var cutoff = McpAuditRetentionService.CutoffFor(new DateTimeOffset(2026, 9, 28, 14, 0, 0, TimeSpan.FromHours(2)), 30);

        cutoff.Should().Be(ExpectedCutoff);
        cutoff.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Test]
    public async Task PurgeExpiredAsync_UsesTheInjectedTimeProviderForTheCutoff()
    {
        // No database: the connection open is suppressed and the DELETE is answered by the interceptor, which
        // captures the cutoff parameter EF bound — proving the purge reads the injected clock, not the system one.
        var interceptor = new CapturingDeleteInterceptor(deletedRows: 3);
        var options = new DbContextOptionsBuilder<NpgsqlTestContext>()
            .UseNpgsql("Host=localhost;Database=test_does_not_exist")
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(interceptor)
            .Options;
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new NpgsqlTestContext(options));
        var deploymentOptions = Options.Create(new McpDeploymentOptions { Audit = new McpAuditOptions { RetentionDays = 30 } });
        var logs = new List<(EventId EventId, string Message)>();
        var service = new McpAuditRetentionService(
            factory.Object, deploymentOptions, new FakeTimeProvider(Now), new LoggerFactory([new ListLoggerProvider(logs)]));

        var deleted = await service.PurgeExpiredAsync(CancellationToken.None);

        deleted.Should().Be(3);
        interceptor.CommandText.Should().Contain("DELETE").And.Contain("created_time <");
        interceptor.ParameterValues.Should().ContainSingle()
            .Which.Should().BeOfType<DateTime>()
            .Which.Should().Be(ExpectedCutoff);
        logs.Should().ContainSingle(x => x.EventId.Id == 9102);
    }

    [Test]
    public async Task NullRetentionDays_IsANoOpAndNeverOpensAContext()
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        var options = Options.Create(new McpDeploymentOptions { Audit = new McpAuditOptions { RetentionDays = null } });
        var service = new McpAuditRetentionService(factory.Object, options, TimeProvider.System, NullLoggerFactory.Instance);

        var deleted = await service.PurgeExpiredAsync(CancellationToken.None);

        deleted.Should().Be(0);
        factory.Verify(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class CapturingDeleteInterceptor(int deletedRows) : IDbConnectionInterceptor, IDbCommandInterceptor
    {
        public string? CommandText { get; private set; }

        public List<object?> ParameterValues { get; } = [];

        public ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult.Suppress());

        public ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            CommandText = command.CommandText;
            foreach (DbParameter parameter in command.Parameters)
            {
                ParameterValues.Add(parameter.Value);
            }

            return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(deletedRows));
        }
    }

    private sealed class ListLoggerProvider(List<(EventId EventId, string Message)> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ListLogger(logs);

        public void Dispose()
        {
        }
    }

    private sealed class ListLogger(List<(EventId EventId, string Message)> logs) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            logs.Add((eventId, formatter(state, exception)));
    }
}
