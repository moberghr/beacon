namespace Semantico.Core.Models.DataSources;

public class AdHocQueryResult
{
    public bool Success { get; set; }

    public List<string> Columns { get; set; } = new();

    public List<Dictionary<string, object?>> Rows { get; set; } = new();

    public int RowCount { get; set; }

    public TimeSpan ExecutionTime { get; set; }

    public string? Error { get; set; }
}
