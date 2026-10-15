using System.Text.Json;
using Beacon.Connector.Api.Models;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Providers;

namespace Beacon.Connector.Api;

/// <summary>
/// An API data source sends its credentials to <c>BaseUrl</c> and <c>OpenApiSpecUrl</c>, so both must be absolute
/// https URLs (the HTTP client then checks the server's certificate). Read the way the provider reads its configuration
/// (property names in any case).
/// </summary>
public sealed class ApiTransportInspector : IDataSourceTransportInspector
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public DataSourceType DataSourceType => DataSourceType.Api;

    public DatabaseEngineType? DatabaseEngineType => null;

    public DataSourceTransportVerdict Inspect(string connectionData)
    {
        var config = JsonSerializer.Deserialize<ApiConnectionConfig>(connectionData, JsonOptions);
        if (config == null)
        {
            return DataSourceTransportVerdict.NotReadable();
        }

        var baseUrl = Https(config.BaseUrl);
        if (baseUrl == null)
        {
            return DataSourceTransportVerdict.Fail("BaseUrl", "Set BaseUrl to an absolute https:// URL");
        }

        var target = new DataSourceTransportTarget(baseUrl.Host, baseUrl.Port, null);
        return Https(config.OpenApiSpecUrl) == null
            ? DataSourceTransportVerdict.Fail("OpenApiSpecUrl", "Set OpenApiSpecUrl to an absolute https:// URL", target)
            : DataSourceTransportVerdict.Pass(target);
    }

    private static Uri? Https(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;
    }
}
