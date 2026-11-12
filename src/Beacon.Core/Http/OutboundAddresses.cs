using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using IPNetwork = System.Net.IPNetwork;

namespace Beacon.Core.Http;

/// <summary>
/// The address facts every outbound connect-time check shares (the notification clients, the data-source clients and the
/// LLM clients): which addresses are public, which are loopback, link-local or cloud metadata, the IPv4 address an IPv6
/// form carries, whether a connection goes to the request's own host, and connecting to the first of a set of vetted
/// addresses that answers.
/// </summary>
internal static class OutboundAddresses
{
    /// <summary>The well-known NAT64 prefix (RFC 6052), the only one judged without configuration.</summary>
    public static readonly IPNetwork WellKnownNat64 = IPNetwork.Parse("64:ff9b::/96");

    public static readonly IReadOnlyList<IPNetwork> WellKnownNat64Prefixes = [WellKnownNat64];

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
    // Link-local, where cloud metadata services live, the Azure wire server and the AWS IPv6 metadata address; plus the
    // IPv6 forms whose embedded address cannot be read (local-use NAT64, Teredo).
    private static readonly IPNetwork[] MetadataRanges =
    [
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("168.63.129.16/32"),
        IPNetwork.Parse("fe80::/10"),
        IPNetwork.Parse("fd00:ec2::254/128"),
        IPNetwork.Parse("64:ff9b:1::/48"),
        IPNetwork.Parse("2001::/32"),
    ];
    // Metadata services outside link-local that the data-source and LLM clients never reach either: Alibaba Cloud's
    // (inside CGNAT space) and Google Cloud's IPv6 one (inside unique-local space).
    private static readonly IPNetwork[] OtherMetadataAddresses =
    [
        IPNetwork.Parse("100.100.100.200/32"),
        IPNetwork.Parse("fd20:ce::254/128"),
    ];
    private static readonly IPNetwork[] NotRoutableIPv4 =
    [
        IPNetwork.Parse("0.0.0.0/8"),
        IPNetwork.Parse("224.0.0.0/4"),
        IPNetwork.Parse("240.0.0.0/4"),
    ];
    private static readonly IPNetwork LoopbackIPv4 = IPNetwork.Parse("127.0.0.0/8");
    private static readonly IPNetwork MulticastIPv6 = IPNetwork.Parse("ff00::/8");
    private static readonly IPNetwork Translated = IPNetwork.Parse("::ffff:0:0:0/96");
    private static readonly IPNetwork Compatible = IPNetwork.Parse("::/96");
    private static readonly IPNetwork SixToFour = IPNetwork.Parse("2002::/16");

    /// <summary>
    /// True for an address on the public internet. An IPv4 address carried in an IPv6 form (mapped, NAT64 under one of
    /// <paramref name="nat64Prefixes"/>, or 6to4) is judged by the IPv4 address it carries.
    /// </summary>
    public static bool IsPublic(IPAddress address, IReadOnlyList<IPNetwork> nat64Prefixes)
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

    /// <summary>Link-local (cloud metadata) and the unreadable IPv6 forms, also when carried in an IPv6 form.</summary>
    public static bool IsMetadata(IPAddress address, IReadOnlyList<IPNetwork> nat64Prefixes)
    {
        var embedded = EmbeddedIPv4(address, nat64Prefixes);

        return MetadataRanges.Any(x => x.Contains(address) || (embedded != null && x.Contains(embedded)));
    }

    /// <summary>
    /// What kind of destination <paramref name="address"/> is for the data-source and LLM clients, judged with
    /// <paramref name="nat64Prefixes"/> (the well-known NAT64 prefix when none are given). An IPv4 address carried in an
    /// IPv6 form (mapped, translated, compatible, NAT64, 6to4) is judged by the IPv4 address it carries.
    /// </summary>
    public static OutboundAddressKind Classify(IPAddress address, IReadOnlyList<IPNetwork>? nat64Prefixes = null)
    {
        var prefixes = nat64Prefixes ?? WellKnownNat64Prefixes;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6Any))
            {
                return OutboundAddressKind.NotRoutable;
            }

            if (address.Equals(IPAddress.IPv6Loopback))
            {
                return OutboundAddressKind.Loopback;
            }

            var embedded = EmbeddedIPv4(address, prefixes);
            if (embedded != null)
            {
                return Classify(embedded);
            }

            if (IsMetadata(address, prefixes) || OtherMetadataAddresses.Any(x => x.Contains(address)))
            {
                return OutboundAddressKind.LinkLocal;
            }

            if (MulticastIPv6.Contains(address))
            {
                return OutboundAddressKind.NotRoutable;
            }

            return IsPublic(address, prefixes) ? OutboundAddressKind.Public : OutboundAddressKind.PrivateNetwork;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return OutboundAddressKind.NotRoutable;
        }

        if (LoopbackIPv4.Contains(address))
        {
            return OutboundAddressKind.Loopback;
        }

        if (IsMetadata(address, WellKnownNat64Prefixes) || OtherMetadataAddresses.Any(x => x.Contains(address)))
        {
            return OutboundAddressKind.LinkLocal;
        }

        if (NotRoutableIPv4.Any(x => x.Contains(address)))
        {
            return OutboundAddressKind.NotRoutable;
        }

        return IsPublic(address, WellKnownNat64Prefixes) ? OutboundAddressKind.Public : OutboundAddressKind.PrivateNetwork;
    }

    /// <summary>The IPv4 address an IPv6 address carries (mapped, translated, compatible, NAT64, 6to4), or null.</summary>
    public static IPAddress? EmbeddedIPv4(IPAddress address, IReadOnlyList<IPNetwork> nat64Prefixes)
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

    /// <summary>Whether a connection endpoint is the request's own host and port, rather than a proxy.</summary>
    public static bool IsDestination(Uri? requestUri, DnsEndPoint endpoint)
    {
        return requestUri != null
            && requestUri.Port == endpoint.Port
            && string.Equals(requestUri.IdnHost.Trim('[', ']'), endpoint.Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a handler that uses the system proxy (<see cref="SocketsHttpHandler.UseProxy"/> without its own
    /// <see cref="SocketsHttpHandler.Proxy"/>) sends a request for <paramref name="destination"/> through a proxy.
    /// </summary>
    public static bool UsesSystemProxy(Uri destination)
    {
        var proxy = HttpClient.DefaultProxy;

        return !proxy.IsBypassed(destination) && proxy.GetProxy(destination) != null;
    }

    /// <summary>Connects to the first of <paramref name="addresses"/> that answers, in order; throws the last failure.</summary>
    public static async ValueTask<Stream> ConnectToFirstAsync(IReadOnlyList<IPAddress> addresses, int port, CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken);
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

        throw lastFailure ?? new SocketException((int)SocketError.HostNotFound);
    }
}

/// <summary>What kind of destination an address is, for the outbound connect-time checks.</summary>
internal enum OutboundAddressKind
{
    /// <summary>An address on the public internet.</summary>
    Public,

    /// <summary>A private, shared (CGNAT), unique-local or other non-public address that is none of the kinds below.</summary>
    PrivateNetwork,

    /// <summary>127.0.0.0/8 and ::1.</summary>
    Loopback,

    /// <summary>Link-local addresses (169.254.0.0/16, fe80::/10), cloud metadata addresses and unreadable IPv6 forms.</summary>
    LinkLocal,

    /// <summary>Unspecified (0.0.0.0/8, ::), multicast and reserved addresses.</summary>
    NotRoutable,
}
