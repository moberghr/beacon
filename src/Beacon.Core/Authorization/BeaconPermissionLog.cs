using Microsoft.Extensions.Logging;

namespace Beacon.Core.Authorization;

/// <summary>
/// The notes the permission accessor gives while it resolves permissions on every request, each at most once per
/// session or caller so a busy session cannot flood the log: identity-provider permissions dropped because their sign-in
/// is older than their lifetime (once per sign-in), and a conflicting pair held under separation of duties (once per
/// caller and <see cref="Interval"/>). Users are named by id, permissions by name only (§1.11).
/// </summary>
internal sealed class BeaconPermissionLog(TimeProvider timeProvider, ILogger<BeaconPermissionLog> logger)
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    // A sign-in is reported once: a cookie session lasts days at most.
    private static readonly TimeSpan SignInInterval = TimeSpan.FromDays(30);

    // Bounds the memory a host with very many sessions can take; past it, entries not reported within the interval are
    // forgotten first.
    private const int MaxTracked = 1000;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _quietUntil = new(StringComparer.Ordinal);

    public void IdpPermissionsExpired(int userId, long? issuedAt)
    {
        if (!ShouldReport($"expired:{userId}:{issuedAt}", SignInInterval))
        {
            return;
        }

        logger.LogInformation(
            "Identity-provider permissions of user {UserId} are no longer honoured: their sign-in is older than Beacon:Authorization:IdpRoles:ClaimLifetimeMinutes; signing in again renews them",
            userId);
    }

    public void ConflictDropped(string? callerId, IReadOnlyList<(string First, string Second)> conflicts)
    {
        if (!ShouldReport($"conflict:{callerId}", Interval))
        {
            return;
        }

        logger.LogWarning(
            "Separation of duties: caller {CallerId} holds both permissions of {Pairs}; neither permission of a pair is granted",
            callerId,
            conflicts.Select(x => $"{x.First}+{x.Second}").ToList());
    }

    private bool ShouldReport(string key, TimeSpan interval)
    {
        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (_quietUntil.TryGetValue(key, out var until) && now < until)
            {
                return false;
            }

            if (_quietUntil.Count >= MaxTracked)
            {
                var expired = _quietUntil
                    .Where(x => x.Value <= now)
                    .Select(x => x.Key)
                    .ToList();
                foreach (var stale in expired)
                {
                    _quietUntil.Remove(stale);
                }

                if (_quietUntil.Count >= MaxTracked)
                {
                    _quietUntil.Clear();
                }
            }

            _quietUntil[key] = now + interval;
        }

        return true;
    }
}
