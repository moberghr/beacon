using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Anomaly;
using Beacon.Core.Models.Recipients;
using Beacon.Core.Models.Subscriptions;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.Subscriptions;

internal sealed class CreateSubscriptionHandler(ISubscriptionService subscriptionService)
    : IRequestHandler<CreateSubscriptionCommand, CreateSubscriptionResult>
{
    public async Task<CreateSubscriptionResult> Handle(CreateSubscriptionCommand request, CancellationToken cancellationToken)
    {
        var data = new SubscriptionData
        {
            QueryId = request.QueryId,
            CronExpression = request.CronExpression,
            MaxRows = request.MaxRows,
            MinimumRowCount = request.MinimumRowCount,
            TimeoutSeconds = request.TimeoutSeconds,
            IncludeAttachment = request.IncludeAttachment,
            ResultAttachmentType = request.ResultAttachmentType,
            ShowQuery = request.ShowQuery,
            StoreResults = request.StoreResults,
            CreateTasks = request.CreateTasks,
            NotificationTrigger = request.NotificationTrigger,
            Recipients = request.RecipientIds
                .Select(x =>
                    new RecipientData
                    {
                        RecipientId = x,
                        Name = string.Empty,
                        Destination = string.Empty,
                    })
                .ToList(),
            Parameters = (request.Parameters ?? [])
                .Select(x =>
                    new SubscriptionParameterData
                    {
                        QueryPlaceholder = x.QueryPlaceholder,
                        Value = x.Value,
                    })
                .ToList(),
            AnomalyConfig = request.AnomalyConfig == null
                ? null
                : new AnomalyConfigData
                {
                    Enabled = true,
                    DetectionMethod = request.AnomalyConfig.DetectionMethod,
                    Sensitivity = request.AnomalyConfig.Sensitivity,
                    LookbackDays = request.AnomalyConfig.LookbackDays,
                    AlertOnIncrease = request.AnomalyConfig.AlertOnIncrease,
                    AlertOnDecrease = request.AnomalyConfig.AlertOnDecrease,
                    MinimumDataPoints = request.AnomalyConfig.MinimumDataPoints,
                },
        };

        var response = await subscriptionService.CreateSubscription(data, cancellationToken);

        if (!response.Success)
        {
            throw new InvalidOperationException(response.Message);
        }

        return new CreateSubscriptionResult(true, response.Message);
    }
}

public record CreateSubscriptionCommand(
    int QueryId,
    string CronExpression,
    List<int> RecipientIds,
    int? MaxRows,
    int? TimeoutSeconds,
    bool IncludeAttachment,
    bool ShowQuery,
    bool StoreResults,
    bool CreateTasks,
    NotificationTrigger NotificationTrigger,
    int? MinimumRowCount,
    FileType? ResultAttachmentType,
    List<CreateSubscriptionParameter>? Parameters,
    CreateSubscriptionAnomalyConfig? AnomalyConfig) : IRequest<CreateSubscriptionResult>;

public record CreateSubscriptionParameter(string QueryPlaceholder, string Value);

/// <summary>
/// Anomaly detection settings for the new subscription; a null config leaves detection off.
/// </summary>
public record CreateSubscriptionAnomalyConfig(
    AnomalyDetectionMethod DetectionMethod,
    AnomalySensitivity Sensitivity,
    int LookbackDays,
    int MinimumDataPoints,
    bool AlertOnIncrease,
    bool AlertOnDecrease);

public record CreateSubscriptionResult(bool Success, string? Message);
