using System.Text.Json;

namespace Beacon.Connector.BigQuery;

/// <summary>
/// The only credential JSON a BigQuery data source accepts: a service account key (<c>"type": "service_account"</c>).
/// Every other credential configuration (external account, impersonated, authorized user) is refused before Google's
/// library reads it.
/// </summary>
internal static class BigQueryCredentialJson
{
    public const string ServiceAccountType = "service_account";

    public static bool IsServiceAccount(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() == ServiceAccountType;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
