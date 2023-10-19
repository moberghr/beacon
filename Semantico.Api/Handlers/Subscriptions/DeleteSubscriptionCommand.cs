using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Api.Data;
using Semantico.Api.Worker.Services;

namespace Semantico.Api.Handlers.Subscriptions;

public class DeleteSubscriptionCommand : IRequestHandler<DeleteSubscriptionRequest, DeleteSubscriptionResponse>
{
    private readonly SemanticoContext _context;
    private readonly IRecurringJobService _recurringJobService;

    public DeleteSubscriptionCommand(SemanticoContext context, IRecurringJobService recurringJobService)
    {
        _context = context;
        _recurringJobService = recurringJobService;
    }

    public async Task<DeleteSubscriptionResponse> Handle(DeleteSubscriptionRequest request, CancellationToken cancellationToken)
    {
        var subscription = await _context.Subscriptions
            .Where(x => x.Id == request.SubscriptionId)
            .SingleAsync(cancellationToken);

        subscription.Archive();
        await _context.SaveChangesAsync(cancellationToken);

        _recurringJobService.Remove(request.SubscriptionId);

        foreach (var param in subscription.Parameters)
        {
            param.Archive();
            await _context.SaveChangesAsync(cancellationToken);
        }

        return new();
    }
}

public class DeleteSubscriptionRequest : IRequest<DeleteSubscriptionResponse>
{
    public int SubscriptionId { get; init; }
}

public class DeleteSubscriptionResponse
{
}