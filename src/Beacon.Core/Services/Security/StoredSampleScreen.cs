using Beacon.Core.Models;

namespace Beacon.Core.Services.Security;

/// <summary>
/// The stored sample values of a column that may be shown now. A metadata refresh screens samples before it stores them,
/// but with the guardrails of that moment: a project that links the data source later, or a custom PII pattern added
/// since, applies only when the values are read back. So every path that shows stored samples passes them through here
/// with the settings that apply now (for a shared data source, the strictest of the live projects that link it).
/// <para>
/// The screen is the one sampling and value-grounding probes use: a column whose name
/// <see cref="ColumnValueExclusions"/> leaves out (PII, built-in or custom pattern; secret-like; a financial amount), or
/// one of whose values looks like a credential (<see cref="CredentialValueScreen"/>), shows none of its samples, and a
/// value that <see cref="PiiValueScreen"/> finds PII-shaped or matching a custom pattern is dropped while the others stay.
/// So samples stored before these screens existed are withheld until the next refresh replaces them. As when values are
/// sampled, the built-in screens apply whatever the PII-detection setting. Nothing is shown while sample-value
/// collection is off (which the deployment lock <c>Beacon:Mcp:ForbidRowDataToLlm</c> pins).
/// </para>
/// </summary>
public static class StoredSampleScreen
{
    /// <returns>The values that may be shown: <paramref name="values"/> itself when nothing is withheld, null when nothing
    /// remains. When fewer values remain, they are no longer the column's complete set of values.</returns>
    public static IReadOnlyList<string>? Screen(
        IQueryGuardrailService guardrail,
        string columnName,
        IReadOnlyList<string>? values,
        McpSettingsData settings)
    {
        if (!settings.EnableSampleValueCollection)
        {
            return null;
        }

        if (values is not { Count: > 0 })
        {
            return values;
        }

        if (ColumnValueExclusions.IsExcludedColumn(guardrail, columnName, settings.CustomPiiPatterns)
            || CredentialValueScreen.ContainsCredential(values))
        {
            return null;
        }

        var shown = values
            .Where(x => !PiiValueScreen.ContainsPiiValue([x], settings.CustomPiiPatterns))
            .ToList();

        if (shown.Count == values.Count)
        {
            return values;
        }

        return shown.Count > 0 ? shown : null;
    }
}
