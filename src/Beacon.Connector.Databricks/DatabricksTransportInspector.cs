using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Providers;

namespace Beacon.Connector.Databricks;

/// <summary>The Databricks client only speaks HTTPS and checks the server's certificate: every connection passes.</summary>
public sealed class DatabricksTransportInspector() : TlsOnlyTransportInspector(DataSourceType.Databricks);
