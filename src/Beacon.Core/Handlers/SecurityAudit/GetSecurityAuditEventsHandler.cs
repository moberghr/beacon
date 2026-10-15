using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Helpers;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Telemetry;

namespace Beacon.Core.Handlers.SecurityAudit;

/// <summary>
/// The Auditor's paged read of the security audit trail, newest first, over a window of at most
/// <see cref="MaxRangeDays"/> days, filtered by category, action, actor, resource and project. Every read is recorded
/// first (<see cref="SecurityAuditActions.AuditRead"/>, with the filters' shape and the page), and fails when the event
/// cannot be written; an identical read by the same actor within a minute is recorded once. The log stream gets the read
/// with the count returned (<see cref="SecurityAuditLogEvents.Read"/>). Free-text filter values never reach the log, and
/// reach the event only when they are codes.
/// </summary>
internal sealed class GetSecurityAuditEventsHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    SecurityAuditReads reads,
    ILoggerFactory loggerFactory)
    : IRequestHandler<GetSecurityAuditEventsQuery, PagedList<SecurityAuditEventItem>>
{
    internal const int MaxRangeDays = 93;

    private readonly ILogger _auditLogger = loggerFactory.CreateLogger(BeaconTelemetry.AuditLogCategory);

    public async Task<PagedList<SecurityAuditEventItem>> Handle(GetSecurityAuditEventsQuery request, CancellationToken cancellationToken)
    {
        var (from, to) = SecurityAuditWindow.Validate(request.From, request.To, MaxRangeDays);

        await reads.RecordAsync(
            new SecurityAuditEntry(SecurityAuditActions.AuditRead)
                .On(SecurityAuditResources.SecurityAudit, null)
                .InProject(request.ProjectId)
                .With(new SecurityAuditDetails()
                    .Add("from", from)
                    .Add("to", to)
                    .AddCode("category", request.Category)
                    .AddCode("action", request.Action)
                    .Add("actorUserId", request.ActorUserId)
                    .AddCode("resourceType", request.ResourceType)
                    .Add("hasResourceId", !string.IsNullOrEmpty(request.ResourceId))
                    .Add("page", request.PageOrDefault)
                    .Add("pageSize", request.PageSizeOrDefault)
                    .AddCode("sort", request.Sort)),
            cancellationToken);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var page = await context.SecurityAuditEvents
            .Where(x => x.CreatedTime >= from)
            .Where(x => x.CreatedTime <= to)
            .WhereIf(!string.IsNullOrEmpty(request.Category), x => x.Category == request.Category)
            .WhereIf(!string.IsNullOrEmpty(request.Action), x => x.Action == request.Action)
            .WhereIf(request.ActorUserId.HasValue, x => x.ActorUserId == request.ActorUserId)
            .WhereIf(!string.IsNullOrEmpty(request.ResourceType), x => x.ResourceType == request.ResourceType)
            .WhereIf(!string.IsNullOrEmpty(request.ResourceId), x => x.ResourceId == request.ResourceId)
            .WhereIf(request.ProjectId.HasValue, x => x.ProjectId == request.ProjectId)
            .Select(x =>
                new SecurityAuditEventItem
                {
                    Id = x.Id,
                    EventGuid = x.EventGuid,
                    CreatedTime = x.CreatedTime,
                    Category = x.Category,
                    Action = x.Action,
                    Outcome = x.Outcome,
                    ActorKind = x.ActorKind,
                    ActorUserId = x.ActorUserId,
                    ApiKeyId = x.ApiKeyId,
                    CallerHash = x.CallerHash,
                    ProjectId = x.ProjectId,
                    ResourceType = x.ResourceType,
                    ResourceId = x.ResourceId,
                    DetailsJson = x.DetailsJson,
                    TraceId = x.TraceId,
                    KeyId = x.KeyId
                })
            .ToPagedListAsync(request, cancellationToken, defaultSort: "-createdTime");

        _auditLogger.Log(
            LogLevel.Information,
            SecurityAuditLogEvents.Read,
            "Security audit read: From={From} To={To} HasCategoryFilter={HasCategoryFilter} HasActionFilter={HasActionFilter} " +
            "ActorUserId={ActorUserId} HasResourceFilter={HasResourceFilter} ProjectId={ProjectId} Page={Page} ReturnedCount={ReturnedCount}",
            from,
            to,
            !string.IsNullOrEmpty(request.Category),
            !string.IsNullOrEmpty(request.Action),
            request.ActorUserId,
            !string.IsNullOrEmpty(request.ResourceType) || !string.IsNullOrEmpty(request.ResourceId),
            request.ProjectId,
            request.PageOrDefault,
            page.Items.Count);

        return page;
    }
}

/// <summary>
/// One page of the security audit trail, newest first. <c>From</c> and <c>To</c> (UTC) are required and at most 93 days
/// apart; the other filters are optional. For an Auditor; the read is recorded.
/// </summary>
[ProjectScopeExempt("the security audit trail, gated by the Auditor permission; not filtered by membership", Policy = BeaconPermissions.AuditorPolicy)]
public record GetSecurityAuditEventsQuery : ListRequest, IRequest<PagedList<SecurityAuditEventItem>>
{
    public DateTime? From { get; init; }

    public DateTime? To { get; init; }

    public string? Category { get; init; }

    public string? Action { get; init; }

    public int? ActorUserId { get; init; }

    public string? ResourceType { get; init; }

    public string? ResourceId { get; init; }

    public int? ProjectId { get; init; }
}

public record SecurityAuditEventItem
{
    public int Id { get; init; }
    public Guid EventGuid { get; init; }
    public DateTime CreatedTime { get; init; }
    public string Category { get; init; } = "";
    public string Action { get; init; } = "";
    public string Outcome { get; init; } = "";
    public string ActorKind { get; init; } = "";
    public int? ActorUserId { get; init; }
    public int? ApiKeyId { get; init; }
    public string? CallerHash { get; init; }
    public int? ProjectId { get; init; }
    public string? ResourceType { get; init; }
    public string? ResourceId { get; init; }
    public string? DetailsJson { get; init; }
    public string? TraceId { get; init; }
    public string KeyId { get; init; } = "";
}
