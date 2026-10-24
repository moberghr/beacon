using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.BigQuery.V2;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    ILogger<BigQueryProvider> logger,
    IOptions<BigQueryOptions> options) : IDataSourceProvider, IRowCappedReadOnlyExecution
{
    private const int MaxResultRows = 10000;

    // BigQuery's reason for a job that would bill more than its MaximumBytesBilled.
    private const string BytesBilledLimitExceeded = "bytesBilledLimitExceeded";

    // How long a best-effort job cancellation may take once the wait for it has ended.
    private static readonly TimeSpan CancelTimeout = TimeSpan.FromSeconds(10);

    [Obsolete("Pass the BigQueryOptions as well; this constructor applies no deployment ceiling on bytes billed.")]
    public BigQueryProvider(
        IDataSourceConnectionResolver connectionResolver,
        SqlReadOnlyAstValidator readOnlyValidator,
        ILogger<BigQueryProvider> logger)
        : this(connectionResolver, readOnlyValidator, logger, Options.Create(new BigQueryOptions()))
    {
    }

    public DataSourceType SupportedType => DataSourceType.BigQuery;
    public string GetQueryLanguageName() => "BigQuery SQL";

    public Task<ConnectionTestResult> TestConnectionAsync(DataSource dataSource, CancellationToken cancellationToken = default)
    {
        return ConnectorProviderSupport.TestConnectionAsync(logger, dataSource, "BigQuery", async () =>
        {
            var config = ParseConfiguration(dataSource);
            using var client = CreateClient(config);

            // Test by listing a single dataset (lightweight operation)
            var datasets = client.ListDatasetsAsync(config.ProjectId);
            await datasets.ReadPageAsync(1, cancellationToken);

            return new Dictionary<string, object?>
            {
                ["ProjectId"] = config.ProjectId,
                ["DatasetId"] = config.DatasetId,
                ["Location"] = config.Location
            };
        });
    }

    public Task<ProviderQueryResult> ExecuteQueryAsync(DataSource dataSource, string query, Dictionary<string, object?> parameters, CancellationToken cancellationToken = default)
    {
        return ExecuteQueryCoreAsync(dataSource, query, parameters, rowLimit: null, cancellationToken);
    }

    // With a row limit the read stops at it (the caller judges a result that reaches it); without one, a result over
    // MaxResultRows fails rather than being returned cut short.
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
            using var client = CreateClient(config);

            var maximumBytesBilled = MaximumBytesBilledOf(config);
            var queryOptions = new QueryOptions
            {
                UseQueryCache = true,
                MaximumBytesBilled = maximumBytesBilled
            };

            if (!string.IsNullOrEmpty(config.DatasetId))
            {
                queryOptions.DefaultDataset = client.GetDatasetReference(config.ProjectId, config.DatasetId);
            }

            // §1.10 — bind caller-supplied values as named BigQuery parameters rather than dropping them.
            var queryParameters = BuildQueryParameters(parameters, queryOptions);

            var job = await client.CreateQueryJobAsync(query, queryParameters, queryOptions, cancellationToken);

            // The configured query timeout, at most DataSourceQueryTimeout.MaxSeconds. A wait that ends before the job does —
            // the timeout or the caller's cancellation — cancels the job, so it stops running (and billing) on BigQuery.
            var timeoutSeconds = DataSourceQueryTimeout.Clamp(config.QueryTimeoutSeconds);
            var resultOptions = new GetQueryResultsOptions
            {
                Timeout = TimeSpan.FromSeconds(timeoutSeconds)
            };
            BigQueryResults result;
            try
            {
                result = await client.GetQueryResultsAsync(job.Reference, resultOptions, cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                await TryCancelJobAsync(client, job, dataSource);
                if (ex is TimeoutException)
                {
                    throw new BeaconException($"BigQuery query timed out after {timeoutSeconds} seconds", ex);
                }

                throw;
            }

            var rows = new List<Dictionary<string, object?>>();
            var schema = result.Schema;
            var readLimit = rowLimit ?? MaxResultRows;

            await foreach (var row in result.GetRowsAsync().WithCancellation(cancellationToken))
            {
                if (rows.Count >= readLimit)
                {
                    if (rowLimit == null)
                    {
                        throw new BeaconException($"The BigQuery result has more than {MaxResultRows.ToString("N0", CultureInfo.InvariantCulture)} rows, the most Beacon reads from one statement without a row cap. Narrow the query.");
                    }

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
                    ["RowLimit"] = readLimit
                }
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
                ErrorMessage = IsBytesBilledLimitExceeded(ex) ? BytesBilledLimitMessage(dataSource) : ex.Message
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
    public Task<ProviderQueryResult> ExecuteReadOnlyQueryAsync(DataSource dataSource, string query, Dictionary<string, object?> parameters, CancellationToken cancellationToken = default)
    {
        return ExecuteReadOnlyQueryCoreAsync(dataSource, query, parameters, rowLimit: null, cancellationToken);
    }

    // The read-only path above, reading at most maxRows rows: the job's result is paged, so the read stops there.
    Task<ProviderQueryResult> IRowCappedReadOnlyExecution.ExecuteReadOnlyQueryAsync(
        DataSource dataSource,
        string query,
        Dictionary<string, object?> parameters,
        int maxRows,
        CancellationToken cancellationToken)
    {
        return ExecuteReadOnlyQueryCoreAsync(dataSource, query, parameters, Math.Max(1, maxRows), cancellationToken);
    }

    private async Task<ProviderQueryResult> ExecuteReadOnlyQueryCoreAsync(
        DataSource dataSource,
        string query,
        Dictionary<string, object?> parameters,
        int? rowLimit,
        CancellationToken cancellationToken)
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The dry run's message can quote the SQL or values (§1.11): the exception type only.
            logger.LogWarning("BigQuery dry run failed for data source {DataSourceId} with {ExceptionType}", dataSource.Id, ex.GetType().Name);

            return Refused($"The read-only dry run failed: {ex.Message}");
        }

        return await ExecuteQueryCoreAsync(dataSource, query, parameters, rowLimit, cancellationToken);
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

        return ConnectorProviderSupport.ValidationVerdict(errors);
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

    // The source's own limit (the default when it sets none), lowered to the deployment's ceiling when one is set.
    private long MaximumBytesBilledOf(BigQueryConfiguration config)
    {
        var limit = config.MaximumBytesBilled is > 0 and var own ? own : BigQueryConfiguration.DefaultMaximumBytesBilled;

        return options.Value.MaxBytesBilled is > 0 and var ceiling ? Math.Min(limit, ceiling) : limit;
    }

    private static bool IsBytesBilledLimitExceeded(Exception ex)
    {
        return ex is GoogleApiException { Error.Errors: { } errors }
            && errors.Any(x => x.Reason == BytesBilledLimitExceeded);
    }

    // Names the limit only: BigQuery's own message can quote the statement.
    private string BytesBilledLimitMessage(DataSource dataSource)
    {
        var limit = MaximumBytesBilledOf(ParseConfiguration(dataSource));

        return $"The query would bill more than {limit.ToString("N0", CultureInfo.InvariantCulture)} bytes, the data source's or the deployment's limit. "
            + $"Read less (fewer columns, a partition or date filter), or ask an administrator to raise the data source's MaximumBytesBilled or {BigQueryOptions.SectionName}:{nameof(BigQueryOptions.MaxBytesBilled)}.";
    }

    // Best effort, on its own short deadline: the caller's token may already be cancelled.
    private async Task TryCancelJobAsync(BigQueryClient client, BigQueryJob job, DataSource dataSource)
    {
        try
        {
            using var cancel = new CancellationTokenSource(CancelTimeout);
            await client.CancelJobAsync(job.Reference, null, cancel.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Failed to cancel BigQuery job {JobId} of data source {DataSourceId} with {Failure}",
                job.Reference.JobId,
                dataSource.Id,
                FailureCodes.Describe(ex));
        }
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
        return ConnectorProviderSupport.ParseConfiguration<BigQueryConfiguration>(
            connectionResolver.GetConnectionData(dataSource),
            "Failed to parse BigQuery configuration");
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
