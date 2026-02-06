using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities;
using Semantico.Core.Models.UserManagement;

namespace Semantico.Core.Services;

internal class RoleService(IDbContextFactory<SemanticoContext> contextFactory) : IRoleService
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

    public async Task<List<SemanticoRoleData>> GetRolesAsync(CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        return await context.Roles
            .OrderByDescending(r => r.Level)
            .Select(r => new SemanticoRoleData
            {
                Id = r.Id,
                Name = r.Name,
                Description = r.Description,
                IsSystemRole = r.IsSystemRole,
                Level = r.Level
            })
            .ToListAsync(ct);
    }

    public async Task<SemanticoRoleData?> GetRoleByNameAsync(string name, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        return await context.Roles
            .Where(r => r.Name == name)
            .Select(r => new SemanticoRoleData
            {
                Id = r.Id,
                Name = r.Name,
                Description = r.Description,
                IsSystemRole = r.IsSystemRole,
                Level = r.Level
            })
            .FirstOrDefaultAsync(ct);
    }

    public async Task<SemanticoRoleData?> GetRoleByIdAsync(int roleId, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        return await context.Roles
            .Where(r => r.Id == roleId)
            .Select(r => new SemanticoRoleData
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

        var rolesToAdd = new List<SemanticoRole>();

        if (!existingRoles.Any(r => r.Name == RoleNames.Admin))
        {
            rolesToAdd.Add(new SemanticoRole
            {
                Name = RoleNames.Admin,
                Description = "Full administrative access. Can manage users, roles, and all system settings.",
                IsSystemRole = true,
                Level = RoleLevels.Admin
            });
        }

        if (!existingRoles.Any(r => r.Name == RoleNames.Editor))
        {
            rolesToAdd.Add(new SemanticoRole
            {
                Name = RoleNames.Editor,
                Description = "Can create, edit, and delete queries, subscriptions, and data sources.",
                IsSystemRole = true,
                Level = RoleLevels.Editor
            });
        }

        if (!existingRoles.Any(r => r.Name == RoleNames.Viewer))
        {
            rolesToAdd.Add(new SemanticoRole
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
