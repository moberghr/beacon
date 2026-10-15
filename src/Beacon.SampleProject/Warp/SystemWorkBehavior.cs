using Beacon.Core.Authorization;
using Warp.Core.Handlers;

namespace Beacon.SampleProject.Warp;

/// <summary>
/// Warp pipeline behavior that marks every job's DI scope as work Beacon does for itself (<see cref="ISystemWorkScope"/>)
/// before the handler runs, so project access treats the job as the system without warning. Work a job does for a user
/// (a subscription's scheduled run) still resolves that user as its owner. Auto-registered by the Warp source generator,
/// like <see cref="JobStatusChangedBehavior{TRequest, TResponse}"/>.
/// </summary>
public sealed class SystemWorkBehavior<TRequest, TResponse>(ISystemWorkScope systemWork)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public Task<TResponse> HandleAsync(
        TRequest request,
        RequestHandlerDelegate<TRequest, TResponse> next,
        CancellationToken cancellationToken)
    {
        systemWork.MarkAsSystemWork();

        return next(request, cancellationToken);
    }
}
