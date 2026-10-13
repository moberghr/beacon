using System.Text.Json;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Approvals;
using Beacon.Core.Notifications;

namespace Beacon.Core.ChangeApprovals;

/// <summary>
/// What an approval of a recipient covers: where it delivers — its type, its destination and its custom headers (names and
/// values), read in plaintext — character for character. Its name, description and body template are left out. The
/// definition holds secrets, so only its keyed hash (<see cref="RecipientDestinationHasher"/>) is stored, on the server
/// only. A reviewer is shown <see cref="ViewOfStored"/>: what identifies where it delivers (the host, the parts of a known
/// provider's path it documents as not secret, header names) with everything else masked, and short keyed fingerprints
/// of the whole definition and of each header value, so a change in a masked part is still seen and can be compared out
/// of band.
/// </summary>
internal static class RecipientDestinationDefinition
{
    private const int SlackIdMinLength = 9;
    private const int SlackIdMaxLength = 15;
    private const int WorkflowIdLength = 32;

    // The webhooks whose documented non-secret path parts a reviewer is shown; every other URL shows its host only.
    private static readonly ProviderPath[] KnownProviders =
    [
        // https://hooks.slack.com/services/{workspace id}/{webhook id}/{token}
        new(
            x => string.Equals(x, "hooks.slack.com", StringComparison.OrdinalIgnoreCase),
            [
                PathSegment.Word("services"), PathSegment.Identifier(x => IsSlackId(x, 'T')), PathSegment.Identifier(x => IsSlackId(x, 'B')),
                PathSegment.Secret
            ]),

        // https://{region}.logic.azure.com/workflows/{workflow id}/triggers/{trigger}/paths/invoke?…&sig={signature}
        new(
            x => x.EndsWith(".logic.azure.com", StringComparison.OrdinalIgnoreCase),
            [
                PathSegment.Word("workflows"), PathSegment.Identifier(IsWorkflowId), PathSegment.Word("triggers"), PathSegment.Identifier(IsManualTrigger),
                PathSegment.Word("paths"), PathSegment.Word("invoke")
            ]),

        // https://{environment}.environment.api.powerplatform.com/powerautomate/automations/direct/workflows/{workflow id}/triggers/{trigger}/paths/invoke?…&sig={signature}
        new(
            x => x.EndsWith(".api.powerplatform.com", StringComparison.OrdinalIgnoreCase),
            [
                PathSegment.Word("powerautomate"), PathSegment.Word("automations"), PathSegment.Word("direct"), PathSegment.Word("workflows"),
                PathSegment.Identifier(IsWorkflowId), PathSegment.Word("triggers"), PathSegment.Identifier(IsManualTrigger), PathSegment.Word("paths"), PathSegment.Word("invoke")
            ]),

        // https://{tenant}.webhook.office.com/webhookb2/{group id}@{tenant id}/IncomingWebhook/{secret}/…
        new(
            x => x.EndsWith(".webhook.office.com", StringComparison.OrdinalIgnoreCase),
            [PathSegment.Word("webhookb2"), PathSegment.Identifier(IsGroupAtTenant), PathSegment.Word("IncomingWebhook")],
            OpenEnded: true)
    ];

    /// <summary>The text a recipient's definition is hashed and fingerprinted from.</summary>
    public static string Canonical(NotificationType type, string destination, string? headersJson)
    {
        // Headers by name, so the same headers hash alike in any order; none and an empty object are the same, as they are
        // to a delivery. Headers that are not a JSON object of strings are hashed as written.
        var headers = RecipientSecrets.TryReadHeaders(headersJson);
        var canonical = new
        {
            type = (int)type,
            destination,
            headers = (headers ?? [])
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => new[] { x.Key, x.Value })
                .ToList(),
            unreadHeaders = headers == null && !string.IsNullOrWhiteSpace(headersJson) ? headersJson : null
        };

