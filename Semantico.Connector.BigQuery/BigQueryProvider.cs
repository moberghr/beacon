using System.Diagnostics;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.BigQuery.V2;
using Microsoft.Extensions.Logging;
using Semantico.Core.Data.Entities;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models;
using Semantico.Core.Models.Providers;
using Semantico.Core.Services;
using Semantico.Core.Services.Providers;

namespace Semantico.Connector.BigQuery;

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
            var client = CreateBigQueryClient(config);

            // Test by listing datasets (lightweight operation)
            var datasets = client.ListDatasets(config.ProjectId);
            var count = 0;
            foreach (var _ in datasets) { count++; if (count >= 1) break; }

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
            var client = CreateBigQueryClient(config);

            var queryOptions = new QueryOptions
            {
                UseQueryCache = true
            };

            if (!string.IsNullOrEmpty(config.DatasetId))
            {
                queryOptions.DefaultDataset = client.GetDatasetReference(config.ProjectId, config.DatasetId);
            }

            var job = await client.CreateQueryJobAsync(query, parameters: null, queryOptions, cancellationToken);
            var result = await client.GetQueryResultsAsync(job.Reference, cancellationToken: cancellationToken);

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

    private BigQueryConfiguration ParseConfiguration(DataSource dataSource)
    {
        var decryptedJson = encryptionService.Decrypt(dataSource.EncryptedConnectionData);
        return JsonSerializer.Deserialize<BigQueryConfiguration>(decryptedJson)
            ?? throw new SemanticoException("Failed to parse BigQuery configuration");
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
