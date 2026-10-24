using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using Beacon.Core.Mcp;

namespace Beacon.Core.Services.Security;

/// <summary>
/// The partitions the request limits of <c>Beacon:RateLimits</c> count by. An authenticated caller is one partition per
/// credential, an anonymous one per client address: the address the host reports (<c>Connection.RemoteIpAddress</c>,
/// which <c>UseForwardedHeaders</c> rewrites only as the host configured it); Beacon never reads forwarding headers itself.
/// </summary>
public static class RateLimitPartitions
{
    // The bytes of an IPv6 address that name its /64 block.
    private const int Ipv6PrefixBytes = 8;

    /// <summary>
    /// The partition of a request's caller: an API key by its id, a mapped MCP caller by its subject hash, any other
    /// authenticated caller by its name identifier; an anonymous one (or one that carries none of these) by
    /// <paramref name="remoteAddress"/>.
    /// </summary>
    public static string Of(ClaimsPrincipal? user, IPAddress? remoteAddress)
    {
        if (user?.Identity?.IsAuthenticated == true)
        {
            var apiKeyId = user.FindFirst(McpCallerClaimTypes.ApiKeyId)?.Value;
            if (!string.IsNullOrEmpty(apiKeyId))
            {
                return "api-key:" + apiKeyId;
            }

            var callerHash = user.FindFirst(McpCallerClaimTypes.CallerHash)?.Value;
            if (!string.IsNullOrEmpty(callerHash))
            {
                return "caller:" + callerHash;
            }

            var nameIdentifier = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!string.IsNullOrEmpty(nameIdentifier))
            {
                return "user:" + nameIdentifier;
            }
        }

        return OfAddress(remoteAddress);
    }

    /// <summary>
    /// The partition of a client address: an IPv4 address (also one mapped into IPv6) by the whole address, an IPv6 address
    /// by its /64 prefix, the block a single site or subscriber is normally given, so one client cannot draw a fresh budget
    /// from every address of its block. Every request without an address shares a single partition.
    /// </summary>
    public static string OfAddress(IPAddress? remoteAddress)
    {
        if (remoteAddress == null)
        {
            return "ip:unknown";
        }

        if (remoteAddress.IsIPv4MappedToIPv6)
        {
            return "ip:" + remoteAddress.MapToIPv4();
        }

        if (remoteAddress.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return "ip:" + remoteAddress;
        }

        var bytes = remoteAddress.GetAddressBytes();
        Array.Clear(bytes, Ipv6PrefixBytes, bytes.Length - Ipv6PrefixBytes);

        return "ip:" + new IPAddress(bytes) + "/64";
    }

    /// <summary>Whole seconds for a <c>Retry-After</c> header, at least one.</summary>
    public static string RetryAfterSeconds(TimeSpan retryAfter)
    {
        return Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
    }
}
