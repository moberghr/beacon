using Microsoft.AspNetCore.Mvc;

namespace Semantico.Api.Controllers;

[ApiController]
[Route("[controller]")]
public class ProjectsController : ControllerBase
{
    [HttpGet]
    public async Task GetProjects()
    {
    }

    [HttpPost]
    public async Task CreateProject()
    {
    }

    [HttpPut]
    public async Task UpdateProject()
    {
    }

    [HttpDelete]
    public async Task DeleteProject()
    {
    }
}