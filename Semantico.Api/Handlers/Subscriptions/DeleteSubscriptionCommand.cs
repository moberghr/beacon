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
            .FirstAsync(cancellationToken);

        subscription.ArchivedTime = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        _recurringJobService.Remove(request.SubscriptionId);

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