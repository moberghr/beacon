using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Services;
using MediatR;
using Beacon.Core.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.Users;

internal sealed class GetRolesHandler(IRoleService roleService, IOptions<RoleMappingOptions> roleMapping)
    : IRequestHandler<GetRolesQuery, GetRolesResult>
{
    public async Task<GetRolesResult> Handle(GetRolesQuery request, CancellationToken cancellationToken)
    {
        // An upgrade path: installations set up before the functional roles existed get them the first time roles are
        // listed to be assigned (first-run setup and single sign-on provisioning seed them too).
        try
        {
            await roleService.SeedSystemRolesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (DbUniqueViolation.IsUniqueViolation(ex))
        {
            // A concurrent request seeded the roles first; nothing is lost.
        }

        var roles = await roleService.GetRolesAsync(cancellationToken);

        var entries = roles
            .Select(x => new RoleEntry(x.Id, x.Name, x.Description, x.Level, x.IsSystemRole, roleMapping.Value.PermissionOfStoredRole(x.Name, x.IsSystemRole, x.Level)))
            .ToList();

        return new GetRolesResult(entries);
    }
}

/// <summary>Every role, the functional roles among them (created when missing). For an Admin or a UserAdmin.</summary>
[ProjectScopeExempt("user management, gated by the UserAdmin permission; no project data", Policy = BeaconPermissions.UserAdminPolicy)]
public record GetRolesQuery : IRequest<GetRolesResult>;

public record GetRolesResult(List<RoleEntry> Entries);

/// <summary>
/// A role. <c>Permission</c> names the functional permission (<see cref="BeaconPermissions"/>) the role grants: only a
/// functional role (a system role at level 0) grants one. Null for the Admin, Editor and Viewer roles and any other role,
/// also one that merely has a functional role's name.
/// </summary>
public record RoleEntry(int Id, string Name, string? Description, int Level, bool IsSystemRole, string? Permission = null);
