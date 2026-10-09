using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace Beacon.Core.Data;

/// <summary>
/// Recognises a transaction the database aborted because it conflicted with a concurrent one, on the providers Beacon's
/// own store runs on: a PostgreSQL serialization failure (40001) or deadlock (40P01), a SQL Server deadlock victim
/// (1205) or snapshot update conflict (3960). The losing request of a race then gets a precise answer instead of a 500.
/// </summary>
public static class DbSerializationConflict
{
    private const string PostgresSerializationFailure = "40001";
    private const string PostgresDeadlockDetected = "40P01";
    private const int SqlServerDeadlockVictim = 1205;
    private const int SqlServerSnapshotUpdateConflict = 3960;

    /// <summary>True when <paramref name="exception"/> or one of its inner exceptions is a serialization conflict.</summary>
    public static bool IsSerializationConflict(Exception? exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            switch (current)
            {
                case SqlException sqlServer when sqlServer.Number is SqlServerDeadlockVictim or SqlServerSnapshotUpdateConflict:
                case DbException db when db.SqlState is PostgresSerializationFailure or PostgresDeadlockDetected:
                    return true;
            }
        }

        return false;
    }
}
