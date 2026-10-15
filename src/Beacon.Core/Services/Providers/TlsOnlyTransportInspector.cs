using Beacon.Core.Data.Enums;

namespace Beacon.Core.Services.Providers;

/// <summary>
/// The inspector of a connector whose client library only speaks HTTPS to a fixed or SDK-built endpoint and checks the
/// server's certificate, with no option in the connection data to turn that off (Databricks, BigQuery, CloudWatch):
/// every connection passes. Each connector derives its own type, so <c>TryAddEnumerable</c> keeps one per connector.
/// </summary>
public abstract class TlsOnlyTransportInspector(DataSourceType dataSourceType) : IDataSourceTransportInspector
{
    public DataSourceType DataSourceType => dataSourceType;

    public DatabaseEngineType? DatabaseEngineType => null;

    public DataSourceTransportVerdict Inspect(string connectionData)
    {
        return DataSourceTransportVerdict.Pass();
    }
}
