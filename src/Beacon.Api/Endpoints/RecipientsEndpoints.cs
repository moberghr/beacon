using Beacon.Api.Authentication;
using Beacon.Core.Authorization;
using Beacon.Core.Handlers.Recipients;
using MediatR;

namespace Beacon.Api.Endpoints;

internal static class RecipientsEndpoints
{
    public static RouteGroupBuilder MapRecipientsEndpoints(this RouteGroupBuilder group)
    {
        var recipients = group.MapGroup("/recipients").WithTags("Recipients");

        // Any reader may list recipients (names and types, to attach them); destinations and headers come back masked, and
        // to Admins and SecurityAdmins only. Creating, changing and deleting a recipient decides where Beacon sends data: a
        // SecurityAdmin's, who changes where a recipient delivers only when it reads everything the recipient is attached to.
        recipients.MapGet("/", ([AsParameters] GetRecipientsQuery query, IMediator m, CancellationToken ct) => m.Send(query, ct))
            .WithName("GetRecipients");

        recipients.MapPost("/", (CreateRecipientCommand cmd, IMediator m, CancellationToken ct) => m.Send(cmd, ct))
            .WithName("CreateRecipient")
            .RequireBeaconPermission(BeaconPermissions.SecurityAdminPolicy);

        recipients.MapPut("/{id:int}", async (int id, UpdateRecipientBody body, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new UpdateRecipientCommand(
                id, body.Name, body.Description, body.Destination,
                body.NotificationType, body.HeadersJson, body.BodyTemplate), ct);
            return TypedResults.NoContent();
        }).WithName("UpdateRecipient").RequireBeaconPermission(BeaconPermissions.SecurityAdminPolicy);

        recipients.MapDelete("/{id:int}", async (int id, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new DeleteRecipientCommand(id), ct);
            return TypedResults.NoContent();
        }).WithName("DeleteRecipient").RequireBeaconPermission(BeaconPermissions.SecurityAdminPolicy);

        return group;
    }
}

internal sealed record UpdateRecipientBody(
    string Name,
    string? Description,
    string? Destination,
    int NotificationType,
    string? HeadersJson,
    string? BodyTemplate)
{
    // The compiler-generated ToString prints every member; the secrets are printed redacted.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"Name = {Name}, Description = {Description}, Destination = {Core.RedactedPrint.Of(Destination)}, NotificationType = {NotificationType}, HeadersJson = {Core.RedactedPrint.Of(HeadersJson)}, BodyTemplate = {BodyTemplate}");

        return true;
    }
}
