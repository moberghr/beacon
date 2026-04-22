namespace Beacon.Connector.Databricks;

public class DatabricksConfiguration
{
    public required string Host { get; set; }
    public required string HttpPath { get; set; }
    public required string Token { get; set; }
    public string? Catalog { get; set; }
    public string? Schema { get; set; }
    public int QueryTimeoutSeconds { get; set; } = 300;
}
