using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.Worker;

namespace Beacon.Core.Handlers.DataQuality.DeleteDataContract;

internal sealed class DeleteDataContractHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconScheduler scheduler,
    IBeaconUserContext userContext) : IRequestHandler<DeleteDataContractCommand>
{
    public async Task Handle(DeleteDataContractCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var contract = await context.DataContracts
            .Where(c => c.Id == request.DataContractId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconException($"Data contract {request.DataContractId} not found");

        // A contract that carries a CustomSql rule is an Admin's to create, change and delete.
        var hasCustomSql = await context.DataContractRules
            .Where(x => x.DataContractId == contract.Id)
            .Where(x => x.RuleType == DataContractRuleType.CustomSql)
            .AnyAsync(cancellationToken);
        if (hasCustomSql)
        {
            DataQualityRuleGuard.EnsureAdmin(userContext);
        }

        await scheduler.RemoveDataQualityJob(contract.Id, contract.Name);

        contract.Archive();
        await context.SaveChangesAsync(cancellationToken);
    }
}

public record DeleteDataContractCommand(int DataContractId) : IRequest;
