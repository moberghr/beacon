using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Api.Data;
using Semantico.Api.Helpers;

namespace Semantico.Api.Handlers.Queries;

public class GetQueriesQuery : IRequestHandler<GetQueriesRequest, List<GetQueriesResponse>>
{
    private readonly SemanticoContext _context;

    public GetQueriesQuery(SemanticoContext context)
    {
        _context = context;
    }

    public async Task<List<GetQueriesResponse>> Handle(GetQueriesRequest request, CancellationToken cancellationToken)
    {
        var queries = await _context.Queries
            .WhereIf(request.QueryId.HasValue, x => x.Id == request.QueryId)
            .Select(x =>
                new GetQueriesResponse
                {
                    SqlValue = x.SqlValue,
                    CronExpression = x.CronExpression,
                    ProjectId = x.ProjectId,
                })
            .ToListAsync(cancellationToken);

        return queries;
    }
}

public class GetQueriesRequest : IRequest<List<GetQueriesResponse>>
{
    public int? QueryId { get; set; }
}

public class GetQueriesResponse
{
    public string SqlValue { get; set; } = string.Empty;

    public string CronExpression { get; set; } = string.Empty;

    public int ProjectId { get; set; }
}

