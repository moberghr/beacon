using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Enums;
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
    private static readonly IPNetwork[] NonPublicIPv4 =
    [
        IPNetwork.Parse("0.0.0.0/8"),
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("168.63.129.16/32"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.0.0.0/24"),
        IPNetwork.Parse("192.0.2.0/24"),
        IPNetwork.Parse("192.88.99.0/24"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("198.18.0.0/15"),
        IPNetwork.Parse("198.51.100.0/24"),
        IPNetwork.Parse("203.0.113.0/24"),
        IPNetwork.Parse("224.0.0.0/4"),
        IPNetwork.Parse("240.0.0.0/4"),
    ];
    // IPv6 is public only inside global unicast (2000::/3), less these special blocks. Everything outside it is
    // reserved or special: unspecified, loopback, IPv4-compatible and IPv4-translated (which fail closed rather than
    // trusting the embedded address), discard, local-use NAT64, unique-local, link-local, site-local and multicast.
    private static readonly IPNetwork GlobalUnicast = IPNetwork.Parse("2000::/3");
    private static readonly IPNetwork[] NonPublicIPv6 =
    [
        // Teredo hides the client address; nothing legitimate is served from it.
        IPNetwork.Parse("2001::/32"),
        IPNetwork.Parse("2001:2::/48"),
        IPNetwork.Parse("2001:10::/28"),
        IPNetwork.Parse("2001:20::/28"),
        IPNetwork.Parse("2001:db8::/32"),
        IPNetwork.Parse("3fff::/20"),
    ];
    // Never a notification target, so a host-name allow-list entry cannot reach them (only an explicit address or range
    // entry can): link-local, where cloud metadata services live, the Azure wire server and the AWS IPv6 metadata
    // address; plus the IPv6 forms whose embedded address cannot be read (local-use NAT64, Teredo).
    private static readonly IPNetwork[] MetadataRanges =
    [
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("168.63.129.16/32"),
        IPNetwork.Parse("fe80::/10"),
        IPNetwork.Parse("fd00:ec2::254/128"),
        IPNetwork.Parse("64:ff9b:1::/48"),
        IPNetwork.Parse("2001::/32"),
    ];
    private static readonly IPNetwork WellKnownNat64 = IPNetwork.Parse("64:ff9b::/96");
    private static readonly IPNetwork Translated = IPNetwork.Parse("::ffff:0:0:0/96");
    private static readonly IPNetwork Compatible = IPNetwork.Parse("::/96");
    private static readonly IPNetwork SixToFour = IPNetwork.Parse("2002::/16");
    private readonly Dictionary<NotificationType, List<IPNetwork>> _allowedNetworks = [];
    private readonly Dictionary<NotificationType, List<string>> _allowedHosts = [];
    private readonly List<IPNetwork> _nat64Prefixes = [WellKnownNat64];
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

        foreach (var entry in options.Value.Nat64Prefixes)
        {
            if (TryParseNat64Prefix(entry, out var prefix))
            {
                _nat64Prefixes.Add(prefix);
            }
        }
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
    /// True for an address on the public internet, judged with the well-known NAT64 prefix only. An IPv4 address carried
    /// in an IPv6 form (mapped, NAT64 or 6to4) is judged by the IPv4 address it carries.
    /// </summary>
    public static bool IsPublic(IPAddress address)
    {
        return IsPublic(address, [WellKnownNat64]);
    }

    /// <summary>Like <see cref="IsPublic(IPAddress)"/>, with the configured NAT64 prefixes as well.</summary>
    public bool IsPublicAddress(IPAddress address)
    {
        return IsPublic(address, _nat64Prefixes);
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
        if (IsHostAllowed(type, host) && !IsMetadata(address))
        {
            return true;
        }

        var embedded = EmbeddedIPv4(address, _nat64Prefixes);
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
        return requestUri != null
            && requestUri.Port == endpoint.Port
            && string.Equals(requestUri.IdnHost.Trim('[', ']'), endpoint.Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase);
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

        Exception? lastFailure = null;
        foreach (var address in allowed)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                lastFailure = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw lastFailure!;
    }

    private static bool IsPublic(IPAddress address, IReadOnlyList<IPNetwork> nat64Prefixes)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !NonPublicIPv4.Any(x => x.Contains(address));
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return IsPublic(address.MapToIPv4(), nat64Prefixes);
        }

        if (nat64Prefixes.Any(x => x.Contains(address)) || SixToFour.Contains(address))
        {
            return IsPublic(EmbeddedIPv4(address, nat64Prefixes)!, nat64Prefixes);
        }

        return GlobalUnicast.Contains(address)
            && !NonPublicIPv6.Any(x => x.Contains(address));
    }

    private bool IsMetadata(IPAddress address)
    {
        var embedded = EmbeddedIPv4(address, _nat64Prefixes);

        return MetadataRanges.Any(x => x.Contains(address) || (embedded != null && x.Contains(embedded)));
    }

    /// <summary>The IPv4 address an IPv6 address carries (mapped, translated, compatible, NAT64, 6to4), or null.</summary>
    private static IPAddress? EmbeddedIPv4(IPAddress address, IReadOnlyList<IPNetwork> nat64Prefixes)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        if (Translated.Contains(address) || Compatible.Contains(address) || nat64Prefixes.Any(x => x.Contains(address)))
        {
            return new IPAddress(bytes[12..16]);
        }

        if (SixToFour.Contains(address))
        {
            return new IPAddress(bytes[2..6]);
        }

        return null;
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
