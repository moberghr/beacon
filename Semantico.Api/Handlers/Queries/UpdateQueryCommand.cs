using MediatR;
using Microsoft.EntityFrameworkCore;
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
            .Where(x => x.Id == request.QueryId)
            .SingleAsync(cancellationToken);

        QueryValidator.CheckForFlaggedWords(request.SqlValue);

        QueryValidator.CheckForParameters(request.SqlValue, request.Parameters);

        query.SqlValue = request.SqlValue;

        foreach (var queryParameter in query.Parameters)
        {
            queryParameter.Archive();
        }

        foreach (var queryParameter in request.Parameters)
        {
            var queryParam = new QueryParameter
            {
                QueryId = query.Id,
                Type = queryParameter.Type,
                Name = queryParameter.Name,
                Placeholder = queryParameter.Placeholder,
                Description = queryParameter.Description,
            };

            _context.QueryParameters.Add(queryParam);
        }

        await _context.SaveChangesAsync(cancellationToken);

        return new();
    }
}

public class UpdateQueryRequest : IRequest<UpdateQueryResponse>
{
    public int QueryId { get; init; }

    public string SqlValue { get; init; } = string.Empty;

    public List<QueryParameterResponseListData> Parameters { get; init; } = new();
}

public class UpdateQueryResponse
{
}

