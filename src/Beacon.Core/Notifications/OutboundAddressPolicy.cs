using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Enums;
using Beacon.Core.Http;
using IPNetwork = System.Net.IPNetwork;

namespace Beacon.Core.Notifications;

/// <summary>
/// Address check for notification calls. Connecting directly (the default), the handler's connect callback resolves
/// the host itself, drops every address that is not public (loopback, private, CGNAT, link-local including cloud
/// metadata, the Azure wire server, unique-local, multicast, reserved and documentation ranges, IPv4-translated
/// addresses, and the IPv4 addresses carried inside mapped, NAT64 and 6to4 IPv6 addresses) unless the notification
/// type's <c>AllowedPrivateNetworks</c> entry lists the host or the address, and connects to an address it vetted, so a
/// DNS answer that changes between the check and the connect cannot redirect the call.
/// <para>
/// With <see cref="NotificationChannelOptions.UseSystemProxy"/> the connection goes to the proxy, which the connect
/// callback lets through; <see cref="EnsureDestinationAllowedAsync"/> then checks the destination before the request is
/// sent (every resolved address must be allowed), and the proxy is trusted to enforce its own egress policy. A
/// destination the proxy configuration bypasses is still connected to directly and vetted as above.
/// </para>
/// </summary>
internal sealed class OutboundAddressPolicy
{
    private readonly Dictionary<NotificationType, List<IPNetwork>> _allowedNetworks = [];
    private readonly Dictionary<NotificationType, List<string>> _allowedHosts = [];
    private readonly IReadOnlyList<IPNetwork> _nat64Prefixes;
    private readonly IHostAddressResolver _resolver;

    public OutboundAddressPolicy(IOptions<NotificationChannelOptions> options, IHostAddressResolver resolver)
    {
        _resolver = resolver;
        UsesSystemProxy = options.Value.UseSystemProxy;

        foreach (var type in HttpTypes)
        {
            _allowedNetworks[type] = [];
            _allowedHosts[type] = [];
            foreach (var entry in options.Value.AllowedPrivateNetworks.For(type))
            {
                if (TryParseNetwork(entry, out var network))
                {
                    _allowedNetworks[type].Add(network);
                }
                else if (HostPattern.IsValid(entry))
                {
                    _allowedHosts[type].Add(entry.Trim());
                }
            }
        }

        _nat64Prefixes = Nat64PrefixesOf(options.Value);
    }

    /// <summary>The notification types that make HTTP calls, each with its own private-network allowances.</summary>
    public static IReadOnlyList<NotificationType> HttpTypes { get; } =
        [NotificationType.Webhook, NotificationType.Slack, NotificationType.Teams, NotificationType.Jira];

    /// <summary>Whether notification calls go through the system proxy (<c>Beacon:Notifications:UseSystemProxy</c>).</summary>
    public bool UsesSystemProxy { get; }

    public static bool IsValidAllowListEntry(string? entry)
    {
        return TryParseNetwork(entry, out _) || HostPattern.IsValid(entry);
    }

    /// <summary>A NAT64 prefix entry: an IPv6 <c>/96</c> range with no host bits set.</summary>
    public static bool IsValidNat64Prefix(string? entry)
    {
        return TryParseNat64Prefix(entry, out _);
    }

    /// <summary>
    /// The NAT64 prefixes of this network: the well-known one and the valid entries of
    /// <c>Beacon:Notifications:Nat64Prefixes</c> (the startup validation refuses an invalid one). The data-source and LLM
    /// clients' address checks judge an address under one of them by the IPv4 address it carries, as the notification
    /// check does.
    /// </summary>
    public static IReadOnlyList<IPNetwork> ConfiguredNat64Prefixes(IServiceProvider? services)
    {
        var options = services?.GetService<IOptions<NotificationChannelOptions>>()?.Value;

        return options == null ? OutboundAddresses.WellKnownNat64Prefixes : Nat64PrefixesOf(options);
    }

    /// <summary>
    /// True for an address on the public internet, judged with the well-known NAT64 prefix only. An IPv4 address carried
    /// in an IPv6 form (mapped, NAT64 or 6to4) is judged by the IPv4 address it carries.
    /// </summary>
    public static bool IsPublic(IPAddress address)
    {
        return OutboundAddresses.IsPublic(address, OutboundAddresses.WellKnownNat64Prefixes);
    }

    /// <summary>Like <see cref="IsPublic(IPAddress)"/>, with the configured NAT64 prefixes as well.</summary>
    public bool IsPublicAddress(IPAddress address)
    {
        return OutboundAddresses.IsPublic(address, _nat64Prefixes);
    }

    /// <summary>Whether the type's <c>AllowedPrivateNetworks</c> names <paramref name="host"/> (exactly or by wildcard).</summary>
    public bool IsHostAllowed(NotificationType type, string host)
    {
        return _allowedHosts.TryGetValue(type, out var hosts) && HostPattern.MatchesAny(hosts, host);
    }

