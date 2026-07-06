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
using Beacon.Core.Services.Validation;

namespace Beacon.Connector.Databricks;

public class DatabricksProvider(
    IHttpClientFactory httpClientFactory,
    IEncryptionService encryptionService,
    SqlReadOnlyAstValidator readOnlyValidator,
    ILogger<DatabricksProvider> logger) : IDataSourceProvider
{
    private const int MaxResultRows = 10000;

    public DataSourceType SupportedType => DataSourceType.Databricks;
    public string GetQueryLanguageName() => "Databricks SQL";

    public async Task<ConnectionTestResult> TestConnectionAsync(DataSource dataSource, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var config = ParseConfiguration(dataSource);
            using var client = CreateHttpClient(config);

            using var response = await client.GetAsync($"https://{config.Host}/api/2.0/sql/warehouses", cancellationToken);
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

            // Databricks only accepts wait_timeout of 0s or 5s-50s; rely on the poll loop below for longer waits.
            var waitSeconds = Math.Clamp(config.QueryTimeoutSeconds, 5, 50);

            var requestBody = new Dictionary<string, object?>
            {
                ["statement"] = query,
                ["warehouse_id"] = ExtractWarehouseId(config.HttpPath),
                ["catalog"] = config.Catalog,
                ["schema"] = config.Schema,
                ["wait_timeout"] = $"{waitSeconds}s"
            };

            if (parameters is { Count: > 0 })
            {
                // §1.10 — bind caller-supplied values via the Statement Execution API's parameters array.
                requestBody["parameters"] = parameters
                    .Select(x =>
                        new
                        {
                            name = x.Key,
                            value = x.Value?.ToString()
                        })
                    .ToList();
            }

            using var response = await client.PostAsJsonAsync(
                $"https://{config.Host}/api/2.0/sql/statements",
                requestBody,
                cancellationToken);

            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<DatabricksStatementResponse>(cancellationToken: cancellationToken);

            if (result?.Status?.State == "FAILED")
            {
                throw new BeaconException($"Databricks query failed: {result.Status.Error?.Message}");
            }

            // Poll if pending, with an overall timeout and gentle backoff (500ms growing to 5s).
            var pollDelayMs = 500;
            while (result?.Status?.State is "PENDING" or "RUNNING")
            {
                if (stopwatch.Elapsed.TotalSeconds >= config.QueryTimeoutSeconds)
                {
                    await TryCancelStatementAsync(client, config, result.StatementId, cancellationToken);
                    throw new BeaconException($"Databricks query timed out after {config.QueryTimeoutSeconds} seconds");
                }

                await Task.Delay(pollDelayMs, cancellationToken);
                pollDelayMs = Math.Min(pollDelayMs * 2, 5000);

                using var pollResponse = await client.GetAsync(
                    $"https://{config.Host}/api/2.0/sql/statements/{result.StatementId}",
                    cancellationToken);
                pollResponse.EnsureSuccessStatusCode();
                result = await pollResponse.Content.ReadFromJsonAsync<DatabricksStatementResponse>(cancellationToken: cancellationToken);
            }

            if (result?.Status?.State != "SUCCEEDED")
            {
                var errorDetail = result?.Status?.Error?.Message;
                var stateMessage = $"Databricks query ended with state: {result?.Status?.State}";
                throw new BeaconException(errorDetail == null ? stateMessage : $"{stateMessage} — {errorDetail}");
            }

            var (rows, truncated) = await ConvertResultToRowsAsync(client, config, result, cancellationToken);
            stopwatch.Stop();

            return new ProviderQueryResult
            {
                Rows = rows,
                TotalRows = rows.Count,
                ExecutionTimeMs = stopwatch.Elapsed.TotalMilliseconds,
                Success = true,
                Metadata = new Dictionary<string, object?>
                {
                    ["StatementId"] = result.StatementId,
                    ["Truncated"] = truncated,
                    ["RowLimit"] = MaxResultRows,
                    ["TotalRowCount"] = result.Manifest?.TotalRowCount
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
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(query))
        {
            errors.Add("Query cannot be empty");
        }

        // Read-only enforcement (§1.5): reject anything that is not a single SELECT.
        var readOnlyError = readOnlyValidator.Validate(query, "databricks");
        if (readOnlyError != null)
        {
            errors.Add(readOnlyError);
        }

        return Task.FromResult(new QueryValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors
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

    private async Task TryCancelStatementAsync(HttpClient client, DatabricksConfiguration config, string? statementId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(statementId))
        {
            return;
        }

        try
        {
            using var cancelResponse = await client.PostAsync(
                $"https://{config.Host}/api/2.0/sql/statements/{statementId}/cancel",
                null,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to cancel Databricks statement {StatementId}", statementId);
        }
    }

    private static async Task<(List<Dictionary<string, object?>> Rows, bool Truncated)> ConvertResultToRowsAsync(
        HttpClient client,
        DatabricksConfiguration config,
        DatabricksStatementResponse result,
        CancellationToken cancellationToken)
    {
        var rows = new List<Dictionary<string, object?>>();
        if (result.Manifest?.Schema?.Columns == null || result.Result?.DataArray == null)
            return (rows, false);

        var columnNames = result.Manifest.Schema.Columns.Select(c => c.Name).ToList();
        var truncated = AppendChunkRows(rows, columnNames, result.Result.DataArray);
        var nextChunkIndex = result.Result.NextChunkIndex;

        // Follow subsequent result chunks; large results arrive in multiple chunks.
        while (!truncated && nextChunkIndex != null)
        {
            using var chunkResponse = await client.GetAsync(
                $"https://{config.Host}/api/2.0/sql/statements/{result.StatementId}/result/chunks/{nextChunkIndex}",
                cancellationToken);
            chunkResponse.EnsureSuccessStatusCode();

            var chunk = await chunkResponse.Content.ReadFromJsonAsync<DatabricksResult>(cancellationToken: cancellationToken);
            if (chunk?.DataArray == null)
            {
                break;
            }

            truncated = AppendChunkRows(rows, columnNames, chunk.DataArray);
            nextChunkIndex = chunk.NextChunkIndex;
        }

        return (rows, truncated);
    }

    private static bool AppendChunkRows(List<Dictionary<string, object?>> rows, List<string> columnNames, List<List<string?>> dataArray)
    {
        foreach (var dataRow in dataArray)
        {
            if (rows.Count >= MaxResultRows)
            {
                return true;
            }

            var row = new Dictionary<string, object?>();
            for (int i = 0; i < columnNames.Count && i < dataRow.Count; i++)
            {
                row[columnNames[i]] = dataRow[i];
            }
            rows.Add(row);
        }

        return false;
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

        [JsonPropertyName("total_row_count")]
        public long? TotalRowCount { get; set; }
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

        [JsonPropertyName("next_chunk_index")]
        public int? NextChunkIndex { get; set; }
    }
}
