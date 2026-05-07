using Beacon.Core.Handlers.Recipients;
using MediatR;

namespace Beacon.SampleProject.Endpoints;

internal static class RecipientsEndpoints
{
    public static RouteGroupBuilder MapRecipientsEndpoints(this RouteGroupBuilder group)
    {
        var recipients = group.MapGroup("/recipients").WithTags("Recipients");

        recipients.MapGet("/", async (IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new GetRecipientsQuery(), ct)))
            .WithName("GetRecipients")
            .Produces<GetRecipientsResult>(StatusCodes.Status200OK);

        recipients.MapPost("/", async (CreateRecipientCommand command, IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(command, ct)))
            .WithName("CreateRecipient")
            .Produces<CreateRecipientResult>(StatusCodes.Status200OK);

        recipients.MapPut("/{id:int}", async (int id, UpdateRecipientBody body, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new UpdateRecipientCommand(
                    id,
                    body.Name,
                    body.Description,
                    body.Destination,
                    body.NotificationType,
                    body.HeadersJson,
                    body.BodyTemplate), ct);
                return Results.NoContent();
            })
            .WithName("UpdateRecipient")
            .Produces(StatusCodes.Status204NoContent);

        recipients.MapDelete("/{id:int}", async (int id, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new DeleteRecipientCommand(id), ct);
                return Results.NoContent();
            })
            .WithName("DeleteRecipient")
            .Produces(StatusCodes.Status204NoContent);

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
