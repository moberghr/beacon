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
/// (<c>Beacon:UserManagement:LocalLogin:BreakGlassUserNames</c>) are never locked: they stay limited per client address by
/// <see cref="RateLimitOptions.Login"/>.
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

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
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
