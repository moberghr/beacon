using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.Queries;

internal sealed class UpdateQueryHandler(IQueryService queryService)
    : IRequestHandler<UpdateQueryCommand, UpdateQueryResult>
{
    public async Task<UpdateQueryResult> Handle(UpdateQueryCommand request, CancellationToken cancellationToken)
    {
        if (request.Query == null)
        {
            throw new InvalidOperationException("Query payload is required.");
        }

        var payload = request.Query;
        payload.QueryId = request.QueryId;

        var response = await queryService.UpdateQuery(payload, cancellationToken);

        if (!response.Success)
        {
            throw new InvalidOperationException(response.Message ?? "Failed to update query.");
        }

        return new UpdateQueryResult
        {
            QueryId = request.QueryId,
            Success = true,
        };
    }
}

public record UpdateQueryCommand : IRequest<UpdateQueryResult>
{
    public required int QueryId { get; init; }

    public required QueryData Query { get; init; }
}

public record UpdateQueryResult
{
    public required int QueryId { get; init; }

    public required bool Success { get; init; }
}
