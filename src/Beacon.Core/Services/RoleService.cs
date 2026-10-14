using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Models.UserManagement;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Beacon.Core.Services;

internal class RoleService(
    IDbContextFactory<BeaconContext> contextFactory,
    IOptions<RoleMappingOptions> roleMapping,
    ILogger<RoleService> logger) : IRoleService
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

        /// <summary>A functional role: it grants its permission, and neither read nor write access to project data.</summary>
        public const int Functional = 0;
    }

    private static readonly IReadOnlyDictionary<string, string> FunctionalRoleDescriptions = new Dictionary<string, string>
    {
        [BeaconPermissions.Approver] = "Approves and rejects query changes. Grants no access to project data.",
        [BeaconPermissions.Auditor] = "Reads the MCP audit, the MCP and admin settings and every user's API keys. Grants no access to project data.",
        [BeaconPermissions.SecurityAdmin] = "Changes the MCP and admin settings (not the LLM connection), recipients, MCP tool publishing and repository tokens. Grants no access to project data.",
        [BeaconPermissions.UserAdmin] = "Manages users, their API keys, resource owners and project members, except Admins and functional roles. Grants no access to project data.",
        [BeaconPermissions.DataSourceAdmin] = "Creates, tests and deletes data sources and links any data source into a project. Grants no access to project data.",
        [BeaconPermissions.MigrationOperator] = "Runs data migration between data sources the user may run and write. Grants no access to project data."
    };

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

    /// <summary>
    /// Adds the built-in roles and the functional roles (under the names the permissions are mapped to,
    /// <c>Beacon:Authorization:Roles</c>) that are missing. Names are compared ignoring case. A role that already has a
    /// functional role's name but is not one (created by someone, or at another level) is left as it is and grants
    /// nothing; that is logged with its id. Each role is added in its own save, so one lost to a concurrent seeding (a
    /// unique name) does not keep the others out; a role still missing after that is logged.
    /// </summary>
    public async Task SeedSystemRolesAsync(CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var existingRoles = await context.Roles
            .Select(x =>
                new ExistingRole(x.Id, x.Name, x.IsSystemRole, x.Level))
            .ToListAsync(ct);

        var builtIn = new[]
        {
            new BeaconRole
            {
                Name = RoleNames.Admin,
                Description = "Full administrative access. Can manage users, roles, and all system settings.",
                IsSystemRole = true,
                Level = RoleLevels.Admin
            },
            new BeaconRole
            {
                Name = RoleNames.Editor,
                Description = "Can create, edit, and delete queries, subscriptions, and data sources.",
                IsSystemRole = true,
                Level = RoleLevels.Editor
            },
            new BeaconRole
            {
                Name = RoleNames.Viewer,
                Description = "Read-only access to view queries, subscriptions, and notifications.",
                IsSystemRole = true,
                Level = RoleLevels.Viewer
            }
        };
        foreach (var role in builtIn)
        {
            if (Find(existingRoles, role.Name) == null)
            {
                await AddAsync(role, null, ct);
            }
        }

        foreach (var permission in BeaconPermissions.All)
        {
            var name = roleMapping.Value.RoleNameFor(permission);
            var existing = Find(existingRoles, name);
            if (existing == null)
            {
                await AddAsync(
                    new BeaconRole
                    {
                        Name = name,
                        Description = FunctionalRoleDescriptions[permission],
                        IsSystemRole = true,
                        Level = RoleLevels.Functional
                    },
                    permission,
                    ct);
                continue;
            }

            if (!existing.IsSystemRole || existing.Level != RoleLevels.Functional)
            {
                logger.LogWarning(
                    "Role {RoleId} has the name of the {Permission} role but is not a functional role, so it grants nothing; rename it, or map the permission to another role name (Beacon:Authorization:Roles)",
                    existing.Id,
                    permission);
            }
        }
    }

    private static ExistingRole? Find(List<ExistingRole> roles, string name)
    {
        return roles
            .Where(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    // In its own context and save: a unique violation (a concurrent seeding, or a collation that compares names ignoring
    // case) loses only this role; whether it exists after all is read again.
    private async Task AddAsync(BeaconRole role, string? permission, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        context.Roles.Add(role);
        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbUniqueViolation.IsUniqueViolation(ex))
        {
            await using var reread = await contextFactory.CreateDbContextAsync(ct);
            var names = await reread.Roles
                .Select(x => x.Name)
                .ToListAsync(ct);
            if (!names.Any(x => string.Equals(x, role.Name, StringComparison.OrdinalIgnoreCase)))
            {
                logger.LogWarning(
                    "The {Role} role could not be added and is still missing",
                    permission ?? role.Name);
            }
        }
    }

    private sealed record ExistingRole(int Id, string Name, bool IsSystemRole, int Level);
}
