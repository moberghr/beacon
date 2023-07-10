using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Semantico.Api.Controllers;

[ApiController]
[Route("[controller]")]
public class AccountsController : ControllerBase
{
    private readonly IMediator _mediator;

    public AccountsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost]
    public async Task CreateUserAccount()
    {

    }

    [HttpPost]
    public async Task RemoveUserAccount()
    { }

}

