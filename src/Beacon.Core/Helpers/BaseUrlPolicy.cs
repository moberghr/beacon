using System.Net;

namespace Beacon.Core.Helpers;

/// <summary>
/// The address the links Beacon sends out (Teams, Slack, webhook payloads) point to. A saved value is an absolute https
/// URL with no user information and no fragment; plain http is accepted only for a loopback host while the host runs in
/// Development. A link is built from a stored value only while it is still usable, so a value that was stored before this
/// check never aborts a delivery: the link is left out.
/// </summary>
internal static class BaseUrlPolicy
{
    public const string InvalidMessage =
        "The base URL must be an absolute https URL without user information or a fragment (plain http is accepted only for localhost in Development).";

    /// <summary>Null when <paramref name="value"/> may be saved (empty clears it); otherwise why it may not.</summary>
    public static string? Validate(string? value, bool development)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!TryParse(value.Trim(), out var uri))
        {
            return InvalidMessage;
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return null;
        }

        return development && IsLoopback(uri) ? null : InvalidMessage;
    }

    /// <summary>
    /// The link to <paramref name="path"/> under <paramref name="baseUrl"/>, or null when the base URL is empty or not a
    /// usable absolute http(s) address.
    /// </summary>
    public static string? Link(string? baseUrl, string path)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || !TryParse(baseUrl.Trim(), out _))
        {
            return null;
        }

        return $"{baseUrl.Trim().TrimEnd('/')}/{path.TrimStart('/')}";
    }

    private static bool TryParse(string value, out Uri uri)
    {
        var parsed = Uri.TryCreate(value, UriKind.Absolute, out var result) ? result : null;
        uri = parsed!;

        return parsed != null
            && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp)
            && string.IsNullOrEmpty(parsed.UserInfo)
            && string.IsNullOrEmpty(parsed.Fragment)
            && !value.Contains('#')
            && !string.IsNullOrEmpty(parsed.Host);
    }

    private static bool IsLoopback(Uri uri)
    {
        return uri.IsLoopback
            || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));
    }
}
