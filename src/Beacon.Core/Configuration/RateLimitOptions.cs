using Microsoft.Extensions.Options;

namespace Beacon.Core.Configuration;

/// <summary>
/// Request limits on Beacon's own routes and the password lockout, bound from <c>Beacon:RateLimits</c>. Each limit is a
/// fixed window per partition: an authenticated caller by its credential (API key, mapped MCP caller, user), an anonymous
/// one by the client address the host reports (<c>Connection.RemoteIpAddress</c>, which <c>UseForwardedHeaders</c>
/// rewrites only as the host configured it). A <see cref="RateLimitWindow.PermitLimit"/> of 0 turns that limit off. The
/// counts are kept in process memory, so each limit holds per Beacon instance; the lockout is stored on the user.
/// </summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "Beacon:RateLimits";

    /// <summary>The largest permit limit accepted for any window.</summary>
    public const int MaxPermitLimit = 100_000;

    /// <summary>The longest window accepted, one hour.</summary>
    public const int MaxWindowSeconds = 3600;

    /// <summary>The most failed sign-ins accepted before a lockout.</summary>
    public const int MaxFailedAttemptsLimit = 100;

    /// <summary>The longest lockout accepted, one day.</summary>
    public const int MaxLockoutMinutes = 1440;

    /// <summary>The partitions each limit keeps by default.</summary>
    public const int DefaultMaxPartitions = 10_000;

    /// <summary>The fewest partitions accepted per limit.</summary>
    public const int MinPartitions = 100;

    /// <summary>The most partitions accepted per limit.</summary>
    public const int MaxPartitionsLimit = 1_000_000;

    /// <summary>Password sign-in and first-run setup, per client address (each in its own partition). Default 10 a minute.</summary>
    public RateLimitWindow Login { get; set; } = new() { PermitLimit = 10 };

    /// <summary>
    /// Every <c>/beacon/api</c> request, per authenticated caller, or per client address for an anonymous one. Default 600
    /// a minute.
    /// </summary>
    public RateLimitWindow Api { get; set; } = new() { PermitLimit = 600 };

    /// <summary>
    /// Anonymous requests to Beacon's routes that are answered 401 (no credential, an unknown or expired API key or
    /// token), per client address. Over the limit they are answered 429. Default 30 a minute.
    /// </summary>
    public RateLimitWindow Anonymous { get; set; } = new() { PermitLimit = 30 };

    /// <summary>Every request to the MCP route, per API key or mapped MCP caller. Default 60 a minute.</summary>
    public RateLimitWindow Mcp { get; set; } = new() { PermitLimit = 60 };

    /// <summary>The <c>ask</c> tool (MCP and the playground), per caller. Default 10 a minute.</summary>
    public RateLimitWindow Ask { get; set; } = new() { PermitLimit = 10 };

    /// <summary>
    /// The most partitions (callers or client addresses) each limit keeps in memory. A new partition beyond it replaces the
    /// one used least recently, which then starts over with a full budget, so memory stays bounded. Default 10,000.
    /// </summary>
    public int MaxPartitions { get; set; } = DefaultMaxPartitions;

    /// <summary>The password lockout of internal users.</summary>
    public LockoutOptions Lockout { get; set; } = new();

    /// <summary>The growing, temporary delay of the break-glass accounts' password sign-in after wrong passwords.</summary>
    public BreakGlassThrottleOptions BreakGlassThrottle { get; set; } = new();
}

/// <summary>A fixed-window limit: <see cref="PermitLimit"/> requests per <see cref="WindowSeconds"/>; 0 permits turns it off.</summary>
public sealed class RateLimitWindow
{
    public int PermitLimit { get; set; }

    public int WindowSeconds { get; set; } = 60;

    public bool IsEnabled => PermitLimit > 0;

    public TimeSpan Window => TimeSpan.FromSeconds(WindowSeconds);
}

