namespace Beacon.Core.Adapters.Shared;

/// <summary>
/// Provides consistent formatting for cell values across all notification adapters.
/// Handles date/time types, booleans, and null values uniformly.
/// </summary>
internal static class CellValueFormatter
{
    /// <summary>
    /// Formats a cell value for display in notifications.
    /// </summary>
    /// <param name="value">The value to format</param>
    /// <returns>Formatted string representation</returns>
    public static string Format(object? value)
    {
        if (value == null)
        {
            return string.Empty;
        }

        return value switch
        {
            DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss"),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("yyyy-MM-dd HH:mm:ss"),
            DateOnly dateOnly => dateOnly.ToString("yyyy-MM-dd"),
            TimeOnly timeOnly => timeOnly.ToString("HH:mm:ss"),
            bool boolean => boolean ? "Yes" : "No",
            _ => value.ToString() ?? string.Empty
        };
    }

    /// <summary>
    /// Formats a cell value and sanitizes it for table display (removes newlines, tabs).
    /// </summary>
    /// <param name="value">The value to format and sanitize</param>
    /// <returns>Formatted and sanitized string</returns>
    public static string FormatAndSanitize(object? value)
    {
        var formatted = Format(value);

        if (string.IsNullOrEmpty(formatted))
        {
            return formatted;
        }

        // Remove characters that would break table formatting
        return System.Text.RegularExpressions.Regex.Replace(formatted, @"\t|\n|\r", " ").Trim();
    }
}
