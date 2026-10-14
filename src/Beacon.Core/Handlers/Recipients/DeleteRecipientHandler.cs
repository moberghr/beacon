using Beacon.Core.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.Recipients;

internal sealed class DeleteRecipientHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<DeleteRecipientCommand>
{
    public async Task Handle(DeleteRecipientCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var entity = await context.Recipients
            .Where(x => x.Id == request.Id)
            .Select(x => new
            {
                Recipient = x,
                HasSubscriptions = x.Subscriptions.Any(),
                HasDataContracts = x.DataContracts.Any(),
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (entity == null)
        {
            throw new InvalidOperationException($"Recipient {request.Id} not found.");
        }

        if (entity.HasSubscriptions)
        {
            throw new InvalidOperationException("Cannot delete recipient with active subscriptions.");
        }

        if (entity.HasDataContracts)
        {
            throw new InvalidOperationException("Cannot delete recipient with active data contracts.");
        }

        entity.Recipient.Archive();
        await context.SaveChangesAsync(cancellationToken);
    }
}

[ProjectScopeExempt("recipients are project-less; changing them is gated by the SecurityAdmin permission", Policy = BeaconPermissions.SecurityAdminPolicy)]
public record DeleteRecipientCommand(int Id) : IRequest;
