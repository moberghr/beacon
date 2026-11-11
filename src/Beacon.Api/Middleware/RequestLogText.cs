using System.Text;
using Microsoft.AspNetCore.Http;

namespace Beacon.Api.Middleware;

/// <summary>
/// The request's method and path as they are written to a log: one line, no control characters, bounded length.
/// The path arrives decoded, so it can carry line breaks the caller chose.
/// </summary>
internal static class RequestLogText
{
    internal const int MaxLength = 256;

    public static string Method(HttpRequest request)
    {
        return OneLine(request.Method);
    }

    public static string Path(HttpRequest request)
    {
        return OneLine(request.Path.Value);
    }

    internal static string OneLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var singleLine = text
            .Replace("\r", " ")
            .Replace("\n", " ");
        var length = Math.Min(singleLine.Length, MaxLength);
        var builder = new StringBuilder(length);

        for (var i = 0; i < length; i++)
        {
            var character = singleLine[i];
            builder.Append(IsLineOrControl(character) ? ' ' : character);
        }

        return builder.ToString();
    }

    // Control and format characters, and the Unicode line and paragraph separators some log viewers break lines on.
    private static bool IsLineOrControl(char character)
    {
        return char.IsControl(character) || character == (char)0x85 || character == (char)0x2028 || character == (char)0x2029;
    }
}
