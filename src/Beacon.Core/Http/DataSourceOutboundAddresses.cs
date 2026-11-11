using Beacon.Core.Configuration;
using Beacon.Core.Models;
using Beacon.Core.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Beacon.Core.Http;

/// <summary>
/// The connect-time address check of the HTTP data-source clients (the REST API connector's and Databricks'): loopback is
/// refused unless <see cref="DataSourceOutboundOptions.AllowLoopback"/> or <see cref="DataSourceTransportOptions.AllowInsecure"/>
/// (development hosts), private networks as
/// <see cref="DataSourceOutboundOptions.AllowPrivateNetworks"/> says, link-local and cloud metadata, unspecified,
/// multicast and reserved addresses always, with the configured NAT64 prefixes as well as the well-known one. A refusal is a <see cref="DataSourceTransportException"/> naming the rule,
/// never the address, host or URL.
/// </summary>
internal static class DataSourceOutboundAddresses
{
    public static OutboundAddressRules Rules(IServiceProvider services)
    {
        var transport = services.GetService<IOptions<DataSourceTransportOptions>>()?.Value ?? new DataSourceTransportOptions();
        var outbound = services.GetService<IOptions<DataSourceOutboundOptions>>()?.Value ?? new DataSourceOutboundOptions();

        return new OutboundAddressRules(
            AllowLoopback: outbound.AllowLoopback || transport.AllowInsecure,
            AllowPrivateNetworks: outbound.AllowPrivateNetworks);
    }

    public static OutboundAddressGuard Guard(IServiceProvider services)
    {
        return new OutboundAddressGuard(
            Rules(services),
            services.GetService<IHostAddressResolver>() ?? new DnsHostAddressResolver(),
            OutboundAddressPolicy.ConfiguredNat64Prefixes(services));
    }

    /// <summary>The primary handler's connect callback and the check handler share one guard per handler chain.</summary>
    public static OutboundAddressCheckHandler CheckHandler(OutboundAddressGuard guard)
    {
        return new OutboundAddressCheckHandler(guard, x => new DataSourceTransportException(RefusalMessage(x)));
    }

    public static string RefusalMessage(OutboundAddressRefusal refusal)
    {
        return refusal switch
        {
            OutboundAddressRefusal.Loopback =>
                "The data source's host resolves to a loopback address, which data-source connections do not reach while Beacon:DataSources:Outbound:AllowLoopback is false.",
            OutboundAddressRefusal.PrivateNetwork =>
                "The data source's host resolves to a private network address, which data-source connections do not reach while Beacon:DataSources:Outbound:AllowPrivateNetworks is false.",
            OutboundAddressRefusal.LinkLocal =>
                "The data source's host resolves to a link-local or cloud metadata address, which data-source connections never reach.",
            _ =>
                "The data source's host resolves to an unspecified, multicast or reserved address, which data-source connections never reach.",
        };
    }
}
