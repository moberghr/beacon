using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Providers;

namespace Beacon.Core.Services;

/// <summary>What ran a statement, as the execution log records it (<see cref="ManualQueryExecutionLog.ExecutionContext"/>).</summary>
public static class SqlExecutionContexts
{
    /// <summary>A step of a query run: from the editor, an AI actor's check, a migration's read.</summary>
    public const string QueryStep = "QueryStep";

    /// <summary>A statement of a step preview: its page, its count, or the step as written.</summary>
    public const string QueryStepPreview = "QueryStepPreview";

    /// <summary>A step of a subscription run.</summary>
    public const string SubscriptionStep = "SubscriptionStep";

    /// <summary>A column value sample of the metadata scan.</summary>
    public const string ColumnValueSample = "ColumnValueSample";

    /// <summary>An enum discovery statement of project documentation generation.</summary>
    public const string DocumentationEnumDiscovery = "DocumentationEnumDiscovery";

    /// <summary>A data contract rule's statement.</summary>
    public const string DataQualityRule = "DataQualityRule";
}

/// <summary>A statement about to run on <paramref name="DataSourceId"/>: <paramref name="Sql"/> as it is sent, with its
/// placeholders (their values are never recorded).</summary>
internal sealed record SqlExecutionStart(int DataSourceId, string Sql, string ExecutionContext);

/// <summary>The execution log row of a statement that is running; <see cref="NotRecorded"/> when none could be written.</summary>
internal sealed record SqlExecutionEntry(int Id)
{
    public static SqlExecutionEntry NotRecorded { get; } = new(0);
}

/// <summary>How a statement ended. <paramref name="ErrorMessage"/> is Beacon's generic text or the database's error
/// redacted as with PII detection on, whatever the caller's setting: never text that may quote values.</summary>
internal sealed record SqlExecutionEnd(QueryExecutionLogStatus Status, int ResultCount, double ExecutionTimeMs, string? ErrorMessage = null);

/// <summary>
/// The execution log of the statements Beacon runs on a data source for its own features: a Started row is written before
/// the statement is sent, so a statement that fails, is cancelled or never returns is still on record, and the row is
/// closed when it ends. Under <see cref="ExecutionLogOptions.Required"/> a statement whose row cannot be written does not
/// run.
/// </summary>
internal interface ISqlExecutionAudit
{
    /// <summary>Writes the Started row. Throws <see cref="InvalidOperationException"/> when it cannot be written and the log
    /// is required; otherwise logs a Warning and returns <see cref="SqlExecutionEntry.NotRecorded"/>.</summary>
    Task<SqlExecutionEntry> StartAsync(SqlExecutionStart start, CancellationToken cancellationToken);

    /// <summary>Records how the statement ended, whatever the caller's cancellation. Never throws: a row whose end cannot be
    /// recorded stays Started, and a Warning is logged.</summary>
    Task CompleteAsync(SqlExecutionEntry entry, SqlExecutionEnd end);
}

internal static class SqlExecutionAuditExtensions
{
    private const string FailedOnTheDataSource = "The statement failed on the data source.";

    private const string TimedOutMessage = "The statement timed out.";

