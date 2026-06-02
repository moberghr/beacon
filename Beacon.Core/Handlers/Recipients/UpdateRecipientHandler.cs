using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.Recipients;

internal sealed class UpdateRecipientHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<UpdateRecipientCommand>
{
    public async Task Handle(UpdateRecipientCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new InvalidOperationException("Recipient name is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Destination))
        {
            throw new InvalidOperationException("Recipient destination is required.");
        }

        if (!Enum.IsDefined(typeof(NotificationType), request.NotificationType))
        {
            throw new InvalidOperationException("Unknown notification type.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var entity = await context.Recipients
            .Where(x => x.Id == request.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (entity == null)
        {
            throw new InvalidOperationException($"Recipient {request.Id} not found.");
        }

        var nameClash = await context.Recipients
            .Where(x => x.Id != request.Id)
            .Where(x => x.Name == request.Name)
            .AnyAsync(cancellationToken);

        if (nameClash)
        {
            throw new InvalidOperationException($"A recipient named '{request.Name}' already exists.");
        }

        entity.Name = request.Name;
        entity.Description = request.Description;
        entity.Destination = request.Destination;
        entity.NotificationType = (NotificationType)request.NotificationType;
        entity.HeadersJson = request.HeadersJson;
        entity.BodyTemplate = request.BodyTemplate;

        await context.SaveChangesAsync(cancellationToken);
    }
}

public record UpdateRecipientCommand(
    int Id,
    string Name,
    string? Description,
    string Destination,
    int NotificationType,
    string? HeadersJson,
    string? BodyTemplate) : IRequest;
