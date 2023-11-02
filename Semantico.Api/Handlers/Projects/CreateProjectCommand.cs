using MediatR;
using Semantico.Api.Data;
using Semantico.Api.Data.Entities;
using Semantico.Api.Data.Enums;

namespace Semantico.Api.Handlers.Projects;

public class CreateProjectCommand : IRequestHandler<CreateProjectRequest, CreateProjectResponse>
{
    private readonly SemanticoContext _context;

    public CreateProjectCommand(SemanticoContext context)
    {
        _context = context;
    }

    public async Task<CreateProjectResponse> Handle(CreateProjectRequest request, CancellationToken cancellationToken)
    {
        var project = new Project
        {
            Name = request.Name,
            ConnectionString = request.ConnectionString,
            DatabaseEngine = request.DatabaseEngine
        };

        _context.Projects.Add(project);
        await _context.SaveChangesAsync(cancellationToken);

        return new();
    }
}

public class CreateProjectRequest : IRequest<CreateProjectResponse>
{
    public string Name { get; init; } = string.Empty;

    public string ConnectionString { get; init; } = string.Empty;

    public DatabaseEngineType DatabaseEngine { get; init; }
}

public class CreateProjectResponse
{
}