    /// <summary>
    /// Runs one provider read between <see cref="ISqlExecutionAudit.StartAsync"/> and
    /// <see cref="ISqlExecutionAudit.CompleteAsync"/>, for the paths that run the gate's FinalSql themselves.
    /// <paramref name="statementToken"/> is what the read runs under (the caller's cancellation linked with the path's own
    /// statement timeout); <paramref name="callerToken"/> is the caller's alone, so a read stopped by the timeout is recorded
    /// as <see cref="QueryExecutionLogStatus.TimedOut"/> and one the caller cancelled as
    /// <see cref="QueryExecutionLogStatus.Cancelled"/>. A failed result is recorded with a generic message: the database's
    /// own error can quote values.
    /// </summary>
    public static async Task<ProviderQueryResult> RunAsync(
        this ISqlExecutionAudit audit,
        SqlExecutionStart start,
        Func<CancellationToken, Task<ProviderQueryResult>> execute,
        CancellationToken statementToken,
        CancellationToken callerToken)
    {
        var entry = await audit.StartAsync(start, statementToken);
        var stopwatch = Stopwatch.StartNew();
        SqlExecutionEnd? end = null;
        try
        {
            var result = await execute(statementToken);
            end = result.Success
                ? new SqlExecutionEnd(QueryExecutionLogStatus.Succeeded, result.Rows?.Count ?? 0, result.ExecutionTimeMs)
                : Stopped(statementToken, callerToken, result.ExecutionTimeMs)
                    ?? new SqlExecutionEnd(QueryExecutionLogStatus.Failed, 0, result.ExecutionTimeMs, FailedOnTheDataSource);

            return result;
        }
        finally
        {
            end ??= Stopped(statementToken, callerToken, stopwatch.Elapsed.TotalMilliseconds)
                ?? new SqlExecutionEnd(QueryExecutionLogStatus.Failed, 0, stopwatch.Elapsed.TotalMilliseconds, FailedOnTheDataSource);
            await audit.CompleteAsync(entry, end);
        }
    }

    // A read stopped by its caller (cancelled) or by its own timeout (timed out); null when it was not stopped.
    private static SqlExecutionEnd? Stopped(CancellationToken statementToken, CancellationToken callerToken, double elapsedMs)
    {
        if (callerToken.IsCancellationRequested)
        {
            return new SqlExecutionEnd(QueryExecutionLogStatus.Cancelled, 0, elapsedMs);
        }

        return statementToken.IsCancellationRequested
            ? new SqlExecutionEnd(QueryExecutionLogStatus.TimedOut, 0, elapsedMs, TimedOutMessage)
            : null;
    }
}

