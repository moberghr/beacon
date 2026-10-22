namespace Beacon.AI.Services.LlmProviders;

/// <summary>
/// The requests-per-minute and tokens-per-minute limits as two token buckets, each holding at most a minute's allowance
/// and refilling it evenly over the minute. An attempt reserves one request and its token estimate before it is sent and
/// settles the tokens with the usage the response reports. Off, or a non-positive rate, leaves that dimension unlimited.
/// A background request draws a bucket only down to a quarter of its allowance (<see cref="BackgroundReserve"/>), which
/// stays for interactive requests. Requests the buckets cannot cover at once wait in one line in arrival order: a request
/// is served only when no earlier request of its lane waits, and an interactive request may pass background requests,
/// so a large request is never overtaken by smaller ones of its lane and an interactive request can use the reserve.
/// </summary>
internal sealed class LlmRateLimiter(TimeProvider timeProvider)
{
    /// <summary>The share of each bucket a background request leaves for interactive requests.</summary>
    internal const double BackgroundReserve = 0.25;

    private const double SecondsPerMinute = 60;

    // Absorbs floating-point drift, so a bucket refilled for exactly the computed wait counts as full enough.
    private const double Tolerance = 1e-6;

    private readonly object _lock = new();
    private readonly LinkedList<Waiter> _line = new();
    private Bucket? _requests;
    private Bucket? _tokens;

    // No rate is limited, so nothing waits for anything. Caller holds the lock.
    private bool Unlimited => _requests is null && _tokens is null;

    public void Update(bool enabled, int requestsPerMinute, int tokensPerMinute)
    {
        lock (_lock)
        {
            var now = timeProvider.GetTimestamp();
            _requests = Next(_requests, enabled ? requestsPerMinute : 0, now);
            _tokens = Next(_tokens, enabled ? tokensPerMinute : 0, now);
            SignalServable();
        }
    }

    /// <summary>
    /// Reserves one request and <paramref name="estimatedTokens"/> (at most a minute's allowance) at once, when no earlier
    /// request of the lane waits in line and both buckets hold enough (or no rate is limited); otherwise null, and the
    /// request joins the line.
    /// </summary>
    public Reservation? TryReserve(int estimatedTokens, bool background)
    {
        lock (_lock)
        {
            if (!Unlimited && _line.Any(x => background || !x.Background))
            {
                return null;
            }

            return TakeIfCovered(estimatedTokens, background, out _);
        }
    }

    /// <summary>Puts a request at the end of the line, to reserve through <see cref="TryReserve(Waiter, int, out TimeSpan)"/>.</summary>
    public Waiter Join(bool background)
    {
        lock (_lock)
        {
            var waiter = new Waiter(background);
            waiter.Node = _line.AddLast(waiter);

            return waiter;
        }
    }

    /// <summary>
    /// Reserves for a request in line when it is served next and the buckets hold enough; it then leaves the line.
    /// Otherwise null, with <paramref name="wait"/> the time until the buckets will hold enough, or infinite while an
    /// earlier request is served first. The waiter is signalled whenever that may have changed.
    /// </summary>
    public Reservation? TryReserve(Waiter waiter, int estimatedTokens, out TimeSpan wait)
    {
        lock (_lock)
        {
            waiter.Reset();
            wait = Timeout.InfiniteTimeSpan;
            if (!Unlimited && !IsServable(waiter))
            {
                return null;
            }

            var reservation = TakeIfCovered(estimatedTokens, waiter.Background, out wait);
            if (reservation is not null)
            {
                Remove(waiter);
            }

            return reservation;
        }
    }

    /// <summary>Takes a request out of the line (it reserved, gave up or failed); does nothing when it already left.</summary>
    public void Leave(Waiter waiter)
    {
        lock (_lock)
        {
            Remove(waiter);
        }
    }

    /// <summary>
    /// Settles a sent attempt's tokens with the usage its response reported: the unused share of the estimate goes back, a
    /// larger use is taken from the bucket. Zero (no usage reported) keeps the estimate.
    /// </summary>
    public void Settle(Reservation reservation, int usedTokens)
    {
        if (reservation.Tokens is null || usedTokens <= 0)
        {
            return;
        }

        lock (_lock)
        {
            reservation.Tokens.Refill(timeProvider.GetTimestamp(), timeProvider);
            reservation.Tokens.Give(reservation.ReservedTokens - usedTokens);
            SignalServable();
        }
    }

    /// <summary>Gives back a reservation whose attempt was never sent.</summary>
    public void Refund(Reservation reservation)
    {
        if (reservation.Requests is null && reservation.Tokens is null)
        {
            return;
        }

        lock (_lock)
        {
            var now = timeProvider.GetTimestamp();
            reservation.Requests?.Refill(now, timeProvider);
            reservation.Requests?.Give(1);
            reservation.Tokens?.Refill(now, timeProvider);
            reservation.Tokens?.Give(reservation.ReservedTokens);
            SignalServable();
        }
    }

