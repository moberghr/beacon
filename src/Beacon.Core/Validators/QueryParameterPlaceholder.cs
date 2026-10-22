using System.Text.RegularExpressions;

namespace Beacon.Core.Validators;

/// <summary>
/// The shape of a step parameter's placeholder: <c>{</c>, an identifier (<c>[A-Za-z_][A-Za-z0-9_]{0,62}</c>) and <c>}</c>.
/// Execution replaces every occurrence of the placeholder text in the step's SQL with the bound value, so a placeholder that
/// is a column name, a literal or a predicate fragment would turn reviewed SQL into a caller-supplied value. Enforced when
/// parameters are saved; parameters stored earlier keep running as they are.
/// </summary>
internal static partial class QueryParameterPlaceholder
{
    /// <summary>True when <paramref name="placeholder"/> is <c>{identifier}</c>.</summary>
    public static bool IsValid(string? placeholder) =>
        placeholder != null && ShapePattern().IsMatch(placeholder);

    /// <summary>
    /// Why the placeholders of one step cannot be saved (a placeholder that is not <c>{identifier}</c>, or one used by two
    /// parameters), or null when they can. The message names the parameter, never SQL.
    /// </summary>
    public static string? RefusalFor(IEnumerable<(string? Name, string? Placeholder)> parameters)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, placeholder) in parameters)
        {
            if (!IsValid(placeholder))
            {
                return $"Parameter '{name}' has an invalid placeholder; use {{identifier}} (letters, digits, underscore).";
            }

            if (!seen.Add(placeholder!))
            {
                return $"Parameter '{name}' uses a placeholder another parameter of the step already uses.";
            }
        }

        return null;
    }

    [GeneratedRegex(@"\A\{[A-Za-z_][A-Za-z0-9_]{0,62}\}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ShapePattern();
}
