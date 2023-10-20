using MediatR;
using Microsoft.AspNetCore.Mvc;
using Semantico.Api.Handlers.Notifications;

namespace Semantico.Api.Controllers;

[ApiController]
[Route("[controller]")]
public class NotificationsController : ControllerBase
{
    private readonly IMediator _mediator;

    public NotificationsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet]
    public async Task<GetNotificationsResponse> GetAccounts([FromQuery] GetNotificationsRequest request, CancellationToken cancellationToken)
    {
        return await _mediator.Send(request, cancellationToken);
    }
}