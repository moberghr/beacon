using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.Subscriptions;

internal sealed class GetSubscriptionAnomalyChartHandler(IAnomalyDetectionService anomalyDetectionService)
    : IRequestHandler<GetSubscriptionAnomalyChartQuery, GetSubscriptionAnomalyChartResult>
{
    public async Task<GetSubscriptionAnomalyChartResult> Handle(GetSubscriptionAnomalyChartQuery request, CancellationToken cancellationToken)
    {
        var data = await anomalyDetectionService.GetAnomalyChartDataAsync(
            request.SubscriptionId,
            request.Days,
            cancellationToken);

        if (data is null || !data.HasAnomalyDetection)
        {
            return new GetSubscriptionAnomalyChartResult(
                false,
                Array.Empty<AnomalyChartPointDto>(),
                null,
                null,
                null);
        }

        var points = data.DataPoints
            .OrderBy(x => x.DateTime)
            .Select(x =>
                new AnomalyChartPointDto(
                    x.DateTime,
                    x.ResultCount,
                    x.IsAnomaly,
                    x.NotificationSent,
                    x.AnomalySeverity,
                    x.QueryExecutionHistoryId))
            .ToArray();

        return new GetSubscriptionAnomalyChartResult(
            true,
            points,
            data.BaselineMean,
            data.UpperThreshold,
            data.LowerThreshold);
    }
}

public record GetSubscriptionAnomalyChartQuery(int SubscriptionId, int Days = 30)
    : IRequest<GetSubscriptionAnomalyChartResult>;

public record GetSubscriptionAnomalyChartResult(
    bool HasAnomalyDetection,
    IReadOnlyList<AnomalyChartPointDto> Points,
    decimal? BaselineMean,
    decimal? UpperThreshold,
    decimal? LowerThreshold);

public record AnomalyChartPointDto(
    DateTime DateTime,
    decimal ResultCount,
    bool IsAnomaly,
    bool NotificationSent,
    string? AnomalySeverity,
    int? QueryExecutionHistoryId);
