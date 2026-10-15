using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Providers;

namespace Beacon.Connector.BigQuery;

/// <summary>The BigQuery client only speaks HTTPS and checks the server's certificate: every connection passes.</summary>
public sealed class BigQueryTransportInspector() : TlsOnlyTransportInspector(DataSourceType.BigQuery);
