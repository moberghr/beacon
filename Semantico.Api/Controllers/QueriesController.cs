using Microsoft.AspNetCore.Mvc;

namespace Semantico.Api.Controllers;

[ApiController]
[Route("[controller]")]
public class QueriesController : ControllerBase
{
    [HttpGet]
    public async Task GetQueries()
    {
    }

    [HttpPost]
    public async Task CreateQuery()
    {
    }

    [HttpPut]
    public async Task UpdateQuery()
    {
    }

    [HttpDelete]
    public async Task DeleteQuery()
    {
    }
}