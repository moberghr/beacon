using MediatR;
using Semantico.Core.Models.DataQuality;
using Semantico.Core.Services;

namespace Semantico.Core.Handlers.DataQuality.EvaluateDataContract;

internal sealed class EvaluateDataContractHandler(
    IDataQualityEvaluationService evaluationService) : IRequestHandler<EvaluateDataContractCommand, DataQualityEvaluationData>
{
    public async Task<DataQualityEvaluationData> Handle(EvaluateDataContractCommand request, CancellationToken cancellationToken)
    {
        return await evaluationService.EvaluateContractAsync(request.DataContractId);
    }
}

public record EvaluateDataContractCommand(int DataContractId) : IRequest<DataQualityEvaluationData>;
