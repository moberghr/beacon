using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.Tasks;

internal sealed class ResolveTaskHandler(ITaskService taskService)
    : IRequestHandler<ResolveTaskCommand>
{
    public async Task Handle(ResolveTaskCommand request, CancellationToken cancellationToken)
    {
        await taskService.ResolveTask(request.Id, request.ResolutionNotes, request.UserId, cancellationToken);
    }
}

public record ResolveTaskCommand(int Id, string? ResolutionNotes, string? UserId) : IRequest;
