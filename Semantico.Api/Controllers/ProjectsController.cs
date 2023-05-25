using MediatR;
using Microsoft.AspNetCore.Mvc;
using Semantico.Api.Handlers.Projects;

namespace Semantico.Api.Controllers;

[ApiController]
[Route("[controller]")]
public class ProjectsController : ControllerBase
{
    private readonly IMediator _mediator;

    public ProjectsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet]
    public async Task<GetProjectsResponse> GetProjects([FromQuery] GetProjectsRequest request, CancellationToken cancellationToken)
    {
        return await _mediator.Send(request, cancellationToken);
    }

    [HttpPost]
    public async Task<CreateProjectResponse> CreateProject(CreateProjectRequest request, CancellationToken cancellationToken)
    {
        return await _mediator.Send(request, cancellationToken);
    }

    [HttpPut]
    public async Task<UpdateProjectResponse> UpdateProject(UpdateProjectRequest request, CancellationToken cancellationToken)
    {
        return await _mediator.Send(request, cancellationToken);
    }

    [HttpDelete]
    public async Task<DeleteProjectResponse> DeleteProject([FromQuery] DeleteProjectRequest request, CancellationToken cancellationToken)
    {
        return await _mediator.Send(request, cancellationToken);
    }
}