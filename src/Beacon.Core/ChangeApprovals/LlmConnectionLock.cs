using Beacon.Core.Data;
using Beacon.Core.HostData;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.ChangeApprovals;

/// <summary>
/// The one lock every change of the stored LLM connection takes, first in its transaction and before it reads the stored
/// connection: a save (applied at once or staged for approval) and an approval's check and write of a staged connection run
/// one after the other, so neither judges or overwrites a connection the other is changing. Held until the transaction
/// ends.
/// </summary>
internal interface ILlmConnectionLock
{
    /// <summary>Takes the lock in <paramref name="context"/>'s open transaction, waiting while another transaction holds it.</summary>
    Task AcquireAsync(BeaconContext context, CancellationToken cancellationToken);
}

/// <summary>
/// The LLM connection lock in Beacon's own database, owned by the transaction: PostgreSQL <c>pg_advisory_xact_lock</c> on a
/// fixed key, SQL Server <c>sp_getapplock</c> with a transaction owner on a fixed resource (a lock it does not grant within
/// <see cref="SqlServerLockTimeoutMilliseconds"/> fails the statement). An application lock, not a row lock: the stored
/// connection has no row of its own, and before a first provider is stored its setting rows do not exist. Both are released
/// when the transaction commits or rolls back; another provider is refused.
/// </summary>
internal sealed class DatabaseLlmConnectionLock : ILlmConnectionLock
{
    /// <summary>"BeaconLC" as a 64-bit advisory-lock key.</summary>
    internal const long PostgreSqlLockKey = 0x4265_6163_6F6E_4C43;

    internal const string SqlServerResource = "beacon:llm-connection";

    internal const int SqlServerLockTimeoutMilliseconds = 30_000;

    public async Task AcquireAsync(BeaconContext context, CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction == null)
        {
            throw new InvalidOperationException("The LLM connection lock is taken inside a transaction, which releases it.");
        }

        await context.Database.ExecuteSqlAsync(Statement(context.Database.ProviderName), cancellationToken);
    }

    /// <summary>The statement that takes the lock on the provider <paramref name="providerName"/>, its values as parameters.</summary>
    internal static FormattableString Statement(string? providerName)
    {
        return DatabaseHostSyncLock.DialectOf(providerName) switch
        {
            HostSyncLockDialect.PostgreSql => $"SELECT pg_advisory_xact_lock({PostgreSqlLockKey})",
            HostSyncLockDialect.SqlServer =>
                $"DECLARE @lockResult int; EXEC @lockResult = sp_getapplock @Resource = {SqlServerResource}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = {SqlServerLockTimeoutMilliseconds}; IF @lockResult < 0 THROW 51000, 'The LLM connection lock was not granted.', 1;",
            _ => throw new InvalidOperationException("Changes of the LLM connection need a PostgreSQL or SQL Server database, which can lock them.")
        };
    }
}
