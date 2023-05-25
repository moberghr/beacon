using Semantico.Api.Data;

namespace Semantico.Api.Handlers.Projects;

public class GetProjectsQuery
{
    private readonly SemanticoContext _context;

    public GetProjectsQuery(SemanticoContext context)
    {
        _context = context;
    }

}

public class GetProjectsRequest
{ }

public class GetProjectsResponse
{ }

public class GetProjectsResponseListData
{

}