        return "beacon:recipient-destination:v2\n" + JsonSerializer.Serialize(canonical);
    }

    /// <summary>The keyed hash of where a recipient delivers, from its plaintext destination and headers.</summary>
    public static string Hash(RecipientDestinationHasher hasher, NotificationType type, string destination, string? headersJson)
    {
        return hasher.Hash(Canonical(type, destination, headersJson));
    }

    /// <summary>
    /// The keyed hash of where a stored recipient delivers, decrypting its destination and headers; null when either cannot
    /// be read (such a destination is never approved).
    /// </summary>
    public static string? HashOfStored(
        RecipientSecretProtector protector,
        RecipientDestinationHasher hasher,
        NotificationType type,
        string storedDestination,
        string? storedHeadersJson)
    {
        var canonical = CanonicalOfStored(protector, type, storedDestination, storedHeadersJson);

        return canonical == null ? null : hasher.Hash(canonical);
    }

    /// <summary>The canonical text of a stored recipient's definition; null when its destination or headers cannot be read.</summary>
    public static string? CanonicalOfStored(RecipientSecretProtector protector, NotificationType type, string storedDestination, string? storedHeadersJson)
    {
        var destination = protector.TryUnprotect(storedDestination);
        var headers = protector.TryUnprotect(storedHeadersJson);
        if (destination == null || (!string.IsNullOrEmpty(storedHeadersJson) && headers == null))
        {
            return null;
        }

        return Canonical(type, destination, headers);
    }

    /// <summary>
    /// True when a recipient whose approved hash is <paramref name="approvedHash"/> may deliver to the definition whose
    /// canonical text is <paramref name="canonical"/>: it is not governed (null), or the definition is the approved one,
    /// under the current key or a previous one. A definition that cannot be read (null) is never approved.
    /// </summary>
    public static bool Allows(RecipientDestinationHasher hasher, string? approvedHash, string? canonical)
    {
        return approvedHash == null || (canonical != null && hasher.Matches(approvedHash, canonical));
    }

    /// <summary>
    /// Where a stored recipient delivers as a reviewer is shown it: what identifies the destination, credentials masked,
    /// header names with fingerprints of their values, and a fingerprint of the whole definition; never a secret.
    /// </summary>
    public static RecipientDestinationView ViewOfStored(
        RecipientSecretProtector protector,
        RecipientDestinationHasher hasher,
        NotificationType type,
        string storedDestination,
        string? storedHeadersJson)
    {
        var destination = protector.TryUnprotect(storedDestination);
        var headersJson = protector.TryUnprotect(storedHeadersJson);
        var unreadable = destination == null || (!string.IsNullOrEmpty(storedHeadersJson) && headersJson == null);
        var headers = (RecipientSecrets.TryReadHeaders(headersJson) ?? [])
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToList();

        return new RecipientDestinationView
        {
            NotificationType = type,
            Destination = destination == null ? RecipientSecrets.Mask : Identifying(type, destination),
            HeaderNames = headers
                .Select(x => x.Key)
                .ToList(),
            Headers = headers
                .Select(x =>
                    new RecipientHeaderView
                    {
                        Name = x.Key,
                        Fingerprint = HeaderFingerprint(hasher, x.Value)
                    })
                .ToList(),
            Fingerprint = unreadable ? null : DefinitionFingerprint(hasher, Canonical(type, destination!, headersJson)),
            Unreadable = unreadable
        };
    }

    /// <summary>The keyed fingerprint of a whole definition (its canonical text).</summary>
    public static string DefinitionFingerprint(RecipientDestinationHasher hasher, string canonical)
    {
        return hasher.Fingerprint("definition\n" + canonical);
    }

    /// <summary>The keyed fingerprint of one header value.</summary>
    public static string HeaderFingerprint(RecipientDestinationHasher hasher, string value)
    {
        return hasher.Fingerprint("header-value\n" + value);
    }

    /// <summary>
    /// True when two views show the same thing — type, destination as shown, header names — but name different definitions
    /// (their fingerprints differ): the change lies only in a masked part.
    /// </summary>
    public static bool ChangeHiddenByMask(RecipientDestinationView? previous, RecipientDestinationView? proposed)
    {
        return previous != null
            && proposed != null
            && previous.NotificationType == proposed.NotificationType
            && string.Equals(previous.Destination, proposed.Destination, StringComparison.Ordinal)
            && previous.HeaderNames.SequenceEqual(proposed.HeaderNames, StringComparer.Ordinal)
            && !string.Equals(previous.Fingerprint, proposed.Fingerprint, StringComparison.Ordinal);
    }

    /// <summary>
    /// What identifies where a destination delivers, its credentials masked. An email destination (addresses) is shown as
    /// is; a Jira one keeps its site, project and email. A URL keeps its scheme, host and port and masks everything else
    /// by default: each path segment is shown as a mask (so the path's shape is visible), as are user info, every query
    /// value (parameter names are kept) and a fragment. Only the parts a known provider documents as not secret are shown:
    /// Slack's workspace and webhook ids in <c>hooks.slack.com/services/T…/B…/{token}</c>, and the fixed path words and
    /// workflow id of a Logic Apps or Power Automate workflow (Teams workflows; their signature is a query value) and the
    /// fixed words and group and tenant ids of a legacy Teams connector. A change in a masked part shows only in the
    /// fingerprints.
    /// </summary>
    internal static string Identifying(NotificationType type, string destination)
    {
        if (type is NotificationType.Email or NotificationType.Jira)
        {
            return RecipientSecrets.MaskDestination(type, destination);
        }

        if (!Uri.TryCreate(destination.Trim(), UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            return RecipientSecrets.Mask;
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        var shown = KnownProviderPath(uri.IdnHost, segments)
            ?? segments
                .Select(_ => RecipientSecrets.Mask)
                .ToList();
        var userInfo = string.IsNullOrEmpty(uri.UserInfo) ? string.Empty : RecipientSecrets.Mask + "@";
        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        var fragment = uri.Fragment.Length > 1 ? "#" + RecipientSecrets.Mask : string.Empty;

        return $"{uri.Scheme}://{userInfo}{uri.IdnHost}{port}/{string.Join('/', shown)}{MaskedQuery(uri.Query)}{fragment}";
    }

    // The path of a known provider's webhook as shown, its documented non-secret parts kept and every other segment masked;
    // null when the host is no known provider's or the path does not have the shape the provider documents.
    private static List<string>? KnownProviderPath(string host, IReadOnlyList<string> segments)
    {
        var provider = KnownProviders
            .Where(x => x.IsHost(host))
            .FirstOrDefault();
        if (provider == null || !provider.Fits(segments))
        {
            return null;
        }

        return segments
            .Select((x, i) => i < provider.Segments.Count && provider.Segments[i].Shows(x) ? x : RecipientSecrets.Mask)
            .ToList();
    }

    // Slack's workspace (T…) and webhook (B…) ids: upper-case letters and digits.
    private static bool IsSlackId(string segment, char prefix)
    {
        return segment.Length is >= SlackIdMinLength and <= SlackIdMaxLength
            && segment[0] == prefix
            && segment.All(x => char.IsAsciiDigit(x) || char.IsAsciiLetterUpper(x));
    }

    // A Logic Apps or Power Automate workflow id: 32 hex digits.
    private static bool IsWorkflowId(string segment)
    {
        return segment.Length == WorkflowIdLength && segment.All(char.IsAsciiHexDigit);
    }

    // The trigger a Teams or Power Automate workflow is called through, under the name its request trigger is documented with.
    private static bool IsManualTrigger(string segment)
    {
        return string.Equals(segment, "manual", StringComparison.OrdinalIgnoreCase);
    }

    // A legacy Teams connector's {group id}@{tenant id}: two GUIDs.
    private static bool IsGroupAtTenant(string segment)
    {
        var parts = segment.Split('@');

        return parts.Length == 2 && parts.All(x => Guid.TryParseExact(x, "D", out _));
    }

    // Query parameter names are kept, their values masked (a signature or key travels there); a parameter without '=' (a
    // bare value) is masked whole.
    private static string MaskedQuery(string query)
    {
        if (string.IsNullOrEmpty(query) || query == "?")
        {
            return string.Empty;
        }

        var parameters = query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Contains('=') ? x.Split('=', 2)[0] + "=" + RecipientSecrets.Mask : RecipientSecrets.Mask);

        return "?" + string.Join('&', parameters);
    }

    // One segment of a provider's documented path: a fixed word (the path must hold it), an identifier the provider
    // documents as not secret (shown when it has the documented form, masked otherwise), or a part that is always masked.
    private sealed record PathSegment(Func<string, bool> Shows, bool Required)
    {
        public static PathSegment Word(string word) => new(x => string.Equals(x, word, StringComparison.OrdinalIgnoreCase), true);

        public static PathSegment Identifier(Func<string, bool> form) => new(form, false);

        public static PathSegment Secret { get; } = new(_ => false, false);
    }

    // A known provider's webhook: its hosts, and the path it documents (with OpenEnded, further segments are masked).
    private sealed record ProviderPath(Func<string, bool> IsHost, IReadOnlyList<PathSegment> Segments, bool OpenEnded = false)
    {
        public bool Fits(IReadOnlyList<string> path)
        {
            return (OpenEnded ? path.Count >= Segments.Count : path.Count == Segments.Count)
                && Segments
                    .Select((x, i) => !x.Required || x.Shows(path[i]))
                    .All(x => x);
        }
    }
}
