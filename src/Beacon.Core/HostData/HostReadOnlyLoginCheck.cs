using System.Data.Common;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.HostData;

/// <summary>What the host database says about a host data source's read-only login.</summary>
internal enum HostLoginWriteVerdict
{
    /// <summary>The answer could not be read: the login was not verified.</summary>
    Unverified = 0,

    /// <summary>The login holds no write access Beacon asked about.</summary>
    ReadOnly,

    /// <summary>The login can write.</summary>
    CanWrite
}

/// <summary>
/// The verdict on a host login, with the fixed label of the write access found, or of why the login was not verified (null
/// when it is read-only).
/// </summary>
internal readonly record struct HostLoginWriteAccess(HostLoginWriteVerdict Verdict, string? Reason)
{
    private const string NotAnswered = "the database did not answer every check";

    private const string NothingFound = "none of the exposed tables was found";

    /// <summary>
    /// PostgreSQL: the login is a superuser (<c>rolsuper</c>) or holds INSERT, UPDATE or DELETE on an exposed table
    /// (<c>has_table_privilege</c>). Either answer true is write access, whatever the other; otherwise a missing answer, or
    /// no exposed table found in the catalog (<paramref name="tablesFound"/>, also when none was exposed), is
    /// <see cref="HostLoginWriteVerdict.Unverified"/>: nothing checked is not read-only.
    /// </summary>
    public static HostLoginWriteAccess ClassifyPostgreSql(bool? isSuperuser, bool? canWriteExposedTable, long? tablesFound)
    {
        if (isSuperuser == true)
        {
            return new HostLoginWriteAccess(HostLoginWriteVerdict.CanWrite, "superuser");
        }

        if (canWriteExposedTable == true)
        {
            return new HostLoginWriteAccess(HostLoginWriteVerdict.CanWrite, "write privilege on an exposed table");
        }

        if (isSuperuser == null || canWriteExposedTable == null || tablesFound == null)
        {
            return new HostLoginWriteAccess(HostLoginWriteVerdict.Unverified, NotAnswered);
        }

        return tablesFound > 0
            ? new HostLoginWriteAccess(HostLoginWriteVerdict.ReadOnly, null)
            : new HostLoginWriteAccess(HostLoginWriteVerdict.Unverified, NothingFound);
    }

    /// <summary>
    /// SQL Server and Azure Synapse: <c>IS_ROLEMEMBER('db_owner')</c>, <c>IS_ROLEMEMBER('db_datawriter')</c>,
    /// <c>HAS_PERMS_BY_NAME(NULL, 'DATABASE', ...)</c> for INSERT, UPDATE and DELETE (1 granted, 0 not, NULL unknown), and
    /// <c>HAS_PERMS_BY_NAME(..., 'OBJECT', ...)</c> on each exposed table (<paramref name="exposedTableWrite"/>: 1 when any is
    /// granted). Any 1 is write access; otherwise a NULL, or no exposed table found (<paramref name="tablesFound"/>, also
    /// when none was exposed), is <see cref="HostLoginWriteVerdict.Unverified"/>.
    /// </summary>
    public static HostLoginWriteAccess ClassifySqlServer(
        int? dbOwner,
        int? dataWriter,
        int? insert,
        int? update,
        int? delete,
        int? tablesFound,
        int? exposedTableWrite)
    {
        if (dbOwner == 1)
        {
            return new HostLoginWriteAccess(HostLoginWriteVerdict.CanWrite, "db_owner");
        }

        if (dataWriter == 1)
        {
            return new HostLoginWriteAccess(HostLoginWriteVerdict.CanWrite, "db_datawriter");
        }

        if (insert == 1 || update == 1 || delete == 1)
        {
            return new HostLoginWriteAccess(HostLoginWriteVerdict.CanWrite, "a database-wide write permission");
        }

        if (exposedTableWrite == 1)
        {
            return new HostLoginWriteAccess(HostLoginWriteVerdict.CanWrite, "write permission on an exposed table");
        }

        if (new int?[] { dbOwner, dataWriter, insert, update, delete, tablesFound, exposedTableWrite }.Any(x => x == null))
        {
            return new HostLoginWriteAccess(HostLoginWriteVerdict.Unverified, NotAnswered);
        }

        return tablesFound > 0
            ? new HostLoginWriteAccess(HostLoginWriteVerdict.ReadOnly, null)
            : new HostLoginWriteAccess(HostLoginWriteVerdict.Unverified, NothingFound);
    }
}

