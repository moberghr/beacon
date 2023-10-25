using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Api.Data;

namespace Semantico.Api.Handlers.Projects;

public class UpdateProjectCommand : IRequestHandler<UpdateProjectRequest, UpdateProjectResponse>
{
    private readonly SemanticoContext _context;

    public UpdateProjectCommand(SemanticoContext context)
    {
        _context = context;
    }

    public async Task<UpdateProjectResponse> Handle(UpdateProjectRequest request, CancellationToken cancellationToken)
    {
        var project = await _context.Projects
            .Where(x => x.Id == request.ProjectId)
            .SingleAsync(cancellationToken);

        project.ConnectionString = request.ConnectionString;
        project.Name = request.Name;

        await _context.SaveChangesAsync(cancellationToken);

        return new();
    }
}

public class UpdateProjectRequest : IRequest<UpdateProjectResponse>
{
    public int ProjectId { get; init; }

    public string Name { get; init; } = string.Empty;

    public string ConnectionString { get; init; } = string.Empty;
}

public class UpdateProjectResponse
{
}