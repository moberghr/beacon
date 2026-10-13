using System.Text.Json;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Approvals;
using Beacon.Core.Notifications;

namespace Beacon.Core.ChangeApprovals;

/// <summary>
/// What an approval of a recipient covers: where it delivers — its type, its destination and its custom headers (names and
/// values), read in plaintext — character for character. Its name, description and body template are left out. The
/// definition holds secrets, so only its keyed hash (<see cref="RecipientDestinationHasher"/>) is stored, on the server
/// only. A reviewer is shown <see cref="ViewOfStored"/>: what identifies where it delivers (the host and path, Slack's
/// workspace and channel ids, header names) with every credential masked, and short keyed fingerprints of the whole
/// definition and of each header value, so a change in a masked part is still seen and can be compared out of band.
/// </summary>
internal static class RecipientDestinationDefinition
{
    private const int TokenLikeLength = 8;
    private const int LongSegmentLength = 20;

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
    /// is; a Jira one keeps its site, project and email. A URL keeps its scheme, full host, port and path, with user info,
    /// query values and token-like path segments masked: Slack's workspace and channel ids are kept and its token masked;
    /// a Teams workflow keeps its path (its signature is in the query), a legacy Teams connector's secret segment is masked.
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
        var shown = segments
            .Select((x, i) => ShowsSegment(type, segments, i) ? x : RecipientSecrets.Mask)
            .ToList();
        var userInfo = string.IsNullOrEmpty(uri.UserInfo) ? string.Empty : RecipientSecrets.Mask + "@";
        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";

        return $"{uri.Scheme}://{userInfo}{uri.IdnHost}{port}/{string.Join('/', shown)}{MaskedQuery(uri.Query)}";
    }

    // Whether a path segment is shown: Slack's ids before its token, a Teams path but its legacy connector secret, and
    // anywhere else a segment that does not look like a token.
    private static bool ShowsSegment(NotificationType type, IReadOnlyList<string> segments, int index)
    {
        switch (type)
        {
            case NotificationType.Slack when segments.Count >= 4 && segments[0] == "services":
                return index is 0 or 1 or 2;
            case NotificationType.Teams:
                return index == 0 || !string.Equals(segments[index - 1], "IncomingWebhook", StringComparison.OrdinalIgnoreCase);
            default:
                return !IsTokenLike(segments[index]);
        }
    }

    // A segment of at least 8 characters with a digit in it, or of at least 20, may be a token.
    private static bool IsTokenLike(string segment)
    {
        return segment.Length >= LongSegmentLength || (segment.Length >= TokenLikeLength && segment.Any(char.IsDigit));
    }

    // Query parameter names are kept, their values masked (a signature or key travels there).
    private static string MaskedQuery(string query)
    {
        if (string.IsNullOrEmpty(query) || query == "?")
        {
            return string.Empty;
        }

        var parameters = query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split('=', 2)[0] + "=" + RecipientSecrets.Mask);

        return "?" + string.Join('&', parameters);
    }
}
