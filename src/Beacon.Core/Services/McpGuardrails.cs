using System.Globalization;
using Beacon.Core.Models;

namespace Beacon.Core.Services;

/// <summary>
/// The settings that bound what a query against a data source may run and return, and what grounding may read from it:
/// read-only enforcement, PII detection and its custom patterns (which the grounding PII screens use too), the row
/// limit, the statement timeout, the result size, the plan-cost limit, live value grounding and its probe budget, and
/// sample-value collection. A data source several live projects link is held to the strictest of each among those
/// projects (read-only and PII detection on when any project turns them on, value grounding and sample values off when
/// any project turns them off, every project's custom PII patterns, the lowest limits), whichever project's caller
/// queries it — so the most protective project's settings follow its data wherever the data is reachable
/// (<see cref="ISharedSourceSettings"/>).
/// </summary>
internal static class McpGuardrails
{
    public const string EnforceReadOnly = "enforceReadOnly";
    public const string EnablePiiDetection = "enablePiiDetection";
    public const string CustomPiiPatterns = "customPiiPatterns";
    public const string MaxRowLimit = "maxRowLimit";
    public const string StatementTimeoutSeconds = "statementTimeoutSeconds";
    public const string MaxResultBytes = "maxResultBytes";
    public const string MaxExplainCost = "maxExplainCost";
    public const string EnableValueGrounding = "enableValueGrounding";
    public const string ValueGroundingMaxProbes = "valueGroundingMaxProbes";
    public const string EnableSampleValueCollection = "enableSampleValueCollection";

    // The single-valued guardrails: when one project's value is stricter than another's, and how the value reads.
    private static readonly IReadOnlyList<Guardrail> SingleValued =
    [
        new(EnforceReadOnly, (x, y) => x.EnforceReadOnly && !y.EnforceReadOnly, x => Format(x.EnforceReadOnly)),
        new(EnablePiiDetection, (x, y) => x.EnablePiiDetection && !y.EnablePiiDetection, x => Format(x.EnablePiiDetection)),
        new(MaxRowLimit, (x, y) => x.MaxRowLimit < y.MaxRowLimit, x => Format(x.MaxRowLimit)),
        new(StatementTimeoutSeconds, (x, y) => x.StatementTimeoutSeconds < y.StatementTimeoutSeconds, x => Format(x.StatementTimeoutSeconds)),
        new(MaxResultBytes, (x, y) => x.MaxResultBytes < y.MaxResultBytes, x => Format(x.MaxResultBytes)),
        new(MaxExplainCost, (x, y) => x.MaxExplainCost != null && (y.MaxExplainCost == null || x.MaxExplainCost < y.MaxExplainCost), x => Format(x.MaxExplainCost)),
        new(EnableValueGrounding, (x, y) => !x.EnableValueGrounding && y.EnableValueGrounding, x => Format(x.EnableValueGrounding)),
        new(ValueGroundingMaxProbes, (x, y) => x.ValueGroundingMaxProbes < y.ValueGroundingMaxProbes, x => Format(x.ValueGroundingMaxProbes)),
        new(EnableSampleValueCollection, (x, y) => !x.EnableSampleValueCollection && y.EnableSampleValueCollection, x => Format(x.EnableSampleValueCollection))
    ];

    /// <summary>Tightens every guardrail of <paramref name="target"/> to the stricter of its own and <paramref name="other"/>'s.</summary>
    public static void Tighten(McpSettingsData target, McpSettingsData other)
    {
        target.EnforceReadOnly = target.EnforceReadOnly || other.EnforceReadOnly;
        target.EnablePiiDetection = target.EnablePiiDetection || other.EnablePiiDetection;
        target.CustomPiiPatterns = target.CustomPiiPatterns
            .Union(other.CustomPiiPatterns, StringComparer.Ordinal)
            .ToList();
        target.MaxRowLimit = Math.Min(target.MaxRowLimit, other.MaxRowLimit);
        target.StatementTimeoutSeconds = Math.Min(target.StatementTimeoutSeconds, other.StatementTimeoutSeconds);
        target.MaxResultBytes = Math.Min(target.MaxResultBytes, other.MaxResultBytes);
        target.MaxExplainCost = other.MaxExplainCost != null && (target.MaxExplainCost == null || other.MaxExplainCost < target.MaxExplainCost)
            ? other.MaxExplainCost
            : target.MaxExplainCost;
        target.EnableValueGrounding = target.EnableValueGrounding && other.EnableValueGrounding;
        target.ValueGroundingMaxProbes = Math.Min(target.ValueGroundingMaxProbes, other.ValueGroundingMaxProbes);
        target.EnableSampleValueCollection = target.EnableSampleValueCollection && other.EnableSampleValueCollection;
    }

    /// <summary>
    /// Each guardrail's effective value over <paramref name="projects"/> — the first is the project asking, the others the
    /// projects it shares the data source with — and the project that sets it: the asking project when it already holds
    /// that value, otherwise the first project (in the given order) that holds it. For the custom PII patterns, the value
    /// is how many patterns apply, set by the asking project unless another project adds one.
    /// </summary>
    public static List<(string Setting, string Value, int ProjectId)> Describe(IReadOnlyList<(int ProjectId, McpSettingsData Settings)> projects)
    {
        var described = new List<(string Setting, string Value, int ProjectId)>();
        foreach (var guardrail in SingleValued)
        {
            var strictest = projects[0];
            foreach (var project in projects.Skip(1))
            {
                if (guardrail.IsStricter(project.Settings, strictest.Settings))
                {
                    strictest = project;
                }
            }

            described.Add((guardrail.Name, guardrail.Format(strictest.Settings), strictest.ProjectId));
        }

        var own = projects[0].Settings.CustomPiiPatterns;
        var patterns = projects
            .SelectMany(x => x.Settings.CustomPiiPatterns)
            .Distinct(StringComparer.Ordinal)
            .Count();
        var adding = projects
            .Skip(1)
            .Where(x => x.Settings.CustomPiiPatterns.Except(own, StringComparer.Ordinal).Any())
            .Select(x => (int?)x.ProjectId)
            .FirstOrDefault();
        described.Add((CustomPiiPatterns, Format(patterns), adding ?? projects[0].ProjectId));

        return described;
    }

    private static string Format(bool value) => value ? "true" : "false";

    private static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Format(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "none";

    private sealed record Guardrail(string Name, Func<McpSettingsData, McpSettingsData, bool> IsStricter, Func<McpSettingsData, string> Format);
}
