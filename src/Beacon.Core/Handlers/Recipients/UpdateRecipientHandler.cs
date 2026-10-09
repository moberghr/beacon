using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Notifications;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.Recipients;

/// <summary>
/// Admin only (the endpoint carries the admin policy). Reads return masked secrets, so a destination or header value
/// echoed back masked (or left empty) keeps the stored secret while the destination stays the same
/// (<see cref="RecipientSecrets"/>); whatever is stored must pass the destination policy and is stored encrypted.
/// </summary>
internal sealed class UpdateRecipientHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    RecipientSecretEditor secretEditor)
    : IRequestHandler<UpdateRecipientCommand>
{
    public async Task Handle(UpdateRecipientCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new InvalidOperationException("Recipient name is required.");
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

        var notificationType = (NotificationType)request.NotificationType;
        var (destination, headersJson) = secretEditor.Prepare(
            notificationType,
            request.Destination,
            request.HeadersJson,
            new StoredRecipientSecrets(entity.NotificationType, entity.Destination, entity.HeadersJson));

        entity.Name = request.Name;
        entity.Description = request.Description;
        entity.Destination = destination;
        entity.NotificationType = notificationType;
        entity.HeadersJson = headersJson;
        entity.BodyTemplate = request.BodyTemplate;

        await context.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>An empty or masked <c>Destination</c>, or masked header values, keep the stored secrets.</summary>
public record UpdateRecipientCommand(
    int Id,
    string Name,
    string? Description,
    string? Destination,
    int NotificationType,
    string? HeadersJson,
    string? BodyTemplate) : IRequest;
