using System.Diagnostics;
using System.Text.Json;
using Amazon;
using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Models.Providers;
using Beacon.Core.Models.Providers.CloudWatch;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using DataSourceEntity = Beacon.Core.Data.Entities.DataSource;

namespace Beacon.Connector.CloudWatch;

public class CloudWatchProvider(
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
                return await ExecuteLogsInsightsQueryAsync(client, config, dataSource.Id, query, parameters, stopwatch, cancellationToken);
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
                // Standard fields are always present, but CloudWatch does not expose
                // sample values or counts without running a probe query. Report the
                // field names with empty samples rather than fabricating data.
                logFields.AddRange(new[]
                {
                    new LogFieldMetadata
                    {
                        FieldName = "@timestamp",
                        DataType = "timestamp",
                        SampleCount = 0,
                        SampleValues = new List<string>()
                    },
                    new LogFieldMetadata
                    {
                        FieldName = "@message",
                        DataType = "string",
                        SampleCount = 0,
                        SampleValues = new List<string>()
                    },
                    new LogFieldMetadata
                    {
                        FieldName = "@logStream",
                        DataType = "string",
                        SampleCount = 0,
                        SampleValues = new List<string>()
                    },
                    new LogFieldMetadata
                    {
                        FieldName = "@log",
                        DataType = "string",
                        SampleCount = 0,
                        SampleValues = new List<string>()
                    }
                });

                // CloudWatch Logs Insights doesn't expose schema metadata directly —
                // additional structured fields can only be discovered by running a
                // probe query (e.g. `fields @message | limit 100`) and parsing JSON
                // out of the messages. Done lazily on first user query, not here.
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
        int dataSourceId,
        string query,
        Dictionary<string, object?> parameters,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        if (!config.LogGroups.Any())
        {
            var errorMsg = "No log groups configured for CloudWatch Logs Insights query";
            logger.LogError(errorMsg);
            throw new BeaconException(errorMsg);
        }

        // Beacon's `{{start_time}}` / `{{end_time}}` placeholders are intentionally
        // not expanded here — CloudWatch Logs Insights takes start/end as separate
        // request fields (StartTime/EndTime below), not in the query string. Users
        // who template a time range in their Insights query get the literal tokens
        // forwarded, which they can spot at query time.
        var processedQuery = query;

        // Start the query
        var startQueryRequest = new StartQueryRequest
        {
            LogGroupNames = config.LogGroups,
            QueryString = processedQuery,
            StartTime = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds(), // Last 1 hour by default
            EndTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        logger.LogInformation(
            "Starting CloudWatch Logs Insights query for data source {DataSourceId}. LogGroupCount: {LogGroupCount}, QueryLength: {QueryLength}",
            dataSourceId, config.LogGroups.Count, processedQuery.Length);

        StartQueryResponse startQueryResponse;
        try
        {
            startQueryResponse = await client.StartQueryAsync(startQueryRequest, cancellationToken);
        }
        catch (Amazon.CloudWatchLogs.Model.InvalidParameterException ex)
        {
            logger.LogError(ex, "Invalid parameter in CloudWatch query");
            throw new BeaconException($"Invalid CloudWatch query parameter: {ex.Message}", ex);
        }
        catch (Amazon.CloudWatchLogs.Model.ResourceNotFoundException ex)
        {
            logger.LogError(ex, "CloudWatch resource not found");
            throw new BeaconException($"CloudWatch log group not found: {ex.Message}", ex);
        }
        catch (AmazonCloudWatchLogsException ex)
        {
            logger.LogError(ex, "AWS CloudWatch service error");
            throw new BeaconException($"CloudWatch service error: {ex.Message}", ex);
        }

        var queryId = startQueryResponse.QueryId;

        logger.LogInformation("Started CloudWatch Logs Insights query {QueryId}", queryId);

        // Poll for results. Track elapsed time in milliseconds so the sub-second
        // poll interval can't be truncated to zero (an integer-division bug that
        // previously made the timeout unreachable and the loop effectively infinite).
        var timeoutMs = config.QueryTimeoutSeconds * 1000;
        var pollIntervalMs = 500;
        var elapsedMs = 0;

        GetQueryResultsResponse? queryResults = null;

        while (elapsedMs < timeoutMs)
        {
            var getResultsRequest = new GetQueryResultsRequest { QueryId = queryId };
            queryResults = await client.GetQueryResultsAsync(getResultsRequest, cancellationToken);

            if (queryResults.Status == QueryStatus.Complete)
            {
                break;
            }

            if (queryResults.Status == QueryStatus.Failed || queryResults.Status == QueryStatus.Cancelled)
            {
                throw new BeaconException($"CloudWatch query {queryId} {queryResults.Status.Value}: {queryResults.Status}");
            }

            await Task.Delay(pollIntervalMs, cancellationToken);
            elapsedMs += pollIntervalMs;
        }

        if (queryResults?.Status != QueryStatus.Complete)
        {
            throw new BeaconException($"CloudWatch query {queryId} timed out after {config.QueryTimeoutSeconds} seconds");
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
                ?? throw new BeaconException("Failed to parse CloudWatch configuration");

            return config;
        }
        catch (JsonException ex)
        {
            throw new BeaconException($"Invalid CloudWatch configuration JSON: {ex.Message}", ex);
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
                : throw new BeaconException($"AWS profile '{config.ProfileName}' not found");
        }

        return credentials != null
            ? new AmazonCloudWatchLogsClient(credentials, regionEndpoint)
            : new AmazonCloudWatchLogsClient(regionEndpoint); // Use default credentials (IAM role)
    }

    private static CloudWatchQueryType DetectQueryType(string query)
    {
        // NOTE: today both branches resolve to LogsInsights (Metrics queries are not
        // yet implemented) and the time range is hardcoded to the last hour upstream.
        // Kept as-is intentionally; revisit when Metrics support / custom ranges land.
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
