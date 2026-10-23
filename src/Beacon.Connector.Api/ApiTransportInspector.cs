using System.Globalization;
using System.Text.Json;
using Beacon.Connector.Api.Models;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Providers;

namespace Beacon.Connector.Api;

/// <summary>
/// An API data source sends its credentials to <c>BaseUrl</c> and <c>OpenApiSpecUrl</c>, so both must be absolute
/// https URLs (the HTTP client then checks the server's certificate). Read the way the provider reads its configuration
/// (property names in any case). The connection's definition is every property of the configuration under the
/// provider's own names (<c>BaseUrl</c> in full, path included; nested ones as <c>Auth.Type</c>), properties left at their
/// default left out, without the bearer token, the API key's value and the password; a property the provider does not
/// know is kept as written. The login is the user name, or the authentication type when there is none.
/// </summary>
public sealed class ApiTransportInspector : IDataSourceTransportInspector
{
    private const string AuthPrefix = "Auth.";
    private const string EndpointFilterPrefix = "EndpointFilter.";
    private const int DefaultTimeoutSeconds = 30;

    private static readonly string[] SecretKeys = ["Auth.Token", "Auth.ApiKeyValue", "Auth.Password"];

    private static readonly string[] TopLevelProperties =
    [
        nameof(ApiConnectionConfig.BaseUrl),
        nameof(ApiConnectionConfig.OpenApiSpecUrl),
        nameof(ApiConnectionConfig.Auth),
        nameof(ApiConnectionConfig.EndpointFilter),
        nameof(ApiConnectionConfig.TimeoutSeconds),
        nameof(ApiConnectionConfig.AllowPostQueries)
    ];

    private static readonly string[] AuthProperties =
    [
        nameof(ApiAuthConfig.Type),
        nameof(ApiAuthConfig.Token),
        nameof(ApiAuthConfig.ApiKeyName),
        nameof(ApiAuthConfig.ApiKeyValue),
        nameof(ApiAuthConfig.ApiKeyLocation),
        nameof(ApiAuthConfig.Username),
        nameof(ApiAuthConfig.Password)
    ];

    private static readonly string[] EndpointFilterProperties =
    [
        nameof(ApiEndpointFilter.IncludePathPatterns),
        nameof(ApiEndpointFilter.ExcludePathPatterns)
    ];

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

        var definition = Definition(config, connectionData);
        var baseUrl = Https(config.BaseUrl);
        if (baseUrl == null)
        {
            return DataSourceTransportVerdict.Fail("BaseUrl", "Set BaseUrl to an absolute https:// URL").WithDefinition(definition);
        }

        var target = new DataSourceTransportTarget(baseUrl.Host, baseUrl.Port, null);
        var verdict = Https(config.OpenApiSpecUrl) == null
            ? DataSourceTransportVerdict.Fail("OpenApiSpecUrl", "Set OpenApiSpecUrl to an absolute https:// URL", target)
            : DataSourceTransportVerdict.Pass(target);

        return verdict.WithDefinition(definition);
    }

    private static Uri? Https(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;
    }

    // The properties the provider reads, as it reads them, then every property it does not know, as written.
    private static DataSourceConnectionDefinition Definition(ApiConnectionConfig config, string connectionData)
    {
        var settings = new List<KeyValuePair<string, string?>>
        {
            new(nameof(ApiConnectionConfig.BaseUrl), config.BaseUrl),
            new(nameof(ApiConnectionConfig.OpenApiSpecUrl), config.OpenApiSpecUrl)
        };
        if (config.TimeoutSeconds != DefaultTimeoutSeconds)
        {
            settings.Add(new(nameof(ApiConnectionConfig.TimeoutSeconds), config.TimeoutSeconds.ToString(CultureInfo.InvariantCulture)));
        }

        if (config.AllowPostQueries)
        {
            settings.Add(new(nameof(ApiConnectionConfig.AllowPostQueries), "true"));
        }

        var auth = config.Auth;
        if (auth != null)
        {
            if (auth.Type != ApiAuthType.None)
            {
                settings.Add(new(AuthPrefix + nameof(ApiAuthConfig.Type), auth.Type.ToString()));
            }

            if (auth.ApiKeyLocation != ApiKeyLocation.Header)
            {
                settings.Add(new(AuthPrefix + nameof(ApiAuthConfig.ApiKeyLocation), auth.ApiKeyLocation.ToString()));
            }

            settings.Add(new(AuthPrefix + nameof(ApiAuthConfig.ApiKeyName), auth.ApiKeyName));
            settings.Add(new(AuthPrefix + nameof(ApiAuthConfig.Username), auth.Username));
        }

        var filter = config.EndpointFilter;
        if (filter != null)
        {
            settings.AddRange(filter.IncludePathPatterns
                .Select(x => new KeyValuePair<string, string?>(EndpointFilterPrefix + nameof(ApiEndpointFilter.IncludePathPatterns), x)));
            settings.AddRange(filter.ExcludePathPatterns
                .Select(x => new KeyValuePair<string, string?>(EndpointFilterPrefix + nameof(ApiEndpointFilter.ExcludePathPatterns), x)));
        }

        using var document = JsonDocument.Parse(connectionData);
        settings.AddRange(Unknown(document.RootElement, string.Empty, TopLevelProperties));
        settings.AddRange(Unknown(Nested(document.RootElement, nameof(ApiConnectionConfig.Auth)), AuthPrefix, AuthProperties));
        settings.AddRange(Unknown(Nested(document.RootElement, nameof(ApiConnectionConfig.EndpointFilter)), EndpointFilterPrefix, EndpointFilterProperties));

        var login = string.IsNullOrEmpty(auth?.Username) ? (auth?.Type ?? ApiAuthType.None).ToString() : auth.Username;

        return DataSourceConnectionDefinition.Create(settings, SecretKeys, login);
    }

    // The object a known property holds (named in any case), when it holds one.
    private static JsonElement? Nested(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return element.EnumerateObject()
            .Where(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
            .Where(x => x.Value.ValueKind == JsonValueKind.Object)
            .Select(x => (JsonElement?)x.Value)
            .LastOrDefault();
    }

    // The properties of an object the provider does not read, under their written names, with their written values.
    private static IEnumerable<KeyValuePair<string, string?>> Unknown(JsonElement? element, string prefix, string[] known)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value)
        {
            return [];
        }

        return value.EnumerateObject()
            .Where(x => !known.Contains(x.Name, StringComparer.OrdinalIgnoreCase))
            .Select(x => new KeyValuePair<string, string?>(prefix + x.Name, x.Value.ValueKind == JsonValueKind.String ? x.Value.GetString() : x.Value.GetRawText()))
            .ToList();
    }
}
