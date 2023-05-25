using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Api.Data;

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
            .Where(x => x.Id == request.QueryId)
            .FirstAsync(cancellationToken);

        _context.Queries.Remove(query);
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

