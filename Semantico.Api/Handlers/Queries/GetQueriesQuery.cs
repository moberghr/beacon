using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Api.Data;
using Semantico.Api.Data.Enums;
using Semantico.Api.Helpers;

namespace Semantico.Api.Handlers.Queries;

public class GetQueriesQuery : IRequestHandler<GetQueriesRequest, GetQueriesResponse>
{
    private readonly SemanticoContext _context;

    public GetQueriesQuery(SemanticoContext context)
    {
        _context = context;
    }

    public async Task<GetQueriesResponse> Handle(GetQueriesRequest request, CancellationToken cancellationToken)
    {
        var queries = await _context.Queries
            .WhereIf(request.QueryId.HasValue, x => x.Id == request.QueryId)
            .WhereIf(request.ProjectId.HasValue, x => x.ProjectId == request.ProjectId)
            .Select(x =>
                new GetQueriesResponseListData
                {
                    SqlValue = x.SqlValue,
                    ProjectId = x.ProjectId,
                    Parameters = x.Parameters.Select(y => 
                        new QueryParameterResponseListData
                        {
                            Name = y.Name,
                            Type = y.Type,
                            Description = y.Description,
                            Placeholder = y.Placeholder
                        }).ToList()
                })
            .ToListAsync(cancellationToken);

        return new GetQueriesResponse
        {
            Queries = queries
        };
    }
}

public class GetQueriesRequest : IRequest<GetQueriesResponse>
{
    public int? QueryId { get; init; }

    public int? ProjectId { get; init; }
}

public class GetQueriesResponse
{
    public required List<GetQueriesResponseListData> Queries { get; init; }
}

public class GetQueriesResponseListData
{
    public required string SqlValue { get; init; }

    public required int ProjectId { get; init; }

    public List<QueryParameterResponseListData> Parameters { get; init; } = new();
}

public class QueryParameterResponseListData
{
    public required string Name { get; init; }

    public required ParameterType Type { get; init; }

    public required string Description { get; init; }

    public required string Placeholder { get; init; }
}