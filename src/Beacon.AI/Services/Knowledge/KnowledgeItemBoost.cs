namespace Beacon.AI.Services.Knowledge;

/// <summary>
/// The bounded rank boost: reorders the knowledge items a project's retrieval already selected by how the <c>ask</c> signals
/// that carried them went (<see cref="KnowledgeItemEvidence"/>, the project's <c>McpKnowledgeItemStats</c> rows). Pure: no
/// database, no clock. It never adds or removes an item.
/// <para>
/// An item's net evidence is <c>(helpful - harmful) / (helpful + harmful + </c><see cref="Prior"/><c>)</c>, between -1 and
/// 1; an item with fewer than <see cref="MinSignals"/> signals or <see cref="MinCallers"/> callers, or without stats, has
/// none. Its sort key is its retrieval index minus <see cref="MaxPlaces"/> times its net evidence; the sort is stable, so
/// items with equal keys keep retrieval order. The keys alone can move an item one place too far when items around it move
/// the other way, so the sorted order is then held to <see cref="MaxPlaces"/> places from each item's index.
/// </para>
/// </summary>
internal static class KnowledgeItemBoost
{
    internal const int MinSignals = 5;
    internal const int MinCallers = 2;
    internal const int MaxPlaces = 2;
    internal const double Prior = 4;

    /// <summary>The net evidence of an item: 0 without stats, below the evidence floor or with a score that is not a number.</summary>
    public static double Net(KnowledgeItemEvidence? evidence)
    {
        if (evidence == null || evidence.SignalCount < MinSignals || evidence.CallerCount < MinCallers)
        {
            return 0;
        }

        if (!double.IsFinite(evidence.HelpfulScore) || !double.IsFinite(evidence.HarmfulScore) || evidence.HelpfulScore < 0 || evidence.HarmfulScore < 0)
        {
            return 0;
        }

        return (evidence.HelpfulScore - evidence.HarmfulScore) / (evidence.HelpfulScore + evidence.HarmfulScore + Prior);
    }

    /// <summary>
    /// <paramref name="items"/>, given in retrieval order, ordered by their sort keys and no item more than
    /// <see cref="MaxPlaces"/> places from its index. <paramref name="evidence"/> holds at most one row per item id, of the
    /// items' own project and kind.
    /// </summary>
    public static List<T> Reorder<T>(IReadOnlyList<T> items, Func<T, int> idOf, IEnumerable<KnowledgeItemEvidence> evidence)
    {
        var netById = evidence.ToDictionary(x => x.ItemId, x => Net(x));
        var sorted = Enumerable.Range(0, items.Count)
            .OrderBy(x => x - MaxPlaces * netById.GetValueOrDefault(idOf(items[x])))
            .ToList();

        return HeldToMaxPlaces(sorted)
            .Select(x => items[x])
            .ToList();
    }

    // The keys stay within MaxPlaces of the index, but an item can still end MaxPlaces + 1 places away: a harmful item
    // passed by two helpful ones and a neutral one. Fills the positions in sorted order, except that an item is placed no
    // later than MaxPlaces after its index and none earlier than MaxPlaces before it. An order that keeps the bound already
    // comes back unchanged. At each position at least one remaining item qualifies, so the scan always finds one.
    private static List<int> HeldToMaxPlaces(List<int> sorted)
    {
        var placed = new bool[sorted.Count];
        var held = new List<int>(sorted.Count);
        var cursor = 0;
        for (var position = 0; position < sorted.Count; position++)
        {
            while (placed[sorted[cursor]])
            {
                cursor++;
            }

            var due = position - MaxPlaces;
            var next = due >= 0 && !placed[due]
                ? due
                : sorted
                    .Skip(cursor)
                    .Where(x => !placed[x])
                    .Where(x => x <= position + MaxPlaces)
                    .First();
            placed[next] = true;
            held.Add(next);
        }

        return held;
    }
}

/// <summary>What the boost reads of one item's stats row: its scores and how many signals and callers they come from.</summary>
internal sealed record KnowledgeItemEvidence
{
    public int ItemId { get; init; }
    public double HelpfulScore { get; init; }
    public double HarmfulScore { get; init; }
    public int SignalCount { get; init; }
    public int CallerCount { get; init; }
}
