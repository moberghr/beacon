namespace Beacon.Core.Configuration;

/// <summary>
/// Which addresses the HTTP data-source connectors (the REST API connector and Databricks) connect to, bound from
/// <c>Beacon:DataSources:Outbound</c>. Their connect-time check resolves the host itself and connects only to an address
/// it allows: loopback (lifted by <see cref="DataSourceTransportOptions.AllowInsecure"/> on development hosts),
/// link-local and cloud metadata, unspecified, multicast and reserved addresses are always refused; private networks
/// follow <see cref="AllowPrivateNetworks"/>.
/// </summary>
public sealed class DataSourceOutboundOptions
{
    public const string SectionName = "Beacon:DataSources:Outbound";

    /// <summary>
    /// Lets the HTTP data-source connectors reach private networks (10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16,
    /// 100.64.0.0/10, fc00::/7). Default true, so an API or warehouse inside the host's network keeps working; set false
    /// where every HTTP source is on the public internet, or list the allowed hosts in <c>Beacon:DataSources:AllowedHosts</c>.
    /// </summary>
    public bool AllowPrivateNetworks { get; set; } = true;
}