    // Caller holds the lock.
    private Reservation? TakeIfCovered(int estimatedTokens, bool background, out TimeSpan wait)
    {
        wait = TimeSpan.Zero;
        if (Unlimited)
        {
            return Reservation.None;
        }

        var now = timeProvider.GetTimestamp();
        _requests?.Refill(now, timeProvider);
        _tokens?.Refill(now, timeProvider);
        var tokens = _tokens is null ? 0 : Math.Min(Math.Max(estimatedTokens, 0), _tokens.Capacity);
        var needed = Max(
            _requests?.TimeUntil(1, background) ?? TimeSpan.Zero,
            _tokens?.TimeUntil(tokens, background) ?? TimeSpan.Zero);
        if (needed > TimeSpan.Zero)
        {
            wait = needed;
            return null;
        }

        _requests?.Take(1);
        _tokens?.Take(tokens);

        return new Reservation(_requests, _tokens, tokens);
    }

    // Served next: no earlier request of its lane waits; a background request also waits behind earlier interactive ones.
    // Caller holds the lock.
    private static bool IsServable(Waiter waiter)
    {
        if (waiter.Node is null)
        {
            return false;
        }

        for (var node = waiter.Node.Previous; node is not null; node = node.Previous)
        {
            if (waiter.Background || !node.Value.Background)
            {
                return false;
            }
        }

        return true;
    }

    // Wakes the requests that may be served next: the first in line and the first interactive one. Caller holds the lock.
    private void SignalServable()
    {
        _line.First?.Value.Signal();
        for (var node = _line.First; node is not null; node = node.Next)
        {
            if (!node.Value.Background)
            {
                node.Value.Signal();
                return;
            }
        }
    }

    // Caller holds the lock.
    private void Remove(Waiter waiter)
    {
        if (waiter.Node is null)
        {
            return;
        }

        _line.Remove(waiter.Node);
        waiter.Node = null;
        SignalServable();
    }

    // A bucket that stays keeps its level (refilled at the old rate up to now, then capped at the new allowance).
    private Bucket? Next(Bucket? current, int perMinute, long now)
    {
        if (perMinute <= 0)
        {
            return null;
        }

        if (current is null)
        {
            return new Bucket(perMinute, now);
        }

        current.Refill(now, timeProvider);
        current.Resize(perMinute);

        return current;
    }

    private static TimeSpan Max(TimeSpan first, TimeSpan second)
    {
        return first > second ? first : second;
    }

    /// <summary>What an attempt took from the buckets; the buckets are the ones in force when it was taken.</summary>
    internal sealed record Reservation(Bucket? Requests, Bucket? Tokens, double ReservedTokens)
    {
        public static Reservation None { get; } = new(null, null, 0);
    }

    /// <summary>A request waiting in line for the rate buckets.</summary>
    internal sealed class Waiter(bool background)
    {
        private volatile TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Background { get; } = background;

        // Set and cleared under the limiter's lock.
        internal LinkedListNode<Waiter>? Node { get; set; }

        /// <summary>Waits until signalled, <paramref name="timeout"/> passes (infinite: no timeout) or the token is cancelled.</summary>
        public async Task WaitAsync(TimeSpan timeout, TimeProvider timeProvider, CancellationToken cancellationToken)
        {
            try
            {
                await _signal.Task.WaitAsync(timeout, timeProvider, cancellationToken);
            }
            catch (TimeoutException)
            {
                // The buckets have refilled for the wait computed; the caller checks them again.
            }
        }

        internal void Signal()
        {
            _signal.TrySetResult();
        }

        // Called under the limiter's lock before the buckets are checked, so a signal sent after the check is never lost.
        internal void Reset()
        {
            if (_signal.Task.IsCompleted)
            {
                _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    internal sealed class Bucket(int perMinute, long now)
    {
        private long _refilledAt = now;

        public double Capacity { get; private set; } = perMinute;

        // May go below zero when an attempt used more than it reserved; the debt is refilled like any other use.
        public double Level { get; private set; } = perMinute;

        public void Refill(long now, TimeProvider timeProvider)
        {
            var elapsed = timeProvider.GetElapsedTime(_refilledAt, now).TotalSeconds;
            _refilledAt = now;
            if (elapsed > 0)
            {
                Level = Math.Min(Capacity, Level + (Capacity * elapsed / SecondsPerMinute));
            }
        }

        /// <summary>
        /// The time until the bucket holds <paramref name="amount"/>; for a background request, also the reserve it leaves
        /// (a quarter of the allowance, less when the amount would not fit beside it in a full bucket).
        /// </summary>
        public TimeSpan TimeUntil(double amount, bool background = false)
        {
            var reserve = background ? Math.Min(Capacity * BackgroundReserve, Math.Max(Capacity - amount, 0)) : 0;
            var missing = amount + reserve - Level;
            if (missing <= Tolerance)
            {
                return TimeSpan.Zero;
            }

            return TimeSpan.FromMilliseconds(Math.Ceiling(missing / Capacity * SecondsPerMinute * 1000));
        }

        public void Take(double amount)
        {
            Level -= amount;
        }

        public void Give(double amount)
        {
            Level = Math.Min(Capacity, Level + amount);
        }

        public void Resize(int perMinute)
        {
            Capacity = perMinute;
            Level = Math.Min(Level, Capacity);
        }
    }
}
