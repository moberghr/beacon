namespace Semantico.Connector.BigQuery;

public class BigQueryConfiguration
{
    public required string ProjectId { get; set; }
    public string? DatasetId { get; set; }
    public string? Location { get; set; }
    public string? ServiceAccountJson { get; set; }
    public int QueryTimeoutSeconds { get; set; } = 300;
}
