using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Notifications;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.Recipients;

/// <summary>
/// Admin only (the endpoint carries the admin policy). The destination and headers must pass the destination policy
/// and are stored encrypted.
/// </summary>
internal sealed class CreateRecipientHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    RecipientSecretEditor secretEditor)
    : IRequestHandler<CreateRecipientCommand, CreateRecipientResult>
{
    public async Task<CreateRecipientResult> Handle(CreateRecipientCommand request, CancellationToken cancellationToken)
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

        var notificationType = (NotificationType)request.NotificationType;
        var (destination, headersJson) = secretEditor.Prepare(notificationType, request.Destination, request.HeadersJson, stored: null);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var nameTaken = await context.Recipients
            .Where(x => x.Name == request.Name)
            .AnyAsync(cancellationToken);

        if (nameTaken)
        {
            throw new InvalidOperationException($"A recipient named '{request.Name}' already exists.");
        }

        var entity = new Recipient
        {
            Name = request.Name,
            Description = request.Description,
            Destination = destination,
            NotificationType = notificationType,
            HeadersJson = headersJson,
            BodyTemplate = request.BodyTemplate,
        };

        context.Recipients.Add(entity);
        await context.SaveChangesAsync(cancellationToken);

        return new CreateRecipientResult(entity.Id);
    }
}

public record CreateRecipientCommand(
    string Name,
    string? Description,
    string Destination,
    int NotificationType,
    string? HeadersJson,
    string? BodyTemplate) : IRequest<CreateRecipientResult>;

public record CreateRecipientResult(int Id);
