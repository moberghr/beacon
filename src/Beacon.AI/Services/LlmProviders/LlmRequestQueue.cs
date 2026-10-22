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

    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _backgroundSlots;
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
        var maxConcurrent = limits.MaxConcurrentRequests > 0 ? limits.MaxConcurrentRequests : DefaultMaxConcurrent;
        var maxBackground = Math.Max(1, maxConcurrent / 2);

        _slots = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        _backgroundSlots = new SemaphoreSlim(maxBackground, maxBackground);
        _maxAttempts = Math.Max(1, maxAttempts);
        _interactiveWait = QueueWait(limits.InteractiveQueueWaitSeconds, defaults.InteractiveQueueWaitSeconds);
        _backgroundWait = QueueWait(limits.BackgroundQueueWaitSeconds, defaults.BackgroundQueueWaitSeconds);
        _maxWaiters = limits.MaxQueuedRequests > 0 ? limits.MaxQueuedRequests : defaults.MaxQueuedRequests;
        _maxBackgroundWaiters = Math.Max(1, _maxWaiters / 2);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _jitter = jitter ?? throw new ArgumentNullException(nameof(jitter));
        _logger = logger ?? NullLogger<LlmRequestQueue>.Instance;
    }

    /// <summary>How long an interactive request waits for a slot.</summary>
    internal TimeSpan InteractiveWait => _interactiveWait;

    /// <summary>How long a background request waits for a slot.</summary>
    internal TimeSpan BackgroundWait => _backgroundWait;

    public async Task<LlmResponse> EnqueueRequestAsync(
        ILlmProvider provider,
        LlmRequest request,
        CancellationToken cancellationToken = default)
    {
        var background = request.Priority == LlmRequestPriority.Background;
        Exception? retrying = null;

        for (var attempt = 0; ; attempt++)
        {
            TimeSpan delay;
            await AcquireAsync(background, retrying, cancellationToken);
            try
            {
                return await provider.CompleteAsync(request, cancellationToken);
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

    private static TimeSpan QueueWait(int seconds, int defaultSeconds)
    {
        return TimeSpan.FromSeconds(Math.Min(seconds > 0 ? seconds : defaultSeconds, MaxQueueWaitSeconds));
    }

    // retrying: the provider failure a retry attempt is acquiring a slot for; a saturated queue reports it as the cause.
    private async Task AcquireAsync(bool background, Exception? retrying, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TryAcquireNow(background))
        {
            return;
        }

        if (!TryEnterWaiting(background))
        {
            throw Saturated(background, "too many requests are waiting", retrying);
        }

        var backgroundHeld = false;
        var acquired = false;
        try
        {
            using var timeout = new CancellationTokenSource(background ? _backgroundWait : _interactiveWait, _timeProvider);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                if (background)
                {
                    await _backgroundSlots.WaitAsync(wait.Token);
                    backgroundHeld = true;
                }

                await _slots.WaitAsync(wait.Token);
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
                _backgroundSlots.Release();
            }
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
            return _slots.Wait(0);
        }

        if (!_backgroundSlots.Wait(0))
        {
            return false;
        }

        if (_slots.Wait(0))
        {
            return true;
        }

        _backgroundSlots.Release();
        return false;
    }

    private void Release(bool background)
    {
        _slots.Release();
        if (background)
        {
            _backgroundSlots.Release();
        }
    }
}
