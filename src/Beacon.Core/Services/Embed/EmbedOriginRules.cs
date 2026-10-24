using System.Globalization;
using System.Text.RegularExpressions;

namespace Beacon.Core.Services.Embed;

/// <summary>
/// The frame origins a grant may list: exact <c>https://host[:port]</c> origins, written in lower case, with no path,
/// query, fragment, user info or wildcard, and a host of ASCII letters, digits, dots and hyphens (an international name in
/// its punycode form). The default port 443 is dropped, as a browser serializes the origin. <c>http://localhost[:port]</c>
/// is accepted only in Development. A grant lists between one and <c>Beacon:Embed:MaxOrigins</c> distinct origins.
/// </summary>
internal static partial class EmbedOriginRules
{
    /// <summary>The origins, normalized and distinct, in the order given; throws when one is not an accepted origin.</summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string?>? origins, int maxOrigins, bool allowLocalhost)
    {
        var normalized = new List<string>();
        foreach (var origin in origins ?? [])
        {
            var value = NormalizeOne(origin, allowLocalhost);
            if (!normalized.Contains(value, StringComparer.Ordinal))
            {
                normalized.Add(value);
            }
        }

        if (normalized.Count == 0)
        {
            throw new InvalidOperationException("An embed grant lists at least one frame origin.");
        }

        if (normalized.Count > maxOrigins)
        {
            throw new InvalidOperationException($"An embed grant lists at most {maxOrigins} frame origins.");
        }

        return normalized;
    }

    private static string NormalizeOne(string? origin, bool allowLocalhost)
    {
        var value = (origin ?? string.Empty).Trim().ToLowerInvariant();
        var match = OriginPattern().Match(value);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                "A frame origin is written as https://host or https://host:port, with no path, wildcard or other part.");
        }

        var scheme = match.Groups["scheme"].Value;
        var host = match.Groups["host"].Value;
        var port = match.Groups["port"].Success
            ? int.Parse(match.Groups["port"].Value, CultureInfo.InvariantCulture)
            : (int?)null;

        if (!IsHostName(host))
        {
            throw new InvalidOperationException("A frame origin's host is a name of letters, digits, dots and hyphens.");
        }

        if (port is < 1 or > 65535)
        {
            throw new InvalidOperationException("A frame origin's port is between 1 and 65535.");
        }

        if (scheme == "http")
        {
            if (!allowLocalhost || host != "localhost")
            {
                throw new InvalidOperationException("A frame origin uses https; http://localhost is accepted only in Development.");
            }

            return port == null ? "http://localhost" : $"http://localhost:{port.Value.ToString(CultureInfo.InvariantCulture)}";
        }

        return port is null or 443
            ? $"https://{host}"
            : $"https://{host}:{port.Value.ToString(CultureInfo.InvariantCulture)}";
    }

    // Labels of letters, digits and inner hyphens, 1–63 characters each, at most 253 in all.
    private static bool IsHostName(string host)
    {
        return host.Length <= 253
            && host.Split('.').All(x => x.Length is >= 1 and <= 63 && !x.StartsWith('-') && !x.EndsWith('-'));
    }

    [GeneratedRegex("^(?<scheme>https?)://(?<host>[a-z0-9.-]+)(:(?<port>[0-9]{1,5}))?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex OriginPattern();
}
