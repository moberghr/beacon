using System.Collections.Concurrent;

namespace Beacon.Core.SecurityAudit;

/// <summary>
/// Per process: the reads of the security audit trail recorded in the last <see cref="CollapseWindow"/> (an identical read
/// by the same actor within it is not recorded again, so paging back and forth or a page that re-verifies its rows does not
/// flood the trail with copies of one event), and the one verification allowed to run at a time (verification reads every
/// event of its window). Singleton; registered by <c>AddBeaconServices</c>.
/// </summary>
internal sealed class SecurityAuditReadGate(TimeProvider timeProvider)
{
    internal static readonly TimeSpan CollapseWindow = TimeSpan.FromSeconds(60);

    private const int PruneAbove = 1000;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _recorded = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _verification = new(1, 1);

    /// <summary>Whether the read <paramref name="key"/> names was recorded within <see cref="CollapseWindow"/>.</summary>
    public bool RecordedRecently(string key)
    {
        return _recorded.TryGetValue(key, out var at) && timeProvider.GetUtcNow() - at < CollapseWindow;
    }

    /// <summary>Notes that the read <paramref name="key"/> names was recorded now.</summary>
    public void Recorded(string key)
    {
        var now = timeProvider.GetUtcNow();
        _recorded[key] = now;
        if (_recorded.Count <= PruneAbove)
        {
            return;
        }

        var expired = _recorded
            .Where(x => now - x.Value >= CollapseWindow)
            .Select(x => x.Key)
            .ToList();
        foreach (var expiredKey in expired)
        {
            _recorded.TryRemove(expiredKey, out _);
        }
    }

    /// <summary>Takes the verification slot if it is free; false while another verification runs.</summary>
    public bool TryStartVerification()
    {
        return _verification.Wait(0);
    }

    /// <summary>Waits for the verification slot (scheduled work, which may wait).</summary>
    public Task StartVerificationAsync(CancellationToken cancellationToken)
    {
        return _verification.WaitAsync(cancellationToken);
    }

    public void EndVerification()
    {
        _verification.Release();
    }
}
