namespace Beacon.Core.Services;

/// <summary>
/// The step orders a query's steps carry: 1, 2, 3, … without gaps or repeats, so a step's order is its place in the run.
/// Two steps sharing an order run in whichever order the store returns them, and a review that pairs steps by order
/// would show one of them only. Checked on a save and on every submission, and again on the snapshot a restore or an
/// approval applies.
/// </summary>
internal static class QueryStepOrders
{
    public const string Refusal = "The steps' orders must run 1, 2, 3, … without gaps or repeats.";

    /// <summary>Why <paramref name="orders"/> cannot be a query's step orders, or null when they can (no steps included).</summary>
    public static string? RefusalFor(IEnumerable<int> orders)
    {
        var sorted = orders
            .Order()
            .ToList();

        return sorted.Where((x, i) => x != i + 1).Any() ? Refusal : null;
    }
}
