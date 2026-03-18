using System.Text.Json.Serialization;

namespace Semantico.Connector.Api.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiAuthType
{
    None,
    ApiKey,
    Bearer,
    Basic
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiKeyLocation
{
    Header,
    Query
}

public class ApiAuthConfig
{
    public ApiAuthType Type { get; set; } = ApiAuthType.None;

    // Bearer
    public string? Token { get; set; }

    // API Key
    public string? ApiKeyName { get; set; }
    public string? ApiKeyValue { get; set; }
    public ApiKeyLocation ApiKeyLocation { get; set; } = ApiKeyLocation.Header;

    // Basic
    public string? Username { get; set; }
    public string? Password { get; set; }
}
