using System.Diagnostics;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;

namespace Beacon.Core.Helpers;

    public class BulkOptions
{
    /// <summary>
    /// Properties that should never be updated or inserted (like auto generated ids)
    /// </summary>
    public string[] IgnoredProperties { get; set; } = [];

    /// <summary>
    /// Columns that uniquely identify the row that can be used to match data with table (ssn, claim key etc)
    /// </summary>
    public string[] MatchOnColumns { get; set; } = [];

    /// <summary>
    /// Which columns to check for differences in values
    /// Empty list for all columns
    /// List of items to only use specific columns
    /// </summary>
    public string[] UpdateIfColumnsChanged { get; set; } = [];

    /// <summary>
    /// Columns ignored when updating rows, for example create date or similar
    /// </summary>
    public string[] DoNotUpdateColumns { get; set; } = [];

    /// <summary>
    /// When updating only update specified columns
    /// </summary>
    public string[] OnlyUpdateColumns { get; set; } = [];

}

public static class BulkExtension
{
    public static async Task<BulkResult<T>> PostgresBulkUpsertAsync<T>(this DbContext db, IEnumerable<T> items, BulkOptions options) where T : class
    {
        var entityType = db.Model.FindEntityType(typeof(T));
        Debug.Assert(entityType != null, "entity type not found");

        var schema = entityType.GetSchema();
        var tableName = entityType.GetTableName() + Guid.NewGuid().ToString("N");
        var targetTable = $"\"{schema}\".\"{entityType.GetTableName()}\"";
        var tempTable = $"\"{tableName}\"";

        var allColumns = entityType.GetProperties().ToList();
        var columns = allColumns.Where(x => !options.IgnoredProperties.Contains(x.Name)).ToList();
        var joinedColumns = JoinColumns(", ", columns, x => x);
        var joinedColumnsWithS = JoinColumns(", ", columns, x => $"s.{x}");

        var conn = db.Database.GetDbConnection() as NpgsqlConnection;
        Debug.Assert(conn != null, "no connection to db");

        // Only open connection if not already open
        var shouldCloseConnection = conn.State != System.Data.ConnectionState.Open;
        if (shouldCloseConnection)
        {
            conn.Open();
        }

        var sw = Stopwatch.StartNew();

        // create temp table
        await conn.ExecuteAsync($"SELECT {joinedColumns} INTO TEMPORARY {tempTable} FROM {targetTable} LIMIT 0;");

        // bulk insert to temp table
        await using (var import = await conn.BeginBinaryImportAsync($"COPY {tempTable} ({joinedColumns}) FROM STDIN BINARY;"))
        {
            foreach (var item in items)
            {
                await import.StartRowAsync();
                foreach (var column in columns)
                {
                    if (column.ClrType.IsEnum || Nullable.GetUnderlyingType(column.ClrType)?.IsEnum == true)
                    {
                        await import.WriteAsync(Convert.ToInt32(column.GetGetter().GetClrValue(item)), column.GetColumnType());
                    }
                    else
                        await import.WriteAsync(column.GetGetter().GetClrValue(item), Normalize(column.GetColumnType()));
                }
            }

            await import.CompleteAsync();
        }

        var bulkInsertTime = sw.Elapsed;
        sw.Restart();

        if (!options.MatchOnColumns.Any())
            throw new InvalidOperationException("You must specify columns to match rows");

        // get columns to be updated
        // include only update columns if specified, exclude do not update columns
        var columnsForUpdate = columns
                               .Select(x => x.GetColumnName())
                               .Where(x => !options.OnlyUpdateColumns.Any() || options.OnlyUpdateColumns.Contains(x))
                               .Where(x => !options.DoNotUpdateColumns.Contains(x));

        var mergeQuery = $"""
                          merge into {targetTable} as t 
                          using {tempTable} as s on {JoinColumns(" AND ", options.MatchOnColumns, col => $"s.{col} = t.{col}")}
                          when not matched then 
                              insert ({joinedColumns})
                              values ({joinedColumnsWithS})
                          when matched {(options.UpdateIfColumnsChanged.Any() ? $"and {JoinColumns(" OR ", options.UpdateIfColumnsChanged, col => $"s.{col} != t.{col}")}" : "")} then
                              update set
                                 {JoinColumns(", ", columnsForUpdate, col => $"{col} = s.{col}")}
                          """;

        int rowsChanged = await conn.ExecuteAsync(mergeQuery);

        var mergeTime = sw.Elapsed;
        sw.Stop();

        // Only close connection if we opened it
        if (shouldCloseConnection)
        {
            await conn.CloseAsync();
        }

        return new BulkResult<T>
        {
            Inserted = rowsChanged,
            Updated = 0,
            InsertTime = bulkInsertTime,
            MergeTime = mergeTime,
        };
    }

    private static string Normalize(string columnName)
    {
        // Check if the column name matches timestamp(n) with time zone or timestamp(n) without time zone
        if (columnName.StartsWith("timestamp(", StringComparison.OrdinalIgnoreCase) &&
            columnName.EndsWith("time zone", StringComparison.OrdinalIgnoreCase))
        {
            // Remove the precision and parentheses
            return columnName.EndsWith("with time zone", StringComparison.OrdinalIgnoreCase) ?
                "timestamp with time zone" :
                "timestamp without time zone";
        }

        return columnName;
    }

    private static string JoinColumns(string separator, IEnumerable<string> columns, Func<string, string> map)
    {
        return string.Join(separator, columns.Select(col => map(Escape(col))));
    }

    private static string JoinColumns(string separator, IEnumerable<IProperty> columns, Func<string, string> map)
    {
        return string.Join(separator, columns.Select(col => map(Escape(col.GetColumnName()))));
    }

    private static string Escape(string col)
    {
        return "\"" + col + "\"";
    }
}

public class BulkResult<T>
{
    public int Inserted { get; init; }
    public int Updated { get; init; }
    public TimeSpan InsertTime { get; set; }
    public TimeSpan MergeTime { get; set; }
}