/// <summary>
/// After <see cref="MaxFailedAttempts"/> wrong passwords an internal user's password sign-in (and password change) is
/// refused for <see cref="DurationMinutes"/>, whatever password is sent; the answer stays the generic failure and the
/// password is still checked. A successful sign-in, a lockout and an administrator's password reset start the count again,
/// and so does a wrong password sent <see cref="DurationMinutes"/> or more after the previous one. The break-glass accounts
/// (<c>Beacon:UserManagement:LocalLogin:BreakGlassUserNames</c>) are never locked: they get the shorter, growing delay of
/// <see cref="BreakGlassThrottleOptions"/> instead, and stay limited per client address by <see cref="RateLimitOptions.Login"/>.
/// <para>
/// The count is kept per user, not per client: anyone who knows a user name can lock that user's password sign-in by
/// sending wrong passwords, again every time the lockout ends. That trades a denial of service against the account for
/// a hard cap on password guessing. A host that prefers no lockout (relying on the per-address login limit, single
/// sign-on or the break-glass exemption) turns it off with <see cref="Enabled"/> false or <see cref="MaxFailedAttempts"/> 0.
/// </para>
/// </summary>
public sealed class LockoutOptions
{
    /// <summary>The lockout is applied. Default: true.</summary>
    public bool Enabled { get; set; } = true;

    public int MaxFailedAttempts { get; set; } = 5;

    public int DurationMinutes { get; set; } = 15;

    public bool IsEnabled => Enabled && MaxFailedAttempts > 0;

    public TimeSpan Duration => TimeSpan.FromMinutes(DurationMinutes);
}

/// <summary>
/// The break-glass accounts (<c>Beacon:UserManagement:LocalLogin:BreakGlassUserNames</c>) are never locked out. After
/// <see cref="FailedAttemptsBeforeDelay"/> wrong passwords their password sign-in is refused for a delay instead, whatever
/// password is sent (the password is still checked and the answer stays the generic failure): <see cref="InitialDelaySeconds"/>
/// first, doubling with every further wrong password sent after a delay ended, up to <see cref="MaxDelaySeconds"/>. Wrong
/// passwords sent during a delay are not counted and do not lengthen it, so a delay always ends and the right password then
/// signs in. A successful sign-in and an administrator's password reset start the count again, and so does a wrong password
/// sent <see cref="ResetAfterMinutes"/> or more after the previous one. The count and the delay are stored on the user.
/// </summary>
public sealed class BreakGlassThrottleOptions
{
    /// <summary>The most doublings of the delay; the delay after them is <see cref="MaxDelaySeconds"/>.</summary>
    public const int MaxDoublings = 7;

    /// <summary>The longest delay accepted, one hour.</summary>
    public const int MaxDelaySecondsLimit = 3600;

    /// <summary>The delay is applied. Default: true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Wrong passwords before the first delay. Default 5.</summary>
    public int FailedAttemptsBeforeDelay { get; set; } = 5;

    /// <summary>The first delay. Default 30 seconds.</summary>
    public int InitialDelaySeconds { get; set; } = 30;

    /// <summary>The longest delay. Default 300 seconds.</summary>
    public int MaxDelaySeconds { get; set; } = 300;

    /// <summary>A wrong password sent this long after the previous one starts the count again. Default 60 minutes.</summary>
    public int ResetAfterMinutes { get; set; } = 60;

    public bool IsEnabled => Enabled && FailedAttemptsBeforeDelay > 0;

    public TimeSpan ResetAfter => TimeSpan.FromMinutes(ResetAfterMinutes);

    /// <summary>The delay after the given number of doublings: the first delay doubled, never longer than the longest.</summary>
    public TimeSpan DelayAfter(int doublings)
    {
        var seconds = doublings >= MaxDoublings
            ? MaxDelaySeconds
            : Math.Min((long)InitialDelaySeconds << doublings, MaxDelaySeconds);

        return TimeSpan.FromSeconds(seconds);
    }
}

