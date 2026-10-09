using System.Text.Json;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Notifications;

/// <summary>
/// How recipient secrets are shown and edited. Reads return masked values only: a vendor URL keeps its scheme and host,
/// a webhook URL keeps only the last two labels of its host, a Jira destination keeps its site, project and email,
/// header values are replaced, and an email destination (addresses, not a secret) is shown as is to the admins who may
/// read it. On update, a value echoed back masked, or left empty, keeps the stored secret, but only while the
/// destination stays exactly the same (scheme, host, port, path and query); otherwise the secret must be entered again,
/// so a stored token or header can never follow a destination to somewhere new. A stored value that cannot be decrypted
/// cannot be kept either.
/// </summary>
internal static class RecipientSecrets
{
    public const string Mask = "********";

    public static string MaskDestination(NotificationType type, string destination)
    {
        switch (type)
        {
            case NotificationType.Email:
                return destination;
            case NotificationType.Jira:
                var parts = destination.Split(';');
                return parts.Length == 4
                    ? $"{parts[0].Trim()};{parts[1].Trim()};{parts[2].Trim()};{Mask}"
                    : Mask;
            case NotificationType.Webhook:
                return Uri.TryCreate(destination.Trim(), UriKind.Absolute, out var webhook) && !string.IsNullOrEmpty(webhook.Host)
                    ? $"{webhook.Scheme}://{MaskHost(webhook)}{Port(webhook)}/{Mask}"
                    : Mask;
            default:
                return Uri.TryCreate(destination.Trim(), UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
                    ? $"{uri.Scheme}://{uri.Authority}/{Mask}"
                    : Mask;
        }
    }

    /// <summary>The header names with masked values, or null when there are none (or they cannot be read).</summary>
    public static string? MaskHeaders(string? headersJson)
    {
        var headers = TryReadHeaders(headersJson);
        if (headers == null || headers.Count == 0)
        {
            return null;
        }

        var masked = headers.ToDictionary(x => x.Key, _ => Mask);
        return JsonSerializer.Serialize(masked);
    }

    /// <summary>
    /// The destination to store. Empty, or the masked form of the stored destination, keeps the stored one (same type,
    /// readable). A Jira destination whose token is masked keeps the stored token when the site is unchanged. Any other
    /// value containing the mask is refused, so a mask is never saved as a secret.
    /// </summary>
    public static string ResolveDestination(
        NotificationType type,
        string? submitted,
        NotificationType storedType,
        string? storedDestination,
        bool storedUnreadable)
    {
        var sameType = storedType == type;
        var value = submitted?.Trim();
        var keepsStored = string.IsNullOrEmpty(value) || value.Contains(Mask, StringComparison.Ordinal);

        if (sameType && storedUnreadable && keepsStored)
        {
            throw new InvalidOperationException("The stored destination could not be read: enter it again.");
        }

        if (sameType && storedDestination != null && (string.IsNullOrEmpty(value) || value == MaskDestination(type, storedDestination)))
        {
            return storedDestination;
        }

        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException("Recipient destination is required.");
        }

        if (sameType && storedDestination != null && type == NotificationType.Jira && TryKeepJiraToken(value, storedDestination, out var merged))
        {
            return merged;
        }

        if (value.Contains(Mask, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Enter the full destination: a masked value can only keep the stored one unchanged.");
        }

        return value;
    }

    /// <summary>
    /// The headers JSON to store. Only webhooks send custom headers, so any other type stores none. For a webhook on the
    /// same destination, empty keeps the stored headers and a masked value keeps that header's stored value; <c>{}</c>
    /// removes them. After any destination change, stored headers must be entered again (or removed with <c>{}</c>).
    /// A value that contains the mask without being it is refused as ambiguous.
    /// </summary>
    public static string? ResolveHeaders(
        NotificationType type,
        string? submitted,
        string? storedHeadersJson,
        bool storedUnreadable,
        bool sameDestination)
    {
        if (type != NotificationType.Webhook)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(submitted))
        {
            if (storedUnreadable)
            {
                throw new InvalidOperationException(
                    "The stored custom headers could not be read: enter them again, or send {} to remove them.");
            }

            if (sameDestination)
            {
                return storedHeadersJson;
            }

            if (TryReadHeaders(storedHeadersJson) is { Count: > 0 })
            {
                throw new InvalidOperationException(
                    "Enter the custom headers again, or send {} to remove them: the destination changed.");
            }

            return null;
        }

        Dictionary<string, string>? headers;
        try
        {
            headers = JsonSerializer.Deserialize<Dictionary<string, string>>(submitted);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Custom headers must be a JSON object of string values.");
        }

        if (headers == null)
        {
            throw new InvalidOperationException("Custom headers must be a JSON object of string values.");
        }

        var storedByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in TryReadHeaders(storedHeadersJson) ?? [])
        {
            storedByName.TryAdd(name, value);
        }

        var resolved = new Dictionary<string, string>();
        foreach (var (name, value) in headers)
        {
            if (value != Mask)
            {
                if (value != null && value.Contains(Mask, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Enter the full header value: a masked value can only keep the stored one unchanged.");
                }

                resolved[name] = value!;
                continue;
            }

            if (storedUnreadable)
            {
                throw new InvalidOperationException(
                    "The stored custom headers could not be read: enter them again, or send {} to remove them.");
            }

            if (!sameDestination || !storedByName.TryGetValue(name, out var storedValue))
            {
                throw new InvalidOperationException(
                    "Enter every header value: a masked value can only keep a stored value for the same destination.");
            }

            resolved[name] = storedValue;
        }

        return resolved.Count == 0 ? null : JsonSerializer.Serialize(resolved);
    }

