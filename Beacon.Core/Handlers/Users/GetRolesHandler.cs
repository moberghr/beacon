using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.Users;

internal sealed class GetRolesHandler(IRoleService roleService)
    : IRequestHandler<GetRolesQuery, GetRolesResult>
{
    public async Task<GetRolesResult> Handle(GetRolesQuery request, CancellationToken cancellationToken)
    {
        var roles = await roleService.GetRolesAsync(cancellationToken);

        var entries = roles
            .Select(x => new RoleEntry(x.Id, x.Name, x.Description, x.Level, x.IsSystemRole))
            .ToList();

        return new GetRolesResult(entries);
    }
}

public record GetRolesQuery : IRequest<GetRolesResult>;

public record GetRolesResult(List<RoleEntry> Entries);

public record RoleEntry(int Id, string Name, string? Description, int Level, bool IsSystemRole);
