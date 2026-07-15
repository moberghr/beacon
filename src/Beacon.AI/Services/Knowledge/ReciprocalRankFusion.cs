namespace Beacon.AI.Services.Knowledge;

/// <summary>
/// Reciprocal Rank Fusion — combines several independently-ranked result lists into one
/// consensus ranking without needing comparable per-list scores. Each list contributes
/// <c>1 / (k + rank)</c> to an item's fused score (rank is 1-based: the top item is rank 1),
/// scores for the same item across lists are summed, and higher fused scores rank first.
/// Items are de-duplicated by <paramref name="keySelector"/>; the first occurrence encountered
/// (earlier list, then earlier rank) is kept as the representative. <c>k = 60</c> is the
/// standard Cormack et al. constant and dampens the influence of any single top-ranked item.
/// </summary>
internal static class ReciprocalRankFusion
{
    /// <summary>
    /// Fuses <paramref name="rankedLists"/> and returns the de-duplicated items ordered best-first.
    /// </summary>
    public static List<T> Fuse<T>(
        IReadOnlyList<IReadOnlyList<T>> rankedLists,
        Func<T, string> keySelector,
        int k = 60)
    {
        return FuseWithScores(rankedLists, keySelector, k)
            .Select(x => x.Item)
            .ToList();
    }

    /// <summary>
    /// Fuses <paramref name="rankedLists"/> and returns each de-duplicated item paired with its
    /// fused RRF score, ordered best-first. Callers that need the score (e.g. to re-weight a
    /// downstream relevance field) use this overload; <see cref="Fuse{T}"/> discards the scores.
    /// </summary>
    public static List<(T Item, double Score)> FuseWithScores<T>(
        IReadOnlyList<IReadOnlyList<T>> rankedLists,
        Func<T, string> keySelector,
        int k = 60)
    {
        ArgumentNullException.ThrowIfNull(rankedLists);
        ArgumentNullException.ThrowIfNull(keySelector);

        var scores = new Dictionary<string, double>();
        var representatives = new Dictionary<string, T>();
        var firstSeenOrder = new List<string>();

        foreach (var list in rankedLists)
        {
            if (list == null)
            {
                continue;
            }

            for (var rank = 0; rank < list.Count; rank++)
            {
                var key = keySelector(list[rank]);
                // rank is 0-based here, so (rank + 1) makes the top item rank 1.
                var contribution = 1.0 / (k + rank + 1);
                if (scores.TryGetValue(key, out var existing))
                {
                    scores[key] = existing + contribution;
                }
                else
                {
                    scores[key] = contribution;
                    representatives[key] = list[rank];
                    firstSeenOrder.Add(key);
                }
            }
        }

        // OrderByDescending is stable, so ties keep first-seen order for deterministic output.
        return firstSeenOrder
            .Select(x => (Item: representatives[x], Score: scores[x]))
            .OrderByDescending(x => x.Score)
            .ToList();
    }
}
