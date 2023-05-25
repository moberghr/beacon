using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Api.Data;

namespace Semantico.Api.Handlers.Queries;

public class UpdateQueryCommand : IRequestHandler<UpdateQueryRequest, UpdateQueryResponse>
{
    private readonly SemanticoContext _context;

    public UpdateQueryCommand(SemanticoContext context)
    {
        _context = context;
    }

    public async Task<UpdateQueryResponse> Handle(UpdateQueryRequest request, CancellationToken cancellationToken)
    {
        var query = await _context.Queries
            .Where(x => x.Id == request.QueryId)
            .FirstAsync(cancellationToken);

        query.SqlValue = request.SqlValue;
        query.CronExpression = request.CronExpression;

        await _context.SaveChangesAsync(cancellationToken);

        return new();
    }
}

public class UpdateQueryRequest : IRequest<UpdateQueryResponse>
{
    public int QueryId { get; init; }

    public string SqlValue { get; init; } = string.Empty;

    public string CronExpression { get; init; } = string.Empty;
}

public class UpdateQueryResponse
{
}

