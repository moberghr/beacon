using System.Diagnostics;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.BigQuery.V2;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.HostData;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Validation;

namespace Beacon.Connector.BigQuery;

public class BigQueryProvider(
    IDataSourceConnectionResolver connectionResolver,
    SqlReadOnlyAstValidator readOnlyValidator,
    ILogger<BigQueryProvider> logger) : IDataSourceProvider
{
    private const int MaxResultRows = 10000;

    public DataSourceType SupportedType => DataSourceType.BigQuery;
    public string GetQueryLanguageName() => "BigQuery SQL";

    public async Task<ConnectionTestResult> TestConnectionAsync(DataSource dataSource, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var config = ParseConfiguration(dataSource);
            using var client = CreateClient(config);

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
                ErrorMessage = ConnectionFailureDescriber.Describe(ex),
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
            using var client = CreateClient(config);

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
            var truncated = false;

            await foreach (var row in result.GetRowsAsync().WithCancellation(cancellationToken))
            {
                if (rows.Count >= MaxResultRows)
                {
                    truncated = true;
                    break;
                }

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
                    ["BytesProcessed"] = job.Statistics?.TotalBytesProcessed,
                    ["Truncated"] = truncated,
                    ["RowLimit"] = MaxResultRows
                }
            };
        }
        catch (Exception ex)
        {
            // The type and status code only (§1.11): a BigQuery error message can quote the query and row values.
            logger.LogError(
                "Query execution failed for BigQuery data source {DataSourceId} with {Failure}",
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
    /// BigQuery has no read-only session, so the read-only path binds two checks before anything runs: the connector's own
    /// verdict (<see cref="ValidateQueryAsync"/>: the BigQuery-dialect read-only parser and the configured-project rule),
    /// and a dry run whose statement type BigQuery itself must report as <c>SELECT</c> — a script, DML, DDL or
    /// <c>EXPORT DATA</c> the parser read differently is refused there. Only then does the statement run.
    /// <see cref="SupportsDatabaseReadOnlyEnforcement"/> stays false: the service account's IAM grants remain the database's
    /// own boundary.
    /// </summary>
    public async Task<ProviderQueryResult> ExecuteReadOnlyQueryAsync(DataSource dataSource, string query, Dictionary<string, object?> parameters, CancellationToken cancellationToken = default)
    {
        var validation = await ValidateQueryAsync(dataSource, query, cancellationToken);
        if (!validation.IsValid)
        {
            return Refused(string.Join("; ", validation.Errors));
        }

        try
        {
            var config = ParseConfiguration(dataSource);
            using var client = CreateClient(config);

            var dryRunOptions = new QueryOptions
            {
                DryRun = true,
                UseQueryCache = false
            };

            if (!string.IsNullOrEmpty(config.DatasetId))
            {
                dryRunOptions.DefaultDataset = client.GetDatasetReference(config.ProjectId, config.DatasetId);
            }

            var dryRun = await client.CreateQueryJobAsync(query, BuildQueryParameters(parameters, dryRunOptions), dryRunOptions, cancellationToken);
            var statementType = dryRun.Statistics?.Query?.StatementType;
            if (!string.Equals(statementType, "SELECT", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("BigQuery read-only query refused for data source {DataSourceId}: the dry run reported statement type {StatementType}", dataSource.Id, statementType ?? "none");

                return Refused($"BigQuery reports this statement as {statementType ?? "an unknown type"}; only a SELECT runs on the read-only path.");
            }
        }
        catch (Exception ex)
        {
            // The dry run's message can quote the SQL or values (§1.11): the exception type only.
            logger.LogWarning("BigQuery dry run failed for data source {DataSourceId} with {ExceptionType}", dataSource.Id, ex.GetType().Name);

            return Refused($"The read-only dry run failed: {ex.Message}");
        }

        return await ExecuteQueryAsync(dataSource, query, parameters, cancellationToken);
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
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(query))
        {
            errors.Add("Query cannot be empty");
        }

        // Read-only enforcement (§1.5): reject anything that is not a single SELECT, or that names another project than
        // the one the source is configured for.
        var readOnlyError = readOnlyValidator.Validate(query, "bigquery")
            ?? SqlCrossDatabaseRule.Validate(query, "bigquery", ConfiguredProjectOf(dataSource));
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

    // The project the source is configured for; undetermined (no project may be named) when its settings cannot be read.
    private string? ConfiguredProjectOf(DataSource dataSource)
    {
        try
        {
            return ParseConfiguration(dataSource).ProjectId;
        }
        catch (Exception ex)
        {
            logger.LogWarning("BigQuery settings of data source {DataSourceId} could not be read ({ExceptionType})", dataSource.Id, ex.GetType().Name);
            return DataSourceConfiguredDatabase.Undetermined;
        }
    }

    private BigQueryConfiguration ParseConfiguration(DataSource dataSource)
    {
        var decryptedJson = connectionResolver.GetConnectionData(dataSource);
        return JsonSerializer.Deserialize<BigQueryConfiguration>(decryptedJson)
            ?? throw new BeaconException("Failed to parse BigQuery configuration");
    }

    private static ProviderQueryResult Refused(string reason)
    {
        return new ProviderQueryResult
        {
            Rows = [],
            TotalRows = 0,
            Success = false,
            ErrorMessage = reason
        };
    }

    /// <summary>The client every call uses. Virtual so a test can hand the provider a client that reaches no service.</summary>
    protected virtual BigQueryClient CreateClient(BigQueryConfiguration config)
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
