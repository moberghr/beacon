using MediatR;
using Microsoft.EntityFrameworkCore;
using NCrontab;
using Semantico.Api.Data;
using Semantico.Api.Data.Entities;
using Semantico.Api.Validators;

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
            .Include(query => query.Parameters)
            .Include(query => query.Subscriptions)
            .ThenInclude(subscription => subscription.Parameters)
            .Where(x => x.Id == request.QueryId)
            .SingleAsync(cancellationToken);

        QueryValidator.CheckForFlaggedWords(request.SqlValue);

        QueryValidator.ValidateQueryUpdate(query, request.Parameters);

        query.Parameters = request.Parameters;
        query.SqlValue = request.SqlValue;

        await _context.SaveChangesAsync(cancellationToken);

        return new();
    }
}

public class UpdateQueryRequest : IRequest<UpdateQueryResponse>
{
    public int QueryId { get; init; }

    public string SqlValue { get; init; } = string.Empty;

    public List<QueryParameter> Parameters { get; init; } = new();
}

public class UpdateQueryResponse
{
}

