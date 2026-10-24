namespace Beacon.Core.Services.Embed;

/// <summary>
/// The cap on embed events that anonymous or repeated traffic can write to the security audit trail: at most
/// <see cref="MaxPerMinute"/> in each one-minute window in this process, shared by the embed middleware's refusals and the
/// render's <c>busy</c> outcomes. An event over the cap is logged, not recorded.
/// </summary>
internal sealed class EmbedAuditLimiter
{
    public const int MaxPerMinute = 600;

    private const long WindowMilliseconds = 60_000;

    private readonly int _permitsPerWindow;
    private readonly object _sync = new();
    private long _windowStart = Environment.TickCount64;
    private int _used;

    public EmbedAuditLimiter()
        : this(MaxPerMinute)
    {
    }

    internal EmbedAuditLimiter(int permitsPerWindow)
    {
        _permitsPerWindow = permitsPerWindow;
    }

    /// <summary>Whether one more event may be written to the audit trail in this window.</summary>
    public bool TryAcquire()
    {
        lock (_sync)
        {
            var now = Environment.TickCount64;
            if (now - _windowStart >= WindowMilliseconds)
            {
                _windowStart = now;
                _used = 0;
            }

            if (_used >= _permitsPerWindow)
            {
                return false;
            }

            _used++;

            return true;
        }
    }
}
