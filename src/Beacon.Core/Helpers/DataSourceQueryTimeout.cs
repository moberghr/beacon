namespace Beacon.Core.Helpers;

/// <summary>
/// The bounds of the query timeout a warehouse data source stores in its connection settings (BigQuery, Databricks and
/// CloudWatch <c>QueryTimeoutSeconds</c>): whatever was stored, one statement waits at most <see cref="MaxSeconds"/>, and
/// a value of zero or less means <see cref="DefaultSeconds"/>.
/// </summary>
public static class DataSourceQueryTimeout
{
    /// <summary>The longest wait for one statement: 15 minutes.</summary>
    public const int MaxSeconds = 900;

    /// <summary>The wait when none, or one of zero or less, is stored.</summary>
    public const int DefaultSeconds = 300;

    /// <summary>The stored <paramref name="seconds"/>, at most <see cref="MaxSeconds"/>; <see cref="DefaultSeconds"/> when zero or less.</summary>
    public static int Clamp(int seconds)
    {
        return seconds <= 0 ? DefaultSeconds : Math.Min(seconds, MaxSeconds);
    }
}
