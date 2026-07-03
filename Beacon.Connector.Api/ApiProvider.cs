using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Beacon.Connector.Api.Models;
using Beacon.Connector.Api.Services;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;

namespace Beacon.Connector.Api;

public class ApiProvider(
    IEncryptionService encryptionService,
    IHttpClientFactory httpClientFactory,
    OpenApiImportService openApiImportService,
    JsonResponseTabularizer tabularizer,
    ILogger<ApiProvider> logger) : IDataSourceProvider
{
    private const long MaxResponseBytes = 50 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public DataSourceType SupportedType => DataSourceType.Api;

    public string GetQueryLanguageName() => "HTTP";

    public async Task<ConnectionTestResult> TestConnectionAsync(
        DataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var config = ParseConfiguration(dataSource);
            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds);

            // Test by fetching the OpenAPI spec URL
            var request = new HttpRequestMessage(HttpMethod.Get, config.OpenApiSpecUrl);
            ApiHttpClientHelper.ApplyAuth(request, config.Auth);

            using var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            stopwatch.Stop();

            return new ConnectionTestResult
            {
                Success = true,
                TestDurationMs = stopwatch.Elapsed.TotalMilliseconds,
                ConnectionInfo = new Dictionary<string, object?>
                {
                    ["BaseUrl"] = config.BaseUrl,
                    ["SpecUrl"] = config.OpenApiSpecUrl,
                    ["StatusCode"] = (int)response.StatusCode
                }
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Connection test failed for API data source {DataSourceId}", dataSource.Id);
            stopwatch.Stop();

            return new ConnectionTestResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                TestDurationMs = stopwatch.Elapsed.TotalMilliseconds
            };
        }
    }

    public async Task<ProviderQueryResult> ExecuteQueryAsync(
        DataSource dataSource,
        string query,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (parameters is { Count: > 0 })
            {
                throw new NotSupportedException("The API connector does not support SQL parameters; encode values in the query definition's path/query parameters instead.");
            }

            var config = ParseConfiguration(dataSource);
            var queryDef = JsonSerializer.Deserialize<ApiQueryDefinition>(query, JsonOptions)
                ?? throw new BeaconException("Failed to parse API query definition");

            // Read-only enforcement (§1.5) at the connector execution level — fail closed here so no
            // caller can execute a mutating verb by skipping ValidateQueryAsync.
            if (!IsReadOnlyMethod(queryDef.Method))
            {
                throw new BeaconException(
                    $"HTTP method '{queryDef.Method}' is not permitted. Only read-only verbs (GET, HEAD, OPTIONS) are allowed.");
            }

            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds);
            // CreateClient() hands out a fresh HttpClient per call, so capping the buffer here
            // doesn't affect other consumers of the factory's shared handler.
            client.MaxResponseContentBufferSize = MaxResponseBytes;

            var request = ApiHttpClientHelper.CreateRequest(config, queryDef);

            logger.LogInformation("Executing API query: {Method} {Path}", queryDef.Method, queryDef.Path);

            using var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var rows = tabularizer.Tabularize(responseBody, queryDef.ResultMapping, out var truncated, out var totalAvailable);

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
                    ["StatusCode"] = (int)response.StatusCode,
                    ["Truncated"] = truncated,
                    ["TotalAvailable"] = totalAvailable
                }
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Query execution failed for API data source {DataSourceId}", dataSource.Id);
            stopwatch.Stop();

            return new ProviderQueryResult
            {
                Rows = new List<Dictionary<string, object?>>(),
                TotalRows = 0,
                ExecutionTimeMs = stopwatch.Elapsed.TotalMilliseconds,
                Success = false,
                ErrorMessage = ex.Message
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

        return Task.FromResult(new QueryValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors,
            Warnings = warnings
        });
    }

    private static readonly string[] ReadOnlyMethods = ["GET", "HEAD", "OPTIONS"];

    private static bool IsReadOnlyMethod(string? method) =>
        !string.IsNullOrWhiteSpace(method) && ReadOnlyMethods.Contains(method.ToUpperInvariant());

    private ApiConnectionConfig ParseConfiguration(DataSource dataSource)
    {
        try
        {
            var decryptedJson = encryptionService.Decrypt(dataSource.EncryptedConnectionData);
            var config = JsonSerializer.Deserialize<ApiConnectionConfig>(decryptedJson, JsonOptions)
                ?? throw new BeaconException("Failed to parse API connection configuration");
            return config;
        }
        catch (JsonException ex)
        {
            throw new BeaconException($"Invalid API configuration JSON: {ex.Message}", ex);
        }
    }
}
