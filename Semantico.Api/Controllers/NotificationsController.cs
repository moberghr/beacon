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
    public async Task<List<GetNotificationsResponse>> GetNotifications([FromQuery] GetNotificationsRequest request, CancellationToken cancellationToken)
    {
        return await _mediator.Send(request, cancellationToken);
    }

    [HttpPost]
    public async Task<CreateNotificationResponse> CreateNotification(CreateNotificationRequest request, CancellationToken cancellationToken)
    {
        return await _mediator.Send(request, cancellationToken);
    }

    [HttpPut]
    public async Task<UpdateNotificationResponse> UpdateNotification(UpdateNotificationRequest request, CancellationToken cancellationToken)
    {
        return await _mediator.Send(request, cancellationToken);
    }

    [HttpDelete]
    public async Task<DeleteNotificationResponse> DeleteNotification([FromQuery] DeleteNotificationRequest request, CancellationToken cancellationToken)
    {
        return await _mediator.Send(request, cancellationToken);
    }
}