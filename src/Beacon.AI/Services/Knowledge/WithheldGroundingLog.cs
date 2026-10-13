using System.Collections.Concurrent;

namespace Beacon.AI.Services.Knowledge;

/// <summary>
/// Remembers which grounding rows a prompt left out — a golden case, a learned pattern's example, a glossary term's metric
/// expression — so each is logged once per <see cref="Interval"/>, not on every prompt that would have shown it. A
/// singleton; the lists say which rows are left out at any time (<c>GoldSqlWithheld</c>, <c>ExampleSqlWithheld</c>).
/// </summary>
internal sealed class WithheldGroundingLog(TimeProvider? timeProvider = null)
{
    /// <summary>How long a row left out stays logged: it is logged again on the first prompt after that.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<(string Kind, int Id), DateTimeOffset> _logged = new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// The ids of <paramref name="ids"/> not logged as <paramref name="kind"/> within the last <see cref="Interval"/>; each
    /// returned id counts as logged from now.
    /// </summary>
    public IReadOnlyList<int> NotLoggedRecently(string kind, IEnumerable<int> ids)
    {
        var now = _time.GetUtcNow();
        var due = new List<int>();
        foreach (var id in ids.Distinct())
        {
            var key = (kind, id);
            if (_logged.TryGetValue(key, out var loggedAt) && now - loggedAt < Interval)
            {
                continue;
            }

            _logged[key] = now;
            due.Add(id);
        }

        return due;
    }
}
