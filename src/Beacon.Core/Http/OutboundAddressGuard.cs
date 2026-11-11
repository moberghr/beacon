using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Beacon.Core.Notifications;

namespace Beacon.Core.Http;

/// <summary>
/// The connect-time address check of an outbound client (the data-source clients, the LLM clients). The handler's connect
/// callback resolves the host itself, drops every address the rules refuse and connects only to an address it vetted, so a
/// DNS answer that changes between a check and the connect cannot redirect the call. Link-local and cloud metadata
/// addresses, unspecified, multicast and reserved addresses are always refused, also when carried in an IPv6 form
/// (mapped, translated, compatible, NAT64, 6to4); loopback and private-network addresses as the
/// <see cref="OutboundAddressRules"/> say.
/// <para>
/// Through the system proxy the connection goes to the proxy, which the callback lets through: the tunnel an https call
/// opens through it, and a connection to another endpoint than the request's own host while the proxy applies to the
/// request. <see cref="OutboundAddressCheckHandler"/> then checks the destination before the request is sent (every
/// address it resolves to must be allowed; a name the Beacon host cannot resolve is left to the proxy), and the proxy is
/// trusted to enforce its own egress policy.
/// </para>
/// </summary>
internal sealed class OutboundAddressGuard(OutboundAddressRules rules, IHostAddressResolver resolver)
{
    public OutboundAddressRules Rules => rules;

    /// <summary>Why the rules refuse <paramref name="address"/>, or null when they allow it.</summary>
    public OutboundAddressRefusal? Judge(IPAddress address)
    {
        return OutboundAddresses.Classify(address) switch
        {
            OutboundAddressKind.Public => null,
            OutboundAddressKind.PrivateNetwork => rules.AllowPrivateNetworks ? null : OutboundAddressRefusal.PrivateNetwork,
            OutboundAddressKind.Loopback => rules.AllowLoopback ? null : OutboundAddressRefusal.Loopback,
            OutboundAddressKind.LinkLocal => OutboundAddressRefusal.LinkLocal,
            _ => OutboundAddressRefusal.NotRoutable,
        };
    }

    /// <summary>
    /// The connect callback: resolves the endpoint, keeps the allowed addresses and connects to the first that answers.
    /// Throws <see cref="OutboundAddressRefusedException"/> when none is allowed. A connection to the system proxy is not
    /// vetted (see the class summary).
    /// </summary>
    public ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var request = context.InitialRequestMessage;
        var toProxy = request.Method == HttpMethod.Connect
            || (request.RequestUri != null
                && !OutboundAddresses.IsDestination(request.RequestUri, context.DnsEndPoint)
                && OutboundAddresses.UsesSystemProxy(request.RequestUri));

        return ConnectAsync(context.DnsEndPoint, checkAddresses: !toProxy, cancellationToken);
    }

    /// <summary>
    /// The check before a request goes through the system proxy: throws <see cref="OutboundAddressRefusedException"/>
    /// when the destination is, or resolves to, any address the rules refuse.
    /// </summary>
    public async Task EnsureDestinationAllowedAsync(Uri destination, CancellationToken cancellationToken)
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
                addresses = await resolver.ResolveAsync(host, cancellationToken);
            }
            catch (SocketException)
            {
                // Only the proxy resolves it: the proxy's own egress policy applies.
                return;
            }
        }

        foreach (var address in addresses)
        {
            if (Judge(address) is { } refusal)
            {
                throw new OutboundAddressRefusedException(refusal);
            }
        }
    }

    internal async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, bool checkAddresses, CancellationToken cancellationToken)
    {
        var host = endpoint.Host.Trim('[', ']');

        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await resolver.ResolveAsync(host, cancellationToken);

        if (!checkAddresses)
        {
            return await OutboundAddresses.ConnectToFirstAsync(addresses, endpoint.Port, cancellationToken);
        }

        var allowed = new List<IPAddress>();
        OutboundAddressRefusal? refusal = null;
        foreach (var address in addresses)
        {
            var verdict = Judge(address);
            if (verdict == null)
            {
                allowed.Add(address);
            }
            else
            {
                refusal ??= verdict;
            }
        }

        if (allowed.Count == 0 && refusal is { } reason)
        {
            throw new OutboundAddressRefusedException(reason);
        }

        return await OutboundAddresses.ConnectToFirstAsync(allowed, endpoint.Port, cancellationToken);
    }
}

/// <summary>
/// Which addresses beyond the public internet an outbound client may connect to. Link-local and cloud metadata,
/// unspecified, multicast and reserved addresses are never allowed.
/// </summary>
internal sealed record OutboundAddressRules(bool AllowLoopback, bool AllowPrivateNetworks)
{
    /// <summary>The LLM clients: loopback and private networks are allowed (local and on-premises model gateways).</summary>
    public static OutboundAddressRules Llm { get; } = new(AllowLoopback: true, AllowPrivateNetworks: true);
}

/// <summary>Why an outbound client refused an address.</summary>
internal enum OutboundAddressRefusal
{
    Loopback,
    PrivateNetwork,
    LinkLocal,
    NotRoutable,
}

/// <summary>
/// An outbound connection refused by its client's address rules, before connecting. Its message names no address;
/// <see cref="OutboundAddressCheckHandler"/> turns it into the client's own error.
/// </summary>
internal sealed class OutboundAddressRefusedException(OutboundAddressRefusal refusal)
    : IOException("The destination address is not allowed for this outbound client.")
{
    public OutboundAddressRefusal Refusal => refusal;
}

/// <summary>
/// Runs on every request of a client whose primary handler connects through an <see cref="OutboundAddressGuard"/>: keeps
/// the request on connections the connect callback sees (no HTTP/3, which runs over QUIC, and no CONNECT request of the
/// caller's own), checks the destination before sending while the system proxy carries the request, and turns a refusal
/// (here or at connect time) into the client's own error through <paramref name="refuse"/>, whose message names the rule
/// and never the address or URL. Logs nothing.
/// </summary>
internal sealed class OutboundAddressCheckHandler(OutboundAddressGuard guard, Func<OutboundAddressRefusal, Exception> refuse)
    : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Connect)
        {
            throw new InvalidOperationException("This client does not send CONNECT requests.");
        }

        if (request.Version.Major >= 3)
        {
            request.Version = HttpVersion.Version20;
        }

        if (request.VersionPolicy == HttpVersionPolicy.RequestVersionOrHigher)
        {
            request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        }

        try
        {
            if (request.RequestUri is { IsAbsoluteUri: true } destination && OutboundAddresses.UsesSystemProxy(destination))
            {
                await guard.EnsureDestinationAllowedAsync(destination, cancellationToken);
            }

            return await base.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (RefusalIn(ex) is { } refusal)
        {
            throw refuse(refusal);
        }
    }

    private static OutboundAddressRefusal? RefusalIn(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is OutboundAddressRefusedException refused)
            {
                return refused.Refusal;
            }
        }

        return null;
    }
}
