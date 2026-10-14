using Microsoft.Extensions.Options;

namespace Beacon.Core.Configuration;

/// <summary>
/// How long a <c>Beacon.Auth</c> sign-in session lives and how often it is checked against the user store, bound from
/// <c>Beacon:Authentication:Session</c>.
/// </summary>
public sealed class SessionLifetimeOptions
{
    public const string SectionName = "Beacon:Authentication:Session";

    /// <summary>The longest revalidation interval accepted, one day.</summary>
    public const int MaxRevalidateMinutes = 1440;

    /// <summary>The longest absolute lifetime accepted, one year.</summary>
    public const int MaxAbsoluteLifetimeHours = 8760;

    /// <summary>
    /// With Beacon user management on, a session is checked against the user store at most this often: the user must
    /// still exist, be enabled and not archived, and carry the security stamp the session was signed in with, or the
    /// session is signed out; the session's roles are replaced by the stored ones. Default 5; 0 checks every request.
    /// </summary>
    public int RevalidateMinutes { get; set; } = 5;

    /// <summary>
    /// A session ends this many hours after its sign-in, however often it is renewed (sliding expiration, "remember me"):
    /// the user signs in again. Default 12.
    /// </summary>
    public int AbsoluteLifetimeHours { get; set; } = 12;

    /// <summary>The revalidation interval as a time span (zero: every request).</summary>
    public TimeSpan RevalidateInterval => TimeSpan.FromMinutes(RevalidateMinutes);

    /// <summary>The absolute lifetime as a time span.</summary>
    public TimeSpan AbsoluteLifetime => TimeSpan.FromHours(AbsoluteLifetimeHours);

    /// <summary>
    /// How long a realtime (SignalR) connection stays open after the check that admitted it: the revalidation interval, at
    /// least one minute. The hub then closes it and the client reconnects, so a revoked or demoted user loses it as soon
    /// as their session would be checked again.
    /// </summary>
    public TimeSpan RealtimeConnectionLifetime => RevalidateMinutes > 0 ? RevalidateInterval : TimeSpan.FromMinutes(1);
}

internal sealed class SessionLifetimeOptionsValidator : IValidateOptions<SessionLifetimeOptions>
{
    public ValidateOptionsResult Validate(string? name, SessionLifetimeOptions options)
    {
        var failures = new List<string>();
        if (options.RevalidateMinutes is < 0 or > SessionLifetimeOptions.MaxRevalidateMinutes)
        {
            failures.Add(
                $"{SessionLifetimeOptions.SectionName}:{nameof(SessionLifetimeOptions.RevalidateMinutes)} must be between 0 and {SessionLifetimeOptions.MaxRevalidateMinutes}.");
        }

        if (options.AbsoluteLifetimeHours is < 1 or > SessionLifetimeOptions.MaxAbsoluteLifetimeHours)
        {
            failures.Add(
                $"{SessionLifetimeOptions.SectionName}:{nameof(SessionLifetimeOptions.AbsoluteLifetimeHours)} must be between 1 and {SessionLifetimeOptions.MaxAbsoluteLifetimeHours}.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