/// <summary>
/// Warns, once per host data source after the startup sync, when the read-only login a host configured for it can write:
/// Beacon trusts that login as the database-level boundary. A Warning only — never a refusal; a check that cannot run, or
/// that finds none of the exposed tables, logs that the login could not be verified. A disabled source is not checked (it
/// makes no connection), and a connection the transport policy refuses is logged as not checked. Fixed catalog statements
/// Beacon writes (the exposed tables' names are parameters), run outside the SQL gate on a connection from
/// <see cref="IDataSourceConnectionResolver"/>, bounded by a short timeout. PostgreSQL, SQL Server and Azure Synapse only.
/// The data source id only (§1.11): never the connection string or the login's name.
/// </summary>
internal interface IHostReadOnlyLoginCheck
{
    Task WarnIfWritableAsync(DataSource dataSource, IReadOnlyList<HostExposedTable> exposedTables, CancellationToken cancellationToken);
}

internal sealed class HostReadOnlyLoginCheck(
    IDataSourceConnectionResolver connectionResolver,
    ILogger<HostReadOnlyLoginCheck> logger) : IHostReadOnlyLoginCheck
{
    // How long connecting and asking may take before the check gives up (logged as not verified).
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // The exposed tables asked about per SQL Server statement: two parameters each, under the driver's 2,100.
    internal const int SqlServerTablesPerStatement = 500;

    // Whether the login is a superuser, whether it holds INSERT, UPDATE or DELETE on any exposed table, and how many exposed
    // tables the catalog holds, matched by schema and name (a table missing from the catalog is not counted).
    internal const string PostgreSqlSql = """
        WITH exposed AS (
            SELECT c.oid
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            JOIN unnest(@schemas, @names) AS t(schema_name, table_name)
                ON t.schema_name = n.nspname AND t.table_name = c.relname
        )
        SELECT
            (SELECT r.rolsuper FROM pg_catalog.pg_roles r WHERE r.rolname = current_user) AS is_superuser,
            EXISTS (
                SELECT 1
                FROM exposed e
                WHERE pg_catalog.has_table_privilege(e.oid, 'INSERT, UPDATE, DELETE')
            ) AS can_write,
            (SELECT count(*) FROM exposed) AS tables_found
        """;

    internal const string SqlServerSql = """
        SELECT
            CAST(IS_ROLEMEMBER('db_owner') AS int),
            CAST(IS_ROLEMEMBER('db_datawriter') AS int),
            CAST(HAS_PERMS_BY_NAME(NULL, 'DATABASE', 'INSERT') AS int),
            CAST(HAS_PERMS_BY_NAME(NULL, 'DATABASE', 'UPDATE') AS int),
            CAST(HAS_PERMS_BY_NAME(NULL, 'DATABASE', 'DELETE') AS int)
        """;

    public async Task WarnIfWritableAsync(DataSource dataSource, IReadOnlyList<HostExposedTable> exposedTables, CancellationToken cancellationToken)
    {
        var engine = dataSource.DatabaseEngineType;
        if (engine is not (DatabaseEngineType.PostgreSQL or DatabaseEngineType.MSSQL or DatabaseEngineType.AzureSynapse))
        {
            return;
        }

        // A disabled source makes no connection, so its login is not asked about (§1.1).
        if (dataSource.DisabledTime != null)
        {
            return;
        }

        HostLoginWriteAccess access;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            var connectionString = connectionResolver.GetConnectionString(dataSource);
            await using var connection = DbConnectionFactory.CreateConnection(engine.Value, connectionString);
            await connection.OpenAsync(timeout.Token);
            access = engine == DatabaseEngineType.PostgreSQL
                ? await AskPostgreSqlAsync(connection, exposedTables, timeout.Token)
                : await AskSqlServerAsync(connection, exposedTables, timeout.Token);
        }
        catch (DataSourceTransportException ex)
        {
            logger.LogWarning(
                "Host data source {DataSourceId}: its read-only login was not checked: the connection was refused by the transport policy ({ExceptionType})",
                dataSource.Id,
                ex.GetType().Name);

            return;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The type only: a driver's message can name the host or the login (§1.11).
            logger.LogWarning(
                "Host data source {DataSourceId}: could not verify that its read-only login cannot write ({ExceptionType})",
                dataSource.Id,
                ex.GetType().Name);

            return;
        }

        if (access.Verdict == HostLoginWriteVerdict.CanWrite)
        {
            logger.LogWarning(
                "Host data source {DataSourceId}: its read-only login can write ({WriteAccess}). Beacon relies on that login as the database-level read-only boundary: give it read access only",
                dataSource.Id,
                access.Reason);
        }
        else if (access.Verdict == HostLoginWriteVerdict.Unverified)
        {
            logger.LogWarning(
                "Host data source {DataSourceId}: could not verify that its read-only login cannot write ({Reason})",
                dataSource.Id,
                access.Reason);
        }
    }

    // The SQL Server statement for a chunk of exposed tables: how many the database finds (HAS_PERMS_BY_NAME answers NULL
    // for a table it cannot find) and 1 when the login holds INSERT, UPDATE or DELETE on any of them. The names are the
    // parameters @s{i} and @n{i}, quoted by QUOTENAME on the server: only their numbered placeholders are written here.
    internal static string SqlServerTablesSql(int tableCount)
    {
        var rows = string.Join(
            "\n                    ",
            Enumerable.Range(0, tableCount)
                .Select(x => x == 0
                    ? $"SELECT @s{x} AS schema_name, @n{x} AS table_name"
                    : $"UNION ALL SELECT @s{x}, @n{x}"));

        return $"""
            SELECT
                COUNT(p.can_insert) AS tables_found,
                MAX(CASE WHEN p.can_insert = 1 OR p.can_update = 1 OR p.can_delete = 1 THEN 1 ELSE 0 END) AS can_write
            FROM (
                SELECT
                    HAS_PERMS_BY_NAME(QUOTENAME(t.schema_name) + '.' + QUOTENAME(t.table_name), 'OBJECT', 'INSERT') AS can_insert,
                    HAS_PERMS_BY_NAME(QUOTENAME(t.schema_name) + '.' + QUOTENAME(t.table_name), 'OBJECT', 'UPDATE') AS can_update,
                    HAS_PERMS_BY_NAME(QUOTENAME(t.schema_name) + '.' + QUOTENAME(t.table_name), 'OBJECT', 'DELETE') AS can_delete
                FROM (
                    {rows}
                ) AS t
            ) AS p
            """;
    }

    private static async Task<HostLoginWriteAccess> AskPostgreSqlAsync(
        DbConnection connection,
        IReadOnlyList<HostExposedTable> exposedTables,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = PostgreSqlSql;
        command.CommandTimeout = (int)Timeout.TotalSeconds;
        command.Parameters.Add(Parameter(command, "schemas", exposedTables.Select(x => x.Schema).ToArray()));
        command.Parameters.Add(Parameter(command, "names", exposedTables.Select(x => x.Name).ToArray()));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return HostLoginWriteAccess.ClassifyPostgreSql(null, null, null);
        }

        return HostLoginWriteAccess.ClassifyPostgreSql(Flag(reader.GetValue(0)), Flag(reader.GetValue(1)), Number(reader.GetValue(2)));
    }

    private static async Task<HostLoginWriteAccess> AskSqlServerAsync(
        DbConnection connection,
        IReadOnlyList<HostExposedTable> exposedTables,
        CancellationToken cancellationToken)
    {
        int?[] databaseWide;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = SqlServerSql;
            command.CommandTimeout = (int)Timeout.TotalSeconds;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            databaseWide = await reader.ReadAsync(cancellationToken)
                ? Enumerable.Range(0, 5)
                    .Select(x => Int(reader.GetValue(x)))
                    .ToArray()
                : new int?[5];
        }

        // No exposed table asks nothing: none found, which classifies as not verified.
        int? tablesFound = 0;
        int? exposedTableWrite = 0;
        foreach (var chunk in exposedTables.Chunk(SqlServerTablesPerStatement))
        {
            var (found, write) = await AskSqlServerTablesAsync(connection, chunk, cancellationToken);
            tablesFound += found;
            if (write == 1)
            {
                exposedTableWrite = 1;
            }
            else if (write == null && exposedTableWrite != 1)
            {
                exposedTableWrite = null;
            }
        }

        return HostLoginWriteAccess.ClassifySqlServer(
            databaseWide[0],
            databaseWide[1],
            databaseWide[2],
            databaseWide[3],
            databaseWide[4],
            tablesFound,
            exposedTableWrite);
    }

    private static async Task<(int? Found, int? Write)> AskSqlServerTablesAsync(
        DbConnection connection,
        IReadOnlyList<HostExposedTable> tables,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = SqlServerTablesSql(tables.Count);
        command.CommandTimeout = (int)Timeout.TotalSeconds;
        for (var index = 0; index < tables.Count; index++)
        {
            command.Parameters.Add(Parameter(command, $"s{index}", tables[index].Schema));
            command.Parameters.Add(Parameter(command, $"n{index}", tables[index].Name));
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (null, null);
        }

        return (Int(reader.GetValue(0)), Int(reader.GetValue(1)));
    }

    private static DbParameter Parameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;

        return parameter;
    }

    private static bool? Flag(object? value)
    {
        return value is bool flag ? flag : null;
    }

    private static long? Number(object? value)
    {
        return value switch
        {
            int number => number,
            long number => number,
            short number => number,
            byte number => number,
            _ => null
        };
    }

    private static int? Int(object? value)
    {
        return Number(value) is { } number ? (int)Math.Clamp(number, int.MinValue, int.MaxValue) : null;
    }
}
