using System.Data;
using System.Data.Common;
using Beacon.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.HostData;

/// <summary>
/// A cross-replica lock around the host startup sync (<c>SyncBeaconHostAsync</c>, <c>SyncBeaconHostDataSourcesAsync</c>),
/// so replicas starting together do not race their read-then-insert upserts into the unique indexes. The default
/// (<see cref="DatabaseHostSyncLock"/>) is a lock in Beacon's own database. Replaceable for hosts that coordinate
/// startup another way.
/// </summary>
public interface IHostSyncLock
{
    /// <summary>Blocks until the lock is held; dispose the result to release it.</summary>
    Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken);
}

internal enum HostSyncLockDialect
{
    None,
    PostgreSql,
    SqlServer
}

/// <summary>The provider-specific statements <see cref="DatabaseHostSyncLock"/> runs on its dedicated connection.</summary>
internal sealed record HostSyncLockSql(string Acquire, string Release);

/// <summary>
/// PostgreSQL: a session-level advisory lock on a fixed key (<c>pg_try_advisory_lock</c>, polled until
/// <see cref="AcquireTimeout"/>). SQL Server: <c>sp_getapplock</c> with a session owner on a fixed resource. Both are
/// held on one dedicated open connection for the whole sync and released explicitly. Any other provider runs
/// unlocked with a warning.
/// </summary>
internal sealed class DatabaseHostSyncLock(
    IDbContextFactory<BeaconContext> contextFactory,
    ILogger<DatabaseHostSyncLock> logger) : IHostSyncLock
{
    /// <summary>"BeaconHS" as a 64-bit advisory-lock key.</summary>
    internal const long PostgreSqlLockKey = 0x4265_6163_6F6E_4853;

    internal const string SqlServerResource = "beacon:host-sync";

    internal static readonly TimeSpan AcquireTimeout = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    public async Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var dialect = DialectOf(context.Database.ProviderName);
        if (dialect == HostSyncLockDialect.None)
        {
            logger.LogWarning(
                "Host sync runs without a cross-replica lock: provider {Provider} has no supported lock primitive.",
                context.Database.ProviderName);
            await context.DisposeAsync();

            return NoLock.Instance;
        }

        try
        {
            var connection = context.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open)
            {
                await connection.OpenAsync(cancellationToken);
            }

            var sql = SqlFor(dialect);
            await AcquireOnConnectionAsync(connection, dialect, sql, cancellationToken);

            return new Handle(context, connection, dialect, sql, logger);
        }
        catch
        {
            await context.DisposeAsync();
            throw;
        }
    }

    internal static HostSyncLockDialect DialectOf(string? providerName) =>
        providerName switch
        {
            "Npgsql.EntityFrameworkCore.PostgreSQL" => HostSyncLockDialect.PostgreSql,
            "Microsoft.EntityFrameworkCore.SqlServer" => HostSyncLockDialect.SqlServer,
            _ => HostSyncLockDialect.None
        };

    internal static HostSyncLockSql SqlFor(HostSyncLockDialect dialect) =>
        dialect switch
        {
            HostSyncLockDialect.PostgreSql => new HostSyncLockSql(
                "SELECT pg_try_advisory_lock(@lock_key)",
                "SELECT pg_advisory_unlock(@lock_key)"),
            HostSyncLockDialect.SqlServer => new HostSyncLockSql(
                "DECLARE @result int; EXEC @result = sp_getapplock @Resource = @lock_resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = @lock_timeout_ms; SELECT @result;",
                "EXEC sp_releaseapplock @Resource = @lock_resource, @LockOwner = 'Session';"),
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "No lock SQL for this provider.")
        };

    internal static DbCommand CreateCommand(DbConnection connection, HostSyncLockDialect dialect, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;

        if (dialect == HostSyncLockDialect.PostgreSql)
        {
            AddParameter(command, "@lock_key", PostgreSqlLockKey);
            return command;
        }

        AddParameter(command, "@lock_resource", SqlServerResource);
        AddParameter(command, "@lock_timeout_ms", (int)AcquireTimeout.TotalMilliseconds);
        command.CommandTimeout = (int)AcquireTimeout.TotalSeconds + 30;

        return command;
    }

    private static async Task AcquireOnConnectionAsync(
        DbConnection connection,
        HostSyncLockDialect dialect,
        HostSyncLockSql sql,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, dialect, sql.Acquire);

        if (dialect == HostSyncLockDialect.SqlServer)
        {
            // 0 = granted, 1 = granted after waiting; negative = timeout, deadlock victim, cancelled or error.
            var result = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
            if (result < 0)
            {
                throw new TimeoutException($"Could not acquire the Beacon host-sync lock (sp_getapplock returned {result}).");
            }

            return;
        }

        var deadline = DateTime.UtcNow + AcquireTimeout;
        while (true)
        {
            if (await command.ExecuteScalarAsync(cancellationToken) is true)
            {
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"Could not acquire the Beacon host-sync lock within {AcquireTimeout.TotalMinutes} minutes.");
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed class Handle(
        BeaconContext context,
        DbConnection connection,
        HostSyncLockDialect dialect,
        HostSyncLockSql sql,
        ILogger logger) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = CreateCommand(connection, dialect, sql.Release);
                await command.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Closing the session below releases a session-level lock anyway.
                logger.LogWarning(ex, "Releasing the Beacon host-sync lock failed; it is released when the connection closes.");
            }
            finally
            {
                await context.DisposeAsync();
            }
        }
    }

    private sealed class NoLock : IAsyncDisposable
    {
        public static readonly NoLock Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Runs a host sync body under <see cref="IHostSyncLock"/>, retrying it once after a lost unique-key race.</summary>
internal static class HostSyncRunner
{
    public static async Task RunLockedAsync(
        IServiceProvider services,
        Func<IServiceProvider, CancellationToken, Task> body,
        CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var provider = scope.ServiceProvider;

        await using var hostSyncLock = await provider.GetRequiredService<IHostSyncLock>().AcquireAsync(cancellationToken);

        try
        {
            await body(provider, cancellationToken);
        }
        catch (DbUpdateException ex) when (DbUniqueViolation.IsUniqueViolation(ex))
        {
            // Someone outside the lock (an older replica, an admin) inserted the same row: the sync is idempotent,
            // so a second pass reloads what now exists and converges.
            provider.GetService<ILoggerFactory>()?
                .CreateLogger(typeof(HostSyncRunner))
                .LogWarning("Host sync lost a unique-key race; re-running it against the current state.");

            await body(provider, cancellationToken);
        }
    }
}
