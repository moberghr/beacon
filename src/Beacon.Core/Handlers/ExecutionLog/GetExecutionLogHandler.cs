using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.SecurityAudit;
using Beacon.Core.Helpers;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Handlers.ExecutionLog;

/// <summary>
/// A paged read of the execution log, newest first, over a window of at most <see cref="MaxRangeDays"/> days, for an
/// Auditor or a SecurityAdmin. Only the statements on data sources the caller may read are listed (a row whose data source
/// is gone only where the caller reads every project). Every read is recorded before it returns
/// (<see cref="SecurityAuditActions.ExecutionLogRead"/>: the filters' shape, the page and how many rows it holds) and fails
/// when the event cannot be written. The SQL text is shown with its literals masked, and a Started row older than the
/// statement timeout plus a margin as Abandoned (<see cref="ExecutionLogView"/>).
/// </summary>
internal sealed class GetExecutionLogHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IProjectAccessEvaluator projectAccess,
    SecurityAuditReads reads,
    IOptions<QueryExecutionOptions> queryOptions,
    TimeProvider timeProvider)
    : IRequestHandler<GetExecutionLogQuery, PagedList<ExecutionLogEntry>>
{
    internal const int MaxRangeDays = 93;

    public async Task<PagedList<ExecutionLogEntry>> Handle(GetExecutionLogQuery request, CancellationToken cancellationToken)
    {
        var (from, to) = SecurityAuditWindow.Validate(request.From, request.To, MaxRangeDays);

        var scope = await projectAccess.GetScopeAsync(cancellationToken);
        var readable = scope.For(ProjectAction.Read);
        var abandonedBefore = ExecutionLogView.AbandonedBefore(timeProvider, queryOptions.Value);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var page = await context.ManualQueryExecutionLogs
            .Where(x => x.CreatedTime >= from)
            .Where(x => x.CreatedTime <= to)
            .WhereSourceAccessible(context, readable, x => x.DataSourceId)
            .WhereIf(request.DataSourceId.HasValue, x => x.DataSourceId == request.DataSourceId)
            .WhereIf(request.TriggeredByUserId.HasValue, x => x.TriggeredByUserId == request.TriggeredByUserId)
            .WhereShownStatus(request.Status, abandonedBefore)
            .WhereIf(!string.IsNullOrEmpty(request.ExecutionContext), x => x.ExecutionContext == request.ExecutionContext)
            .Select(x =>
                new ExecutionLogEntry
                {
                    Id = x.Id,
                    CreatedTime = x.CreatedTime,
                    CompletedTime = x.CompletedTime,
                    Status = x.Status == QueryExecutionLogStatus.Started && x.CreatedTime < abandonedBefore
                        ? QueryExecutionLogStatus.Abandoned
                        : x.Status,
                    Success = x.Success,
                    ExecutionContext = x.ExecutionContext,
                    DataSourceId = x.DataSourceId,
                    DataSourceName = x.DataSource != null ? x.DataSource.Name : null,
                    TriggeredByUserId = x.TriggeredByUserId,
                    ResultCount = x.ResultCount,
                    ExecutionTimeMs = x.ExecutionTimeMs,
                    QueryText = x.QueryText,
                    ErrorMessage = x.ErrorMessage,
                    DataSourceType = x.DataSource != null ? x.DataSource.DataSourceType : null,
                    DatabaseEngineType = x.DataSource != null ? x.DataSource.DatabaseEngineType : null
                })
            .ToPagedListAsync(request, cancellationToken, defaultSort: "-createdTime");

        // The stored text keeps the values the statement was written with; readers get it masked.
        for (var i = 0; i < page.Items.Count; i++)
        {
            var item = page.Items[i];
            page.Items[i] = item with { QueryText = ExecutionLogView.MaskedQueryText(item.QueryText, item.DataSourceType, item.DatabaseEngineType) };
        }

        // Nothing is handed out without its record.
        await reads.RecordAsync(
            new SecurityAuditEntry(SecurityAuditActions.ExecutionLogRead)
                .On(SecurityAuditResources.ExecutionLog, null)
                .With(new SecurityAuditDetails()
                    .Add("from", from)
                    .Add("to", to)
                    .Add("dataSourceId", request.DataSourceId)
                    .Add("triggeredByUserId", request.TriggeredByUserId)
                    .AddCode("status", request.Status?.ToString())
                    .AddCode("executionContext", request.ExecutionContext)
                    .Add("page", request.PageOrDefault)
                    .Add("pageSize", request.PageSizeOrDefault)
                    .Add("returned", page.Items.Count)
                    .AddCode("sort", request.Sort)),
            cancellationToken);

        return page;
    }
}

/// <summary>
/// One page of the execution log, newest first. <c>From</c> and <c>To</c> (UTC) are required and at most 93 days apart;
/// the other filters are optional. A Started row is a statement still running; <c>Abandoned</c> matches the Started rows
/// older than the statement timeout plus a margin, whose end was never recorded.
/// </summary>
public record GetExecutionLogQuery : ListRequest, IRequest<PagedList<ExecutionLogEntry>>, IProjectFilteredRequest
{
    public DateTime? From { get; init; }

    public DateTime? To { get; init; }

    [ProjectScopeExempt("filter only: the list is scope-filtered")]
    public int? DataSourceId { get; init; }

    [ProjectScopeExempt("filter only: a Users.Id, the list is scope-filtered")]
    public int? TriggeredByUserId { get; init; }

    public QueryExecutionLogStatus? Status { get; init; }

    public string? ExecutionContext { get; init; }
}

/// <summary>
/// A statement in the execution log. <c>Status</c> is null on rows recorded only after their statement ran (before Beacon
/// wrote a row first); <c>TriggeredByUserId</c> is the <c>Users.Id</c> the statement ran for. Parameter values are never
/// recorded, <c>QueryText</c> has its string literals and numbers replaced by <c>?</c> and its comments removed, and
/// <c>ErrorMessage</c> is the database's error redacted as with PII detection on, or Beacon's own generic text.
/// <c>Status</c> is <c>Abandoned</c> for a Started row whose end was never recorded.
/// </summary>
public record ExecutionLogEntry
{
    public int Id { get; init; }
    public DateTime CreatedTime { get; init; }
    public DateTime? CompletedTime { get; init; }
    public QueryExecutionLogStatus? Status { get; init; }
    public bool Success { get; init; }
    public string? ExecutionContext { get; init; }
    public int? DataSourceId { get; init; }
    public string? DataSourceName { get; init; }
    public int? TriggeredByUserId { get; init; }
    public int ResultCount { get; init; }
    public double ExecutionTimeMs { get; init; }
    public string QueryText { get; init; } = "";
    public string? ErrorMessage { get; init; }

    // The dialect the text is masked in; not part of the response.
    internal DataSourceType? DataSourceType { get; init; }
    internal DatabaseEngineType? DatabaseEngineType { get; init; }
}