internal sealed class RateLimitOptionsValidator : IValidateOptions<RateLimitOptions>
{
    public ValidateOptionsResult Validate(string? name, RateLimitOptions options)
    {
        var failures = new List<string>();
        AddWindowFailures(failures, nameof(RateLimitOptions.Login), options.Login);
        AddWindowFailures(failures, nameof(RateLimitOptions.Api), options.Api);
        AddWindowFailures(failures, nameof(RateLimitOptions.Anonymous), options.Anonymous);
        AddWindowFailures(failures, nameof(RateLimitOptions.Mcp), options.Mcp);
        AddWindowFailures(failures, nameof(RateLimitOptions.Ask), options.Ask);

        if (options.MaxPartitions is < RateLimitOptions.MinPartitions or > RateLimitOptions.MaxPartitionsLimit)
        {
            failures.Add($"{RateLimitOptions.SectionName}:{nameof(RateLimitOptions.MaxPartitions)} must be between {RateLimitOptions.MinPartitions} and {RateLimitOptions.MaxPartitionsLimit}.");
        }

        var lockout = $"{RateLimitOptions.SectionName}:{nameof(RateLimitOptions.Lockout)}";
        if (options.Lockout.MaxFailedAttempts is < 0 or > RateLimitOptions.MaxFailedAttemptsLimit)
        {
            failures.Add($"{lockout}:{nameof(LockoutOptions.MaxFailedAttempts)} must be between 0 and {RateLimitOptions.MaxFailedAttemptsLimit}.");
        }

        if (options.Lockout.DurationMinutes is < 1 or > RateLimitOptions.MaxLockoutMinutes)
        {
            failures.Add($"{lockout}:{nameof(LockoutOptions.DurationMinutes)} must be between 1 and {RateLimitOptions.MaxLockoutMinutes}.");
        }

        AddThrottleFailures(failures, options.BreakGlassThrottle);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void AddThrottleFailures(List<string> failures, BreakGlassThrottleOptions? throttle)
    {
        var prefix = $"{RateLimitOptions.SectionName}:{nameof(RateLimitOptions.BreakGlassThrottle)}";
        if (throttle == null)
        {
            failures.Add($"{prefix} must be set.");
            return;
        }

        if (throttle.FailedAttemptsBeforeDelay is < 0 or > RateLimitOptions.MaxFailedAttemptsLimit)
        {
            failures.Add($"{prefix}:{nameof(BreakGlassThrottleOptions.FailedAttemptsBeforeDelay)} must be between 0 and {RateLimitOptions.MaxFailedAttemptsLimit}.");
        }

        if (throttle.InitialDelaySeconds < 1 || throttle.InitialDelaySeconds > throttle.MaxDelaySeconds)
        {
            failures.Add($"{prefix}:{nameof(BreakGlassThrottleOptions.InitialDelaySeconds)} must be at least 1 and at most {nameof(BreakGlassThrottleOptions.MaxDelaySeconds)}.");
        }

        if (throttle.MaxDelaySeconds is < 1 or > BreakGlassThrottleOptions.MaxDelaySecondsLimit)
        {
            failures.Add($"{prefix}:{nameof(BreakGlassThrottleOptions.MaxDelaySeconds)} must be between 1 and {BreakGlassThrottleOptions.MaxDelaySecondsLimit}.");
        }

        if (throttle.ResetAfterMinutes < 1
            || throttle.ResetAfterMinutes > RateLimitOptions.MaxLockoutMinutes
            || throttle.ResetAfterMinutes * 60L <= throttle.MaxDelaySeconds)
        {
            failures.Add($"{prefix}:{nameof(BreakGlassThrottleOptions.ResetAfterMinutes)} must be between 1 and {RateLimitOptions.MaxLockoutMinutes} and longer than {nameof(BreakGlassThrottleOptions.MaxDelaySeconds)}.");
        }
    }

    private static void AddWindowFailures(List<string> failures, string limit, RateLimitWindow? window)
    {
        var prefix = $"{RateLimitOptions.SectionName}:{limit}";
        if (window == null)
        {
            failures.Add($"{prefix} must be set.");
            return;
        }

        if (window.PermitLimit is < 0 or > RateLimitOptions.MaxPermitLimit)
        {
            failures.Add($"{prefix}:{nameof(RateLimitWindow.PermitLimit)} must be between 0 and {RateLimitOptions.MaxPermitLimit}.");
        }

        if (window.WindowSeconds is < 1 or > RateLimitOptions.MaxWindowSeconds)
        {
            failures.Add($"{prefix}:{nameof(RateLimitWindow.WindowSeconds)} must be between 1 and {RateLimitOptions.MaxWindowSeconds}.");
        }
    }
}
