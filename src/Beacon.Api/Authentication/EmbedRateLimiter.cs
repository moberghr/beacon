using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Beacon.Core.Services.Embed;
using Microsoft.Extensions.Options;

namespace Beacon.Api.Authentication;

/// <summary>
/// Per-minute limits on the anonymous embed routes (<c>Beacon:Embed:RateLimits</c>), self-contained like
/// <see cref="LoginRateLimiter"/>. A grant's exchange and render budgets are keyed on the grant a verified credential named
/// (never on an unverified token's claim, so nobody can spend a grant's budget without its secret or a valid token), which
/// bounds those partitions by the grants that exist. The embed page is anonymous, so it is limited per client address and
/// grant, never per grant alone. Refusals are counted per client address: only refusals count. On the render the budget
/// decides only how a refusal is answered (401/403 while it lasts, 429 after) and whether it is audited, never whether a
/// valid token is served; on the exchange and the page, which cost a lookup, a client past it is answered 429 before the
/// lookup (a valid secret from that address waits for the next minute). At most <see cref="MaxAuditedRefusalsPerMinute"/> refusals a minute reach the security audit
/// trail, whatever their source (the cap <see cref="EmbedAuditLimiter"/> shares with the render's busy outcomes). A client address is an IPv4 address, or an IPv6 address by its /64 network. Idle partitions
/// are removed by the limiters themselves. Behind a reverse proxy the host must run <c>UseForwardedHeaders</c> with its known
/// proxies, otherwise every viewer shares the proxy's address.
/// </summary>
internal sealed class EmbedRateLimiter : IDisposable
{
    public const int MaxAuditedRefusalsPerMinute = EmbedAuditLimiter.MaxPerMinute;

    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private readonly PartitionedRateLimiter<string> _grants;
    private readonly PartitionedRateLimiter<string> _refusals;
    private readonly EmbedAuditLimiter _audited;

    public EmbedRateLimiter(IOptions<EmbedOptions> options, EmbedAuditLimiter audited)
    {
        var limits = options.Value.RateLimits;
        _grants = PartitionedRateLimiter.Create<string, string>(
            x =>
                RateLimitPartition.GetFixedWindowLimiter(
                    x,
                    y => FixedWindow(PermitsOf(y, limits))));
        _refusals = PartitionedRateLimiter.Create<string, string>(
            x =>
                RateLimitPartition.GetFixedWindowLimiter(
                    x,
                    _ => FixedWindow(limits.RefusalsPerIpPerMinute)));
        _audited = audited;
    }

    /// <summary>Spends one of a verified grant's per-minute exchanges (<paramref name="exchange"/>) or renders.</summary>
    public bool TryAcquireGrant(bool exchange, string grantKey, out TimeSpan retryAfter)
    {
        return TryAcquire(_grants, (exchange ? "x:" : "r:") + grantKey, out retryAfter);
    }

    /// <summary>Spends one of the client's page loads of the grant <paramref name="publicId"/> this minute.</summary>
    public bool TryAcquirePage(HttpContext context, string publicId, out TimeSpan retryAfter)
    {
        return TryAcquire(_grants, "p:" + ClientKeyOf(context) + "|" + publicId, out retryAfter);
    }

    /// <summary>
    /// Whether the client still has refusals left this minute, without spending one: checked before the lookups an
    /// anonymous request costs (the exchange's secret, the page's public id), never before a render, whose token the codec
    /// verifies without a lookup.
    /// </summary>
    public bool HasRefusalsLeft(HttpContext context, out TimeSpan retryAfter)
    {
        retryAfter = Window;
        var statistics = _refusals.GetStatistics(ClientKeyOf(context));

        return statistics == null || statistics.CurrentAvailablePermits > 0;
    }

    /// <summary>Spends one of the client's refusals; false when it has none left.</summary>
    public bool TryAcquireRefusal(HttpContext context, out TimeSpan retryAfter)
    {
        return TryAcquire(_refusals, ClientKeyOf(context), out retryAfter);
    }

    /// <summary>Whether one more refusal may be written to the audit trail this minute.</summary>
    public bool TryAcquireRefusalAudit()
    {
        return _audited.TryAcquire();
    }

    public void Dispose()
    {
        _grants.Dispose();
        _refusals.Dispose();
    }

    /// <summary>The partition a client's refusals count in: its address, an IPv6 address by its /64 network.</summary>
    internal static string ClientKeyOf(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address == null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);

        return new IPAddress(bytes).ToString() + "/64";
    }

    private static int PermitsOf(string partition, EmbedRateLimitOptions limits)
    {
        return partition[0] switch
        {
            'x' => limits.ExchangePerGrantPerMinute,
            'p' => limits.PagePerClientPerMinute,
            _ => limits.RenderPerGrantPerMinute
        };
    }

    private static bool TryAcquire(PartitionedRateLimiter<string> limiter, string key, out TimeSpan retryAfter)
    {
        using var lease = limiter.AttemptAcquire(key);
        if (lease.IsAcquired)
        {
            retryAfter = TimeSpan.Zero;

            return true;
        }

        retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var value) ? value : Window;

        return false;
    }

    private static FixedWindowRateLimiterOptions FixedWindow(int permits)
    {
        return new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = Window,
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true
        };
    }

    /// <summary>The seconds a 429 tells the client to wait.</summary>
    internal static string RetryAfterSeconds(TimeSpan retryAfter)
    {
        return Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
    }
}
