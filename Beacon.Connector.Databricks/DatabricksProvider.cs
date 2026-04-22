using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;

namespace Beacon.Connector.Databricks;

public class DatabricksProvider(
    IHttpClientFactory httpClientFactory,
    IEncryptionService encryptionService,
    ILogger<DatabricksProvider> logger) : IDataSourceProvider
{
    public DataSourceType SupportedType => DataSourceType.Databricks;
    public string GetQueryLanguageName() => "Databricks SQL";

    public async Task<ConnectionTestResult> TestConnectionAsync(DataSource dataSource, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var config = ParseConfiguration(dataSource);
            using var client = CreateHttpClient(config);

            var response = await client.GetAsync($"https://{config.Host}/api/2.0/sql/warehouses", cancellationToken);
            response.EnsureSuccessStatusCode();

            stopwatch.Stop();
            return new ConnectionTestResult
            {
                Success = true,
                TestDurationMs = stopwatch.Elapsed.TotalMilliseconds,
                ConnectionInfo = new Dictionary<string, object?>
                {
                    ["Host"] = config.Host,
                    ["HttpPath"] = config.HttpPath,
                    ["Catalog"] = config.Catalog,
                    ["Schema"] = config.Schema
                }
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Connection test failed for Databricks data source {DataSourceId}", dataSource.Id);
            stopwatch.Stop();
            return new ConnectionTestResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                TestDurationMs = stopwatch.Elapsed.TotalMilliseconds
            };
        }
    }

    public async Task<ProviderQueryResult> ExecuteQueryAsync(DataSource dataSource, string query, Dictionary<string, object?> parameters, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var config = ParseConfiguration(dataSource);
            using var client = CreateHttpClient(config);

            var requestBody = new
            {
                statement = query,
                warehouse_id = ExtractWarehouseId(config.HttpPath),
                catalog = config.Catalog,
                schema = config.Schema,
                wait_timeout = $"{config.QueryTimeoutSeconds}s"
            };

            var response = await client.PostAsJsonAsync(
                $"https://{config.Host}/api/2.0/sql/statements",
                requestBody,
                cancellationToken);

            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<DatabricksStatementResponse>(cancellationToken: cancellationToken);

            if (result?.Status?.State == "FAILED")
            {
                throw new BeaconException($"Databricks query failed: {result.Status.Error?.Message}");
            }

            // Poll if pending
            while (result?.Status?.State is "PENDING" or "RUNNING")
            {
                await Task.Delay(500, cancellationToken);
                var pollResponse = await client.GetAsync(
                    $"https://{config.Host}/api/2.0/sql/statements/{result.StatementId}",
                    cancellationToken);
                pollResponse.EnsureSuccessStatusCode();
                result = await pollResponse.Content.ReadFromJsonAsync<DatabricksStatementResponse>(cancellationToken: cancellationToken);
            }

            if (result?.Status?.State != "SUCCEEDED")
            {
                throw new BeaconException($"Databricks query ended with state: {result?.Status?.State}");
            }

            var rows = ConvertResultToRows(result);
            stopwatch.Stop();

            return new ProviderQueryResult
            {
                Rows = rows,
                TotalRows = rows.Count,
                ExecutionTimeMs = stopwatch.Elapsed.TotalMilliseconds,
                Success = true,
                Metadata = new Dictionary<string, object?>
                {
                    ["StatementId"] = result.StatementId
                }
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Query execution failed for Databricks data source {DataSourceId}", dataSource.Id);
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

    public Task<DataSourceMetadata> GetMetadataAsync(DataSource dataSource, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new DataSourceMetadata
        {
            Type = DataSourceType.Databricks,
            LastRefreshed = DateTime.UtcNow
        });
    }

    public Task<QueryValidationResult> ValidateQueryAsync(DataSource dataSource, string query, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new QueryValidationResult
        {
            IsValid = !string.IsNullOrWhiteSpace(query),
            Errors = string.IsNullOrWhiteSpace(query) ? new List<string> { "Query cannot be empty" } : new List<string>()
        });
    }

    private DatabricksConfiguration ParseConfiguration(DataSource dataSource)
    {
        var decryptedJson = encryptionService.Decrypt(dataSource.EncryptedConnectionData);
        return JsonSerializer.Deserialize<DatabricksConfiguration>(decryptedJson)
            ?? throw new BeaconException("Failed to parse Databricks configuration");
    }

    private HttpClient CreateHttpClient(DatabricksConfiguration config)
    {
        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);
        client.Timeout = TimeSpan.FromSeconds(config.QueryTimeoutSeconds + 30);
        return client;
    }

    private static string ExtractWarehouseId(string httpPath)
    {
        // httpPath format: /sql/1.0/warehouses/<warehouse_id>
        var parts = httpPath.TrimEnd('/').Split('/');
        return parts[^1];
    }

    private static List<Dictionary<string, object?>> ConvertResultToRows(DatabricksStatementResponse result)
    {
        var rows = new List<Dictionary<string, object?>>();
        if (result.Manifest?.Schema?.Columns == null || result.Result?.DataArray == null)
            return rows;

        var columnNames = result.Manifest.Schema.Columns.Select(c => c.Name).ToList();

        foreach (var dataRow in result.Result.DataArray)
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < columnNames.Count && i < dataRow.Count; i++)
            {
                row[columnNames[i]] = dataRow[i];
            }
            rows.Add(row);
        }

        return rows;
    }

    // Response DTOs
    private class DatabricksStatementResponse
    {
        [JsonPropertyName("statement_id")]
        public string? StatementId { get; set; }

        [JsonPropertyName("status")]
        public DatabricksStatus? Status { get; set; }

        [JsonPropertyName("manifest")]
        public DatabricksManifest? Manifest { get; set; }

        [JsonPropertyName("result")]
        public DatabricksResult? Result { get; set; }
    }

    private class DatabricksStatus
    {
        [JsonPropertyName("state")]
        public string? State { get; set; }

        [JsonPropertyName("error")]
        public DatabricksError? Error { get; set; }
    }

    private class DatabricksError
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    private class DatabricksManifest
    {
        [JsonPropertyName("schema")]
        public DatabricksSchema? Schema { get; set; }
    }

    private class DatabricksSchema
    {
        [JsonPropertyName("columns")]
        public List<DatabricksColumn>? Columns { get; set; }
    }

    private class DatabricksColumn
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";
    }

    private class DatabricksResult
    {
        [JsonPropertyName("data_array")]
        public List<List<string?>>? DataArray { get; set; }
    }
}