    /// <summary>Whether two URL destinations are the same: scheme, host, port, path and query (no user info or fragment).</summary>
    public static bool SameDestination(string? first, string? second)
    {
        if (first == null || second == null)
        {
            return false;
        }

        return Uri.TryCreate(first.Trim(), UriKind.Absolute, out var a)
            && Uri.TryCreate(second.Trim(), UriKind.Absolute, out var b)
            && string.Equals(
                a.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped),
                b.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped),
                StringComparison.Ordinal);
    }

    private static bool TryKeepJiraToken(string submitted, string storedDestination, out string merged)
    {
        merged = string.Empty;
        var parts = submitted.Split(';');
        var stored = storedDestination.Split(';');
        if (parts.Length != 4 || stored.Length != 4 || parts[3].Trim() != Mask)
        {
            return false;
        }

        if (!SameJiraSite(submitted, storedDestination))
        {
            throw new InvalidOperationException("Enter the Jira API token again: the Jira site changed.");
        }

        merged = $"{parts[0].Trim()};{parts[1].Trim()};{parts[2].Trim()};{stored[3].Trim()}";
        return true;
    }

    // "acme" and "https://acme.atlassian.net" are the same site: compare the URL each one calls.
    private static bool SameJiraSite(string first, string second)
    {
        try
        {
            return string.Equals(
                NotificationDestinationPolicy.JiraSiteUrl(first).TrimEnd('/'),
                NotificationDestinationPolicy.JiraSiteUrl(second).TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    // Only the last two labels of a webhook host are shown; an address literal is hidden entirely.
    private static string MaskHost(Uri uri)
    {
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            return "***";
        }

        var labels = uri.IdnHost
            .TrimEnd('.')
            .Split('.');

        return labels.Length > 2
            ? $"***.{labels[^2]}.{labels[^1]}"
            : uri.IdnHost;
    }

    private static string Port(Uri uri)
    {
        return uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
    }

    private static Dictionary<string, string>? TryReadHeaders(string? headersJson)
    {
        if (string.IsNullOrWhiteSpace(headersJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Turns what a caller submits for a recipient into what is stored, and a stored recipient into what an admin sees:
/// resolves kept (masked or empty) secrets against the stored ones (<see cref="RecipientSecrets"/>), checks the result
/// against the destination policy, and encrypts it. Shared by the REST handlers and <c>IRecipientService</c>.
/// </summary>
internal sealed class RecipientSecretEditor(NotificationDestinationPolicy destinationPolicy, RecipientSecretProtector protector)
{
    /// <summary>
    /// The encrypted destination and headers to store, or <see cref="InvalidOperationException"/> when the submission
    /// breaks a rule. <paramref name="stored"/> is null for a new recipient.
    /// </summary>
    public (string Destination, string? HeadersJson) Prepare(
        NotificationType type,
        string? destination,
        string? headersJson,
        StoredRecipientSecrets? stored)
    {
        // A new recipient, or one whose type changes, has nothing it may keep.
        var keepable = stored != null && stored.Type == type ? stored : null;
        var storedDestination = protector.TryUnprotect(keepable?.Destination);
        var storedHeadersJson = protector.TryUnprotect(keepable?.HeadersJson);
        var destinationUnreadable = keepable != null && storedDestination == null;
        var headersUnreadable = !string.IsNullOrEmpty(keepable?.HeadersJson) && storedHeadersJson == null;

        var resolvedDestination = RecipientSecrets.ResolveDestination(type, destination, keepable?.Type ?? type, storedDestination, destinationUnreadable);
        var allowedDestination = destinationPolicy.EnsureAllowed(type, resolvedDestination);

        var sameDestination = RecipientSecrets.SameDestination(allowedDestination, storedDestination);
        var resolvedHeaders = RecipientSecrets.ResolveHeaders(type, headersJson, storedHeadersJson, headersUnreadable, sameDestination);
        destinationPolicy.ParseHeaders(resolvedHeaders);

        return (protector.Protect(allowedDestination), protector.ProtectOptional(resolvedHeaders));
    }

    /// <summary>What an admin sees of a stored recipient: masked destination and headers, and whether any stored value cannot be decrypted.</summary>
    public MaskedRecipientSecrets ReadMasked(NotificationType type, string storedDestination, string? storedHeadersJson)
    {
        var destination = protector.TryUnprotect(storedDestination);
        var headers = protector.TryUnprotect(storedHeadersJson);
        var unreadable = destination == null || (!string.IsNullOrEmpty(storedHeadersJson) && headers == null);

        return new MaskedRecipientSecrets(
            destination == null ? RecipientSecrets.Mask : RecipientSecrets.MaskDestination(type, destination),
            RecipientSecrets.MaskHeaders(headers),
            unreadable);
    }
}

/// <summary>A recipient's stored (encrypted, or legacy plaintext) secrets and type.</summary>
internal sealed record StoredRecipientSecrets(NotificationType Type, string Destination, string? HeadersJson);

internal sealed record MaskedRecipientSecrets(string Destination, string? HeadersJson, bool Unreadable);
