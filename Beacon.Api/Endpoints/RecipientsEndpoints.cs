using Beacon.Core.Handlers.Recipients;
using MediatR;

namespace Beacon.Api.Endpoints;

internal static class RecipientsEndpoints
{
    public static RouteGroupBuilder MapRecipientsEndpoints(this RouteGroupBuilder group)
    {
        var recipients = group.MapGroup("/recipients").WithTags("Recipients");

        recipients.MapGet("/", (IMediator m, CancellationToken ct) => m.Send(new GetRecipientsQuery(), ct))
            .WithName("GetRecipients");

        recipients.MapPost("/", (CreateRecipientCommand cmd, IMediator m, CancellationToken ct) => m.Send(cmd, ct))
            .WithName("CreateRecipient");

        recipients.MapPut("/{id:int}", async (int id, UpdateRecipientBody body, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new UpdateRecipientCommand(
                id, body.Name, body.Description, body.Destination,
                body.NotificationType, body.HeadersJson, body.BodyTemplate), ct);
            return TypedResults.NoContent();
        }).WithName("UpdateRecipient");

        recipients.MapDelete("/{id:int}", async (int id, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new DeleteRecipientCommand(id), ct);
            return TypedResults.NoContent();
        }).WithName("DeleteRecipient");

        return group;
    }
}

internal sealed record UpdateRecipientBody(
    string Name,
    string? Description,
    string Destination,
    int NotificationType,
    string? HeadersJson,
    string? BodyTemplate);
