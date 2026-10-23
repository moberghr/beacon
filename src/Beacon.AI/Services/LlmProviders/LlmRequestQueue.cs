using Beacon.AI.Models.Configuration;
using Beacon.Core.Exceptions;
using Beacon.Core.Models.Ai;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Beacon.AI.Services.LlmProviders;

/// <summary>
/// Every LLM completion goes through here (§6.1). Each attempt takes one of the concurrency slots and gives it back when
/// the attempt ends, before any wait to retry. A request waits for a slot at most its lane's wait limit (at most an
/// hour), and only so many requests wait at once (background requests at most half as many, at least one); past either
/// it fails as busy (<see cref="LlmQueueSaturatedException"/>) without reaching the provider. Background requests hold
/// at most half the slots (at least one), so interactive calls always find room. Retried, up to three attempts with
/// full-jitter exponential backoff or the wait the provider asked for (whichever is longer): a
/// <see cref="LlmTransientException"/> and a provider timeout (a cancellation while the caller's own token is not
/// cancelled). Nothing else is retried: a provider that retries itself (the OpenAI-compatible one) reports what is left
/// after its own attempts as a failure the queue does not retry.
/// The concurrency limit and the rate limits follow <see cref="UpdateLimits"/> (the provider manager calls it whenever an
/// Admin saves the settings): a higher limit lets waiting requests run at once, a lower one takes effect as running attempts
/// end and never cancels them. With <see cref="ProviderLimits.RateLimitsEnabled"/> on, each attempt also reserves one
/// request and its token estimate (<see cref="EstimateTokens"/>) from per-minute buckets before it takes a slot, waiting
/// for them within its lane's wait limit in arrival order (an interactive request may pass background ones), and holds
/// that reservation while it waits for a slot; an attempt that is never sent gives it back. A background request leaves a
/// quarter of each bucket for interactive requests. The tokens are settled with the usage the response reports when it
/// reports the input tokens; otherwise the estimate stands.
/// </summary>
public class LlmRequestQueue
{
    /// <summary>The longest wait a provider may ask for before a retry; asking for longer fails the request.</summary>
    internal static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

    /// <summary>The longest a request waits for a slot, whatever the configuration says.</summary>
    internal const int MaxQueueWaitSeconds = 3600;

    private static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private const double RetryAfterJitter = 0.1;
    private const int DefaultMaxConcurrent = 50;
    private const int DefaultMaxAttempts = 3;

    private readonly LlmSlotPool _slots;
    private readonly LlmSlotPool _backgroundSlots;
    private readonly LlmRateLimiter _rateLimiter;
    private readonly object _limitsLock = new();
    private readonly int _maxAttempts;
    private readonly TimeSpan _interactiveWait;
    private readonly TimeSpan _backgroundWait;
    private readonly int _maxWaiters;
    private readonly int _maxBackgroundWaiters;
    private readonly TimeProvider _timeProvider;
    private readonly Func<double> _jitter;
    private readonly ILogger<LlmRequestQueue> _logger;
    private int _waiters;
    private int _backgroundWaiters;
    private ProviderLimits _appliedLimits;

    public LlmRequestQueue(int maxConcurrent = DefaultMaxConcurrent, int maxRetries = DefaultMaxAttempts)
        : this(new ProviderLimits { MaxConcurrentRequests = maxConcurrent }, TimeProvider.System, Random.Shared.NextDouble, null, maxRetries)
    {
    }

    /// <summary>
    /// A queue with the concurrency and queue-wait limits of <paramref name="limits"/>; non-positive values take their
    /// defaults, and a wait is capped at an hour.
    /// </summary>
    public LlmRequestQueue(ProviderLimits limits, TimeProvider? timeProvider = null, ILogger<LlmRequestQueue>? logger = null)
        : this(limits, timeProvider ?? TimeProvider.System, Random.Shared.NextDouble, logger)
    {
    }

    internal LlmRequestQueue(
        ProviderLimits limits,
        TimeProvider timeProvider,
        Func<double> jitter,
        ILogger<LlmRequestQueue>? logger = null,
        int maxAttempts = DefaultMaxAttempts)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var defaults = new ProviderLimits();
        var maxConcurrent = MaxConcurrent(limits);

