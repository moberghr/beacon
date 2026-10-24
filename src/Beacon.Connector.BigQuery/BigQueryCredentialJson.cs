using System.Text.Json;

namespace Beacon.Connector.BigQuery;

/// <summary>
/// The only credential JSON a BigQuery data source accepts: a service account key (<c>"type": "service_account"</c>)
/// that reaches only Google: its <c>token_uri</c> (where Google's library posts the signed sign-in) is Google's token
/// endpoint or absent, its <c>universe_domain</c> is <c>googleapis.com</c> or absent, and every other <c>*_uri</c> or
/// <c>*_url</c> is an https URL on <c>accounts.google.com</c> or below <c>googleapis.com</c>. Every other credential
/// configuration (external account, impersonated, authorized user) is refused before Google's library reads it.
/// </summary>
internal static class BigQueryCredentialJson
{
    public const string ServiceAccountType = "service_account";

    public const string GoogleTokenUri = "https://oauth2.googleapis.com/token";

    private const string GoogleUniverseDomain = "googleapis.com";

    // Google's token endpoint, and the one older keys name.
    private static readonly string[] GoogleTokenUris = [GoogleTokenUri, "https://accounts.google.com/o/oauth2/token"];

    /// <summary>Whether the JSON is a service account key that reaches only Google.</summary>
    public static bool IsServiceAccount(string json)
    {
        return Read(json).Kind == BigQueryCredentialKind.ServiceAccount;
    }

    /// <summary>
    /// What the JSON is, and the host of its <c>token_uri</c> when it names one (null when it does not: Google's library
    /// then uses <see cref="GoogleTokenUri"/>).
    /// </summary>
    public static (BigQueryCredentialKind Kind, string? TokenHost) Read(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || type.GetString() != ServiceAccountType)
            {
                return (BigQueryCredentialKind.Other, null);
            }

            // Property names in any case: Google's library reads them case-insensitively.
            string? tokenHost = null;
            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                if (property.Name.Equals("universe_domain", StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() != GoogleUniverseDomain)
                    {
                        return (BigQueryCredentialKind.ServiceAccountReachingOtherHosts, null);
                    }

                    continue;
                }

                if (property.Name.Equals("token_uri", StringComparison.OrdinalIgnoreCase))
                {
                    var tokenUri = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                    if (tokenUri == null || !GoogleTokenUris.Contains(tokenUri, StringComparer.Ordinal))
                    {
                        return (BigQueryCredentialKind.ServiceAccountReachingOtherHosts, null);
                    }

                    tokenHost = new Uri(tokenUri).Host;

                    continue;
                }

                if (IsUrlProperty(property.Name) && !IsGoogleUrl(property.Value))
                {
                    return (BigQueryCredentialKind.ServiceAccountReachingOtherHosts, null);
                }
            }

            return (BigQueryCredentialKind.ServiceAccount, tokenHost);
        }
        catch (JsonException)
        {
            return (BigQueryCredentialKind.Other, null);
        }
    }

    private static bool IsUrlProperty(string name)
    {
        return name.EndsWith("_uri", StringComparison.OrdinalIgnoreCase) || name.EndsWith("_url", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGoogleUrl(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String
            || !Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.IsDefaultPort)
        {
            return false;
        }

        var host = uri.Host.ToLowerInvariant();

        return host == "accounts.google.com" || host.EndsWith(".googleapis.com", StringComparison.Ordinal);
    }
}

/// <summary>What a BigQuery data source's credential JSON is.</summary>
internal enum BigQueryCredentialKind
{
    /// <summary>Not a service account key, or not JSON.</summary>
    Other,

    /// <summary>A service account key that reaches only Google.</summary>
    ServiceAccount,

    /// <summary>A service account key whose token endpoint, universe or another URL is not Google's.</summary>
    ServiceAccountReachingOtherHosts
}
