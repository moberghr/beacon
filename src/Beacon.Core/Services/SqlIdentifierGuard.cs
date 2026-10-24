using System.Text.RegularExpressions;

namespace Beacon.Core.Services;

/// <summary>
/// Validation helpers for SQL identifiers (schema / table / column names) that are interpolated
/// directly into SQL text because identifiers cannot be parameterized. Per §1.10, every such
/// identifier must be whitelist-validated before it reaches a query string.
/// </summary>
public static partial class SqlIdentifierGuard
{
    /// <summary>The longest name <see cref="ForLog"/> writes in full.</summary>
    public const int MaxLoggedLength = 128;

    // \z, not $: `$` also matches before a trailing newline, which would let "orders\n" through.
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*\z", RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex IdentifierRegex();

    /// <summary>
    /// Validates a single SQL identifier against <c>^[A-Za-z_][A-Za-z0-9_]*\z</c> and returns it unchanged.
    /// Throws <see cref="InvalidOperationException"/> for anything that is not a plain identifier.
    /// </summary>
    public static string Validate(string identifier, string kind = "identifier")
    {
        if (string.IsNullOrEmpty(identifier) || !IsPlainIdentifier(identifier))
        {
            throw new InvalidOperationException($"Invalid SQL {kind} '{identifier}'.");
        }

        return identifier;
    }

    private static bool IsPlainIdentifier(string identifier)
    {
        try
        {
            return IdentifierRegex().IsMatch(identifier);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Doubles the supplied quote character inside an identifier so it can be safely wrapped in quotes.
    /// Use as defence-in-depth when the identifier has already been validated.
    /// </summary>
    public static string EscapeQuote(string identifier, char quoteChar)
    {
        return identifier.Replace(quoteChar.ToString(), new string(quoteChar, 2));
    }

    /// <summary>
    /// <paramref name="identifier"/> as a quoted identifier of <paramref name="dialect"/> (the data source's dialect name),
    /// its own quote character doubled so no part of it is read as SQL: <c>[name]</c> with <c>]]</c> on SQL Server and
    /// Synapse, <c>`name`</c> with doubled backticks on MySQL and Databricks, <c>"name"</c> with <c>""</c> everywhere
    /// else (PostgreSQL, Snowflake, SQLite).
    /// </summary>
    public static string Quote(string identifier, string? dialect)
    {
        if (Validation.SqlDialects.IsTSql(dialect))
        {
            return $"[{EscapeQuote(identifier, ']')}]";
        }

        var engine = (dialect ?? string.Empty).ToLowerInvariant();
        if (engine is "mysql" or "mariadb" or "databricks")
        {
            return $"`{EscapeQuote(identifier, '`')}`";
        }

        return $"\"{EscapeQuote(identifier, '"')}\"";
    }

    /// <summary>
    /// True when a catalog name holds a character that ends or escapes a quoted identifier, or ends a statement, in some
    /// dialect — a quote, backtick, bracket or semicolon — or a control character. Such names are not ingested from a
    /// connected database, and Beacon never builds SQL from one.
    /// </summary>
    public static bool HasUnsafeCharacters(string identifier)
    {
        return identifier.Any(x => x is '\'' or '"' or '`' or '[' or ']' or ';' || char.IsControl(x));
    }

    /// <summary>
    /// A catalog name made safe for a log line: every control or format character (a line break, a direction override) is
    /// written as <c>\uXXXX</c>, so the name cannot forge or reorder log text, and a name longer than
    /// <see cref="MaxLoggedLength"/> characters is cut with <c>…</c>.
    /// </summary>
    public static string ForLog(string identifier)
    {
        var text = identifier.Length > MaxLoggedLength ? identifier[..MaxLoggedLength] + "…" : identifier;
        if (!text.Any(IsUnprintable))
        {
            return text;
        }

        var builder = new System.Text.StringBuilder(text.Length + 16);
        foreach (var character in text)
        {
            if (IsUnprintable(character))
            {
                builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"\\u{(int)character:X4}");
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static bool IsUnprintable(char character)
    {
        return char.IsControl(character)
            || char.GetUnicodeCategory(character) is System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.LineSeparator
                or System.Globalization.UnicodeCategory.ParagraphSeparator;
    }
}
