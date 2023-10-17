using MediatR;
using Microsoft.AspNetCore.Mvc;
using Semantico.Api.Handlers.Subscriptions;

namespace Semantico.Api.Controllers;

[ApiController]
[Route("[controller]")]
public class SubcriptionsController : ControllerBase
{
    private readonly IMediator _mediator;

    public SubcriptionsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet]
    public async Task<GetSubscriptionsResponse> GetSubscriptions([FromQuery] GetSubscriptionsRequest request, CancellationToken cancellationToken)
    {
        return await _mediator.Send(request, cancellationToken);
    }

    [HttpPost]
    public async Task<CreateSubscriptionResponse> CreateSubscription(CreateSubscriptionRequest request, CancellationToken cancellationToken)
    {
        return await _mediator.Send(request, cancellationToken);
    }

    [HttpPut]
    public async Task<UpdateSubscriptionResponse> UpdateSubscription(UpdateSubscriptionRequest request, CancellationToken cancellationToken)
    {
        return await _mediator.Send(request, cancellationToken);
    }

    [HttpDelete]
    public async Task<DeleteSubscriptionResponse> DeleteSubscription(DeleteSubscriptionRequest request, CancellationToken cancellationToken)
    {
        return await _mediator.Send(request, cancellationToken);
    }
}