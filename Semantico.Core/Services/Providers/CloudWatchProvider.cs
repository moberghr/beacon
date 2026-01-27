using System.Diagnostics;
using System.Text.Json;
using Amazon;
using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Logging;
using Semantico.Core.Data.Entities;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models;
using Semantico.Core.Models.Providers;
using Semantico.Core.Models.Providers.CloudWatch;
using DataSourceEntity = Semantico.Core.Data.Entities.DataSource;

namespace Semantico.Core.Services.Providers;

internal class CloudWatchProvider(
    IEncryptionService encryptionService,
    ILogger<CloudWatchProvider> logger) : IDataSourceProvider
{
    public DataSourceType SupportedType => DataSourceType.CloudWatch;

    public string GetQueryLanguageName() => "CloudWatch Logs Insights";

    public async Task<ConnectionTestResult> TestConnectionAsync(
        DataSourceEntity dataSource,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var config = ParseConfiguration(dataSource);
            using var client = CreateCloudWatchLogsClient(config);

            // Test connection by describing log groups
            var request = new DescribeLogGroupsRequest { Limit = 1 };
            var response = await client.DescribeLogGroupsAsync(request, cancellationToken);

            stopwatch.Stop();

            return new ConnectionTestResult
            {
                Success = true,
                TestDurationMs = stopwatch.Elapsed.TotalMilliseconds,
                ConnectionInfo = new Dictionary<string, object?>
                {
                    ["Region"] = config.Region,
                    ["LogGroupCount"] = config.LogGroups.Count,
                    ["ConfiguredLogGroups"] = string.Join(", ", config.LogGroups),
                    ["AvailableLogGroups"] = response.LogGroups.Count
                }
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Connection test failed for CloudWatch data source {DataSourceId}", dataSource.Id);

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
        DataSourceEntity dataSource,
        string query,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var config = ParseConfiguration(dataSource);
            using var client = CreateCloudWatchLogsClient(config);

            // Detect query type
            var queryType = DetectQueryType(query);

            if (queryType == CloudWatchQueryType.LogsInsights)
            {
                return await ExecuteLogsInsightsQueryAsync(client, config, query, parameters, stopwatch, cancellationToken);
            }
            else
            {
                throw new NotImplementedException("CloudWatch Metrics queries are not yet implemented");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Query execution failed for CloudWatch data source {DataSourceId}", dataSource.Id);

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
        DataSourceEntity dataSource,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var config = ParseConfiguration(dataSource);
            using var client = CreateCloudWatchLogsClient(config);

            var logFields = new List<LogFieldMetadata>();

            // If log groups are configured, discover common fields
            if (config.LogGroups.Any())
            {
                // Standard CloudWatch Logs fields that are always available
                logFields.AddRange(new[]
                {
                    new LogFieldMetadata
                    {
                        FieldName = "@timestamp",
                        DataType = "timestamp",
                        SampleCount = 1000,
                        SampleValues = new List<string> { "2024-01-26T10:30:00.000Z" }
                    },
                    new LogFieldMetadata
                    {
                        FieldName = "@message",
                        DataType = "string",
                        SampleCount = 1000,
                        SampleValues = new List<string> { "Log message text" }
                    },
                    new LogFieldMetadata
                    {
                        FieldName = "@logStream",
                        DataType = "string",
                        SampleCount = 1000,
                        SampleValues = config.LogGroups.Select(lg => $"stream-for-{lg}").ToList()
                    },
                    new LogFieldMetadata
                    {
                        FieldName = "@log",
                        DataType = "string",
                        SampleCount = 1000,
                        SampleValues = config.LogGroups
                    }
                });

                // TODO: Run sample queries to discover additional fields
                // This would require executing: fields @message | limit 100
                // Then parsing the result to find common JSON fields
            }

            return new DataSourceMetadata
            {
                Type = DataSourceType.CloudWatch,
                LogFields = logFields,
                LastRefreshed = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Metadata retrieval failed for CloudWatch data source {DataSourceId}", dataSource.Id);
            throw;
        }
    }

    public Task<QueryValidationResult> ValidateQueryAsync(
        DataSourceEntity dataSource,
        string query,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var config = ParseConfiguration(dataSource);

            // Basic validation
            var errors = new List<string>();
            var warnings = new List<string>();
            var suggestions = new List<string>();

            // Check if log groups are configured
            if (!config.LogGroups.Any())
            {
                errors.Add("No log groups configured for this CloudWatch data source");
            }

            // Detect query type
            var queryType = DetectQueryType(query);

            if (queryType == CloudWatchQueryType.LogsInsights)
            {
                // Basic Logs Insights syntax validation
                var hasFields = query.Contains("fields ", StringComparison.OrdinalIgnoreCase);
                if (!hasFields)
                {
                    suggestions.Add("Consider adding 'fields @timestamp, @message' to specify which fields to return");
                }

                var hasLimit = query.Contains("limit ", StringComparison.OrdinalIgnoreCase);
                if (!hasLimit)
                {
                    suggestions.Add("Consider adding '| limit 100' to control result set size");
                }
            }

            return Task.FromResult(new QueryValidationResult
            {
                IsValid = !errors.Any(),
                Errors = errors,
                Warnings = warnings,
                Suggestions = suggestions
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Query validation failed for CloudWatch data source {DataSourceId}", dataSource.Id);

            return Task.FromResult(new QueryValidationResult
            {
                IsValid = false,
                Errors = new List<string> { ex.Message }
            });
        }
    }

    private async Task<ProviderQueryResult> ExecuteLogsInsightsQueryAsync(
        AmazonCloudWatchLogsClient client,
        CloudWatchConfiguration config,
        string query,
        Dictionary<string, object?> parameters,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        if (!config.LogGroups.Any())
        {
            var errorMsg = "No log groups configured for CloudWatch Logs Insights query";
            logger.LogError(errorMsg);
            throw new SemanticoException(errorMsg);
        }

        // TODO: Replace parameters in query (e.g., {{start_time}}, {{end_time}})
        var processedQuery = query;

        // Start the query
        var startQueryRequest = new StartQueryRequest
        {
            LogGroupNames = config.LogGroups,
            QueryString = processedQuery,
            StartTime = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds(), // Last 1 hour by default
            EndTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        logger.LogInformation("Starting CloudWatch Logs Insights query. LogGroups: {LogGroups}, Query: {Query}",
            string.Join(", ", config.LogGroups), processedQuery);

        StartQueryResponse startQueryResponse;
        try
        {
            startQueryResponse = await client.StartQueryAsync(startQueryRequest, cancellationToken);
        }
        catch (Amazon.CloudWatchLogs.Model.InvalidParameterException ex)
        {
            logger.LogError(ex, "Invalid parameter in CloudWatch query");
            throw new SemanticoException($"Invalid CloudWatch query parameter: {ex.Message}", ex);
        }
        catch (Amazon.CloudWatchLogs.Model.ResourceNotFoundException ex)
        {
            logger.LogError(ex, "CloudWatch resource not found");
            throw new SemanticoException($"CloudWatch log group not found: {ex.Message}", ex);
        }
        catch (AmazonCloudWatchLogsException ex)
        {
            logger.LogError(ex, "AWS CloudWatch service error");
            throw new SemanticoException($"CloudWatch service error: {ex.Message}", ex);
        }

        var queryId = startQueryResponse.QueryId;

        logger.LogInformation("Started CloudWatch Logs Insights query {QueryId}", queryId);

        // Poll for results
        var timeoutSeconds = config.QueryTimeoutSeconds;
        var pollIntervalMs = 500;
        var elapsedSeconds = 0;

        GetQueryResultsResponse? queryResults = null;

        while (elapsedSeconds < timeoutSeconds)
        {
            var getResultsRequest = new GetQueryResultsRequest { QueryId = queryId };
            queryResults = await client.GetQueryResultsAsync(getResultsRequest, cancellationToken);

            if (queryResults.Status == QueryStatus.Complete)
            {
                break;
            }

            if (queryResults.Status == QueryStatus.Failed || queryResults.Status == QueryStatus.Cancelled)
            {
                throw new SemanticoException($"CloudWatch query {queryId} {queryResults.Status.Value}: {queryResults.Status}");
            }

            await Task.Delay(pollIntervalMs, cancellationToken);
            elapsedSeconds += pollIntervalMs / 1000;
        }

        if (queryResults?.Status != QueryStatus.Complete)
        {
            throw new SemanticoException($"CloudWatch query {queryId} timed out after {timeoutSeconds} seconds");
        }

        // Convert CloudWatch results to normalized format
        var rows = ConvertCloudWatchResultsToRows(queryResults.Results);

        stopwatch.Stop();

        logger.LogInformation("CloudWatch query {QueryId} completed with {RowCount} rows in {ElapsedMs}ms",
            queryId, rows.Count, stopwatch.Elapsed.TotalMilliseconds);

        return new ProviderQueryResult
        {
            Rows = rows,
            TotalRows = rows.Count,
            ExecutionTimeMs = stopwatch.Elapsed.TotalMilliseconds,
            Success = true,
            Metadata = new Dictionary<string, object?>
            {
                ["QueryId"] = queryId,
                ["Status"] = queryResults.Status.Value,
                ["BytesScanned"] = queryResults.Statistics?.BytesScanned ?? 0,
                ["RecordsMatched"] = queryResults.Statistics?.RecordsMatched ?? 0,
                ["RecordsScanned"] = queryResults.Statistics?.RecordsScanned ?? 0
            }
        };
    }

    private CloudWatchConfiguration ParseConfiguration(DataSourceEntity dataSource)
    {
        try
        {
            var decryptedJson = encryptionService.Decrypt(dataSource.EncryptedConnectionData);
            var config = JsonSerializer.Deserialize<CloudWatchConfiguration>(decryptedJson)
                ?? throw new SemanticoException("Failed to parse CloudWatch configuration");

            return config;
        }
        catch (JsonException ex)
        {
            throw new SemanticoException($"Invalid CloudWatch configuration JSON: {ex.Message}", ex);
        }
    }

    private AmazonCloudWatchLogsClient CreateCloudWatchLogsClient(CloudWatchConfiguration config)
    {
        var regionEndpoint = RegionEndpoint.GetBySystemName(config.Region);

        AWSCredentials? credentials = null;

        // Priority: Access Keys > Profile > Default (IAM role)
        if (!string.IsNullOrEmpty(config.AccessKeyId) && !string.IsNullOrEmpty(config.SecretAccessKey))
        {
            credentials = new BasicAWSCredentials(config.AccessKeyId, config.SecretAccessKey);

            if (!string.IsNullOrEmpty(config.SessionToken))
            {
                credentials = new SessionAWSCredentials(
                    config.AccessKeyId,
                    config.SecretAccessKey,
                    config.SessionToken);
            }
        }
        else if (!string.IsNullOrEmpty(config.ProfileName))
        {
            credentials = new Amazon.Runtime.CredentialManagement.CredentialProfileStoreChain().TryGetAWSCredentials(config.ProfileName, out var profileCredentials)
                ? profileCredentials
                : throw new SemanticoException($"AWS profile '{config.ProfileName}' not found");
        }

        return credentials != null
            ? new AmazonCloudWatchLogsClient(credentials, regionEndpoint)
            : new AmazonCloudWatchLogsClient(regionEndpoint); // Use default credentials (IAM role)
    }

    private static CloudWatchQueryType DetectQueryType(string query)
    {
        // Logs Insights queries typically contain these keywords
        var logsInsightsKeywords = new[] { "fields", "@timestamp", "@message", "filter", "stats", "sort" };

        if (logsInsightsKeywords.Any(keyword => query.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
        {
            return CloudWatchQueryType.LogsInsights;
        }

        // Default to Logs Insights
        return CloudWatchQueryType.LogsInsights;
    }

    private static List<Dictionary<string, object?>> ConvertCloudWatchResultsToRows(List<List<ResultField>> results)
    {
        var rows = new List<Dictionary<string, object?>>();

        foreach (var result in results)
        {
            var row = new Dictionary<string, object?>();

            foreach (var field in result)
            {
                // CloudWatch returns all values as strings
                // We could attempt to parse them, but for simplicity, keep as strings
                row[field.Field] = field.Value;
            }

            rows.Add(row);
        }

        return rows;
    }
}
