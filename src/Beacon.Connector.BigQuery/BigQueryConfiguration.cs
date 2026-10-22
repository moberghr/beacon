namespace Beacon.Connector.BigQuery;

public class BigQueryConfiguration
{
    /// <summary>The bytes one query job may bill when the data source sets no limit of its own: 10 GiB.</summary>
    public const long DefaultMaximumBytesBilled = 10L * 1024 * 1024 * 1024;

    public required string ProjectId { get; set; }
    public string? DatasetId { get; set; }
    public string? Location { get; set; }
    public string? ServiceAccountJson { get; set; }

    /// <summary>How long a query may run; at most <see cref="Beacon.Core.Helpers.DataSourceQueryTimeout.MaxSeconds"/>.</summary>
    public int QueryTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// The most bytes one query job may bill; a job that would bill more fails without a charge. Null, zero or less means
    /// <see cref="DefaultMaximumBytesBilled"/>; the deployment's <see cref="BigQueryOptions.MaxBytesBilled"/> lowers it.
    /// </summary>
    public long? MaximumBytesBilled { get; set; }
}
