using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Beacon.Connector.Api.Models;
using Beacon.Connector.Api.Services;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.HostData;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;

namespace Beacon.Connector.Api;

public class ApiProvider(
    IDataSourceConnectionResolver connectionResolver,
    IHttpClientFactory httpClientFactory,
    OpenApiImportService openApiImportService,
    JsonResponseTabularizer tabularizer,
    ILogger<ApiProvider> logger) : IDataSourceProvider
{
    // The most one API response may hold: a query's rows are read whole into memory.
    private const long MaxResponseBytes = 10 * 1024 * 1024;

    // The bounds of the stored TimeoutSeconds, and the wait used when it is zero or less.
    private const int MinTimeoutSeconds = 1;
    private const int MaxTimeoutSeconds = 120;
    private const int DefaultTimeoutSeconds = 30;

    // The data sources whose replaced timeout was logged, so it is logged once per process.
    private static readonly ConcurrentDictionary<int, bool> ReplacedTimeoutLogged = new();

    // The data sources warned about for sending their API key in the query string, so it is logged once per process.
    private static readonly ConcurrentDictionary<int, bool> QueryKeyWarned = new();

    /// <summary>What a new or changed configuration that sends the API key in the query string is told.</summary>
    public const string QueryKeyRefusedMessage =
        "Send the API key in a header (Auth.ApiKeyLocation Header): a key in the query string travels in every request URL, which servers and proxies log, and an API that echoes its URL returns it.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public DataSourceType SupportedType => DataSourceType.Api;

    public string GetQueryLanguageName() => "HTTP";

    public Task<ConnectionTestResult> TestConnectionAsync(
        DataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        return ConnectorProviderSupport.TestConnectionAsync(logger, dataSource, "API", async () =>
        {
            var config = ParseConfiguration(dataSource);
            var client = httpClientFactory.CreateClient(ApiConnectorHttpClient.Name);
            client.Timeout = TimeoutOf(dataSource, config);

            // Test by fetching the OpenAPI spec URL; only its status is needed, so its body is never read. The data
            // source's credentials go only to the base URL's origin: a description served elsewhere is fetched without them.
            using var request = new HttpRequestMessage(HttpMethod.Get, config.OpenApiSpecUrl);
            if (ApiHttpClientHelper.SameOrigin(config.OpenApiSpecUrl, config.BaseUrl))
            {
                ApiHttpClientHelper.ApplyAuth(request, config.Auth);
            }

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            ApiHttpClientHelper.EnsureNotRedirected(response);
            response.EnsureSuccessStatusCode();

            return new Dictionary<string, object?>
            {
                ["BaseUrl"] = config.BaseUrl,
                ["SpecUrl"] = config.OpenApiSpecUrl,
                ["StatusCode"] = (int)response.StatusCode
            };
        });
    }

    public async Task<ProviderQueryResult> ExecuteQueryAsync(
        DataSource dataSource,
        string query,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var scrubber = ApiSecretScrubber.For(null);

        try
        {
            if (parameters is { Count: > 0 })
            {
                throw new NotSupportedException("The API connector does not support SQL parameters; encode values in the query definition's path/query parameters instead.");
            }

            var config = ParseConfiguration(dataSource);
            scrubber = ApiSecretScrubber.For(config.Auth);
            var queryDef = JsonSerializer.Deserialize<ApiQueryDefinition>(query, JsonOptions)
                ?? throw new BeaconException("Failed to parse API query definition");

            // Read-only enforcement (§1.5) at the connector execution level — fail closed here so no
            // caller can execute a mutating verb by skipping ValidateQueryAsync.
            if (!IsReadOnlyMethod(queryDef.Method))
            {
                throw new BeaconException(
                    $"HTTP method '{queryDef.Method}' is not permitted. Only read-only verbs (GET, HEAD, OPTIONS) are allowed.");
            }

            WarnOnQueryKey(dataSource, config);

            var timeout = TimeoutOf(dataSource, config);
            var client = httpClientFactory.CreateClient(ApiConnectorHttpClient.Name);
            client.Timeout = timeout;

            var request = ApiHttpClientHelper.CreateRequest(config, queryDef);

            // The data source only (§1.11): the path can carry identifiers or a credential in its query string.
            logger.LogInformation("Executing API query on data source {DataSourceId}", dataSource.Id);

            // The timeout covers the body too: HttpClient.Timeout ends once the headers are read.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            int statusCode;
            string responseBody;
            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
                ApiHttpClientHelper.EnsureNotRedirected(response);
                response.EnsureSuccessStatusCode();
                statusCode = (int)response.StatusCode;

                await using var body = await ApiHttpClientHelper.ReadCappedAsync(response, MaxResponseBytes, "API response", timeoutCts.Token);
                using var reader = new StreamReader(body, ApiHttpClientHelper.EncodingOf(response.Content), detectEncodingFromByteOrderMarks: true);
                responseBody = await reader.ReadToEndAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new BeaconException($"The API did not answer within {timeout.TotalSeconds:0} seconds.", ex);
            }

            // The data source's own credentials never reach the caller, also when the API echoes the request.
            var rows = tabularizer.Tabularize(scrubber.Scrub(responseBody), queryDef.ResultMapping, out var truncated, out var totalAvailable);
            scrubber.Scrub(rows);

            stopwatch.Stop();

            logger.LogInformation("API query completed with {RowCount} rows in {ElapsedMs}ms",
                rows.Count, stopwatch.Elapsed.TotalMilliseconds);

            return new ProviderQueryResult
            {
                Rows = rows,
                TotalRows = rows.Count,
                ExecutionTimeMs = stopwatch.Elapsed.TotalMilliseconds,
                Success = true,
                Metadata = new Dictionary<string, object?>
                {
                    ["Method"] = queryDef.Method,
                    ["Path"] = queryDef.Path,
                    ["StatusCode"] = statusCode,
                    ["Truncated"] = truncated,
                    ["TotalAvailable"] = totalAvailable
                }
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The type and status code only (§1.11): the message can quote the request URL or response content.
            logger.LogError(
                "Query execution failed for API data source {DataSourceId} with {Failure}",
                dataSource.Id,
                FailureCodes.Describe(ex));
            stopwatch.Stop();

            return new ProviderQueryResult
            {
                Rows = new List<Dictionary<string, object?>>(),
                TotalRows = 0,
                ExecutionTimeMs = stopwatch.Elapsed.TotalMilliseconds,
                Success = false,
                ErrorMessage = scrubber.Scrub(ex.Message)
            };
        }
    }

    public async Task<DataSourceMetadata> GetMetadataAsync(
        DataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var config = ParseConfiguration(dataSource);
            var endpoints = await openApiImportService.ImportAsync(
                config.OpenApiSpecUrl,
                config.EndpointFilter,
                config,
                cancellationToken);

            return new DataSourceMetadata
            {
                Type = DataSourceType.Api,
                Endpoints = endpoints,
                LastRefreshed = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Metadata retrieval failed for API data source {DataSourceId}", dataSource.Id);
            throw;
        }
    }

    public Task<QueryValidationResult> ValidateQueryAsync(
        DataSource dataSource,
        string query,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        try
        {
            var queryDef = JsonSerializer.Deserialize<ApiQueryDefinition>(query, JsonOptions);
            if (queryDef == null)
            {
                errors.Add("Failed to parse API query definition JSON");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(queryDef.Method))
                    errors.Add("HTTP method is required");

                if (string.IsNullOrWhiteSpace(queryDef.Path))
                    errors.Add("Endpoint path is required");

                if (string.IsNullOrWhiteSpace(queryDef.ResultMapping?.ArrayPath))
                    errors.Add("Result mapping arrayPath is required");

                // Read-only enforcement (§1.5): only safe/read HTTP verbs are executable. Mutating verbs
                // (POST/PUT/DELETE/PATCH) are rejected outright rather than whitelisted. The same check
                // is enforced fail-closed in ExecuteQueryAsync so validation cannot be skipped.
                if (!string.IsNullOrWhiteSpace(queryDef.Method) && !IsReadOnlyMethod(queryDef.Method))
                {
                    errors.Add($"HTTP method '{queryDef.Method}' is not permitted. Only read-only verbs (GET, HEAD, OPTIONS) are allowed.");
                }
            }
        }
        catch (JsonException ex)
        {
            errors.Add($"Invalid JSON: {ex.Message}");
        }

        return ConnectorProviderSupport.ValidationVerdict(errors, warnings);
    }

    /// <summary>
    /// Refuses a new or changed configuration that sends the API key in the query string (<see cref="QueryKeyRefusedMessage"/>).
    /// A stored one keeps working, with one Warning per data source per process.
    /// </summary>
    public void EnsureNewConnectionDataAllowed(string connectionData)
    {
        ApiConnectionConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<ApiConnectionConfig>(connectionData, JsonOptions);
        }
        catch (JsonException)
        {
            // Not readable: the transport check refuses it, and a query on it fails on the same read.
            return;
        }

        if (Uri.TryCreate(config?.BaseUrl, UriKind.Absolute, out var baseUri) && !string.IsNullOrEmpty(baseUri.UserInfo))
        {
            throw new InvalidOperationException("BaseUrl must not contain credentials (user:password@host); configure them in Auth.");
        }

        if (SendsKeyInQuery(config?.Auth))
        {
            throw new InvalidOperationException(QueryKeyRefusedMessage);
        }
    }

    private static readonly string[] ReadOnlyMethods = ["GET", "HEAD", "OPTIONS"];

    private static bool SendsKeyInQuery(ApiAuthConfig? auth) =>
        auth is { Type: ApiAuthType.ApiKey, ApiKeyLocation: ApiKeyLocation.Query };

    private void WarnOnQueryKey(DataSource dataSource, ApiConnectionConfig config)
    {
        if (SendsKeyInQuery(config.Auth) && QueryKeyWarned.TryAdd(dataSource.Id, true))
        {
            logger.LogWarning(
                "API data source {DataSourceId} sends its API key in the query string; it keeps working, but a changed configuration must send the key in a header",
                dataSource.Id);
        }
    }

    private static bool IsReadOnlyMethod(string? method) =>
        !string.IsNullOrWhiteSpace(method) && ReadOnlyMethods.Contains(method.ToUpperInvariant());

    // The stored TimeoutSeconds within its bounds; zero or less means the default, logged once per data source.
    private TimeSpan TimeoutOf(DataSource dataSource, ApiConnectionConfig config)
    {
        if (config.TimeoutSeconds > 0)
        {
            return TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSeconds, MinTimeoutSeconds, MaxTimeoutSeconds));
        }

        if (ReplacedTimeoutLogged.TryAdd(dataSource.Id, true))
        {
            logger.LogInformation(
                "API data source {DataSourceId} stores a timeout of zero or less; {TimeoutSeconds} seconds is used",
                dataSource.Id,
                DefaultTimeoutSeconds);
        }

        return TimeSpan.FromSeconds(DefaultTimeoutSeconds);
    }

    private ApiConnectionConfig ParseConfiguration(DataSource dataSource)
    {
        return ConnectorProviderSupport.ParseConfiguration<ApiConnectionConfig>(
            connectionResolver.GetConnectionData(dataSource),
            "Failed to parse API connection configuration",
            "Invalid API configuration JSON: ",
            JsonOptions);
    }
}
