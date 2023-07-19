using MediatR;
using Microsoft.AspNetCore.Mvc;
using Semantico.Api.Handlers.Accounts;

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

    [HttpGet]
    public async Task<GetAccountsResponse> GetAccounts([FromQuery] GetAccountsRequst requst, CancellationToken cancellationToken)
    {
        return await _mediator.Send(requst, cancellationToken);
    }

    [HttpPost("create-account")]
    public async Task<CreateAccountResponse> CreateAccount(CreateAccountRequest request, CancellationToken cancellationToken)
    {
        return await _mediator.Send(request, cancellationToken);
    }

    [HttpPost("remove-account")]
    public async Task<RemoveAccountResponse> RemoveAccount(RemoveAccountRequest request, CancellationToken cancellationToken)
    {
        return await _mediator.Send(request, cancellationToken);
    }
}