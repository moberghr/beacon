using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace Beacon.Core.Data;

/// <summary>
/// Recognises a unique-constraint violation across the providers Beacon's own store runs on, so a check-then-insert
/// that lost a race can be treated as "someone else did it" instead of surfacing as a 500.
/// </summary>
public static class DbUniqueViolation
{
    private const string PostgresUniqueViolation = "23505";
    private const int SqlServerUniqueIndexViolation = 2601;
    private const int SqlServerUniqueConstraintViolation = 2627;
    private const int SqliteConstraintUnique = 2067;
    private const int SqliteConstraintPrimaryKey = 1555;

    /// <summary>True when <paramref name="exception"/> or one of its inner exceptions is a unique-key violation.</summary>
    public static bool IsUniqueViolation(Exception? exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            switch (current)
            {
                case SqlException sqlServer when sqlServer.Number is SqlServerUniqueIndexViolation or SqlServerUniqueConstraintViolation:
                case SqliteException sqlite when sqlite.SqliteExtendedErrorCode is SqliteConstraintUnique or SqliteConstraintPrimaryKey:
                case DbException db when db.SqlState == PostgresUniqueViolation:
                    return true;
            }
        }

        return false;
    }
}
