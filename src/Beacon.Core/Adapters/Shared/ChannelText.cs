using System.Globalization;
using System.Text;

namespace Beacon.Core.Adapters.Shared;

/// <summary>
/// Makes text that comes from query results, names and SQL safe to show in a channel's own markup, and keeps it within
/// the channel's size limits.
/// </summary>
internal static class ChannelText
{
    public const string TruncationMarker = "... (truncated)";

    /// <summary>
    /// Removes characters that change how the surrounding text reads or is laid out: bidirectional and other format
    /// (Cf) characters such as zero-width and direction overrides, and control characters other than tab and line breaks.
    /// </summary>
    public static string StripUnsafe(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (!text.Any(IsUnsafe))
        {
            return text;
        }

        return string.Concat(text.Where(x => !IsUnsafe(x)));
    }

    /// <summary>Slack's own escaping of text: <c>&amp; &lt; &gt;</c>, so no mention, link or command is formed.</summary>
    public static string Slack(string? text)
    {
        return StripUnsafe(text)
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="Slack"/> for text that is shown inside a code fence: a backtick is replaced by a look-alike, so the
    /// text cannot close the fence.
    /// </summary>
    public static string SlackFenced(string? text)
    {
        return Slack(text).Replace('`', 'ˋ');
    }

    /// <summary>Text for a Teams card's markdown: markup characters are backslash-escaped, so no link, emphasis or tag is formed.</summary>
    public static string Teams(string? text)
    {
        var clean = StripUnsafe(text);
        var builder = new StringBuilder(clean.Length + 8);
        foreach (var x in clean)
        {
            if (x is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '~')
            {
                builder.Append('\\');
            }

            builder.Append(x);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Cuts <paramref name="text"/> to at most <paramref name="maxLength"/> characters including the marker, never
    /// between the two halves of a surrogate pair.
    /// </summary>
    public static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        var keep = Math.Max(0, maxLength - TruncationMarker.Length);
        if (keep > 0 && char.IsHighSurrogate(text[keep - 1]))
        {
            keep--;
        }

        return text[..keep] + TruncationMarker;
    }

    /// <summary>
    /// <see cref="Truncate"/> for text that went through <see cref="Slack"/>: a cut never leaves half of an
    /// <c>&amp;amp;</c>-style entity behind.
    /// </summary>
    public static string TruncateEscaped(string escaped, int maxLength)
    {
        if (escaped.Length <= maxLength)
        {
            return escaped;
        }

        var cut = Truncate(escaped, maxLength);
        var keep = cut.Length - TruncationMarker.Length;
        var entityStart = cut.LastIndexOf('&', Math.Max(0, keep - 1), Math.Min(keep, 5));
        if (entityStart >= 0 && cut.IndexOf(';', entityStart, keep - entityStart) < 0)
        {
            return cut[..entityStart] + TruncationMarker;
        }

        return cut;
    }

    /// <summary>Cuts <paramref name="text"/> to at most <paramref name="maxLength"/> characters, without a marker and never inside a surrogate pair.</summary>
    public static string Cut(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        var keep = maxLength;
        if (keep > 0 && char.IsHighSurrogate(text[keep - 1]))
        {
            keep--;
        }

        return text[..keep];
    }

    private static bool IsUnsafe(char x)
    {
        if (x is '\t' or '\n' or '\r')
        {
            return false;
        }

        var category = char.GetUnicodeCategory(x);

        return category is UnicodeCategory.Control or UnicodeCategory.Format;
    }
}
