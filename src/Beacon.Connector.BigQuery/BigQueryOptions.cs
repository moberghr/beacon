namespace Beacon.Connector.BigQuery;

/// <summary>
/// The deployment's bounds on every BigQuery data source, bound from <c>Beacon:DataSources:BigQuery</c>.
/// </summary>
public sealed class BigQueryOptions
{
    public const string SectionName = "Beacon:DataSources:BigQuery";

    /// <summary>
    /// The most bytes one query job may bill, whatever a data source's own
    /// <see cref="BigQueryConfiguration.MaximumBytesBilled"/> says: a larger source limit is lowered to it. Null (the
    /// default) sets no ceiling; when set, at least 1.
    /// </summary>
    public long? MaxBytesBilled { get; set; }
}
