namespace Beacon.Core.Models.Providers;

public class ProviderQueryResult
{
    public List<Dictionary<string, object?>> Rows { get; set; } = new();
    public int TotalRows { get; set; }
    public double ExecutionTimeMs { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Provider-specific metadata
    /// For CloudWatch: { "QueryId": "...", "Status": "Complete", "BytesScanned": 12345 }
    /// For Database: { "RowsAffected": 10 }
    /// </summary>
    public Dictionary<string, object?> Metadata { get; set; } = new();
}
