using MediatR;
using Beacon.Core.Models.DataQuality;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.DataQuality.GetEvaluationHistory;

internal sealed class GetEvaluationHistoryHandler(
    IDataQualityEvaluationService evaluationService)
    : IRequestHandler<GetEvaluationHistoryQuery, GetEvaluationHistoryResult>
{
    public async Task<GetEvaluationHistoryResult> Handle(GetEvaluationHistoryQuery request, CancellationToken cancellationToken)
    {
        var evaluations = await evaluationService.GetEvaluationHistoryAsync(request.DataContractId, request.Take ?? 20);
        return new GetEvaluationHistoryResult(evaluations);
    }
}

public record GetEvaluationHistoryQuery(int DataContractId, int? Take = null) : IRequest<GetEvaluationHistoryResult>;
public record GetEvaluationHistoryResult(List<DataQualityEvaluationData> Evaluations);
