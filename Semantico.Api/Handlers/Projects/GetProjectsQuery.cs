using MediatR;
using Semantico.Api.Data;
using Semantico.Api.Data.Entities;

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
        return new GetProjectsResponse
        {
            Projects =
        };
    }

}

public class GetProjectsRequest : IRequest<GetProjectsResponse>
{
    public int?
}

public class GetProjectsResponse
{
    public required List<GetProjectsResponseListData> Projects { get; set; }
}

public class GetProjectsResponseListData
{
    public required string Name { get; set; }

    public required string ConnectionString { get; set; }

    public List<Query> Queries { get; set; } = new();
}