internal sealed class SqlExecutionAudit(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconActorAccessor actorAccessor,
    IOptions<SecurityAuditOptions> options,
    ILogger<SqlExecutionAudit> logger,
    TimeProvider? timeProvider = null,
    SecurityAudit.SecurityAuditIntegrity? integrity = null,
    SecurityAudit.AuditTableSealSchema? sealSchema = null) : ISqlExecutionAudit
{
    // The column's length (BeaconContext.ConfigureManualQueryExecutionLogEntity).
    internal const int MaxErrorMessageLength = 4000;

    private const string RequiredSetting = $"{SecurityAuditOptions.SectionName}:{nameof(SecurityAuditOptions.ExecutionLog)}:{nameof(ExecutionLogOptions.Required)}";

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<SqlExecutionEntry> StartAsync(SqlExecutionStart start, CancellationToken cancellationToken)
    {
        try
        {
            var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            var row = new ManualQueryExecutionLog
            {
                CreatedTime = _time.GetUtcNow().UtcDateTime,
                TriggeredByUserId = actor.BeaconUserId,
                QueryText = start.Sql,
                ResultCount = 0,
                ExecutionTimeMs = 0,
                DataSourceId = start.DataSourceId,
                ExecutionContext = start.ExecutionContext,
                Status = QueryExecutionLogStatus.Started,
                Success = false
            };

            context.ManualQueryExecutionLogs.Add(row);
            await context.SaveChangesAsync(cancellationToken);

            return new SqlExecutionEntry(row.Id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (!options.Value.ExecutionLog.Required)
        {
            logger.LogWarning(
                ex,
                "The execution log row of a {ExecutionContext} statement on data source {DataSourceId} could not be written; the statement runs without it ({Setting} is off)",
                start.ExecutionContext,
                start.DataSourceId,
                RequiredSetting);

            return SqlExecutionEntry.NotRecorded;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "The execution log row of a {ExecutionContext} statement on data source {DataSourceId} could not be written; the statement does not run ({Setting} is on)",
                start.ExecutionContext,
                start.DataSourceId,
                RequiredSetting);

            throw new InvalidOperationException("The statement was not run: its execution log row could not be written.");
        }
    }

    public async Task CompleteAsync(SqlExecutionEntry entry, SqlExecutionEnd end)
    {
        if (entry.Id == 0)
        {
            return;
        }

        var error = end.ErrorMessage is { Length: > MaxErrorMessageLength } longError
            ? longError[..MaxErrorMessageLength]
            : end.ErrorMessage;
        var completedTime = _time.GetUtcNow().UtcDateTime;
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(CancellationToken.None);
            if (integrity == null
                || (sealSchema != null && await sealSchema.SealColumnsMissingAsync(context, typeof(ManualQueryExecutionLog), CancellationToken.None)))
            {
                // No seal to keep (none configured, or the seal migration not applied): the row is closed as it is.
                await context.ManualQueryExecutionLogs
                    .Where(x => x.Id == entry.Id)
                    .Where(x => x.Status == QueryExecutionLogStatus.Started)
                    .ExecuteUpdateAsync(
                        x => x
                            .SetProperty(y => y.Status, end.Status)
                            .SetProperty(y => y.Success, end.Status == QueryExecutionLogStatus.Succeeded)
                            .SetProperty(y => y.ResultCount, end.ResultCount)
                            .SetProperty(y => y.ExecutionTimeMs, end.ExecutionTimeMs)
                            .SetProperty(y => y.ErrorMessage, error)
                            .SetProperty(y => y.CompletedTime, completedTime),
                        CancellationToken.None);

                return;
            }

            await CloseSealedAsync(context, entry.Id, end, error, completedTime, integrity);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The end of logged statement {ExecutionLogId} could not be recorded; its row stays Started", entry.Id);
        }
    }

    // The row is read and its seal verified; the close is applied to it and, when the seal verified, it is sealed again over
    // the closed row; then one update writes the close (and the new seal) only while the row is still Started and carries
    // the seal it was read with. A row whose seal does not verify is closed without a new seal, so it keeps failing.
    private static async Task CloseSealedAsync(
        BeaconContext context,
        int id,
        SqlExecutionEnd end,
        string? error,
        DateTime completedTime,
        SecurityAudit.SecurityAuditIntegrity integrity)
    {
        var row = await context.ManualQueryExecutionLogs
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Where(x => x.Status == QueryExecutionLogStatus.Started)
            .FirstOrDefaultAsync(CancellationToken.None);
        if (row == null)
        {
            return;
        }

        var store = SecurityAudit.AuditTableStores.ExecutionLog;
        var intact = SecurityAudit.AuditTableSeal.Verify(row, store, integrity) == SecurityAudit.SecurityAuditVerdicts.Intact;
        var readHmac = row.Hmac;
        row.Status = end.Status;
        row.Success = end.Status == QueryExecutionLogStatus.Succeeded;
        row.ResultCount = end.ResultCount;
        row.ExecutionTimeMs = end.ExecutionTimeMs;
        row.ErrorMessage = error;
        row.CompletedTime = SecurityAudit.SecurityAuditIntegrity.StoredTime(completedTime);
        if (intact)
        {
            SecurityAudit.AuditTableSeal.Seal(row, store, integrity);
        }

        await context.ManualQueryExecutionLogs
            .Where(x => x.Id == id)
            .Where(x => x.Status == QueryExecutionLogStatus.Started)
            .Where(x => x.Hmac == readHmac)
            .ExecuteUpdateAsync(
                x =>
                {
                    x.SetProperty(y => y.Status, row.Status);
                    x.SetProperty(y => y.Success, row.Success);
                    x.SetProperty(y => y.ResultCount, row.ResultCount);
                    x.SetProperty(y => y.ExecutionTimeMs, row.ExecutionTimeMs);
                    x.SetProperty(y => y.ErrorMessage, row.ErrorMessage);
                    x.SetProperty(y => y.CompletedTime, row.CompletedTime);
                    if (intact)
                    {
                        x.SetProperty(y => y.Hmac, row.Hmac);
                        x.SetProperty(y => y.HmacKeyId, row.HmacKeyId);
                        x.SetProperty(y => y.SealVersion, row.SealVersion);
                    }
                },
                CancellationToken.None);
    }
}
