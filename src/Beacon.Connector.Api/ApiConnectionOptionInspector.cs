using System.Text.Json;
using Beacon.Connector.Api.Models;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Providers;

namespace Beacon.Connector.Api;

/// <summary>
/// The hosts an API data source reaches: those of <c>BaseUrl</c> and <c>OpenApiSpecUrl</c> (null when either is not an
/// absolute URL). Read the way the provider reads its configuration (property names in any case).
/// </summary>
public sealed class ApiConnectionOptionInspector : IDataSourceConnectionOptionInspector
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public DataSourceType DataSourceType => DataSourceType.Api;

    public DatabaseEngineType? DatabaseEngineType => null;

    public DataSourceConnectionOptionVerdict Inspect(string connectionData)
    {
        var config = JsonSerializer.Deserialize<ApiConnectionConfig>(connectionData, JsonOptions);
        if (config == null)
        {
            return DataSourceConnectionOptionVerdict.NotReadable();
        }

        var baseUrl = Absolute(config.BaseUrl);
        var specUrl = Absolute(config.OpenApiSpecUrl);

        return DataSourceConnectionOptionVerdict.Accept(baseUrl == null || specUrl == null ? null : [baseUrl.Host, specUrl.Host]);
    }

    private static Uri? Absolute(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host) ? uri : null;
    }
}
