using MediatR;
using Beacon.Core.Models.DataQuality;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.DataQuality.EvaluateDataContract;

internal sealed class EvaluateDataContractHandler(
    IDataQualityEvaluationService evaluationService) : IRequestHandler<EvaluateDataContractCommand, DataQualityEvaluationData>
{
    public async Task<DataQualityEvaluationData> Handle(EvaluateDataContractCommand request, CancellationToken cancellationToken)
    {
        return await evaluationService.EvaluateContractAsync(request.DataContractId, cancellationToken);
    }
}

public record EvaluateDataContractCommand(int DataContractId) : IRequest<DataQualityEvaluationData>;
