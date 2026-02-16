using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;

namespace Semantico.Core.Handlers.Dashboards.RevokeDashboardPermission;

internal sealed class RevokeDashboardPermissionHandler(
    IDbContextFactory<SemanticoContext> contextFactory) : IRequestHandler<RevokeDashboardPermissionCommand, Unit>
{
    public async Task<Unit> Handle(RevokeDashboardPermissionCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var permission = await context.DashboardPermissions
            .FirstOrDefaultAsync(p => p.Id == request.PermissionId, cancellationToken);

        if (permission != null)
        {
            context.DashboardPermissions.Remove(permission);
            await context.SaveChangesAsync(cancellationToken);
        }

        return Unit.Value;
    }
}

public record RevokeDashboardPermissionCommand(int PermissionId) : IRequest<Unit>;
