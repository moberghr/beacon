using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Models;
using Beacon.Core.Worker;

namespace Beacon.Core.Handlers.DataQuality.DeleteDataContract;

internal sealed class DeleteDataContractHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconScheduler scheduler) : IRequestHandler<DeleteDataContractCommand>
{
    public async Task Handle(DeleteDataContractCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var contract = await context.DataContracts
            .FirstOrDefaultAsync(c => c.Id == request.DataContractId, cancellationToken)
            ?? throw new BeaconException($"Data contract {request.DataContractId} not found");

        scheduler.RemoveDataQualityJob(contract.Id, contract.Name);

        contract.Archive();
        await context.SaveChangesAsync(cancellationToken);
    }
}

public record DeleteDataContractCommand(int DataContractId) : IRequest;
