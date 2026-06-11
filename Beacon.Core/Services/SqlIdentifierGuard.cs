using System.Text.RegularExpressions;

namespace Beacon.Core.Services;

/// <summary>
/// Validation helpers for SQL identifiers (schema / table / column names) that are interpolated
/// directly into SQL text because identifiers cannot be parameterized. Per §1.10, every such
/// identifier must be whitelist-validated before it reaches a query string.
/// </summary>
internal static partial class SqlIdentifierGuard
{
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierRegex();

    /// <summary>
    /// Validates a single SQL identifier against <c>^[A-Za-z_][A-Za-z0-9_]*$</c> and returns it unchanged.
    /// Throws <see cref="InvalidOperationException"/> for anything that is not a plain identifier.
    /// </summary>
    public static string Validate(string identifier, string kind = "identifier")
    {
        if (string.IsNullOrEmpty(identifier) || !IdentifierRegex().IsMatch(identifier))
        {
            throw new InvalidOperationException($"Invalid SQL {kind} '{identifier}'.");
        }

        return identifier;
    }

    /// <summary>
    /// Doubles the supplied quote character inside an identifier so it can be safely wrapped in quotes.
    /// Use as defence-in-depth when the identifier has already been validated.
    /// </summary>
    public static string EscapeQuote(string identifier, char quoteChar)
    {
        return identifier.Replace(quoteChar.ToString(), new string(quoteChar, 2));
    }
}
