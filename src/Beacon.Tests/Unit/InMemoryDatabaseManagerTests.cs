using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Beacon.Core.Services;

namespace Beacon.Tests.Unit;

/// <summary>
/// The in-memory SQLite join store must stop a running statement when its timeout or the caller's token fires.
/// Microsoft.Data.Sqlite only checks the token before a statement starts and ignores CommandTimeout for a running
/// one, so without an interrupt a runaway join holds the thread until it finishes.
/// </summary>
[TestFixture]
public class InMemoryDatabaseManagerTests
{
    // Counts to 100M one row at a time — tens of seconds of CPU, far past every deadline below.
    private const string RunawayQuery = "WITH RECURSIVE r(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM r WHERE i < 100000000) SELECT COUNT(*) FROM r";

    [Test]
    public async Task ExecuteQueryAsync_RunawayQuery_TimesOutAtTheDeadline()
    {
        using var manager = new InMemoryDatabaseManager(NullLogger<InMemoryDatabaseManager>.Instance);
        var stopwatch = Stopwatch.StartNew();

        var (results, _, timedOut) = await manager.ExecuteQueryAsync(RunawayQuery, timeoutSeconds: 1);

        timedOut.Should().BeTrue();
        results.Should().BeEmpty();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task ExecuteQueryAsync_CallerCancels_ThrowsAndConnectionStaysUsable()
    {
        using var manager = new InMemoryDatabaseManager(NullLogger<InMemoryDatabaseManager>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var stopwatch = Stopwatch.StartNew();

        var act = () => manager.ExecuteQueryAsync(RunawayQuery, timeoutSeconds: 30, cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));

        var (results, _, timedOut) = await manager.ExecuteQueryAsync("SELECT 41 + 1 AS answer", timeoutSeconds: 5);

        timedOut.Should().BeFalse();
        results.Should().ContainSingle()
            .Which["answer"].Should().Be(42L);
    }
}
