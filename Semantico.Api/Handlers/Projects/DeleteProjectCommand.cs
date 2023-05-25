using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Api.Data;

namespace Semantico.Api.Handlers.Projects;

public class DeleteProjectCommand : IRequestHandler<DeleteProjectRequest, DeleteProjectResponse>
{
    private readonly SemanticoContext _context;

    public DeleteProjectCommand(SemanticoContext context)
    {
        _context = context;
    }

    public async Task<DeleteProjectResponse> Handle(DeleteProjectRequest request, CancellationToken cancellationToken)
    {
        var project = await _context.Projects
            .Where(x => x.Id == request.ProjectId)
            .FirstAsync(cancellationToken);

        _context.Projects.Remove(project);
        await _context.SaveChangesAsync(cancellationToken);

        return new();
    }
}

public class DeleteProjectRequest : IRequest<DeleteProjectResponse>
{
    public int ProjectId { get; init; }
}

public class DeleteProjectResponse
{
}