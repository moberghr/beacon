using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Providers;

namespace Beacon.Connector.CloudWatch;

/// <summary>The CloudWatch client only speaks HTTPS and checks the server's certificate: every connection passes.</summary>
public sealed class CloudWatchTransportInspector() : TlsOnlyTransportInspector(DataSourceType.CloudWatch);
