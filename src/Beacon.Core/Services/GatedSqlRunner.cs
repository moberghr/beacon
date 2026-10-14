using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Entities;
using Beacon.Core.Exceptions;
using Beacon.Core.Models;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;

namespace Beacon.Core.Services;

/// <summary>One statement for <see cref="GatedSqlRunner"/>.</summary>
/// <param name="Sql">The statement as it runs: placeholders bound, their values in <paramref name="Parameters"/> (§1.10).</param>
/// <param name="MaxRows">The row cap the gate places in the SQL — a bound of the statement's own above it is lowered, or the
/// statement wrapped — which is also the most rows the provider reads. Every statement has one.</param>
/// <param name="Pii">The settings whose PII detection applies; null detects nothing in the SQL text (a host data source's
/// masked columns are reported and masked either way).</param>
/// <param name="Timeout">A shorter statement timeout than the runner's own (a subscription's); never a longer one.</param>
internal sealed record GatedSqlRequest(
    DataSource DataSource,
    string Sql,
    IReadOnlyDictionary<string, object?> Parameters,
    int MaxRows,
    McpSettingsData? Pii = null,
    TimeSpan? Timeout = null);

/// <summary>A statement that ran: its rows (host-masked by the provider), the gate's report and the dialect it was judged in.</summary>
internal sealed record GatedSqlRun(
    List<Dictionary<string, object?>> Rows,
    SqlGateReport Report,
    string? Dialect,
    double ExecutionTimeMs,
    bool TimedOut);

