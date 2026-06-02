using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.Subscriptions;

internal sealed class GetSubscriptionDetailHandler(ISubscriptionService subscriptionService)
    : IRequestHandler<GetSubscriptionDetailQuery, GetSubscriptionDetailResult>
{
    public async Task<GetSubscriptionDetailResult> Handle(GetSubscriptionDetailQuery request, CancellationToken cancellationToken)
    {
        var data = await subscriptionService.GetSubscriptionDetails(request.SubscriptionId, cancellationToken);

        var cronDescription = data.CronExpression?.GetCronDescription() ?? string.Empty;
        var cronNextAt = data.CronExpression?.GetCronNextAt();

        var recipients = data.Recipients
            .Select(x =>
                new SubscriptionDetailRecipient(
                    x.RecipientId ?? 0,
                    x.Name,
                    x.Description,
                    x.Destination,
                    x.NotificationType))
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
            anomaly);

        return new GetSubscriptionDetailResult(detail);
    }
}

public record GetSubscriptionDetailQuery(int SubscriptionId) : IRequest<GetSubscriptionDetailResult>;

public record GetSubscriptionDetailResult(SubscriptionDetail Detail);

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
    SubscriptionDetailAnomalyConfig? AnomalyConfig);

public record SubscriptionDetailParameter(string? QueryPlaceholder, string? Value);

public record SubscriptionDetailRecipient(
    int Id,
    string Name,
    string? Description,
    string Destination,
    NotificationType NotificationType);

public record SubscriptionDetailAnomalyConfig(
    bool Enabled,
    AnomalyDetectionMethod DetectionMethod,
    AnomalySensitivity Sensitivity,
    int LookbackDays,
    bool AlertOnIncrease,
    bool AlertOnDecrease,
    int MinimumDataPoints);