        _slots = new LlmSlotPool(maxConcurrent);
        _backgroundSlots = new LlmSlotPool(BackgroundShare(maxConcurrent));
        _maxAttempts = Math.Max(1, maxAttempts);
        _interactiveWait = QueueWait(limits.InteractiveQueueWaitSeconds, defaults.InteractiveQueueWaitSeconds);
        _backgroundWait = QueueWait(limits.BackgroundQueueWaitSeconds, defaults.BackgroundQueueWaitSeconds);
        _maxWaiters = limits.MaxQueuedRequests > 0 ? limits.MaxQueuedRequests : defaults.MaxQueuedRequests;
        _maxBackgroundWaiters = Math.Max(1, _maxWaiters / 2);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _jitter = jitter ?? throw new ArgumentNullException(nameof(jitter));
        _logger = logger ?? NullLogger<LlmRequestQueue>.Instance;
        _rateLimiter = new LlmRateLimiter(_timeProvider);
        _rateLimiter.Update(limits.RateLimitsEnabled, limits.RequestsPerMinute, limits.TokensPerMinute);
        _appliedLimits = limits;
    }

    /// <summary>How long an interactive request waits for a slot.</summary>
    internal TimeSpan InteractiveWait => _interactiveWait;

    /// <summary>How long a background request waits for a slot.</summary>
    internal TimeSpan BackgroundWait => _backgroundWait;

    /// <summary>The limits last applied: the concurrency and rate limits in force (the queue waits are the constructor's).</summary>
    internal ProviderLimits AppliedLimits => Volatile.Read(ref _appliedLimits);

    /// <summary>
    /// Applies a new concurrency limit (non-positive: the default) and new rate limits to the running queue. The queue-wait
    /// and waiter limits stay those it was built with.
    /// </summary>
    public void UpdateLimits(ProviderLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var maxConcurrent = MaxConcurrent(limits);
        lock (_limitsLock)
        {
            _slots.Resize(maxConcurrent);
            _backgroundSlots.Resize(BackgroundShare(maxConcurrent));
            _rateLimiter.Update(limits.RateLimitsEnabled, limits.RequestsPerMinute, limits.TokensPerMinute);
            Volatile.Write(ref _appliedLimits, limits);
        }

        _logger.LogInformation(
            "LLM request limits applied: {MaxConcurrent} concurrent attempts, rate limits {RateLimits}.",
            maxConcurrent,
            limits.RateLimitsEnabled ? "enforced" : "off");
    }

    /// <summary>
    /// The tokens an attempt reserves while rate limits are on: the prompt's characters (system prompt and messages) over
    /// four, rounded up, plus the most the answer may use.
    /// </summary>
    internal static int EstimateTokens(LlmRequest request)
    {
        long characters = request.SystemPrompt?.Length ?? 0;
        foreach (var message in request.Messages ?? [])
        {
            characters += message?.Content?.Length ?? 0;
        }

        var estimate = ((characters + 3) / 4) + Math.Max(request.MaxTokens, 0);

        return (int)Math.Min(estimate, int.MaxValue);
    }

    public async Task<LlmResponse> EnqueueRequestAsync(
        ILlmProvider provider,
        LlmRequest request,
        CancellationToken cancellationToken = default)
    {
        var background = request.Priority == LlmRequestPriority.Background;
        var estimatedTokens = EstimateTokens(request);
        Exception? retrying = null;

        for (var attempt = 0; ; attempt++)
        {
            TimeSpan delay;
            var reservation = await AcquireAsync(background, estimatedTokens, retrying, cancellationToken);
            try
            {
                var response = await provider.CompleteAsync(request, cancellationToken);
                _rateLimiter.Settle(reservation, UsedTokens(response));

                return response;
            }
            catch (Exception ex) when (IsRetryable(ex, cancellationToken))
            {
                if (attempt >= _maxAttempts - 1)
                {
                    _logger.LogWarning(
                        "LLM request failed after {Attempts} attempts ({ExceptionType}); not retried again.",
                        attempt + 1,
                        ex.GetType().Name);
                    throw;
                }

                if (RetryDelay(ex, attempt) is not { } retryDelay)
                {
                    _logger.LogWarning(
                        "LLM request attempt {Attempt} failed ({ExceptionType}) and the provider asked to wait longer than {MaxWaitSeconds} s; not retried.",
                        attempt + 1,
                        ex.GetType().Name,
                        MaxRetryAfter.TotalSeconds);
                    throw;
                }

                _logger.LogInformation(
                    "LLM request attempt {Attempt} failed ({ExceptionType}); retrying in {WaitMilliseconds} ms.",
                    attempt + 1,
                    ex.GetType().Name,
                    (long)retryDelay.TotalMilliseconds);
                delay = retryDelay;
                retrying = ex;
            }
            finally
            {
                Release(background);
            }

            // The slot is back in the pool while this request waits to retry.
            await Task.Delay(delay, _timeProvider, cancellationToken);
        }
    }

    /// <summary>Full jitter: a uniform share (<paramref name="sample"/> in [0, 1)) of 1 s doubled per attempt, capped at 30 s.</summary>
    internal static TimeSpan BackoffDelay(int attempt, double sample)
    {
        var ceiling = Math.Min(MaxBackoff.TotalSeconds, BaseBackoff.TotalSeconds * Math.Pow(2, attempt));

        return TimeSpan.FromSeconds(ceiling * Math.Clamp(sample, 0, 1));
    }

    /// <summary>
    /// The wait before a retry the provider asked to wait <paramref name="retryAfter"/> for: that wait plus up to 10 %
    /// (<paramref name="topUpSample"/> in [0, 1)), and never shorter than the backoff; null when it asked for longer than
    /// <see cref="MaxRetryAfter"/>.
    /// </summary>
    internal static TimeSpan? RetryAfterDelay(TimeSpan retryAfter, TimeSpan backoff, double topUpSample)
    {
        if (retryAfter > MaxRetryAfter)
        {
            return null;
        }

        var asked = retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.Zero;
        var toppedUp = asked + (asked * (RetryAfterJitter * Math.Clamp(topUpSample, 0, 1)));

        return toppedUp > backoff ? toppedUp : backoff;
    }

    // A cancellation while the caller's token is not cancelled can only come from the provider's own deadline
    // (HttpClient.Timeout, an attempt timeout).
    private static bool IsRetryable(Exception exception, CancellationToken cancellationToken)
    {
        return !cancellationToken.IsCancellationRequested
            && exception is LlmTransientException or OperationCanceledException;
    }

    // The wait before the next attempt, or null when the provider asked for a wait past the cap.
    private TimeSpan? RetryDelay(Exception exception, int attempt)
    {
        var backoff = BackoffDelay(attempt, _jitter());
        if (exception is LlmTransientException { RetryAfter: { } retryAfter })
        {
            return RetryAfterDelay(retryAfter, backoff, _jitter());
        }

        return backoff;
    }

    private static int MaxConcurrent(ProviderLimits limits)
    {
        return limits.MaxConcurrentRequests > 0 ? limits.MaxConcurrentRequests : DefaultMaxConcurrent;
    }

    private static int BackgroundShare(int maxConcurrent)
    {
        return Math.Max(1, maxConcurrent / 2);
    }

    // Every token the provider processed for the attempt; zero, which keeps the estimate, unless the response reports the
    // input tokens: without them the reported total is short of what the attempt used.
    private static int UsedTokens(LlmResponse response)
    {
        if (response.InputTokens <= 0)
        {
            return 0;
        }

        var used = (long)response.InputTokens + response.OutputTokens + response.CacheReadInputTokens + response.CacheWriteInputTokens;

        return (int)Math.Clamp(used, 0, int.MaxValue);
    }

    private static TimeSpan QueueWait(int seconds, int defaultSeconds)
    {
        return TimeSpan.FromSeconds(Math.Min(seconds > 0 ? seconds : defaultSeconds, MaxQueueWaitSeconds));
    }

    // retrying: the provider failure a retry attempt is acquiring a slot for; a saturated queue reports it as the cause.
    // Returns the attempt's rate reservation, already taken; on any failure nothing stays reserved or held.
    private async Task<LlmRateLimiter.Reservation> AcquireAsync(
        bool background,
        int estimatedTokens,
        Exception? retrying,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reservation = _rateLimiter.TryReserve(estimatedTokens, background);
        if (reservation is not null && TryAcquireNow(background))
        {
            return reservation;
        }

        if (!TryEnterWaiting(background))
        {
            Refund(reservation);
            throw Saturated(background, "too many requests are waiting", retrying);
        }

        var laneWait = background ? _backgroundWait : _interactiveWait;
        var waitStarted = _timeProvider.GetTimestamp();
        var backgroundHeld = false;
        var acquired = false;
        try
        {
            using var timeout = new CancellationTokenSource(laneWait, _timeProvider);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                // A reservation taken before the wait is held while the request waits for a slot.
                reservation ??= await ReserveInLineAsync(background, estimatedTokens, laneWait, waitStarted, retrying, wait.Token);

                if (background)
                {
                    await _backgroundSlots.TakeAsync(wait.Token);
                    backgroundHeld = true;
                }

                await _slots.TakeAsync(wait.Token);
                acquired = true;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw Saturated(background, "no slot came free within the wait limit", retrying);
            }
        }
        finally
        {
            LeaveWaiting(background);
            if (backgroundHeld && !acquired)
            {
                _backgroundSlots.Return();
            }

            if (!acquired)
            {
                Refund(reservation);
            }
        }

        return reservation;
    }

    // Waits in the rate limiter's line (arrival order) until the buckets cover the attempt; fails as busy as soon as they
    // cannot before the lane's wait ends. The request always leaves the line, also when it fails or is cancelled.
    private async Task<LlmRateLimiter.Reservation> ReserveInLineAsync(
        bool background,
        int estimatedTokens,
        TimeSpan laneWait,
        long waitStarted,
        Exception? retrying,
        CancellationToken cancellationToken)
    {
        var waiter = _rateLimiter.Join(background);
        try
        {
            while (true)
            {
                var reservation = _rateLimiter.TryReserve(waiter, estimatedTokens, out var rateWait);
                if (reservation is not null)
                {
                    return reservation;
                }

                if (rateWait != Timeout.InfiniteTimeSpan && rateWait >= laneWait - _timeProvider.GetElapsedTime(waitStarted))
                {
                    throw Saturated(background, "the rate limits allow no request within the wait limit", retrying);
                }

                await waiter.WaitAsync(rateWait, _timeProvider, cancellationToken);
            }
        }
        finally
        {
            _rateLimiter.Leave(waiter);
        }
    }

    private void Refund(LlmRateLimiter.Reservation? reservation)
    {
        if (reservation is not null)
        {
            _rateLimiter.Refund(reservation);
        }
    }

    private bool TryEnterWaiting(bool background)
    {
        if (!background)
        {
            if (Interlocked.Increment(ref _waiters) <= _maxWaiters)
            {
                return true;
            }

            Interlocked.Decrement(ref _waiters);
            return false;
        }

        if (Interlocked.Increment(ref _backgroundWaiters) <= _maxBackgroundWaiters)
        {
            return true;
        }

        Interlocked.Decrement(ref _backgroundWaiters);
        return false;
    }

    private void LeaveWaiting(bool background)
    {
        if (background)
        {
            Interlocked.Decrement(ref _backgroundWaiters);
            return;
        }

        Interlocked.Decrement(ref _waiters);
    }

    private LlmQueueSaturatedException Saturated(bool background, string reason, Exception? retrying)
    {
        _logger.LogWarning(
            "LLM request queue is saturated ({Lane}): {Reason}; the request fails as busy.",
            background ? "background" : "interactive",
            reason);

        return retrying is null ? new LlmQueueSaturatedException() : new LlmQueueSaturatedException(retrying);
    }

    private bool TryAcquireNow(bool background)
    {
        if (!background)
        {
            return _slots.TryTake();
        }

        if (!_backgroundSlots.TryTake())
        {
            return false;
        }

        if (_slots.TryTake())
        {
            return true;
        }

        _backgroundSlots.Return();
        return false;
    }

    private void Release(bool background)
    {
        _slots.Return();
        if (background)
        {
            _backgroundSlots.Return();
        }
    }
}
