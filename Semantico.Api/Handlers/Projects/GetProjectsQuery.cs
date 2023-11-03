using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Api.Data;
using Semantico.Api.Data.Entities;
using Semantico.Api.Data.Enums;
using Semantico.Api.Helpers;

namespace Semantico.Api.Handlers.Projects;

public class GetProjectsQuery : IRequestHandler<GetProjectsRequest, GetProjectsResponse>
{
    private readonly SemanticoContext _context;

    public GetProjectsQuery(SemanticoContext context)
    {
        _context = context;
    }

    public async Task<GetProjectsResponse> Handle(GetProjectsRequest request, CancellationToken cancellationToken)
    {
        var projects = await _context.Projects
            .WhereIf(request.ProjectId.HasValue, x => x.Id == request.ProjectId)
            .Select(x =>
                new GetProjectsResponseListData
                {
                    Name = x.Name,
                    ConnectionString = x.ConnectionString,
                    DatabaseEngine = x.DatabaseEngine,
                    Queries = x.Queries,
                })
             .ToListAsync(cancellationToken);

        return new GetProjectsResponse
        {
            Projects = projects
        };
    }

}

public class GetProjectsRequest : IRequest<GetProjectsResponse>
{
    public int? ProjectId { get; init; }
}

public class GetProjectsResponse
{
    public required List<GetProjectsResponseListData> Projects { get; init; }
}

public class GetProjectsResponseListData
{
    public required string Name { get; init; }

    public required string ConnectionString { get; init; }

    public required DatabaseEngineType DatabaseEngine { get; init; }

    public List<Query> Queries { get; init; } = new();
}

