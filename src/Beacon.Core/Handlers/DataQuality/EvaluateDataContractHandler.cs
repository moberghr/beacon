using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Models.DataQuality;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.DataQuality.EvaluateDataContract;

internal sealed class EvaluateDataContractHandler(
    IDataQualityEvaluationService evaluationService,
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconActorAccessor actorAccessor,
    ILogger<EvaluateDataContractHandler> logger) : IRequestHandler<EvaluateDataContractCommand, DataQualityEvaluationData>
{
    public async Task<DataQualityEvaluationData> Handle(EvaluateDataContractCommand request, CancellationToken cancellationToken)
    {
        await EnsureOwnerOrAdminAsync(request.DataContractId, cancellationToken);

        return await evaluationService.EvaluateContractAsync(request.DataContractId, cancellationToken);
    }

    private async Task EnsureOwnerOrAdminAsync(int contractId, CancellationToken cancellationToken)
    {
        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var contract = await context.DataContracts
            .Where(x => x.Id == contractId)
            .Select(x =>
                new
                {
                    x.OwnerUserId
                })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw DataContractOwnership.Missing(actor, contractId, logger);

        DataContractOwnership.EnsureOwnerOrAdmin(actor, contractId, contract.OwnerUserId, logger);
    }
}

/// <summary>Runs a data contract's rules now. Allowed for the contract's owner or an Admin.</summary>
public record EvaluateDataContractCommand(int DataContractId) : IRequest<DataQualityEvaluationData>;
