namespace Semantico.Core.Adapters.Shared;

/// <summary>
/// Extension methods for safely working with query results.
/// </summary>
internal static class QueryResultExtensions
{
    /// <summary>
    /// Safely gets column names from a collection of query result records.
    /// Returns empty list if the collection is empty.
    /// </summary>
    /// <param name="records">Collection of query result records</param>
    /// <param name="maxColumns">Maximum number of columns to return</param>
    /// <returns>List of column names, or empty list if no records</returns>
    public static List<string> GetColumnNamesSafe(this List<IDictionary<string, object?>> records, int maxColumns = int.MaxValue)
    {
        if (records == null || records.Count == 0)
        {
            return new List<string>();
        }

        return records[0].Keys.Take(maxColumns).ToList();
    }

    /// <summary>
    /// Checks if the query result records collection has data.
    /// </summary>
    /// <param name="records">Collection of query result records</param>
    /// <returns>True if records exist, false otherwise</returns>
    public static bool HasRecords(this List<IDictionary<string, object?>> records)
    {
        return records != null && records.Count > 0;
    }

    /// <summary>
    /// Gets the first record safely, returning null if collection is empty.
    /// </summary>
    /// <param name="records">Collection of query result records</param>
    /// <returns>First record or null if empty</returns>
    public static IDictionary<string, object?>? GetFirstRecordSafe(this List<IDictionary<string, object?>> records)
    {
        if (records == null || records.Count == 0)
        {
            return null;
        }

        return records[0];
    }
}
