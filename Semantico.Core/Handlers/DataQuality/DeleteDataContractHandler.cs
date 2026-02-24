using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.Core.Models;
using Semantico.Core.Worker;

namespace Semantico.Core.Handlers.DataQuality.DeleteDataContract;

internal sealed class DeleteDataContractHandler(
    IDbContextFactory<SemanticoContext> contextFactory,
    ISemanticoScheduler scheduler) : IRequestHandler<DeleteDataContractCommand>
{
    public async Task Handle(DeleteDataContractCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var contract = await context.DataContracts
            .FirstOrDefaultAsync(c => c.Id == request.DataContractId, cancellationToken)
            ?? throw new SemanticoException($"Data contract {request.DataContractId} not found");

        scheduler.RemoveDataQualityJob(contract.Id, contract.Name);

        contract.Archive();
        await context.SaveChangesAsync(cancellationToken);
    }
}

public record DeleteDataContractCommand(int DataContractId) : IRequest;
