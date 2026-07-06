namespace Beacon.Core.Models.Providers;

public class ConnectionTestResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public double TestDurationMs { get; set; }

    /// <summary>
    /// Provider-specific connection info
    /// For Database: { "ServerVersion": "PostgreSQL 15.2" }
    /// For CloudWatch: { "Region": "us-east-1", "LogGroupCount": 5 }
    /// </summary>
    public Dictionary<string, object?> ConnectionInfo { get; set; } = new();
}
