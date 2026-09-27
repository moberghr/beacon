using Beacon.Core.HostData;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NUnit.Framework;

namespace Beacon.Tests.Unit.HostData;

/// <summary>
/// The host startup sync is serialized across replicas by a lock in Beacon's database, and a lost unique-key race
/// re-runs the (idempotent) sync instead of failing startup.
/// </summary>
[TestFixture]
public class HostSyncLockTests
{
    [TestCase("Npgsql.EntityFrameworkCore.PostgreSQL", "PostgreSql")]
    [TestCase("Microsoft.EntityFrameworkCore.SqlServer", "SqlServer")]
    [TestCase("Microsoft.EntityFrameworkCore.Sqlite", "None")]
    [TestCase(null, "None")]
    public void DialectOf_MapsTheEfProvider(string? provider, string expected)
    {
        DatabaseHostSyncLock.DialectOf(provider).ToString().Should().Be(expected);
    }

    [Test]
    public void PostgreSql_UsesASessionAdvisoryLockOnAFixedKey()
    {
        var sql = DatabaseHostSyncLock.SqlFor(HostSyncLockDialect.PostgreSql);
        using var connection = new NpgsqlConnection("Host=localhost;Database=unused");
        using var command = DatabaseHostSyncLock.CreateCommand(connection, HostSyncLockDialect.PostgreSql, sql.Acquire);

        sql.Acquire.Should().Be("SELECT pg_try_advisory_lock(@lock_key)");
        sql.Release.Should().Be("SELECT pg_advisory_unlock(@lock_key)");
        command.Parameters.Cast<NpgsqlParameter>().Single().Value.Should().Be(DatabaseHostSyncLock.PostgreSqlLockKey, "the key is a parameter, never interpolated (§1.10)");
    }

    [Test]
    public void SqlServer_UsesAnExclusiveSessionAppLock_WithATimeout()
    {
        var sql = DatabaseHostSyncLock.SqlFor(HostSyncLockDialect.SqlServer);
        using var connection = new SqlConnection("Server=unused;Database=unused");
        using var command = DatabaseHostSyncLock.CreateCommand(connection, HostSyncLockDialect.SqlServer, sql.Acquire);

        sql.Acquire.Should().Contain("sp_getapplock @Resource = @lock_resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = @lock_timeout_ms");
        sql.Release.Should().Contain("sp_releaseapplock @Resource = @lock_resource, @LockOwner = 'Session'");
        command.Parameters.Cast<SqlParameter>().Select(x => (x.ParameterName, x.Value)).Should().BeEquivalentTo(
            [("@lock_resource", (object)DatabaseHostSyncLock.SqlServerResource), ("@lock_timeout_ms", (object)(int)DatabaseHostSyncLock.AcquireTimeout.TotalMilliseconds)]);
        command.CommandTimeout.Should().BeGreaterThan((int)DatabaseHostSyncLock.AcquireTimeout.TotalSeconds, "the command must outlive the lock wait");
    }

    [Test]
    public async Task RunLocked_RunsTheBodyInsideTheLock_AndReleasesIt()
    {
        var events = new List<string>();
        var services = Services(events);

        await HostSyncRunner.RunLockedAsync(services, (_, _) =>
        {
            events.Add("body");
            return Task.CompletedTask;
        }, CancellationToken.None);

        events.Should().Equal("acquire", "body", "release");
    }

    [Test]
    public async Task RunLocked_ReleasesTheLock_WhenTheBodyFails()
    {
        var events = new List<string>();
        var services = Services(events);

        var act = () => HostSyncRunner.RunLockedAsync(services, (_, _) => throw new InvalidOperationException("boom"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        events.Should().Equal("acquire", "release");
    }

    [Test]
    public async Task RunLocked_LostUniqueRace_ReRunsTheSyncOnce()
    {
        var events = new List<string>();
        var services = Services(events);
        var attempts = 0;

        await HostSyncRunner.RunLockedAsync(services, (_, _) =>
        {
            attempts++;
            events.Add("body");
            if (attempts == 1)
            {
                throw new DbUpdateException("insert", new PostgresException("duplicate key", "ERROR", "ERROR", "23505"));
            }

            return Task.CompletedTask;
        }, CancellationToken.None);

        attempts.Should().Be(2);
        events.Should().Equal("acquire", "body", "body", "release");
    }

    private static ServiceProvider Services(List<string> events)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostSyncLock>(new RecordingLock(events));

        return services.BuildServiceProvider();
    }

    private sealed class RecordingLock(List<string> events) : IHostSyncLock
    {
        public Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken)
        {
            events.Add("acquire");

            return Task.FromResult<IAsyncDisposable>(new Release(events));
        }

        private sealed class Release(List<string> events) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                events.Add("release");
                return ValueTask.CompletedTask;
            }
        }
    }
}
