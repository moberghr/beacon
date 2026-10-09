using System.Text.RegularExpressions;

namespace Beacon.Core.Notifications;

/// <summary>
/// Host allow-list entries: an exact host (<c>hooks.slack.com</c>) or a wildcard (<c>*.atlassian.net</c>) that matches
/// any subdomain at any depth but never the domain itself. A wildcard needs at least two labels after <c>*.</c>, so a
/// whole top-level domain (<c>*.com</c>) cannot be allowed. Comparison is case-insensitive on the ASCII (IDN) form and
/// ignores a trailing dot.
/// </summary>
internal static partial class HostPattern
{
    public static bool IsValid(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var host = pattern.Trim();
        if (!host.StartsWith("*.", StringComparison.Ordinal))
        {
            return HostNameRegex().IsMatch(host);
        }

        var domain = host[2..].TrimEnd('.');
        return HostNameRegex().IsMatch(domain) && domain.Contains('.');
    }

    public static bool Matches(string pattern, string host)
    {
        var normalizedHost = Normalize(host);
        var normalizedPattern = Normalize(pattern);
        if (normalizedHost.Length == 0 || normalizedPattern.Length == 0)
        {
            return false;
        }

        if (normalizedPattern.StartsWith("*.", StringComparison.Ordinal))
        {
            return normalizedHost.EndsWith(normalizedPattern[1..], StringComparison.Ordinal);
        }

        return normalizedHost == normalizedPattern;
    }

    public static bool MatchesAny(IEnumerable<string> patterns, string host)
    {
        return patterns.Any(x => Matches(x, host));
    }

    private static string Normalize(string value)
    {
        return value
            .Trim()
            .TrimEnd('.')
            .ToLowerInvariant();
    }

    // Dot-separated labels of letters, digits and hyphens (IDN hosts are compared in their punycode form).
    [GeneratedRegex(@"^(?=.{1,253}$)([A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*\.?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HostNameRegex();
}
