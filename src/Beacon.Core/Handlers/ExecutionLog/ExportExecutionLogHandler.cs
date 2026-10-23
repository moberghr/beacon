using System.Globalization;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.SecurityAudit;
using Beacon.Core.Helpers;
using Beacon.Core.Helpers.File;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Handlers.ExecutionLog;

/// <summary>
/// The execution log as CSV, newest first, over a window of at most <see cref="GetExecutionLogHandler.MaxRangeDays"/>
/// days and at most <see cref="MaxRows"/> rows (the file says when more matched), for an Auditor or a SecurityAdmin. The
/// rows are the ones the paged read lists for the same filters. The export is recorded before it returns
/// (<see cref="SecurityAuditActions.ExecutionLogExported"/>: the filters' shape, the row count, whether it was cut) and
/// fails when the event cannot be written.
/// </summary>
internal sealed class ExportExecutionLogHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IProjectAccessEvaluator projectAccess,
    SecurityAuditReads reads)
    : IRequestHandler<ExportExecutionLogQuery, ExecutionLogExport>
{
    internal const int MaxRows = 10_000;

    private static readonly string[] Columns =
    [
        "id", "createdTime", "completedTime", "status", "success", "executionContext", "dataSourceId", "triggeredByUserId",
        "resultCount", "executionTimeMs", "errorMessage", "queryText"
    ];

    public async Task<ExecutionLogExport> Handle(ExportExecutionLogQuery request, CancellationToken cancellationToken)
    {
        var (from, to) = SecurityAuditWindow.Validate(request.From, request.To, GetExecutionLogHandler.MaxRangeDays);

        var scope = await projectAccess.GetScopeAsync(cancellationToken);
        var readable = scope.For(ProjectAction.Read);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // One row past the cap tells whether the file holds every match.
        var rows = await context.ManualQueryExecutionLogs
            .Where(x => x.CreatedTime >= from)
            .Where(x => x.CreatedTime <= to)
            .WhereSourceAccessible(context, readable, x => x.DataSourceId)
            .WhereIf(request.DataSourceId.HasValue, x => x.DataSourceId == request.DataSourceId)
            .WhereIf(request.TriggeredByUserId.HasValue, x => x.TriggeredByUserId == request.TriggeredByUserId)
            .WhereIf(request.Status.HasValue, x => x.Status == request.Status)
            .WhereIf(!string.IsNullOrEmpty(request.ExecutionContext), x => x.ExecutionContext == request.ExecutionContext)
            .OrderByDescending(x => x.CreatedTime)
            .ThenByDescending(x => x.Id)
            .Select(x =>
                new
                {
                    x.Id,
                    x.CreatedTime,
                    x.CompletedTime,
                    x.Status,
                    x.Success,
                    x.ExecutionContext,
                    x.DataSourceId,
                    x.TriggeredByUserId,
                    x.ResultCount,
                    x.ExecutionTimeMs,
                    x.ErrorMessage,
                    x.QueryText
                })
            .Take(MaxRows + 1)
            .ToListAsync(cancellationToken);

        var truncated = rows.Count > MaxRows;
        var records = new List<IDictionary<string, object?>>();
        foreach (var row in rows.Take(MaxRows))
        {
            records.Add(new Dictionary<string, object?>
            {
                ["id"] = row.Id,
                ["createdTime"] = row.CreatedTime.ToString("O", CultureInfo.InvariantCulture),
                ["completedTime"] = row.CompletedTime?.ToString("O", CultureInfo.InvariantCulture),
                ["status"] = row.Status?.ToString(),
                ["success"] = row.Success,
                ["executionContext"] = row.ExecutionContext,
                ["dataSourceId"] = row.DataSourceId,
                ["triggeredByUserId"] = row.TriggeredByUserId,
                ["resultCount"] = row.ResultCount,
                ["executionTimeMs"] = row.ExecutionTimeMs,
                ["errorMessage"] = row.ErrorMessage,
                ["queryText"] = row.QueryText
            });
        }

        // Nothing is handed out without its record.
        await reads.RecordAsync(
            new SecurityAuditEntry(SecurityAuditActions.ExecutionLogExported)
                .On(SecurityAuditResources.ExecutionLog, null)
                .With(new SecurityAuditDetails()
                    .Add("from", from)
                    .Add("to", to)
                    .Add("dataSourceId", request.DataSourceId)
                    .Add("triggeredByUserId", request.TriggeredByUserId)
                    .AddCode("status", request.Status?.ToString())
                    .AddCode("executionContext", request.ExecutionContext)
                    .Add("rows", records.Count)
                    .Add("truncated", truncated)),
            cancellationToken);

        var content = await CsvBuilder.GetReport(records, Columns, cancellationToken);

        return new ExecutionLogExport(content, $"execution-log-{from:yyyyMMdd}-{to:yyyyMMdd}.csv", records.Count, truncated);
    }
}

/// <summary>
/// The execution log as CSV. <c>From</c> and <c>To</c> (UTC) are required and at most 93 days apart; at most 10,000 rows,
/// newest first.
/// </summary>
public record ExportExecutionLogQuery : IRequest<ExecutionLogExport>, IProjectFilteredRequest
{
    public DateTime? From { get; init; }

    public DateTime? To { get; init; }

    [ProjectScopeExempt("filter only: the export is scope-filtered")]
    public int? DataSourceId { get; init; }

    [ProjectScopeExempt("filter only: a Users.Id, the export is scope-filtered")]
    public int? TriggeredByUserId { get; init; }

    public QueryExecutionLogStatus? Status { get; init; }

    public string? ExecutionContext { get; init; }
}

/// <summary>The CSV file, how many rows it holds, and whether more rows matched than it holds.</summary>
public record ExecutionLogExport(byte[] Content, string FileName, int Rows, bool Truncated);