    /// <summary>
    /// Whether a <paramref name="type"/> notification call to <paramref name="host"/> may connect to
    /// <paramref name="address"/>: a public address, one the type's configuration allows by address or range, or one an
    /// allowed host name resolves to, except link-local and metadata addresses (also when carried in an IPv6 form).
    /// </summary>
    public bool IsAllowed(NotificationType type, string host, IPAddress address)
    {
        if (IsHostAllowed(type, host) && !OutboundAddresses.IsMetadata(address, _nat64Prefixes))
        {
            return true;
        }

        var embedded = OutboundAddresses.EmbeddedIPv4(address, _nat64Prefixes);
        if (_allowedNetworks.TryGetValue(type, out var networks)
            && networks.Any(x => x.Contains(address) || (embedded != null && x.Contains(embedded))))
        {
            return true;
        }

        return IsPublicAddress(address);
    }

    /// <summary>
    /// The connect callback of the <paramref name="type"/> client: resolves the endpoint, keeps the allowed addresses and
    /// connects to the first that answers. Throws <see cref="OutboundConnectionBlockedException"/> when none is allowed.
    /// In proxy mode a connection to the proxy itself is not vetted: the tunnel (<c>CONNECT</c>) an https call opens
    /// through it, or any endpoint other than the request's own host.
    /// </summary>
    public ValueTask<Stream> ConnectAsync(NotificationType type, SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var request = context.InitialRequestMessage;
        var checkAddresses = !UsesSystemProxy
            || (request.Method != HttpMethod.Connect && IsDestination(request.RequestUri, context.DnsEndPoint));

        return ConnectAsync(type, context.DnsEndPoint, checkAddresses, cancellationToken);
    }

    /// <summary>
    /// The proxy-mode check before a request is sent: resolves the destination's host and throws
    /// <see cref="OutboundConnectionBlockedException"/> unless it resolves and every address is allowed.
    /// </summary>
    public async Task EnsureDestinationAllowedAsync(NotificationType type, Uri destination, CancellationToken cancellationToken)
    {
        var host = destination.IdnHost.Trim('[', ']');

        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await _resolver.ResolveAsync(host, cancellationToken);
            }
            catch (SocketException)
            {
                throw new OutboundConnectionBlockedException();
            }
        }

        if (addresses.Length == 0 || addresses.Any(x => !IsAllowed(type, host, x)))
        {
            throw new OutboundConnectionBlockedException();
        }
    }

    /// <summary>Whether a connection endpoint is the request's own host and port, rather than a proxy.</summary>
    internal static bool IsDestination(Uri? requestUri, DnsEndPoint endpoint)
    {
        return OutboundAddresses.IsDestination(requestUri, endpoint);
    }

    internal async ValueTask<Stream> ConnectAsync(NotificationType type, DnsEndPoint endpoint, bool checkAddresses, CancellationToken cancellationToken)
    {
        var host = endpoint.Host.Trim('[', ']');

        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await _resolver.ResolveAsync(host, cancellationToken);

        var allowed = addresses
            .Where(x => !checkAddresses || IsAllowed(type, host, x))
            .ToList();

        if (allowed.Count == 0)
        {
            throw new OutboundConnectionBlockedException();
        }

        return await OutboundAddresses.ConnectToFirstAsync(allowed, endpoint.Port, cancellationToken);
    }

    private static List<IPNetwork> Nat64PrefixesOf(NotificationChannelOptions options)
    {
        var prefixes = new List<IPNetwork> { OutboundAddresses.WellKnownNat64 };
        foreach (var entry in options.Nat64Prefixes)
        {
            if (TryParseNat64Prefix(entry, out var prefix))
            {
                prefixes.Add(prefix);
            }
        }

        return prefixes;
    }

    private static bool TryParseNat64Prefix(string? entry, out IPNetwork prefix)
    {
        return TryParseNetwork(entry, out prefix)
            && entry!.Contains('/')
            && prefix.BaseAddress.AddressFamily == AddressFamily.InterNetworkV6
            && prefix.PrefixLength == 96;
    }

    private static bool TryParseNetwork(string? entry, out IPNetwork network)
    {
        network = default;
        if (string.IsNullOrWhiteSpace(entry))
        {
            return false;
        }

        var value = entry.Trim();
        if (value.Contains('/'))
        {
            // Strict: "10.1.2.3/8" is refused rather than read as 10.0.0.0/8, so a typo cannot open a wider range.
            return IPNetwork.TryParse(value, out network)
                && IPAddress.TryParse(value[..value.IndexOf('/')], out var baseAddress)
                && network.BaseAddress.Equals(baseAddress);
        }

        if (!IPAddress.TryParse(value, out var address))
        {
            return false;
        }

        var prefixLength = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        network = new IPNetwork(address, prefixLength);
        return true;
    }
}

/// <summary>Resolves host names for the notification address checks; replaceable in tests.</summary>
internal interface IHostAddressResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

internal sealed class DnsHostAddressResolver : IHostAddressResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        return Dns.GetHostAddressesAsync(host, cancellationToken);
    }
}

/// <summary>A notification call was refused before connecting: its host resolves only to addresses the policy blocks.</summary>
public sealed class OutboundConnectionBlockedException : IOException
{
    public OutboundConnectionBlockedException()
        : base("The destination address is not allowed for outbound notification calls.")
    {
    }
}