/// <summary>
/// The one way Beacon runs a statement on a data source for a feature of its own — query previews, subscriptions, the AI
/// actor's checks, migration reads, ad-hoc queries (§1.5). The statement passes the shared <see cref="ISqlExecutionGate"/>
/// with read-only enforcement always on — the regex guardrail, the read-only AST in the source's dialect, the names in the
/// source's own configured database, the host policy, the row cap placed in the SQL — and then runs through
/// <see cref="IDataSourceProvider.ExecuteReadOnlyQueryAsync"/> (a READ ONLY session and transaction where the engine has
/// one, see <see cref="IDataSourceProvider.SupportsDatabaseReadOnlyEnforcement"/>), with the caller's cancellation and a
/// bounded statement timeout. A gate refusal throws <see cref="InvalidOperationException"/> before anything connects; a
/// failure on the data source throws <see cref="QueryExecutionFailedException"/>; a timeout is reported, not thrown. The
/// failure's message is the database's own error, which can quote row values: while the data source's effective PII
/// detection is on (the request's settings, else the global settings tightened by the projects that link the source —
/// on, when none can be read), it is redacted (<see cref="DatabaseErrorRedactor.ForCaller"/>).
/// </summary>
internal sealed class GatedSqlRunner(
    ISqlExecutionGate gate,
    IDataSourceProviderFactory providerFactory,
    IConfiguredDatabaseResolver configuredDatabases,
    IOptions<QueryExecutionOptions> queryOptions,
    ISharedSourceSettings? sharedSourceSettings = null)
{
    /// <summary>How long one statement may run (<c>Beacon:Query:StatementTimeoutSeconds</c>). Settable for tests.</summary>
    internal TimeSpan StatementTimeout { get; init; } = TimeSpan.FromSeconds(queryOptions.Value.StatementTimeoutSeconds);

    /// <summary>
    /// The verdict <see cref="RunAsync"/> acts on, without running anything. The statement is judged first as if the
    /// source had no configured database (no database qualifier allowed); only a statement that names a database is judged
    /// again against the database its connection is configured for — so the connection data is read only when the verdict
    /// depends on it, never for a statement another stage refuses.
    /// </summary>
    public SqlGateReport Evaluate(GatedSqlRequest request)
    {
        var dataSource = request.DataSource;
        var gateRequest = new SqlGateRequest(
            request.Sql,
            DataSourceSqlDialect.Of(dataSource),
            EnforceReadOnly: true,
            DetectPii: request.Pii?.EnablePiiDetection ?? false,
            CustomPiiPatterns: request.Pii is { CustomPiiPatterns.Count: > 0 } pii ? pii.CustomPiiPatterns : null,
            Catalog: null,
            BlockOnSchemaFailure: false,
            LintContext: null,
            EnableSemanticLint: false,
            request.MaxRows)
        {
            HostManagedKey = dataSource.HostManagedKey,
            ConfiguredDatabase = DataSourceConfiguredDatabase.Undetermined
        };

        var report = gate.Evaluate(gateRequest);
        if (!report.Blocked || report.Verdicts.Schema.Code != SqlGateCodes.CrossDatabase)
        {
            return report;
        }

        return gate.Evaluate(gateRequest with { ConfiguredDatabase = configuredDatabases.Resolve(dataSource) });
    }

    public async Task<GatedSqlRun> RunAsync(GatedSqlRequest request, CancellationToken cancellationToken)
    {
        var report = Evaluate(request);
        if (report.Blocked)
        {
            throw new InvalidOperationException(report.BlockReason ?? "The SQL was rejected.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var dialect = DataSourceSqlDialect.Of(request.DataSource);
        var provider = providerFactory.GetProvider(request.DataSource.DataSourceType);
        var timeout = request.Timeout is { } requested && requested > TimeSpan.Zero && requested < StatementTimeout
            ? requested
            : StatementTimeout;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        ProviderQueryResult result;
        try
        {
            // The gate wrote the cap into the SQL; a provider that can also stop reading at the cap reads no further.
            result = provider is IRowCappedReadOnlyExecution capped
                ? await capped.ExecuteReadOnlyQueryAsync(
                    request.DataSource,
                    report.FinalSql,
                    new Dictionary<string, object?>(request.Parameters),
                    request.MaxRows,
                    timeoutCts.Token)
                : await provider.ExecuteReadOnlyQueryAsync(
                    request.DataSource,
                    report.FinalSql,
                    new Dictionary<string, object?>(request.Parameters),
                    timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new GatedSqlRun([], report, dialect, timeout.TotalMilliseconds, TimedOut: true);
        }

        // Providers report a cancellation as a failed result, so the caller's token is checked here.
        cancellationToken.ThrowIfCancellationRequested();

        if (!result.Success)
        {
            if (timeoutCts.IsCancellationRequested)
            {
                return new GatedSqlRun([], report, dialect, result.ExecutionTimeMs, TimedOut: true);
            }

            throw new QueryExecutionFailedException(await CallerErrorAsync(request, result.ErrorMessage ?? "Query execution failed.", cancellationToken));
        }

        return new GatedSqlRun(result.Rows ?? [], report, dialect, result.ExecutionTimeMs, TimedOut: false);
    }

    /// <summary>
    /// Checks a statement on its data source without running it, for SQL that may not run before it is reviewed. The
    /// statement passes the verdict <see cref="RunAsync"/> acts on (nothing reaches the provider when it is refused), then
    /// the provider's own non-executing validation (<see cref="IDataSourceProvider.ValidateQueryAsync"/>), never an
    /// execution: <c>EXPLAIN</c> without <c>ANALYZE</c> on PostgreSQL, MySQL and Snowflake and
    /// <c>sp_describe_first_result_set</c> on SQL Server and Azure Synapse, which plan the statement or describe its result
    /// and return none of its rows, and a local parse on the other providers. An engine without such a validation (the
    /// provider reports it skipped) is left to the gate. Null when the statement passes; otherwise why not, a database
    /// error redacted as <see cref="RunAsync"/> redacts one.
    /// </summary>
    /// <param name="sql">The statement as it would run, flattened and with no bind markers: a dry run binds no values.</param>
    public async Task<string?> ValidationFailureAsync(DataSource dataSource, string sql, CancellationToken cancellationToken)
    {
        var request = new GatedSqlRequest(dataSource, sql, new Dictionary<string, object?>(), MaxRows: 1);
        var report = Evaluate(request);
        if (report.Blocked)
        {
            return report.BlockReason ?? "The SQL was rejected.";
        }

        var validation = await providerFactory
            .GetProvider(dataSource.DataSourceType)
            .ValidateQueryAsync(dataSource, sql, cancellationToken);
        if (validation.IsValid || validation.Skipped)
        {
            return null;
        }

        var error = validation.Errors is { Count: > 0 } ? string.Join("; ", validation.Errors) : "Query validation failed.";

        return await CallerErrorAsync(request, error, cancellationToken);
    }

    // A host data source's error is already Beacon's own generic text.
    private async Task<string> CallerErrorAsync(GatedSqlRequest request, string error, CancellationToken cancellationToken)
    {
        if (request.DataSource.HostManagedKey != null)
        {
            return error;
        }

        var detectPii = request.Pii?.EnablePiiDetection
            ?? (sharedSourceSettings == null || (await sharedSourceSettings.GetDataSourceSettingsAsync(request.DataSource.Id, cancellationToken)).EnablePiiDetection);

        return DatabaseErrorRedactor.ForCaller(error, request.Sql, detectPii);
    }
}
