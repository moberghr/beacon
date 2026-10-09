using System.Net;
using System.Net.Mail;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Beacon.Core.Adapters.Jira;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;

namespace Beacon.Core.Notifications;

/// <summary>
/// Where a recipient may point. Checked when a recipient is saved and again before every delivery, so a destination
/// stored before the policy existed (or under a looser configuration) is never sent to:
/// <list type="bullet">
/// <item>Slack, Teams, Jira and webhooks need an absolute https URL without user credentials.</item>
/// <item>Slack, Teams and Jira hosts must be the vendor's (<see cref="BuiltInHosts"/>) or configured in
/// <c>Beacon:Notifications:AllowedHosts:{Type}</c>.</item>
/// <item>A webhook may target any host unless <c>AllowedHosts:Webhook</c> is set; an IP literal or <c>localhost</c>
/// must still be public or listed in <c>AllowedPrivateNetworks:Webhook</c> (the connect-time check covers host
/// names).</item>
/// <item>Email destinations are bare addresses (no display names), with domains listed when <c>AllowedHosts:Email</c>
/// is set; they are stored and sent as a normalised comma-separated list.</item>
/// <item>A type in <c>DisabledTypes</c> is refused outright.</item>
/// </list>
/// Webhook custom headers are limited to <see cref="ParseHeaders"/>'s allow-list. Messages never echo a destination.
/// </summary>
internal sealed partial class NotificationDestinationPolicy(
    IOptions<NotificationChannelOptions> options,
    OutboundAddressPolicy addressPolicy)
{
    public const int MaxHeaders = 20;

    public const int MaxHeaderValueLength = 4096;

    public static readonly IReadOnlyDictionary<NotificationType, string[]> BuiltInHosts = new Dictionary<NotificationType, string[]>
    {
        [NotificationType.Slack] = ["hooks.slack.com"],
        // Legacy Office 365 connectors, Azure Logic Apps, and Power Automate workflow triggers (Teams "Workflows").
        [NotificationType.Teams] = ["*.webhook.office.com", "*.logic.azure.com", "*.environment.api.powerplatform.com"],
        [NotificationType.Jira] = ["*.atlassian.net", "api.atlassian.com"],
    };

    public static readonly IReadOnlyList<string> BuiltInHeaderNames = ["Authorization", "Api-Key", "X-Api-Key", "Ocp-Apim-Subscription-Key"];

    // Headers that control the connection, framing, routing, cookies or cloud metadata access: never allowed, not even
    // through AllowedHeaders.
    private static readonly HashSet<string> ReservedHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host",
        "Content-Length",
        "Content-Type",
        "Content-Encoding",
        "Transfer-Encoding",
        "Connection",
        "Keep-Alive",
        "Upgrade",
        "TE",
        "Trailer",
        "Expect",
        "Proxy-Authorization",
        "Proxy-Authenticate",
        "Proxy-Connection",
        "Cookie",
        "Set-Cookie",
        "Forwarded",
        "Via",
        "X-Forwarded-For",
        "X-Forwarded-Host",
        "X-Forwarded-Proto",
        "X-Forwarded-Port",
        "X-Forwarded-Prefix",
        "X-Forwarded-Server",
        "X-Real-IP",
        "X-Client-IP",
        "X-Original-URL",
        "X-Original-Host",
        "X-Rewrite-URL",
        "X-Host",
        "X-HTTP-Method",
        "X-HTTP-Method-Override",
        "X-Method-Override",
        "Metadata",
        "Metadata-Flavor",
        "X-aws-ec2-metadata-token",
        "X-aws-ec2-metadata-token-ttl-seconds",
    };

    /// <summary>Whether <paramref name="name"/> may be added through <c>AllowedHeaders</c>: a header token that is not reserved.</summary>
    public static bool IsConfigurableHeaderName(string? name)
    {
        return name != null
            && HeaderNameRegex().IsMatch(name)
            && !ReservedHeaderNames.Contains(name);
    }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when <paramref name="destination"/> is not allowed for
    /// <paramref name="type"/>; otherwise returns it in the form to store and send (trimmed, and for email the normalised
    /// comma-separated address list).
    /// </summary>
    public string EnsureAllowed(NotificationType type, string destination)
    {
        if (options.Value.DisabledTypes.Contains(type))
        {
            throw new InvalidOperationException($"Notifications of type '{type}' are disabled on this server.");
        }

        if (string.IsNullOrWhiteSpace(destination))
        {
            throw new InvalidOperationException("Recipient destination is required.");
        }

        switch (type)
        {
            case NotificationType.Email:
                return NormalizeEmail(destination);
            case NotificationType.Jira:
                EnsureUrlAllowed(type, JiraSiteUrl(destination));
                return destination.Trim();
            case NotificationType.Slack:
            case NotificationType.Teams:
            case NotificationType.Webhook:
                EnsureUrlAllowed(type, destination.Trim());
                return destination.Trim();
            default:
                throw new InvalidOperationException("Unknown notification type.");
        }
    }

    /// <summary>
    /// Parses and checks webhook custom headers: a JSON object of string values, at most <see cref="MaxHeaders"/>, each
    /// name one of <see cref="BuiltInHeaderNames"/> or <c>AllowedHeaders</c>, each value short and free of control
    /// characters. Null or empty means no headers.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ParseHeaders(string? headersJson)
    {
        if (string.IsNullOrWhiteSpace(headersJson))
        {
            return null;
        }

        Dictionary<string, string>? headers;
        try
        {
            headers = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Custom headers must be a JSON object of string values.");
        }

        if (headers == null)
        {
            throw new InvalidOperationException("Custom headers must be a JSON object of string values.");
        }

        if (headers.Count > MaxHeaders)
        {
            throw new InvalidOperationException($"Custom headers allow at most {MaxHeaders} entries.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
        {
            if (!HeaderNameRegex().IsMatch(name))
            {
                throw new InvalidOperationException("Custom header names must be 1-100 letters, digits or '-'.");
            }

            if (!seen.Add(name))
            {
                throw new InvalidOperationException($"Custom header '{name}' appears more than once.");
            }

            if (!IsAllowedHeaderName(name))
            {
                throw new InvalidOperationException($"Custom header '{name}' is not allowed.");
            }

            if (value == null || value.Length > MaxHeaderValueLength || value.Any(char.IsControl))
            {
                throw new InvalidOperationException(
                    $"Custom header values must be at most {MaxHeaderValueLength} characters without control characters.");
            }
        }

        return headers;
    }

    /// <summary>
    /// The base URL a Jira destination (<c>site;project;email;token</c>) calls: an https URL as given, or a bare site
    /// name expanded to <c>https://{site}.atlassian.net</c> exactly as <see cref="JiraCredentials"/> does.
    /// </summary>
    public static string JiraSiteUrl(string destination)
    {
        JiraCredentials credentials;
        try
        {
            credentials = new JiraCredentials(destination);
        }
        catch (BeaconException ex)
        {
            throw new InvalidOperationException(ex.Message);
        }

        var site = destination
            .Split(';')[0]
            .Trim();

        if (!site.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !JiraSiteNameRegex().IsMatch(site))
        {
            throw new InvalidOperationException("The Jira site must be an https URL or a site name such as 'yourcompany'.");
        }

        return credentials.DomainUrl;
    }

    private bool IsAllowedHeaderName(string name)
    {
        if (ReservedHeaderNames.Contains(name))
        {
            return false;
        }

        return BuiltInHeaderNames.Contains(name, StringComparer.OrdinalIgnoreCase)
            || options.Value.AllowedHeaders.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    private void EnsureUrlAllowed(NotificationType type, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host))
        {
            throw new InvalidOperationException("The destination must be an absolute https URL.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException("The destination URL must not contain user credentials.");
        }

        var host = uri.IdnHost;
        var configured = options.Value.AllowedHosts.For(type);

        if (BuiltInHosts.TryGetValue(type, out var builtIn))
        {
            if (!HostPattern.MatchesAny(builtIn, host) && !HostPattern.MatchesAny(configured, host))
            {
                throw new InvalidOperationException($"The destination host is not allowed for {type} recipients.");
            }

            return;
        }

        if (configured.Count > 0 && !HostPattern.MatchesAny(configured, host))
        {
            throw new InvalidOperationException($"The destination host is not allowed for {type} recipients.");
        }

        var bareHost = host.Trim('[', ']');
        if (IPAddress.TryParse(bareHost, out var address) && !addressPolicy.IsAllowed(type, bareHost, address))
        {
            throw new InvalidOperationException("The destination address is not a public address.");
        }

        if (IsLocalHostName(bareHost) && !addressPolicy.IsHostAllowed(type, bareHost))
        {
            throw new InvalidOperationException("The destination address is not a public address.");
        }
    }

    // Bare addresses only: each part must be exactly what MailAddress reads as the address (no display name, no
    // comments, no whitespace or control characters inside), with a host-name domain.
    private string NormalizeEmail(string destination)
    {
        var configured = options.Value.AllowedHosts.Email;
        var parts = destination
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
        {
            throw new InvalidOperationException("Recipient destination is required.");
        }

        var addresses = new List<string>();
        foreach (var part in parts)
        {
            if (part.Any(x => char.IsWhiteSpace(x) || char.IsControl(x))
                || !MailAddress.TryCreate(part, out var address)
                || address.Address != part
                || !HostPattern.IsValid(address.Host))
            {
                throw new InvalidOperationException("The destination must be email addresses separated by commas, without display names.");
            }

            if (configured.Count > 0 && !HostPattern.MatchesAny(configured, address.Host))
            {
                throw new InvalidOperationException("The destination contains an email domain that is not allowed.");
            }

            addresses.Add(address.Address);
        }

        return string.Join(",", addresses);
    }

    private static bool IsLocalHostName(string host)
    {
        var normalized = host
            .TrimEnd('.')
            .ToLowerInvariant();

        return normalized == "localhost" || normalized.EndsWith(".localhost", StringComparison.Ordinal);
    }

    // RFC 9110 token characters, kept to the conventional subset for header names.
    [GeneratedRegex("^[A-Za-z0-9-]{1,100}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HeaderNameRegex();

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex JiraSiteNameRegex();
}
