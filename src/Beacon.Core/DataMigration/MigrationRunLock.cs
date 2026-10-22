using System.Data.Common;
using Dapper;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.DataMigration;

/// <summary>
/// The destination-side lock a migration run holds for its whole write: taken without waiting, inside the run's
/// transaction, on the job's own key. A run whose claim went stale (its host paused past the in-progress window while a new
/// run of the job started) cannot write over the new one: whichever run holds the lock writes, the other fails and writes
/// nothing. PostgreSQL and SQL Server hold it until the transaction ends; MySQL's is the session's, released after the
/// commit or rollback (<see cref="ReleaseAsync"/>). Values travel as parameters.
/// </summary>
internal static class MigrationRunLock
{
    /// <summary>The first key of a run's PostgreSQL advisory lock, shared by every migration run; the job id is the second.</summary>
    internal const int PostgreSqlLockNamespace = 0x4D494752;

    internal const string PostgreSqlAcquire = "SELECT pg_try_advisory_xact_lock(@lockNamespace, @jobId)";

    internal const string SqlServerAcquire =
        "DECLARE @lockResult int; EXEC @lockResult = sp_getapplock @Resource = @lockResource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 0; SELECT @lockResult;";

    internal const string MySqlAcquire = "SELECT GET_LOCK(@lockResource, 0)";

    internal const string MySqlRelease = "SELECT RELEASE_LOCK(@lockResource)";

    /// <summary>The lock's name on SQL Server and MySQL.</summary>
    public static string ResourceName(int migrationJobId) => $"beacon-migration-{migrationJobId}";

    /// <summary>Takes the job's lock without waiting; false when another session holds it.</summary>
    public static async Task<bool> TryAcquireAsync(
        DbConnection connection,
        DbTransaction transaction,
        DatabaseEngineType engine,
        int migrationJobId,
        CancellationToken cancellationToken)
    {
        switch (engine)
        {
            case DatabaseEngineType.PostgreSQL:
                return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                    PostgreSqlAcquire,
                    new { lockNamespace = PostgreSqlLockNamespace, jobId = migrationJobId },
                    transaction,
                    cancellationToken: cancellationToken));
            case DatabaseEngineType.MSSQL:
                // 0 granted, 1 granted after waiting; negative: not granted (timeout, deadlock, cancelled, error).
                var result = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                    SqlServerAcquire,
                    new { lockResource = ResourceName(migrationJobId) },
                    transaction,
                    cancellationToken: cancellationToken));
                return result >= 0;
            case DatabaseEngineType.MySQL:
                // 1 granted, 0 held by another session, NULL an error.
                var granted = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
                    MySqlAcquire,
                    new { lockResource = ResourceName(migrationJobId) },
                    transaction,
                    cancellationToken: cancellationToken));
                return granted == 1;
            default:
                throw new NotSupportedException($"Database engine type '{engine}' is not supported");
        }
    }

    /// <summary>Releases a lock the session holds past its transaction (MySQL); nothing to do on the other engines.</summary>
    public static async Task ReleaseAsync(DbConnection connection, DatabaseEngineType engine, int migrationJobId)
    {
        if (engine != DatabaseEngineType.MySQL)
        {
            return;
        }

        await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            MySqlRelease,
            new { lockResource = ResourceName(migrationJobId) },
            cancellationToken: CancellationToken.None));
    }
}

/// <summary>
/// A run that failed for a reason the run reports as itself (its message, and <see cref="ReasonCode"/> on the run's
/// security event), not as the database error underneath.
/// </summary>
internal sealed class MigrationRunFailedException(string message, string reasonCode, Exception? innerException = null)
    : InvalidOperationException(message, innerException)
{
    public string ReasonCode { get; } = reasonCode;
}
