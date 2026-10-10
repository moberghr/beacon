using System.Data;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authentication;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services.Security;

namespace Beacon.Core.Services;

internal class UserManagementService(
    IDbContextFactory<BeaconContext> contextFactory,
    IPasswordHasher passwordHasher,
    IRoleService roleService,
    BeaconConfiguration configuration,
    TimeProvider timeProvider) : IUserManagementService
{
    private const string InvalidCredentials = "Invalid username or password.";

    // A well-formed credential no password matches: verifying against it costs what a real verification costs.
    private static readonly string DummyPasswordHash = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static readonly string DummyPasswordSalt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    // "No super admin has EVER existed": an archived super admin counts, so archiving it never reopens first-run setup,
    // and users created by any other path (SSO, MCP, an administrator) never close it.
    public async Task<bool> IsFirstRunAsync(CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        return !await context.Users
            .IgnoreQueryFilters()
            .Where(x => x.IsSuperAdmin)
            .AnyAsync(ct);
    }

    public async Task<BeaconUserData> CreateSuperAdminAsync(CreateSuperAdminRequest request, CancellationToken ct = default)
    {
        ValidatePassword(request.Password);

        // Seeded on its own connection before the transaction below, so the serializable unit stays
        // read-super-admins-then-insert. Two concurrent first-run requests may both try to seed: the loser's unique
        // violation is harmless, and the Admin role lookup inside the transaction proves the roles exist.
        try
        {
            await roleService.SeedSystemRolesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbUniqueViolation.IsUniqueViolation(ex))
        {
            // A concurrent request seeded the roles first; nothing is lost.
        }

        var (hash, salt) = passwordHasher.HashPassword(request.Password);

        await using var context = await contextFactory.CreateDbContextAsync(ct);

        // Through the execution strategy, so a host that enables retry-on-failure gets a retried unit instead of an
        // "execution strategy does not support user-initiated transactions" error. A retry starts from a clean tracker
        // and re-reads: after a concurrent setup committed, it refuses below.
        var strategy = context.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();

            // Serializable makes "no super admin has ever existed → insert" atomic on both providers without
            // provider-specific SQL: PostgreSQL's SSI aborts one of two concurrent attempts with a serialization
            // failure, and SQL Server's key-range locks make them deadlock so one is chosen as the victim. Either way
            // exactly one first-run insert commits.
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

            var superAdminEverExisted = await context.Users
                .IgnoreQueryFilters()
                .Where(x => x.IsSuperAdmin)
                .AnyAsync(ct);
            if (superAdminEverExisted)
            {
                throw new BeaconException("Super admin already exists. Setup has already been completed.");
            }

            var adminRole = await context.Roles
                .Where(x => x.Name == RoleService.RoleNames.Admin)
                .FirstAsync(ct);

            // EF fixes up the join-table FK on SaveChanges, so the user row and its Admin role assignment commit
            // together (§5.7 — one SaveChangesAsync per unit of work).
            var user = new BeaconUser
            {
                ExternalId = Guid.NewGuid().ToString(),
                UserName = request.UserName,
                Email = request.Email,
                DisplayName = request.DisplayName ?? request.UserName,
                IsInternalUser = true,
                PasswordHash = hash,
                PasswordSalt = salt,
                IsSuperAdmin = true,
                IsEnabled = true,
                UserRoles = new List<BeaconUserRole>
                {
                    new()
                    {
                        RoleId = adminRole.Id,
                        AssignedAt = DateTime.UtcNow,
                    },
                },
            };

            context.Users.Add(user);
            await context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return new BeaconUserData
            {
                Id = user.Id,
                ExternalId = user.ExternalId,
                UserName = user.UserName,
                Email = user.Email,
                DisplayName = user.DisplayName,
                IsInternalUser = user.IsInternalUser,
                IsSuperAdmin = user.IsSuperAdmin,
                IsEnabled = user.IsEnabled,
                CreatedTime = user.CreatedTime,
                Roles = new List<BeaconRoleData>
                {
                    new()
                    {
                        Id = adminRole.Id,
                        Name = adminRole.Name,
                        Description = adminRole.Description,
                        IsSystemRole = adminRole.IsSystemRole,
                        Level = adminRole.Level
                    }
                }
            };
        });
    }

    public async Task<List<BeaconUserData>> GetUsersAsync(string? search = null, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var query = context.Users.AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(u =>
                u.UserName.Contains(search) ||
                (u.Email != null && u.Email.Contains(search)) ||
                (u.DisplayName != null && u.DisplayName.Contains(search)));
        }

        return await query
            .OrderBy(u => u.UserName)
            .Select(u => new BeaconUserData
            {
                Id = u.Id,
                ExternalId = u.ExternalId,
                IdentityProvider = u.IdentityProvider,
                UserName = u.UserName,
                Email = u.Email,
                DisplayName = u.DisplayName,
                IsInternalUser = u.IsInternalUser,
                IsSuperAdmin = u.IsSuperAdmin,
                IsEnabled = u.IsEnabled,
                LastLoginAt = u.LastLoginAt,
                CreatedTime = u.CreatedTime,
                Roles = u.UserRoles.Select(ur => new BeaconRoleData
                {
                    Id = ur.Role.Id,
                    Name = ur.Role.Name,
                    Description = ur.Role.Description,
                    IsSystemRole = ur.Role.IsSystemRole,
                    Level = ur.Role.Level
                }).ToList()
            })
            .ToListAsync(ct);
    }

    public async Task<BeaconUserData?> GetUserByIdAsync(int userId, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        return await context.Users
            .Where(u => u.Id == userId)
            .Select(u => new BeaconUserData
            {
                Id = u.Id,
                ExternalId = u.ExternalId,
                IdentityProvider = u.IdentityProvider,
                UserName = u.UserName,
                Email = u.Email,
                DisplayName = u.DisplayName,
                IsInternalUser = u.IsInternalUser,
                IsSuperAdmin = u.IsSuperAdmin,
                IsEnabled = u.IsEnabled,
                LastLoginAt = u.LastLoginAt,
                CreatedTime = u.CreatedTime,
                Roles = u.UserRoles.Select(ur => new BeaconRoleData
                {
                    Id = ur.Role.Id,
                    Name = ur.Role.Name,
                    Description = ur.Role.Description,
                    IsSystemRole = ur.Role.IsSystemRole,
                    Level = ur.Role.Level
                }).ToList()
            })
            .FirstOrDefaultAsync(ct);
    }

    public async Task<BeaconUserData?> GetUserByExternalIdAsync(string externalId, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        return await context.Users
            .Where(u => u.ExternalId == externalId)
            .Select(u => new BeaconUserData
            {
                Id = u.Id,
                ExternalId = u.ExternalId,
                IdentityProvider = u.IdentityProvider,
                UserName = u.UserName,
                Email = u.Email,
                DisplayName = u.DisplayName,
                IsInternalUser = u.IsInternalUser,
                IsSuperAdmin = u.IsSuperAdmin,
                IsEnabled = u.IsEnabled,
                LastLoginAt = u.LastLoginAt,
                CreatedTime = u.CreatedTime,
                Roles = u.UserRoles.Select(ur => new BeaconRoleData
                {
                    Id = ur.Role.Id,
                    Name = ur.Role.Name,
                    Description = ur.Role.Description,
                    IsSystemRole = ur.Role.IsSystemRole,
                    Level = ur.Role.Level
                }).ToList()
            })
            .FirstOrDefaultAsync(ct);
    }

    public async Task<BeaconUserData?> GetUserByExternalIdAndProviderAsync(string externalId, string? identityProvider, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        return await context.Users
            .Where(x => x.ExternalId == externalId)
            .Where(x => x.IdentityProvider == identityProvider)
            .Select(x =>
                new BeaconUserData
                {
                    Id = x.Id,
                    ExternalId = x.ExternalId,
                    IdentityProvider = x.IdentityProvider,
                    UserName = x.UserName,
                    Email = x.Email,
                    DisplayName = x.DisplayName,
                    IsInternalUser = x.IsInternalUser,
                    IsSuperAdmin = x.IsSuperAdmin,
                    IsEnabled = x.IsEnabled,
                    LastLoginAt = x.LastLoginAt,
                    CreatedTime = x.CreatedTime,
                    Roles = x.UserRoles.Select(y =>
                        new BeaconRoleData
                        {
                            Id = y.Role.Id,
                            Name = y.Role.Name,
                            Description = y.Role.Description,
                            IsSystemRole = y.Role.IsSystemRole,
                            Level = y.Role.Level
                        }).ToList()
                })
            .FirstOrDefaultAsync(ct);
    }

    public async Task<List<BearerUserCandidate>> GetBearerUserCandidatesAsync(
        string externalId,
        string identityProvider,
        bool includeWithoutIdentityProvider,
        CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        // Archived rows included (flagged): the binding refuses an archived match instead of overlooking it.
        return await context.Users
            .IgnoreQueryFilters()
            .Where(x => !x.IsInternalUser)
            .Where(x => !x.IsSuperAdmin)
            .Where(x => x.ExternalId == externalId)
            .Where(x => x.IdentityProvider == identityProvider
                || (includeWithoutIdentityProvider && x.IdentityProvider == null))
            .OrderBy(x => x.Id)
            .Select(x =>
                new BearerUserCandidate
                {
                    IsArchived = x.ArchivedTime != null,
                    User = new BeaconUserData
                    {
                        Id = x.Id,
                        ExternalId = x.ExternalId,
                        IdentityProvider = x.IdentityProvider,
                        UserName = x.UserName,
                        Email = x.Email,
                        DisplayName = x.DisplayName,
                        IsInternalUser = x.IsInternalUser,
                        IsSuperAdmin = x.IsSuperAdmin,
                        IsEnabled = x.IsEnabled,
                        LastLoginAt = x.LastLoginAt,
                        CreatedTime = x.CreatedTime,
                        Roles = x.UserRoles.Select(y =>
                            new BeaconRoleData
                            {
                                Id = y.Role.Id,
                                Name = y.Role.Name,
                                Description = y.Role.Description,
                                IsSystemRole = y.Role.IsSystemRole,
                                Level = y.Role.Level
                            }).ToList()
                    }
                })
            .ToListAsync(ct);
    }

    public async Task<BeaconUserData?> GetUserByUserNameAsync(string userName, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        return await context.Users
            .Where(u => u.UserName == userName)
            .Select(u => new BeaconUserData
            {
                Id = u.Id,
                ExternalId = u.ExternalId,
                IdentityProvider = u.IdentityProvider,
                UserName = u.UserName,
                Email = u.Email,
                DisplayName = u.DisplayName,
                IsInternalUser = u.IsInternalUser,
                IsSuperAdmin = u.IsSuperAdmin,
                IsEnabled = u.IsEnabled,
                LastLoginAt = u.LastLoginAt,
                CreatedTime = u.CreatedTime,
                Roles = u.UserRoles.Select(ur => new BeaconRoleData
                {
                    Id = ur.Role.Id,
                    Name = ur.Role.Name,
                    Description = ur.Role.Description,
                    IsSystemRole = ur.Role.IsSystemRole,
                    Level = ur.Role.Level
                }).ToList()
            })
            .FirstOrDefaultAsync(ct);
    }

    public async Task<BaseResponse> CreateInternalUserAsync(CreateInternalUserRequest request, CancellationToken ct = default)
    {
        if (!configuration.UserManagement.AllowInternalUsers)
        {
            return new BaseResponse { Success = false, Message = "Internal user creation is disabled." };
        }

        await using var context = await contextFactory.CreateDbContextAsync(ct);

        // Check for duplicate username
        if (await context.Users.AnyAsync(u => u.UserName == request.UserName, ct))
        {
            return new BaseResponse { Success = false, Message = "A user with this username already exists." };
        }

        // Validate password
        try
        {
            ValidatePassword(request.Password);
        }
        catch (BeaconException ex)
        {
            return new BaseResponse { Success = false, Message = ex.Message };
        }

        var (hash, salt) = passwordHasher.HashPassword(request.Password);

        // Filter the requested roles to those that actually exist before staging,
        // so the user + UserRoles commit together in a single SaveChanges (§5.7).
        var validRoleIds = await context.Roles
            .Where(r => request.RoleIds.Contains(r.Id))
            .Select(r => r.Id)
            .ToListAsync(ct);

        var user = new BeaconUser
        {
            ExternalId = Guid.NewGuid().ToString(),
            UserName = request.UserName,
            Email = request.Email,
            DisplayName = request.DisplayName ?? request.UserName,
            IsInternalUser = true,
            PasswordHash = hash,
            PasswordSalt = salt,
            IsSuperAdmin = false,
            IsEnabled = true,
            UserRoles = validRoleIds
                .Select(roleId => new BeaconUserRole
                {
                    RoleId = roleId,
                    AssignedAt = DateTime.UtcNow,
                })
                .ToList(),
        };

        context.Users.Add(user);
        await context.SaveChangesAsync(ct);

        return new BaseResponse { Success = true, Message = "User created successfully." };
    }

    public async Task<BeaconUserData> GetOrCreateExternalUserAsync(
        string externalId,
        string identityProvider,
        string userName,
        string? email,
        string? displayName,
        string? defaultRoleName,
        CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        // Archived rows included: an archived subject is refused below instead of being provisioned again.
        var existingData = await context.Users
            .IgnoreQueryFilters()
            .Where(x => x.ExternalId == externalId)
            .Where(x => x.IdentityProvider == identityProvider)
            .Select(x =>
                new
                {
                    x.Id,
                    x.ExternalId,
                    x.IdentityProvider,
                    x.UserName,
                    x.Email,
                    x.DisplayName,
                    x.IsInternalUser,
                    x.IsSuperAdmin,
                    x.IsEnabled,
                    x.ArchivedTime,
                    x.CreatedTime,
                    Roles = x.UserRoles.Select(y =>
                        new BeaconRoleData
                        {
                            Id = y.Role.Id,
                            Name = y.Role.Name,
                            Description = y.Role.Description,
                            IsSystemRole = y.Role.IsSystemRole,
                            Level = y.Role.Level
                        }).ToList()
                })
            .FirstOrDefaultAsync(ct);

        if (existingData != null)
        {
            if (existingData.ArchivedTime.HasValue)
            {
                throw new BeaconException("This account has been archived.");
            }

            if (!existingData.IsEnabled)
            {
                throw new BeaconException("This account has been disabled.");
            }

            var now = DateTime.UtcNow;
            await context.Users
                .Where(x => x.Id == existingData.Id)
                .ExecuteUpdateAsync(x => x.SetProperty(u => u.LastLoginAt, now), ct);

            return new BeaconUserData
            {
                Id = existingData.Id,
                ExternalId = existingData.ExternalId,
                IdentityProvider = existingData.IdentityProvider,
                UserName = existingData.UserName,
                Email = existingData.Email,
                DisplayName = existingData.DisplayName,
                IsInternalUser = existingData.IsInternalUser,
                IsSuperAdmin = existingData.IsSuperAdmin,
                IsEnabled = existingData.IsEnabled,
                LastLoginAt = now,
                CreatedTime = existingData.CreatedTime,
                Roles = existingData.Roles
            };
        }

        // Nobody is provisioned before first-run setup: the first user of an installation is always the super admin
        // created with the setup token, never whoever reaches SSO or MCP first.
        var setupCompleted = await context.Users
            .IgnoreQueryFilters()
            .Where(x => x.IsSuperAdmin)
            .AnyAsync(ct);
        if (!setupCompleted)
        {
            throw new BeaconException(
                "First-run setup has not been completed: users are provisioned only after the super admin exists.");
        }

        await roleService.SeedSystemRolesAsync(ct);

        // No default role configured: the user is provisioned with no role and has no permissions until an admin
        // assigns one.
        BeaconRole? defaultRole = null;
        if (!string.IsNullOrWhiteSpace(defaultRoleName))
        {
            defaultRole = await context.Roles
                .Where(x => x.Name == defaultRoleName)
                .FirstOrDefaultAsync(ct)
                ?? throw new BeaconException($"Default role '{defaultRoleName}' does not exist.");
        }

        // Stage the default role on the navigation collection so the user and
        // the UserRole commit together in a single SaveChanges (§5.7).
        var newUser = new BeaconUser
        {
            ExternalId = externalId,
            IdentityProvider = identityProvider,
            UserName = userName,
            Email = email,
            DisplayName = displayName ?? userName,
            IsInternalUser = false,
            IsSuperAdmin = false,
            IsEnabled = true,
            LastLoginAt = DateTime.UtcNow,
            UserRoles = defaultRole == null
                ? new List<BeaconUserRole>()
                : new List<BeaconUserRole>
                {
                    new()
                    {
                        RoleId = defaultRole.Id,
                        AssignedAt = DateTime.UtcNow,
                    }
                },
        };

        context.Users.Add(newUser);
        await context.SaveChangesAsync(ct);

        return new BeaconUserData
        {
            Id = newUser.Id,
            ExternalId = newUser.ExternalId,
            IdentityProvider = newUser.IdentityProvider,
            UserName = newUser.UserName,
            Email = newUser.Email,
            DisplayName = newUser.DisplayName,
            IsInternalUser = newUser.IsInternalUser,
            IsSuperAdmin = newUser.IsSuperAdmin,
            IsEnabled = newUser.IsEnabled,
            LastLoginAt = newUser.LastLoginAt,
            CreatedTime = newUser.CreatedTime,
            Roles = defaultRole == null
                ? new List<BeaconRoleData>()
                : new List<BeaconRoleData>
                {
                    new()
                    {
                        Id = defaultRole.Id,
                        Name = defaultRole.Name,
                        Description = defaultRole.Description,
                        IsSystemRole = defaultRole.IsSystemRole,
                        Level = defaultRole.Level
                    }
                }
        };
    }

    public async Task<BaseResponse> CreateExternalUserAsync(CreateExternalUserRequest request, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        // Check for duplicate external ID
        if (await context.Users.AnyAsync(u => u.ExternalId == request.ExternalId, ct))
        {
            return new BaseResponse { Success = false, Message = "A user with this external ID already exists." };
        }

        // Check for duplicate username
        if (await context.Users.AnyAsync(u => u.UserName == request.UserName, ct))
        {
            return new BaseResponse { Success = false, Message = "A user with this username already exists." };
        }

        // Filter the requested roles to those that actually exist before staging,
        // so the user + UserRoles commit together in a single SaveChanges (§5.7).
        var validRoleIds = await context.Roles
            .Where(r => request.RoleIds.Contains(r.Id))
            .Select(r => r.Id)
            .ToListAsync(ct);

        var user = new BeaconUser
        {
            ExternalId = request.ExternalId,
            UserName = request.UserName,
            Email = request.Email,
            DisplayName = request.DisplayName ?? request.UserName,
            IsInternalUser = false,
            IsSuperAdmin = false,
            IsEnabled = true,
            UserRoles = validRoleIds
                .Select(roleId => new BeaconUserRole
                {
                    RoleId = roleId,
                    AssignedAt = DateTime.UtcNow,
                })
                .ToList(),
        };

        context.Users.Add(user);
        await context.SaveChangesAsync(ct);

        return new BaseResponse { Success = true, Message = "External user pre-registered successfully." };
    }

    public async Task<BaseResponse> UpdateUserAsync(UpdateUserRequest request, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var user = await context.Users.FindAsync(new object[] { request.UserId }, ct);
        if (user == null)
        {
            return new BaseResponse { Success = false, Message = "User not found." };
        }

        // Check for duplicate username (if changed)
        if (user.UserName != request.UserName &&
            await context.Users.AnyAsync(u => u.UserName == request.UserName && u.Id != request.UserId, ct))
        {
            return new BaseResponse { Success = false, Message = "A user with this username already exists." };
        }

        var wasEnabled = user.IsEnabled;

        user.UserName = request.UserName;
        user.Email = request.Email;
        user.DisplayName = request.DisplayName;
        user.IsEnabled = request.IsEnabled;

        if (!wasEnabled || !user.IsEnabled)
        {
            await RevokeApiKeysAsync(context, user.Id, ct);
        }

        await context.SaveChangesAsync(ct);

        return new BaseResponse { Success = true, Message = "User updated successfully." };
    }

    public async Task<BaseResponse> DeleteUserAsync(int userId, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var user = await context.Users.FindAsync(new object[] { userId }, ct);
        if (user == null)
        {
            return new BaseResponse { Success = false, Message = "User not found." };
        }

        if (user.IsSuperAdmin)
        {
            // Check if this is the last super admin
            var superAdminCount = await context.Users.CountAsync(u => u.IsSuperAdmin && u.ArchivedTime == null, ct);
            if (superAdminCount <= 1)
            {
                return new BaseResponse { Success = false, Message = "Cannot delete the last super admin." };
            }
        }

        user.Archive();
        await RevokeApiKeysAsync(context, user.Id, ct);
        await context.SaveChangesAsync(ct);

        return new BaseResponse { Success = true, Message = "User deleted successfully." };
    }

    public async Task<BaseResponse> ToggleUserEnabledAsync(int userId, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var user = await context.Users.FindAsync(new object[] { userId }, ct);
        if (user == null)
        {
            return new BaseResponse { Success = false, Message = "User not found." };
        }

        if (user.IsSuperAdmin && user.IsEnabled)
        {
            // Check if this is the last enabled super admin
            var enabledSuperAdminCount = await context.Users.CountAsync(u => u.IsSuperAdmin && u.IsEnabled && u.ArchivedTime == null, ct);
            if (enabledSuperAdminCount <= 1)
            {
                return new BaseResponse { Success = false, Message = "Cannot disable the last super admin." };
            }
        }

        // Either transition revokes: disabling, and re-enabling too (see RevokeApiKeysAsync).
        user.IsEnabled = !user.IsEnabled;
        await RevokeApiKeysAsync(context, user.Id, ct);

        await context.SaveChangesAsync(ct);

        return new BaseResponse
        {
            Success = true,
            Message = user.IsEnabled ? "User enabled successfully." : "User disabled successfully."
        };
    }

    public async Task<BaseResponse> AssignRoleAsync(int userId, int roleId, string? assignedBy, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var user = await context.Users.FindAsync(new object[] { userId }, ct);
        if (user == null)
        {
            return new BaseResponse { Success = false, Message = "User not found." };
        }

        var role = await context.Roles.FindAsync(new object[] { roleId }, ct);
        if (role == null)
        {
            return new BaseResponse { Success = false, Message = "Role not found." };
        }

        // Check if already assigned
        if (await context.UserRoles.AnyAsync(ur => ur.UserId == userId && ur.RoleId == roleId, ct))
        {
            return new BaseResponse { Success = false, Message = "User already has this role." };
        }

        context.UserRoles.Add(new BeaconUserRole
        {
            UserId = userId,
            RoleId = roleId,
            AssignedByUserId = assignedBy,
            AssignedAt = DateTime.UtcNow
        });

        await context.SaveChangesAsync(ct);

        return new BaseResponse { Success = true, Message = $"Role '{role.Name}' assigned successfully." };
    }

    public async Task<BaseResponse> RemoveRoleAsync(int userId, int roleId, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var userRole = await context.UserRoles
            .FirstOrDefaultAsync(ur => ur.UserId == userId && ur.RoleId == roleId, ct);

        if (userRole == null)
        {
            return new BaseResponse { Success = false, Message = "User does not have this role." };
        }

        context.UserRoles.Remove(userRole);
        await context.SaveChangesAsync(ct);

        return new BaseResponse { Success = true, Message = "Role removed successfully." };
    }

    public async Task<BaseResponse> ChangePasswordAsync(int userId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var user = await context.Users.FindAsync(new object[] { userId }, ct);
        if (user == null)
        {
            return new BaseResponse { Success = false, Message = "User not found." };
        }

        if (!user.IsInternalUser)
        {
            return new BaseResponse { Success = false, Message = "Cannot change password for external users." };
        }

        if (!passwordHasher.VerifyPassword(currentPassword, user.PasswordHash!, user.PasswordSalt!))
        {
            return new BaseResponse { Success = false, Message = "Current password is incorrect." };
        }

        try
        {
            ValidatePassword(newPassword);
        }
        catch (BeaconException ex)
        {
            return new BaseResponse { Success = false, Message = ex.Message };
        }

        var (hash, salt) = passwordHasher.HashPassword(newPassword);
        user.PasswordHash = hash;
        user.PasswordSalt = salt;

        await context.SaveChangesAsync(ct);

        return new BaseResponse { Success = true, Message = "Password changed successfully." };
    }

    public async Task<BaseResponse> ResetPasswordAsync(int userId, string newPassword, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var user = await context.Users.FindAsync(new object[] { userId }, ct);
        if (user == null)
        {
            return new BaseResponse { Success = false, Message = "User not found." };
        }

        if (!user.IsInternalUser)
        {
            return new BaseResponse { Success = false, Message = "Cannot reset password for external users." };
        }

        try
        {
            ValidatePassword(newPassword);
        }
        catch (BeaconException ex)
        {
            return new BaseResponse { Success = false, Message = ex.Message };
        }

        var (hash, salt) = passwordHasher.HashPassword(newPassword);
        user.PasswordHash = hash;
        user.PasswordSalt = salt;

        await context.SaveChangesAsync(ct);

        return new BaseResponse { Success = true, Message = "Password reset successfully." };
    }

    public async Task<AuthenticationResult> AuthenticateInternalUserAsync(string username, string password, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        // Internal users only, in a deterministic order: an exact user name (unique among active users) wins over an
        // e-mail match, and among several e-mail matches the oldest account wins.
        var user = await context.Users
            .Include(x => x.UserRoles)
            .ThenInclude(x => x.Role)
            .Where(x => x.IsInternalUser)
            .Where(x => x.UserName == username || x.Email == username)
            .OrderBy(x => x.UserName == username ? 0 : 1)
            .ThenBy(x => x.Id)
            .FirstOrDefaultAsync(ct);

        // The password is verified BEFORE the account state is looked at, and against a dummy credential when there is
        // no account, so neither the answer nor its timing tells an unknown or disabled account from a wrong password.
        var passwordMatches = user is { PasswordHash: not null, PasswordSalt: not null }
            ? passwordHasher.VerifyPassword(password, user.PasswordHash, user.PasswordSalt)
            : VerifyAgainstDummyCredential(password);

        if (user == null || !passwordMatches || !user.IsEnabled || user.ArchivedTime.HasValue)
        {
            return AuthenticationResult.Failed(InvalidCredentials);
        }

        user.LastLoginAt = DateTime.UtcNow;
        await context.SaveChangesAsync(ct);

        return AuthenticationResult.Succeeded(new AuthenticatedUser
        {
            UserId = user.ExternalId,
            UserName = user.UserName,
            Email = user.Email,
            DisplayName = user.DisplayName,
            Roles = user.UserRoles
                .Select(x => x.Role.Name)
                .ToList()
        });
    }

    public async Task UpdateLastLoginAsync(int userId, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var user = await context.Users
            .Where(x => x.Id == userId)
            .FirstOrDefaultAsync(ct);
        if (user != null)
        {
            user.LastLoginAt = DateTime.UtcNow;
            await context.SaveChangesAsync(ct);
        }
    }

    private bool VerifyAgainstDummyCredential(string password)
    {
        passwordHasher.VerifyPassword(password, DummyPasswordHash, DummyPasswordSalt);

        return false;
    }

    private void ValidatePassword(string password)
    {
        if (password.Length < configuration.UserManagement.MinimumPasswordLength)
        {
            throw new BeaconException($"Password must be at least {configuration.UserManagement.MinimumPasswordLength} characters long.");
        }

        if (configuration.UserManagement.RequirePasswordComplexity)
        {
            var hasUppercase = password.Any(char.IsUpper);
            var hasLowercase = password.Any(char.IsLower);
            var hasDigit = password.Any(char.IsDigit);
            var hasSpecial = password.Any(c => !char.IsLetterOrDigit(c));

            if (!hasUppercase || !hasLowercase || !hasDigit || !hasSpecial)
            {
                throw new BeaconException("Password must contain at least one uppercase letter, one lowercase letter, one digit, and one special character.");
            }
        }
    }

    // A disabled or archived user has no working API keys, and re-enabling them brings none back. Their keys are
    // revoked in the same unit of work as the change (one SaveChangesAsync, so one transaction) whenever the user is
    // saved disabled or archived — again on every such save, so it is idempotent — and on the re-enable itself, which
    // also catches a key issued while the user was being disabled and a key of a user disabled before this rule
    // existed. Already revoked keys keep their first revocation time.
    private async Task RevokeApiKeysAsync(BeaconContext context, int userId, CancellationToken ct)
    {
        var keys = await context.ApiKeyCredentials
            .Where(x => x.UserId == userId)
            .Where(x => !x.IsRevoked)
            .ToListAsync(ct);

        var revokedAt = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var key in keys)
        {
            key.IsRevoked = true;
            key.RevokedAt = revokedAt;
        }
    }
}
