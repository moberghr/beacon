using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Beacon.Core.Authorization.ProjectAccess;

/// <summary>
/// Runs the <see cref="IProjectAuthorizedRequest.ProjectChecks"/> of every request that declares them before its handler
/// (404 for an unreadable or missing resource, 403 for a refused action). Other requests pass through untouched; the
/// authorizer is resolved only for a request that declares checks.
/// </summary>
internal sealed class ProjectAuthorizationBehavior<TRequest, TResponse>(IServiceProvider services)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (request is IProjectAuthorizedRequest authorized)
        {
            await services
                .GetRequiredService<ProjectRequestAuthorizer>()
                .DemandAsync(authorized.ProjectChecks, cancellationToken);
        }

        return await next();
    }
}
