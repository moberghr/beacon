namespace Beacon.Core.Models.DataSources;

public class AdHocQueryResult
{
    public bool Success { get; set; }

    public List<string> Columns { get; set; } = new();

    public List<Dictionary<string, object?>> Rows { get; set; } = new();

    /// <summary>The rows shown — at most <see cref="Constants.Query.MaxUiDisplayRows"/>.</summary>
    public int RowCount { get; set; }

    /// <summary>True when the statement had more rows than <see cref="Rows"/> shows; the rest were not read.</summary>
    public bool Truncated { get; set; }

    public TimeSpan ExecutionTime { get; set; }

    public string? Error { get; set; }
}
