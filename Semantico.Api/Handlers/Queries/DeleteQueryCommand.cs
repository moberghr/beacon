using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Api.Data;
using Semantico.Api.Types;

namespace Semantico.Api.Handlers.Queries;

public class DeleteQueryCommand : IRequestHandler<DeleteQueryRequest, DeleteQueryResponse>
{
    private readonly SemanticoContext _context;

    public DeleteQueryCommand(SemanticoContext context)
    {
        _context = context;
    }

    public async Task<DeleteQueryResponse> Handle(DeleteQueryRequest request, CancellationToken cancellationToken)
    {
        var query = await _context.Queries
            .Include(x => x.Parameters)
            .Where(x => x.Id == request.QueryId)
            .SingleAsync(cancellationToken);

        if (query.Subscriptions.Count > 0)
        {
            throw new SemanticoException($"Unable to remove query due to active subscriptions.");
        }

        query.Archive();

        foreach (var param in query.Parameters)
        {
            param.Archive();
        }

        await _context.SaveChangesAsync(cancellationToken);

        return new();
    }
}

public class DeleteQueryRequest : IRequest<DeleteQueryResponse>
{
    public int QueryId { get; init; }
}

public class DeleteQueryResponse
{
}

