using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace Beacon.Core.Data;

/// <summary>
/// Day and hour buckets in UTC, evaluated by the database. A plain <c>.Date</c> / <c>.Hour</c> on a
/// <c>timestamp with time zone</c> column is cut in the session time zone, which shifts the buckets on a server
/// or role that is not on UTC. PostgreSQL converts to UTC first (<c>timezone('UTC', x)</c>); SQL Server's
/// <c>datetime2</c> carries no zone and Beacon stores UTC in it, so it truncates as is. Evaluated client-side the same way for in-memory sources.
/// </summary>
public static class UtcBuckets
{
    private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    // The client-side bodies serve in-memory queryables (test doubles); EF translates the calls in SQL instead.
    public static DateTime Day(DateTime value) =>
        AsUtc(value).Date;

    public static DateTime Hour(DateTime value)
    {
        var utc = AsUtc(value);
        return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0);
    }

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;

    internal static void Register(ModelBuilder modelBuilder, string? providerName)
    {
        var postgres = providerName == NpgsqlProviderName;

        modelBuilder.HasDbFunction(Method(nameof(Day)))
            .HasTranslation(x => postgres ? Truncate("day", x[0]) : SqlServerDay(x[0]));
        modelBuilder.HasDbFunction(Method(nameof(Hour)))
            .HasTranslation(x => postgres ? Truncate("hour", x[0]) : SqlServerHour(x[0]));
    }

    private static MethodInfo Method(string name) =>
        typeof(UtcBuckets).GetMethod(name, [typeof(DateTime)])!;

    private static SqlExpression Truncate(string unit, SqlExpression value)
    {
        var utc = new SqlFunctionExpression(
            "timezone",
            [new SqlFragmentExpression("'UTC'"), value],
            true,
            [false, true],
            typeof(DateTime),
            value.TypeMapping);

        return new SqlFunctionExpression(
            "date_trunc",
            [new SqlFragmentExpression($"'{unit}'"), utc],
            true,
            [false, true],
            typeof(DateTime),
            value.TypeMapping);
    }

    private static SqlExpression SqlServerDay(SqlExpression value) =>
        new SqlFunctionExpression(
            "CONVERT",
            [new SqlFragmentExpression("datetime2"), new SqlFunctionExpression("CONVERT", [new SqlFragmentExpression("date"), value], true, [false, true], typeof(DateTime), value.TypeMapping)],
            true,
            [false, true],
            typeof(DateTime),
            value.TypeMapping);

    private static SqlExpression SqlServerHour(SqlExpression value) =>
        new SqlFunctionExpression(
            "DATEADD",
            [
                new SqlFragmentExpression("hour"),
                new SqlFunctionExpression("DATEDIFF", [new SqlFragmentExpression("hour"), new SqlFragmentExpression("0"), value], true, [false, false, true], typeof(int), value.TypeMapping),
                new SqlFragmentExpression("0")
            ],
            true,
            [false, true, false],
            typeof(DateTime),
            value.TypeMapping);
}
