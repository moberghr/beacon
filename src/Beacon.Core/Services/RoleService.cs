using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Models.UserManagement;

namespace Beacon.Core.Services;

internal class RoleService(IDbContextFactory<BeaconContext> contextFactory) : IRoleService
{
    public static class RoleNames
    {
        public const string Admin = "Admin";
        public const string Editor = "Editor";
        public const string Viewer = "Viewer";
    }

    public static class RoleLevels
    {
        public const int Admin = 3;
        public const int Editor = 2;
        public const int Viewer = 1;
    }

    public async Task<List<BeaconRoleData>> GetRolesAsync(CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        return await context.Roles
            .OrderByDescending(r => r.Level)
            .Select(r => new BeaconRoleData
            {
                Id = r.Id,
                Name = r.Name,
                Description = r.Description,
                IsSystemRole = r.IsSystemRole,
                Level = r.Level
            })
            .ToListAsync(ct);
    }

    public async Task<BeaconRoleData?> GetRoleByNameAsync(string name, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        return await context.Roles
            .Where(r => r.Name == name)
            .Select(r => new BeaconRoleData
            {
                Id = r.Id,
                Name = r.Name,
                Description = r.Description,
                IsSystemRole = r.IsSystemRole,
                Level = r.Level
            })
            .FirstOrDefaultAsync(ct);
    }

    public async Task<BeaconRoleData?> GetRoleByIdAsync(int roleId, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        return await context.Roles
            .Where(r => r.Id == roleId)
            .Select(r => new BeaconRoleData
            {
                Id = r.Id,
                Name = r.Name,
                Description = r.Description,
                IsSystemRole = r.IsSystemRole,
                Level = r.Level
            })
            .FirstOrDefaultAsync(ct);
    }

    public async Task SeedSystemRolesAsync(CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var existingRoles = await context.Roles.ToListAsync(ct);

        var rolesToAdd = new List<BeaconRole>();

        if (!existingRoles.Any(r => r.Name == RoleNames.Admin))
        {
            rolesToAdd.Add(new BeaconRole
            {
                Name = RoleNames.Admin,
                Description = "Full administrative access. Can manage users, roles, and all system settings.",
                IsSystemRole = true,
                Level = RoleLevels.Admin
            });
        }

        if (!existingRoles.Any(r => r.Name == RoleNames.Editor))
        {
            rolesToAdd.Add(new BeaconRole
            {
                Name = RoleNames.Editor,
                Description = "Can create, edit, and delete queries, subscriptions, and data sources.",
                IsSystemRole = true,
                Level = RoleLevels.Editor
            });
        }

        if (!existingRoles.Any(r => r.Name == RoleNames.Viewer))
        {
            rolesToAdd.Add(new BeaconRole
            {
                Name = RoleNames.Viewer,
                Description = "Read-only access to view queries, subscriptions, and notifications.",
                IsSystemRole = true,
                Level = RoleLevels.Viewer
            });
        }

        if (rolesToAdd.Count > 0)
        {
            context.Roles.AddRange(rolesToAdd);
            await context.SaveChangesAsync(ct);
        }
    }
}
