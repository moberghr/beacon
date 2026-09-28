using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Telemetry;

namespace Beacon.Core.Handlers.McpAudit;

/// <summary>
/// Paged admin read of <c>McpAuditLog</c>. Rows are returned exactly as stored — retention redaction has
/// already been applied at write time (§9.5), so this handler never re-reads or reconstructs content. Reading
/// the audit is itself audited: one <c>Beacon.Audit</c> event (9103) per call, carrying the actor and the
/// filter shape only — presence of each optional filter, plus ids and dates, never a filter's free-text value
/// (the admin-supplied Tool filter included, §1.11).
/// </summary>
internal sealed class GetMcpAuditLogsHandler(IDbContextFactory<BeaconContext> contextFactory, ILoggerFactory loggerFactory)
    : IRequestHandler<GetMcpAuditLogsQuery, GetMcpAuditLogsResult>
{
    internal const int MaxRangeDays = 93;
    internal const int MinPageSize = 1;
    internal const int MaxPageSize = 500;
    internal static readonly EventId AuditReadEvent = new(9103, "McpAuditRead");

    private readonly ILogger _auditLogger = loggerFactory.CreateLogger(BeaconTelemetry.AuditLogCategory);

    public async Task<GetMcpAuditLogsResult> Handle(GetMcpAuditLogsQuery query, CancellationToken cancellationToken)
    {
        // Npgsql's timestamptz rejects Kind=Unspecified, which is what an offset-less 'from'/'to' binds to; the
        // stored CreatedTime is UTC, so normalise first and validate the normalised range.
        var request = query with { From = ToUtc(query.From), To = ToUtc(query.To) };
        Validate(request);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var filtered = Filtered(context.McpAuditLogs, request);

        var totalCount = await filtered.CountAsync(cancellationToken);

        var items = await Project(filtered)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);

        LogRead(request, items.Count);

        return new GetMcpAuditLogsResult(items, totalCount, request.Page, request.PageSize);
    }

    /// <summary>The filter + ordering the export runs. Shared with the translation test (§4.3-§4.5).</summary>
    internal static IQueryable<McpAuditLog> Filtered(IQueryable<McpAuditLog> source, GetMcpAuditLogsQuery request)
    {
        var query = source
            .Where(x => x.CreatedTime >= request.From)
            .Where(x => x.CreatedTime <= request.To);

        if (request.ProjectId.HasValue)
        {
            query = query.Where(x => x.ProjectId == request.ProjectId);
        }

        if (!string.IsNullOrEmpty(request.Tool))
        {
            query = query.Where(x => x.Tool == request.Tool);
        }

        if (!string.IsNullOrEmpty(request.CallerHash))
        {
            query = query.Where(x => x.CallerHash == request.CallerHash);
        }

        if (request.UserId.HasValue)
        {
            query = query.Where(x => x.UserId == request.UserId);
        }

        return query
            .OrderByDescending(x => x.CreatedTime)
            .ThenByDescending(x => x.Id);
    }

    /// <summary>No <c>.Include()</c> alongside this projection (§0.5) — every column the caller sees is selected here.</summary>
    internal static IQueryable<McpAuditLogItem> Project(IQueryable<McpAuditLog> source) =>
        source.Select(x =>
            new McpAuditLogItem(
                x.Id,
                x.CreatedTime,
                x.SessionId,
                x.UserId,
                x.Tool,
                x.Parameters,
                x.DataSourceId,
                x.ProjectId,
                x.ExecutionTimeMs,
                x.ResultRowCount,
                x.ErrorMessage,
                x.CallerKind,
                x.CallerHash,
                x.TraceId,
                x.SpanId,
                x.McpSessionId,
                x.UpstreamRequestId,
                x.ApiKeyId));

    /// <summary>Unspecified is taken as UTC (the storage kind); Local is converted.</summary>
    internal static DateTime ToUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

    private static void Validate(GetMcpAuditLogsQuery request)
    {
        if (request.To < request.From)
        {
            throw new InvalidOperationException("The audit query 'to' date must not be earlier than 'from'.");
        }

        if (request.To - request.From > TimeSpan.FromDays(MaxRangeDays))
        {
            throw new InvalidOperationException($"The audit query range must not exceed {MaxRangeDays} days.");
        }

        if (request.Page < 1)
        {
            throw new InvalidOperationException("The audit query page must be greater than or equal to 1.");
        }

        if (request.PageSize is < MinPageSize or > MaxPageSize)
        {
            throw new InvalidOperationException($"The audit query page size must be between {MinPageSize} and {MaxPageSize}.");
        }

        // Skip/Take bind to int; compute the offset in long first so a huge Page can't silently wrap.
        if ((long)(request.Page - 1) * request.PageSize > int.MaxValue)
        {
            throw new InvalidOperationException("The audit query page is too large for the given page size.");
        }
    }

    // §1.11 — the filter shape (which optional filters were present, plus ids/dates) and the returned count only.
    // Tool is admin free text (not validated against a catalogue), so only its presence is logged, never the value;
    // CallerHash is likewise logged only as presence.
    private void LogRead(GetMcpAuditLogsQuery request, int returnedCount)
    {
        _auditLogger.Log(
            LogLevel.Information,
            AuditReadEvent,
            "MCP audit read: RequestedByUserId={RequestedByUserId} From={From} To={To} ProjectId={ProjectId} " +
            "HasToolFilter={HasToolFilter} UserId={UserId} HasCallerHashFilter={HasCallerHashFilter} Page={Page} " +
            "PageSize={PageSize} ReturnedCount={ReturnedCount}",
            request.RequestedByUserId,
            request.From,
            request.To,
            request.ProjectId,
            !string.IsNullOrEmpty(request.Tool),
            request.UserId,
            !string.IsNullOrEmpty(request.CallerHash),
            request.Page,
            request.PageSize,
            returnedCount);
    }
}

public record GetMcpAuditLogsQuery(
    DateTime From,
    DateTime To,
    int? ProjectId,
    string? Tool,
    string? CallerHash,
    int? UserId,
    int Page,
    int PageSize,
    int? RequestedByUserId) : IRequest<GetMcpAuditLogsResult>;

public record GetMcpAuditLogsResult(IReadOnlyList<McpAuditLogItem> Items, int TotalCount, int Page, int PageSize);

public record McpAuditLogItem(
    int Id,
    DateTime CreatedTime,
    int? SessionId,
    int? UserId,
    string Tool,
    string? Parameters,
    int? DataSourceId,
    int? ProjectId,
    int ExecutionTimeMs,
    int? ResultRowCount,
    string? ErrorMessage,
    string? CallerKind,
    string? CallerHash,
    string? TraceId,
    string? SpanId,
    string? McpSessionId,
    string? UpstreamRequestId,
    int? ApiKeyId);
