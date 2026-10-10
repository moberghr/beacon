using MediatR;
using Beacon.Core.Models.DataQuality;
using Beacon.Core.Services;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.DataQuality.GetEvaluationHistory;

internal sealed class GetEvaluationHistoryHandler(
    IDataQualityEvaluationService evaluationService)
    : IRequestHandler<GetEvaluationHistoryQuery, GetEvaluationHistoryResult>
{
    public async Task<GetEvaluationHistoryResult> Handle(GetEvaluationHistoryQuery request, CancellationToken cancellationToken)
    {
        var evaluations = await evaluationService.GetEvaluationHistoryAsync(request.DataContractId, request.Take ?? 20, cancellationToken);
        return new GetEvaluationHistoryResult(evaluations);
    }
}

public record GetEvaluationHistoryQuery(int DataContractId, int? Take = null) : IRequest<GetEvaluationHistoryResult>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.DataContract(DataContractId), ProjectAction.Read)
    ];
}
public record GetEvaluationHistoryResult(List<DataQualityEvaluationData> Evaluations);
