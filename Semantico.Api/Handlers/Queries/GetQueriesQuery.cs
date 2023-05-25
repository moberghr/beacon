using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Api.Data;
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
            .WhereIf(request.ProjectId.HasValue, x => x.Id == request.ProjectId)
            .Select(x =>
                new GetQueriesResponseListData
                {
                    SqlValue = x.SqlValue,
                    CronExpression = x.CronExpression,
                    ProjectId = x.ProjectId,
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
    public int? QueryId { get; set; }

    public int? ProjectId { get; set; }
}

public class GetQueriesResponse
{
    public List<GetQueriesResponseListData> Queries { get; set; } = new();
}

public class GetQueriesResponseListData
{
    public string SqlValue { get; set; } = string.Empty;

    public string CronExpression { get; set; } = string.Empty;

    public int ProjectId { get; set; }
}