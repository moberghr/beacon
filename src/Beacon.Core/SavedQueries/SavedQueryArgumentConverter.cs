using System.Globalization;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.SavedQueries;

/// <summary>
/// Converts a saved query's argument values from text to the typed values its parameters bind (§1.10): a number to an
/// integer or a decimal, a date-time to a <see cref="DateTime"/> (an explicit offset normalized to UTC, a bare value left
/// unspecified), a string as given up to <see cref="MaxStringArgumentLength"/> characters. Shared by the MCP saved-query
/// tools (which read JSON arguments) and the embed render (whose values are the grant's fixed values and the token's
/// locked ones), so both bind a value alike.
/// </summary>
internal static class SavedQueryArgumentConverter
{
    public const int MaxStringArgumentLength = 4000;

    /// <summary>
    /// Converts every parameter's value from <paramref name="supplied"/>. Returns an error message, or null with
    /// <paramref name="values"/> keyed by parameter name. An unknown name or a missing value is refused.
    /// </summary>
    public static string? ConvertAll(
        IReadOnlyList<SavedQueryToolParameter> parameters,
        IReadOnlyDictionary<string, string> supplied,
        out Dictionary<string, object?> values)
    {
        values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var name in supplied.Keys)
        {
            if (!parameters.Any(x => x.Name == name))
            {
                return $"Unknown argument '{name}'.";
            }
        }

        foreach (var parameter in parameters)
        {
            if (!supplied.TryGetValue(parameter.Name, out var text) || text == null)
            {
                return $"Missing required argument '{parameter.Name}'.";
            }

            var error = ConvertText(parameter, text, out var value);
            if (error != null)
            {
                return error;
            }

            values[parameter.Name] = value;
        }

        return null;
    }

    /// <summary>Converts one value written as text. Returns an error message, or null with <paramref name="value"/> set.</summary>
    public static string? ConvertText(SavedQueryToolParameter parameter, string text, out object? value)
    {
        value = null;

        switch (parameter.Type)
        {
            case ParameterType.Number:
                if (TryParseInteger(text, out var integer))
                {
                    value = integer;
                    return null;
                }

                if (TryParseDecimal(text, out var number))
                {
                    value = number;
                    return null;
                }

                return $"Argument '{parameter.Name}' must be a number.";

            case ParameterType.DateTime:
                if (TryParseDateTime(text, out var dateTime))
                {
                    value = dateTime;
                    return null;
                }

                return $"Argument '{parameter.Name}' must be an ISO 8601 date or date-time string (for example 2026-01-31 or 2026-01-31T00:00:00Z).";

            default:
                if (text.Length > MaxStringArgumentLength)
                {
                    return $"Argument '{parameter.Name}' is longer than {MaxStringArgumentLength} characters.";
                }

                value = text;
                return null;
        }
    }

    public static bool TryParseInteger(string? text, out long value)
    {
        return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    public static bool TryParseDecimal(string? text, out decimal value)
    {
        return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    // An explicit offset or Z is honored and normalized to UTC; a bare date or local date-time stays unspecified, so
    // it compares with timestamp-without-time-zone columns as written.
    public static bool TryParseDateTime(string text, out DateTime value)
    {
        value = default;
        var hasOffset = text.EndsWith('Z') || text.EndsWith('z') || HasNumericOffset(text);

        if (hasOffset)
        {
            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var offset))
            {
                return false;
            }

            value = offset.UtcDateTime;
            return true;
        }

        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return false;
        }

        value = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return true;
    }

    private static bool HasNumericOffset(string text)
    {
        var timeIndex = text.IndexOf('T');
        if (timeIndex < 0)
        {
            return false;
        }

        var time = text[(timeIndex + 1)..];

        return time.Contains('+') || time.Contains('-');
    }
}
