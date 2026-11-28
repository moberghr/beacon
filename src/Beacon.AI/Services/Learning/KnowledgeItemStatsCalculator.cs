namespace Beacon.AI.Services.Learning;

/// <summary>
/// Turns the <c>ask</c> signals that carried knowledge items into each item's helpful and harmful score, signal count and
/// caller count. Pure: no database, no clock.
/// <para>
/// A signal's verdict wins over its implicit outcome: a curator's verdict weighs <see cref="CuratorVerdictWeight"/>, any
/// other <see cref="VerdictWeight"/>, Correct counting as helpful and Incorrect as harmful. Without a verdict a successful
/// answer that was not re-run edited adds <see cref="ImplicitOutcomeWeight"/> helpful, and a failed answer, or one a
/// <c>query</c>/<c>dry_run</c> re-ran with edited SQL, the same harmful. Per item and score one caller adds at most
/// <see cref="CallerCap"/> from its verdicts and implicit outcomes, and at most <see cref="CuratorCallerCap"/> from
/// curator verdicts on its signals.
/// </para>
/// </summary>
internal static class KnowledgeItemStatsCalculator
{
    internal const double CuratorVerdictWeight = 3;
    internal const double VerdictWeight = 1;
    internal const double ImplicitOutcomeWeight = 0.25;
    internal const double CallerCap = 2;
    internal const double CuratorCallerCap = 6;

    /// <summary>The score of every item the signals carried, ordered by kind and id.</summary>
    public static IReadOnlyList<KnowledgeItemScore> Compute(IEnumerable<KnowledgeItemSignal> signals)
    {
        var tallies = new Dictionary<KnowledgeItemKey, ItemTally>();
        foreach (var signal in signals)
        {
            var caller = KnowledgeItemCaller.Of(signal.CallerHash, signal.UserId);
            var outcome = OutcomeOf(signal);
            foreach (var item in signal.Items)
            {
                if (!tallies.TryGetValue(item, out var tally))
                {
                    tally = new ItemTally();
                    tallies.Add(item, tally);
                }

                tally.Add(signal.SignalId, caller, outcome);
            }
        }

        return tallies
            .OrderBy(x => x.Key.Kind)
            .ThenBy(x => x.Key.ItemId)
            .Select(x => x.Value.ScoreOf(x.Key))
            .ToList();
    }

    private static SignalOutcome OutcomeOf(KnowledgeItemSignal signal)
    {
        var verdictWeight = signal.FromCurator ? CuratorVerdictWeight : VerdictWeight;

        return signal.Verdict switch
        {
            McpUserVerdict.Correct => new SignalOutcome(verdictWeight, 0, signal.FromCurator),
            McpUserVerdict.Incorrect => new SignalOutcome(0, verdictWeight, signal.FromCurator),
            _ => signal.Succeeded && !signal.HasEditedRerun
                ? new SignalOutcome(ImplicitOutcomeWeight, 0, FromCurator: false)
                : new SignalOutcome(0, ImplicitOutcomeWeight, FromCurator: false)
        };
    }

    private readonly record struct SignalOutcome(double Helpful, double Harmful, bool FromCurator);

    // Who asked: the caller's hash, else its Beacon user; a signal with neither belongs to one shared anonymous caller. A
    // hash and a user id never compare equal.
    private readonly record struct KnowledgeItemCaller(string? Hash, int? UserId)
    {
        public static KnowledgeItemCaller Of(string? callerHash, int? userId)
        {
            return string.IsNullOrEmpty(callerHash)
                ? new KnowledgeItemCaller(null, userId)
                : new KnowledgeItemCaller(callerHash, null);
        }
    }

    private sealed class CallerTally
    {
        public double Helpful { get; set; }

        public double Harmful { get; set; }

        public double CuratorHelpful { get; set; }

        public double CuratorHarmful { get; set; }
    }

    private sealed class ItemTally
    {
        private readonly HashSet<int> _signalIds = [];
        private readonly Dictionary<KnowledgeItemCaller, CallerTally> _callers = [];

        // A signal that carried the item twice counts once.
        public void Add(int signalId, KnowledgeItemCaller caller, SignalOutcome outcome)
        {
            if (!_signalIds.Add(signalId))
            {
                return;
            }

            if (!_callers.TryGetValue(caller, out var tally))
            {
                tally = new CallerTally();
                _callers.Add(caller, tally);
            }

            if (outcome.FromCurator)
            {
                tally.CuratorHelpful += outcome.Helpful;
                tally.CuratorHarmful += outcome.Harmful;

                return;
            }

            tally.Helpful += outcome.Helpful;
            tally.Harmful += outcome.Harmful;
        }

        public KnowledgeItemScore ScoreOf(KnowledgeItemKey item)
        {
            var helpful = _callers.Values.Sum(x => Math.Min(x.Helpful, CallerCap) + Math.Min(x.CuratorHelpful, CuratorCallerCap));
            var harmful = _callers.Values.Sum(x => Math.Min(x.Harmful, CallerCap) + Math.Min(x.CuratorHarmful, CuratorCallerCap));

            return new KnowledgeItemScore(item, helpful, harmful, _signalIds.Count, _callers.Count);
        }
    }
}

/// <summary>A knowledge item: its kind and its id in that kind's table.</summary>
internal readonly record struct KnowledgeItemKey(McpKnowledgeItemKind Kind, int ItemId);

/// <summary>What the calculation reads of one <c>ask</c> signal: ids, the caller key, the verdict, flags and the items it carried.</summary>
internal sealed record KnowledgeItemSignal
{
    public int SignalId { get; init; }

    public string? CallerHash { get; init; }

    public int? UserId { get; init; }

    public McpUserVerdict Verdict { get; init; }

    /// <summary>The verdict was recorded by a curator (<c>McpQuerySignal.FeedbackFromCurator</c>).</summary>
    public bool FromCurator { get; init; }

    public bool Succeeded { get; init; }

    /// <summary>A <c>query</c> or <c>dry_run</c> signal of the project followed it with edited SQL.</summary>
    public bool HasEditedRerun { get; init; }

    public IReadOnlyList<KnowledgeItemKey> Items { get; init; } = [];
}

/// <summary>One item's scores over the signals that carried it.</summary>
internal sealed record KnowledgeItemScore(KnowledgeItemKey Item, double HelpfulScore, double HarmfulScore, int SignalCount, int CallerCount);
