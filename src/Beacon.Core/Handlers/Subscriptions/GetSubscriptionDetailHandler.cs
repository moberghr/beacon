using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models.Approvals;
using Beacon.Core.Services;
using MediatR;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.Subscriptions;

internal sealed class GetSubscriptionDetailHandler(ISubscriptionService subscriptionService, RecipientDeliveryApproval deliveryApproval)
    : IRequestHandler<GetSubscriptionDetailQuery, GetSubscriptionDetailResult>
{
    public async Task<GetSubscriptionDetailResult> Handle(GetSubscriptionDetailQuery request, CancellationToken cancellationToken)
    {
        var data = await subscriptionService.GetSubscriptionDetails(request.SubscriptionId, cancellationToken);

        var cronDescription = data.CronExpression?.GetCronDescription() ?? string.Empty;
        var cronNextAt = data.CronExpression?.GetCronNextAt();

        // The recipients its deliveries skip while where they deliver waits for approval, each with the pending request of
        // that change or else the last decision on it.
        var awaiting = await deliveryApproval.AwaitingAsync(
            data.Recipients
                .Select(x => x.RecipientId)
                .OfType<int>()
                .ToList(),
            cancellationToken);
        var recipients = data.Recipients
            .Select(x =>
            {
                var state = x.RecipientId is { } id ? awaiting.GetValueOrDefault(id) : null;

                return new SubscriptionDetailRecipient(
                    x.RecipientId ?? 0,
                    x.Name,
                    x.Description,
                    null,
                    x.NotificationType)
                {
                    AwaitingApproval = state != null,
                    PendingApprovalRequestId = state?.PendingRequestId,
                    LastApprovalDecision = state?.LastDecision
                };
            })
            .ToList();

        var parameters = data.Parameters
            .Select(x => new SubscriptionDetailParameter(x.QueryPlaceholder, x.Value))
            .ToList();

        SubscriptionDetailAnomalyConfig? anomaly = null;
        if (data.AnomalyConfig is not null)
        {
            anomaly = new SubscriptionDetailAnomalyConfig(
                data.AnomalyConfig.Enabled,
                data.AnomalyConfig.DetectionMethod,
                data.AnomalyConfig.Sensitivity,
                data.AnomalyConfig.LookbackDays,
                data.AnomalyConfig.AlertOnIncrease,
                data.AnomalyConfig.AlertOnDecrease,
                data.AnomalyConfig.MinimumDataPoints);
        }

        var detail = new SubscriptionDetail(
            data.SubscriptionId,
            data.QueryId,
            data.QueryName,
            data.Status,
            data.CronExpression ?? string.Empty,
            cronDescription,
            cronNextAt,
            data.AiActorId,
            data.AiActorName,
            data.MaxRows,
            data.MinimumRowCount,
            data.IncludeAttachment,
            data.ResultAttachmentType,
            data.ShowQuery,
            data.TimeoutSeconds,
            data.StoreResults,
            data.CreateTasks,
            data.NotificationTrigger,
            parameters,
            recipients,
            anomaly,
            data.OwnerUserId != null,
            data.OwnerDisplayName,
            data.OwnerNotAuthorized,
            data.NotApproved);

        return new GetSubscriptionDetailResult(detail);
    }
}

public record GetSubscriptionDetailQuery(int SubscriptionId) : IRequest<GetSubscriptionDetailResult>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Subscription(SubscriptionId), ProjectAction.Read)
    ];
}

public record GetSubscriptionDetailResult(SubscriptionDetail Detail);

/// <summary>
/// A subscription's detail. <c>HasOwner</c>: an owner is recorded (its scheduled runs run as the owner, checked against
/// the owner's access at every run); <c>OwnerDisplayName</c>: the owner's display name, or a neutral "User {id}" label,
/// never an external id or user name (null when no owner or no matching user); <c>OwnerNotAuthorized</c>: the last
/// scheduled run was skipped because the owner may no longer run the query; <c>NotApproved</c>: the last run was skipped
/// because, with the approval workflow on, the query's stored SQL is not its approved version.
/// </summary>
public record SubscriptionDetail(
    int Id,
    int QueryId,
    string QueryName,
    string Status,
    string CronExpression,
    string CronDescription,
    DateTime? CronNextAt,
    int? AiActorId,
    string? AiActorName,
    int? MaxRows,
    int? MinimumRowCount,
    bool IncludeAttachment,
    FileType? ResultAttachmentType,
    bool ShowQuery,
    int? TimeoutSeconds,
    bool StoreResults,
    bool CreateTasks,
    NotificationTrigger NotificationTrigger,
    List<SubscriptionDetailParameter> Parameters,
    List<SubscriptionDetailRecipient> Recipients,
    SubscriptionDetailAnomalyConfig? AnomalyConfig,
    bool HasOwner,
    string? OwnerDisplayName,
    bool OwnerNotAuthorized,
    bool NotApproved = false);

public record SubscriptionDetailParameter(string? QueryPlaceholder, string? Value);

/// <summary><c>Destination</c> is always null: destinations are secrets, shown masked to admins on the recipients list only.</summary>
public record SubscriptionDetailRecipient(
    int Id,
    string Name,
    string? Description,
    string? Destination,
    NotificationType NotificationType)
{
    /// <summary>
    /// While the approval workflow covers recipients: where this recipient delivers waits for approval, so the
    /// subscription's deliveries skip it.
    /// </summary>
    public bool AwaitingApproval { get; init; }

    /// <summary>The pending change request of where this recipient delivers, if one waits; an Approver opens it.</summary>
    public int? PendingApprovalRequestId { get; init; }

    /// <summary>
    /// While the recipient waits and no request of it is pending: the last decision on where it delivers (a rejection,
    /// with the reviewer's comment: the recipient is changed and saved again).
    /// </summary>
    public ChangeApprovalOutcome? LastApprovalDecision { get; init; }
}

public record SubscriptionDetailAnomalyConfig(
    bool Enabled,
    AnomalyDetectionMethod DetectionMethod,
    AnomalySensitivity Sensitivity,
    int LookbackDays,
    bool AlertOnIncrease,
    bool AlertOnDecrease,
    int MinimumDataPoints);
