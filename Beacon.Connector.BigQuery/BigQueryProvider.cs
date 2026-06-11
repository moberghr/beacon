using System.Diagnostics;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.BigQuery.V2;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;

namespace Beacon.Connector.BigQuery;

public class BigQueryProvider(
    IEncryptionService encryptionService,
    ILogger<BigQueryProvider> logger) : IDataSourceProvider
{
    public DataSourceType SupportedType => DataSourceType.BigQuery;
    public string GetQueryLanguageName() => "BigQuery SQL";

    public async Task<ConnectionTestResult> TestConnectionAsync(DataSource dataSource, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var config = ParseConfiguration(dataSource);
            using var client = CreateBigQueryClient(config);

            // Test by listing a single dataset (lightweight operation)
            var datasets = client.ListDatasetsAsync(config.ProjectId);
            await datasets.ReadPageAsync(1, cancellationToken);

            stopwatch.Stop();
            return new ConnectionTestResult
            {
                Success = true,
                TestDurationMs = stopwatch.Elapsed.TotalMilliseconds,
                ConnectionInfo = new Dictionary<string, object?>
                {
                    ["ProjectId"] = config.ProjectId,
                    ["DatasetId"] = config.DatasetId,
                    ["Location"] = config.Location
                }
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Connection test failed for BigQuery data source {DataSourceId}", dataSource.Id);
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
            using var client = CreateBigQueryClient(config);

            var queryOptions = new QueryOptions
            {
                UseQueryCache = true
            };

            if (!string.IsNullOrEmpty(config.DatasetId))
            {
                queryOptions.DefaultDataset = client.GetDatasetReference(config.ProjectId, config.DatasetId);
            }

            // §1.10 — bind caller-supplied values as named BigQuery parameters rather than dropping them.
            var queryParameters = BuildQueryParameters(parameters, queryOptions);

            var job = await client.CreateQueryJobAsync(query, queryParameters, queryOptions, cancellationToken);

            // Honor the configured query timeout (defaults to ~5 minutes when unset).
            var resultOptions = new GetQueryResultsOptions
            {
                Timeout = TimeSpan.FromSeconds(config.QueryTimeoutSeconds)
            };
            var result = await client.GetQueryResultsAsync(job.Reference, resultOptions, cancellationToken);

            var rows = new List<Dictionary<string, object?>>();
            var schema = result.Schema;

            await foreach (var row in result.GetRowsAsync().WithCancellation(cancellationToken))
            {
                var dict = new Dictionary<string, object?>();
                for (int i = 0; i < schema.Fields.Count; i++)
                {
                    dict[schema.Fields[i].Name] = row[schema.Fields[i].Name];
                }
                rows.Add(dict);
            }

            stopwatch.Stop();
            return new ProviderQueryResult
            {
                Rows = rows,
                TotalRows = rows.Count,
                ExecutionTimeMs = stopwatch.Elapsed.TotalMilliseconds,
                Success = true,
                Metadata = new Dictionary<string, object?>
                {
                    ["JobId"] = job.Reference.JobId,
                    ["BytesProcessed"] = job.Statistics?.TotalBytesProcessed
                }
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Query execution failed for BigQuery data source {DataSourceId}", dataSource.Id);
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
            Type = DataSourceType.BigQuery,
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

    private static IEnumerable<BigQueryParameter>? BuildQueryParameters(Dictionary<string, object?> parameters, QueryOptions queryOptions)
    {
        if (parameters == null || parameters.Count == 0)
        {
            return null;
        }

        queryOptions.ParameterMode = BigQueryParameterMode.Named;

        var result = new List<BigQueryParameter>();
        foreach (var parameter in parameters)
        {
            // Pass a null db type so BigQuery infers it from the value.
            result.Add(new BigQueryParameter(parameter.Key, (BigQueryDbType?)null, parameter.Value));
        }

        return result;
    }

    private BigQueryConfiguration ParseConfiguration(DataSource dataSource)
    {
        var decryptedJson = encryptionService.Decrypt(dataSource.EncryptedConnectionData);
        return JsonSerializer.Deserialize<BigQueryConfiguration>(decryptedJson)
            ?? throw new BeaconException("Failed to parse BigQuery configuration");
    }

    private static BigQueryClient CreateBigQueryClient(BigQueryConfiguration config)
    {
        if (!string.IsNullOrEmpty(config.ServiceAccountJson))
        {
            var credential = GoogleCredential.FromJson(config.ServiceAccountJson);
            return BigQueryClient.Create(config.ProjectId, credential);
        }

        // Use application default credentials
        return BigQueryClient.Create(config.ProjectId);
    }
}
