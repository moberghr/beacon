using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.HostData;
using Beacon.Core.Http;
using Beacon.Core.Exceptions;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Validation;

namespace Beacon.Connector.Databricks;

public class DatabricksProvider(
    IHttpClientFactory httpClientFactory,
    IDataSourceConnectionResolver connectionResolver,
    SqlReadOnlyAstValidator readOnlyValidator,
    ILogger<DatabricksProvider> logger) : IDataSourceProvider, IRowCappedReadOnlyExecution
{
    private const int MaxResultRows = 10000;

    // The most result bytes one statement may return (the Statement Execution API's byte_limit): 16 MiB.
    private const long MaxResultBytes = 16L * 1024 * 1024;

    // How long a best-effort statement cancellation may take once the wait for it has ended.
    private static readonly TimeSpan CancelTimeout = TimeSpan.FromSeconds(10);

    // How long reading the submission's answer may take; the caller's token does not end it.
    private static readonly TimeSpan SubmitTimeout = TimeSpan.FromSeconds(30);

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
                ErrorMessage = ConnectionFailureDescriber.Describe(ex),
                TestDurationMs = stopwatch.Elapsed.TotalMilliseconds
            };
        }
    }

    public Task<ProviderQueryResult> ExecuteQueryAsync(DataSource dataSource, string query, Dictionary<string, object?> parameters, CancellationToken cancellationToken = default)
    {
        return ExecuteQueryCoreAsync(dataSource, query, parameters, rowLimit: null, cancellationToken);
    }

    // Sends the statement with row_limit = maxRows, so the warehouse returns no more than the caller reads; a result that
    // holds maxRows rows may have had more, and the caller judges it.
    async Task<ProviderQueryResult> IRowCappedReadOnlyExecution.ExecuteReadOnlyQueryAsync(
        DataSource dataSource,
        string query,
        Dictionary<string, object?> parameters,
        int maxRows,
        CancellationToken cancellationToken)
    {
        var validation = await ValidateQueryAsync(dataSource, query, cancellationToken);
        if (!validation.IsValid)
        {
            return Refused(validation);
        }

        return await ExecuteQueryCoreAsync(dataSource, query, parameters, Math.Max(1, maxRows), cancellationToken);
    }

    private async Task<ProviderQueryResult> ExecuteQueryCoreAsync(
        DataSource dataSource,
        string query,
        Dictionary<string, object?> parameters,
        int? rowLimit,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var config = ParseConfiguration(dataSource);
            var timeoutSeconds = DataSourceQueryTimeout.Clamp(config.QueryTimeoutSeconds);
            using var client = CreateHttpClient(config);

            // wait_timeout 0s answers at once with the statement id, so a caller's cancellation can always cancel the
            // statement on the warehouse; the poll loop below waits for it.
            var requestBody = new Dictionary<string, object?>
            {
                ["statement"] = query,
                ["warehouse_id"] = ExtractWarehouseId(config.HttpPath),
                ["catalog"] = config.Catalog,
                ["schema"] = config.Schema,
                ["wait_timeout"] = "0s",
                ["byte_limit"] = MaxResultBytes
            };

            if (rowLimit is { } limit)
            {
                requestBody["row_limit"] = limit;
            }

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

            // Once sent, the submission's answer (status and statement id) is read to the end under a deadline of its own,
            // not the caller's token: a statement the warehouse accepted is then always known, and cancelled below when the
            // caller has gone.
            cancellationToken.ThrowIfCancellationRequested();
            var result = await SubmitAsync(client, config, requestBody);

            if (result?.Status?.State == "FAILED")
            {
                throw new DataSourceServiceException($"Databricks query failed: {result.Status.Error?.Message}", result.Status.Error?.ErrorCode);
            }

            var statementId = result?.StatementId;
            if (IsRunning(result) && string.IsNullOrEmpty(statementId))
            {
                // A statement Beacon cannot poll it cannot cancel either.
                throw new DataSourceServiceException("Databricks accepted the statement without a statement id.", null);
            }

            // Poll while it runs: once at once, then with gentle backoff (500ms growing to 5s), under an overall timeout.
            // Every way out before a terminal state is seen (the timeout, the caller's cancellation, a failed or unreadable
            // poll) cancels the statement on the warehouse and keeps the original failure.
            var pollDelayMs = 0;
            try
            {
                while (IsRunning(result))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (stopwatch.Elapsed.TotalSeconds >= timeoutSeconds)
                    {
                        throw new BeaconException($"Databricks query timed out after {timeoutSeconds} seconds");
                    }

                    if (pollDelayMs > 0)
                    {
                        await Task.Delay(pollDelayMs, cancellationToken);
                    }

                    pollDelayMs = pollDelayMs == 0 ? 500 : Math.Min(pollDelayMs * 2, 5000);

                    using var pollResponse = await client.GetAsync(
                        $"https://{config.Host}/api/2.0/sql/statements/{statementId}",
                        cancellationToken);
                    pollResponse.EnsureSuccessStatusCode();
                    result = await pollResponse.Content.ReadFromJsonAsync<DatabricksStatementResponse>(cancellationToken: cancellationToken)
                        ?? throw new DataSourceServiceException("Databricks answered a poll without a statement.", null);
                }
            }
            catch (Exception)
            {
                await TryCancelStatementAsync(client, config, statementId);
                throw;
            }

            if (result?.Status?.State != "SUCCEEDED")
            {
                var errorDetail = result?.Status?.Error?.Message;
                var stateMessage = $"Databricks query ended with state: {result?.Status?.State}";
                throw new DataSourceServiceException(errorDetail == null ? stateMessage : $"{stateMessage} — {errorDetail}", result?.Status?.Error?.ErrorCode);
            }

            // The warehouse cut the result short. Only a cut at the row limit, with the full rowLimit rows returned, is
            // handed back (the caller asked for no more and judges it); any other cut was the byte limit, and a partial
            // result is never returned as a whole one.
            var cutAtRowLimit = rowLimit is { } requested && result.Manifest?.TotalRowCount is { } returned && returned >= requested;
            if (result.Manifest?.Truncated == true && !cutAtRowLimit)
            {
                throw new BeaconException($"The Databricks result is larger than the {MaxResultBytes / (1024 * 1024)} MB Beacon reads from one statement. Select fewer columns or rows.");
            }

            var rows = await ConvertResultToRowsAsync(client, config, result, rowLimit, cancellationToken);
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
                    ["RowLimit"] = rowLimit ?? MaxResultRows,
                    ["TotalRowCount"] = result.Manifest?.TotalRowCount
                }
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The type and status code only (§1.11): a Databricks error message can quote the query and row values.
            logger.LogError(
                "Query execution failed for Databricks data source {DataSourceId} with {Failure}",
                dataSource.Id,
                FailureCodes.Describe(ex));
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

    /// <summary>
    /// The Statement Execution API offers no read-only session, so the connector's own verdict binds before anything is sent:
    /// <see cref="ValidateQueryAsync"/> (the Databricks-dialect read-only parser and the configured-catalog rule) must pass,
    /// which leaves a single SELECT, or the statement is refused without reaching the warehouse.
    /// <see cref="SupportsDatabaseReadOnlyEnforcement"/> stays false: the token's Unity Catalog grants remain the
    /// workspace's own boundary.
    /// </summary>
    public async Task<ProviderQueryResult> ExecuteReadOnlyQueryAsync(DataSource dataSource, string query, Dictionary<string, object?> parameters, CancellationToken cancellationToken = default)
    {
        var validation = await ValidateQueryAsync(dataSource, query, cancellationToken);
        if (!validation.IsValid)
        {
            return Refused(validation);
        }

        return await ExecuteQueryAsync(dataSource, query, parameters, cancellationToken);
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

        // Read-only enforcement (§1.5): reject anything that is not a single SELECT, or that names another catalog than
        // the one the source is configured for.
        var readOnlyError = readOnlyValidator.Validate(query, "databricks")
            ?? SqlCrossDatabaseRule.Validate(query, "databricks", ConfiguredCatalogOf(dataSource));
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

    // The catalog the source is configured for; null (no catalog may be named) when none is set, undetermined (no catalog either) when its settings cannot be read.
    private string? ConfiguredCatalogOf(DataSource dataSource)
    {
        try
        {
            return ParseConfiguration(dataSource).Catalog;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Databricks settings of data source {DataSourceId} could not be read ({ExceptionType})", dataSource.Id, ex.GetType().Name);
            return DataSourceConfiguredDatabase.Undetermined;
        }
    }

    private DatabricksConfiguration ParseConfiguration(DataSource dataSource)
    {
        var decryptedJson = connectionResolver.GetConnectionData(dataSource);
        return JsonSerializer.Deserialize<DatabricksConfiguration>(decryptedJson)
            ?? throw new BeaconException("Failed to parse Databricks configuration");
    }

    private HttpClient CreateHttpClient(DatabricksConfiguration config)
    {
        var client = httpClientFactory.CreateClient(BeaconHttpClients.Databricks);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);
        client.Timeout = TimeSpan.FromSeconds(DataSourceQueryTimeout.Clamp(config.QueryTimeoutSeconds) + 30);
        return client;
    }

    private static ProviderQueryResult Refused(QueryValidationResult validation)
    {
        return new ProviderQueryResult
        {
            Rows = [],
            TotalRows = 0,
            Success = false,
            ErrorMessage = string.Join("; ", validation.Errors)
        };
    }

    private static string ExtractWarehouseId(string httpPath)
    {
        // httpPath format: /sql/1.0/warehouses/<warehouse_id>
        var parts = httpPath.TrimEnd('/').Split('/');
        return parts[^1];
    }

    private static async Task<DatabricksStatementResponse?> SubmitAsync(HttpClient client, DatabricksConfiguration config, Dictionary<string, object?> requestBody)
    {
        using var submit = new CancellationTokenSource(SubmitTimeout);
        using var response = await client.PostAsJsonAsync(
            $"https://{config.Host}/api/2.0/sql/statements",
            requestBody,
            submit.Token);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<DatabricksStatementResponse>(cancellationToken: submit.Token);
    }

    private static bool IsRunning(DatabricksStatementResponse? result) =>
        result?.Status?.State is "PENDING" or "RUNNING";

    // Best effort, on its own short deadline: the caller's token may already be cancelled.
    private async Task TryCancelStatementAsync(HttpClient client, DatabricksConfiguration config, string? statementId)
    {
        if (string.IsNullOrEmpty(statementId))
        {
            return;
        }

        try
        {
            using var cancel = new CancellationTokenSource(CancelTimeout);
            using var cancelResponse = await client.PostAsync(
                $"https://{config.Host}/api/2.0/sql/statements/{statementId}/cancel",
                null,
                cancel.Token);
            if (!cancelResponse.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Databricks refused to cancel statement {StatementId} with status {StatusCode}",
                    statementId,
                    (int)cancelResponse.StatusCode);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Failed to cancel Databricks statement {StatementId} with {Failure}",
                statementId,
                FailureCodes.Describe(ex));
        }
    }

    // Reads every chunk of the result. With a row limit the read stops at it (the warehouse sends no more; the caller judges
    // a result that reaches it); without one, a result over MaxResultRows fails, and so does a chunk that is missing, so a
    // partial result is never returned as a whole one.
    private static async Task<List<Dictionary<string, object?>>> ConvertResultToRowsAsync(
        HttpClient client,
        DatabricksConfiguration config,
        DatabricksStatementResponse result,
        int? rowLimit,
        CancellationToken cancellationToken)
    {
        var rows = new List<Dictionary<string, object?>>();
        if (result.Manifest?.Schema?.Columns == null || result.Result?.DataArray == null)
        {
            if (result.Manifest?.TotalRowCount is > 0)
            {
                throw new DataSourceServiceException("Databricks reported rows but returned none of them.", null);
            }

            return rows;
        }

        var readLimit = rowLimit ?? MaxResultRows;
        var columnNames = result.Manifest.Schema.Columns.Select(x => x.Name).ToList();
        var overflow = AppendChunkRows(rows, columnNames, result.Result.DataArray, readLimit);
        var nextChunkIndex = result.Result.NextChunkIndex;

        // Follow subsequent result chunks; large results arrive in multiple chunks.
        while (!overflow && nextChunkIndex != null)
        {
            if (rows.Count >= readLimit)
            {
                // The next chunk holds rows past the limit.
                overflow = true;
                break;
            }

            using var chunkResponse = await client.GetAsync(
                $"https://{config.Host}/api/2.0/sql/statements/{result.StatementId}/result/chunks/{nextChunkIndex}",
                cancellationToken);
            chunkResponse.EnsureSuccessStatusCode();

            var chunk = await chunkResponse.Content.ReadFromJsonAsync<DatabricksResult>(cancellationToken: cancellationToken);
            if (chunk?.DataArray == null)
            {
                throw new DataSourceServiceException("Databricks returned an empty chunk of the result.", null);
            }

            overflow = AppendChunkRows(rows, columnNames, chunk.DataArray, readLimit);
            nextChunkIndex = chunk.NextChunkIndex;
        }

        if (overflow && rowLimit == null)
        {
            throw new BeaconException($"The Databricks result has more than {MaxResultRows.ToString("N0", CultureInfo.InvariantCulture)} rows, the most Beacon reads from one statement without a row cap. Narrow the query.");
        }

        return rows;
    }

    // Adds the chunk's rows up to readLimit; true when a row did not fit.
    private static bool AppendChunkRows(List<Dictionary<string, object?>> rows, List<string> columnNames, List<List<string?>> dataArray, int readLimit)
    {
        foreach (var dataRow in dataArray)
        {
            if (rows.Count >= readLimit)
            {
                return true;
            }

            var row = new Dictionary<string, object?>();
            for (var i = 0; i < columnNames.Count && i < dataRow.Count; i++)
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
        [JsonPropertyName("error_code")]
        public string? ErrorCode { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    private class DatabricksManifest
    {
        [JsonPropertyName("schema")]
        public DatabricksSchema? Schema { get; set; }

        [JsonPropertyName("total_row_count")]
        public long? TotalRowCount { get; set; }

        [JsonPropertyName("truncated")]
        public bool? Truncated { get; set; }
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
