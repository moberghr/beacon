using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Beacon.Core.Configuration;

/// <summary>
/// The one syntax a host in <see cref="DataSourceConnectionOptions.AllowedHosts"/>, and a host a connection reaches, may
/// take: an ASCII host name (labels of letters, digits, <c>-</c> and <c>_</c>, one optional trailing dot; an
/// internationalised name in its <c>xn--</c> form) or an IP address (IPv6 with or without brackets). Anything else
/// (a scheme, port, path, query, user part, space or non-ASCII character) is no host and matches no entry.
/// </summary>
internal static partial class DataSourceHostName
{
    /// <summary>
    /// The comparable form of <paramref name="value"/>: an IP address in its canonical text (so <c>10.1</c> and
    /// <c>10.0.0.1</c> are the same host), a host name in lower case without its trailing dot; null when it is neither.
    /// </summary>
    public static string? Canonical(string? value)
    {
        var host = value?.Trim() ?? string.Empty;
        if (host.StartsWith('[') && host.EndsWith(']'))
        {
            return IPAddress.TryParse(host[1..^1], out var bracketed) && bracketed.AddressFamily == AddressFamily.InterNetworkV6
                ? bracketed.ToString().ToLowerInvariant()
                : null;
        }

        if (IsIpAddressSyntax(host) && IPAddress.TryParse(host, out var address))
        {
            return address.ToString().ToLowerInvariant();
        }

        return Name(host);
    }

    /// <summary>Whether <paramref name="value"/> is an IP address (IPv6 with or without brackets).</summary>
    public static bool IsIpAddress(string? value)
    {
        var host = value?.Trim() ?? string.Empty;

        return Canonical(host) != null && (host.StartsWith('[') || (IsIpAddressSyntax(host) && IPAddress.TryParse(host, out _)));
    }

    /// <summary>The lower-case form of a host name, without its trailing dot; null when it is not a host name.</summary>
    public static string? Name(string? value)
    {
        var host = value?.Trim() ?? string.Empty;

        return HostName().IsMatch(host) ? host.TrimEnd('.').ToLowerInvariant() : null;
    }

    // Digits and dots (IPv4, also its short forms) or hex digits and colons (IPv6, with an optional zone).
    private static bool IsIpAddressSyntax(string host)
    {
        return host.Length > 0 && (host.All(x => char.IsAsciiDigit(x) || x == '.') || host.Contains(':'));
    }

    [GeneratedRegex(@"^(?=.{1,253}\.?$)[A-Za-z0-9_](?:[A-Za-z0-9_-]{0,61}[A-Za-z0-9_])?(?:\.[A-Za-z0-9_](?:[A-Za-z0-9_-]{0,61}[A-Za-z0-9_])?)*\.?$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HostName();
}
