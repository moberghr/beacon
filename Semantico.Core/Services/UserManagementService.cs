using Microsoft.EntityFrameworkCore;
using Semantico.Core.Authentication;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities;
using Semantico.Core.Helpers;
using Semantico.Core.Models;
using Semantico.Core.Models.UserManagement;
using Semantico.Core.Services.Security;

namespace Semantico.Core.Services;

internal class UserManagementService(
    IDbContextFactory<SemanticoContext> contextFactory,
    IPasswordHasher passwordHasher,
    IRoleService roleService,
    SemanticoConfiguration configuration) : IUserManagementService
{
    public async Task<bool> IsFirstRunAsync(CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        return !await context.Users.AnyAsync(ct);
    }

    public async Task<SemanticoUserData> CreateSuperAdminAsync(CreateSuperAdminRequest request, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        // Ensure this is actually the first run
        if (await context.Users.AnyAsync(ct))
        {
            throw new SemanticoException("Super admin already exists. Setup has already been completed.");
        }

        // Ensure system roles exist
        await roleService.SeedSystemRolesAsync(ct);

        // Get the Admin role
        var adminRole = await context.Roles.FirstAsync(r => r.Name == RoleService.RoleNames.Admin, ct);

        // Validate password
        ValidatePassword(request.Password);

        // Hash the password
        var (hash, salt) = passwordHasher.HashPassword(request.Password);

        var user = new SemanticoUser
        {
            ExternalId = Guid.NewGuid().ToString(),
            UserName = request.UserName,
            Email = request.Email,
            DisplayName = request.DisplayName ?? request.UserName,
            IsInternalUser = true,
            PasswordHash = hash,
            PasswordSalt = salt,
            IsSuperAdmin = true,
            IsEnabled = true
        };

        context.Users.Add(user);
        await context.SaveChangesAsync(ct);

        // Assign Admin role
        var userRole = new SemanticoUserRole
        {
            UserId = user.Id,
            RoleId = adminRole.Id,
            AssignedAt = DateTime.UtcNow
        };

        context.UserRoles.Add(userRole);
        await context.SaveChangesAsync(ct);

        return new SemanticoUserData
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
            Roles = new List<SemanticoRoleData>
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
    }

    public async Task<List<SemanticoUserData>> GetUsersAsync(string? search = null, CancellationToken ct = default)
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
            .Select(u => new SemanticoUserData
            {
                Id = u.Id,
                ExternalId = u.ExternalId,
                UserName = u.UserName,
                Email = u.Email,
                DisplayName = u.DisplayName,
                IsInternalUser = u.IsInternalUser,
                IsSuperAdmin = u.IsSuperAdmin,
                IsEnabled = u.IsEnabled,
                LastLoginAt = u.LastLoginAt,
                CreatedTime = u.CreatedTime,
                Roles = u.UserRoles.Select(ur => new SemanticoRoleData
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

    public async Task<SemanticoUserData?> GetUserByIdAsync(int userId, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        return await context.Users
            .Where(u => u.Id == userId)
            .Select(u => new SemanticoUserData
            {
                Id = u.Id,
                ExternalId = u.ExternalId,
                UserName = u.UserName,
                Email = u.Email,
                DisplayName = u.DisplayName,
                IsInternalUser = u.IsInternalUser,
                IsSuperAdmin = u.IsSuperAdmin,
                IsEnabled = u.IsEnabled,
                LastLoginAt = u.LastLoginAt,
                CreatedTime = u.CreatedTime,
                Roles = u.UserRoles.Select(ur => new SemanticoRoleData
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

    public async Task<SemanticoUserData?> GetUserByExternalIdAsync(string externalId, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        return await context.Users
            .Where(u => u.ExternalId == externalId)
            .Select(u => new SemanticoUserData
            {
                Id = u.Id,
                ExternalId = u.ExternalId,
                UserName = u.UserName,
                Email = u.Email,
                DisplayName = u.DisplayName,
                IsInternalUser = u.IsInternalUser,
                IsSuperAdmin = u.IsSuperAdmin,
                IsEnabled = u.IsEnabled,
                LastLoginAt = u.LastLoginAt,
                CreatedTime = u.CreatedTime,
                Roles = u.UserRoles.Select(ur => new SemanticoRoleData
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

    public async Task<SemanticoUserData?> GetUserByUserNameAsync(string userName, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        return await context.Users
            .Where(u => u.UserName == userName)
            .Select(u => new SemanticoUserData
            {
                Id = u.Id,
                ExternalId = u.ExternalId,
                UserName = u.UserName,
                Email = u.Email,
                DisplayName = u.DisplayName,
                IsInternalUser = u.IsInternalUser,
                IsSuperAdmin = u.IsSuperAdmin,
                IsEnabled = u.IsEnabled,
                LastLoginAt = u.LastLoginAt,
                CreatedTime = u.CreatedTime,
                Roles = u.UserRoles.Select(ur => new SemanticoRoleData
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
        catch (SemanticoException ex)
        {
            return new BaseResponse { Success = false, Message = ex.Message };
        }

        var (hash, salt) = passwordHasher.HashPassword(request.Password);

        var user = new SemanticoUser
        {
            ExternalId = Guid.NewGuid().ToString(),
            UserName = request.UserName,
            Email = request.Email,
            DisplayName = request.DisplayName ?? request.UserName,
            IsInternalUser = true,
            PasswordHash = hash,
            PasswordSalt = salt,
            IsSuperAdmin = false,
            IsEnabled = true
        };

        context.Users.Add(user);
        await context.SaveChangesAsync(ct);

        // Assign roles
        foreach (var roleId in request.RoleIds)
        {
            var role = await context.Roles.FindAsync(new object[] { roleId }, ct);
            if (role != null)
            {
                context.UserRoles.Add(new SemanticoUserRole
                {
                    UserId = user.Id,
                    RoleId = roleId,
                    AssignedAt = DateTime.UtcNow
                });
            }
        }

        await context.SaveChangesAsync(ct);

        return new BaseResponse { Success = true, Message = "User created successfully." };
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

        var user = new SemanticoUser
        {
            ExternalId = request.ExternalId,
            UserName = request.UserName,
            Email = request.Email,
            DisplayName = request.DisplayName ?? request.UserName,
            IsInternalUser = false,
            IsSuperAdmin = false,
            IsEnabled = true
        };

        context.Users.Add(user);
        await context.SaveChangesAsync(ct);

        // Assign roles
        foreach (var roleId in request.RoleIds)
        {
            var role = await context.Roles.FindAsync(new object[] { roleId }, ct);
            if (role != null)
            {
                context.UserRoles.Add(new SemanticoUserRole
                {
                    UserId = user.Id,
                    RoleId = roleId,
                    AssignedAt = DateTime.UtcNow
                });
            }
        }

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

        user.UserName = request.UserName;
        user.Email = request.Email;
        user.DisplayName = request.DisplayName;
        user.IsEnabled = request.IsEnabled;

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

        user.IsEnabled = !user.IsEnabled;
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

        context.UserRoles.Add(new SemanticoUserRole
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
        catch (SemanticoException ex)
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
        catch (SemanticoException ex)
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

        var user = await context.Users
            .Include(u => u.UserRoles)
            .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.UserName == username || u.Email == username, ct);

        if (user == null)
        {
            return AuthenticationResult.Failed("Invalid username or password.");
        }

        if (!user.IsInternalUser)
        {
            return AuthenticationResult.Failed("Invalid username or password.");
        }

        if (!user.IsEnabled)
        {
            return AuthenticationResult.Failed("This account has been disabled.");
        }

        if (user.ArchivedTime.HasValue)
        {
            return AuthenticationResult.Failed("Invalid username or password.");
        }

        if (!passwordHasher.VerifyPassword(password, user.PasswordHash!, user.PasswordSalt!))
        {
            return AuthenticationResult.Failed("Invalid username or password.");
        }

        // Update last login
        user.LastLoginAt = DateTime.UtcNow;
        await context.SaveChangesAsync(ct);

        var roles = user.UserRoles.Select(ur => ur.Role.Name).ToList();

        return AuthenticationResult.Succeeded(new AuthenticatedUser
        {
            UserId = user.ExternalId,
            UserName = user.UserName,
            Email = user.Email,
            DisplayName = user.DisplayName,
            Roles = roles
        });
    }

    public async Task UpdateLastLoginAsync(string externalId, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var user = await context.Users.FirstOrDefaultAsync(u => u.ExternalId == externalId, ct);
        if (user != null)
        {
            user.LastLoginAt = DateTime.UtcNow;
            await context.SaveChangesAsync(ct);
        }
    }

    private void ValidatePassword(string password)
    {
        if (password.Length < configuration.UserManagement.MinimumPasswordLength)
        {
            throw new SemanticoException($"Password must be at least {configuration.UserManagement.MinimumPasswordLength} characters long.");
        }

        if (configuration.UserManagement.RequirePasswordComplexity)
        {
            var hasUppercase = password.Any(char.IsUpper);
            var hasLowercase = password.Any(char.IsLower);
            var hasDigit = password.Any(char.IsDigit);
            var hasSpecial = password.Any(c => !char.IsLetterOrDigit(c));

            if (!hasUppercase || !hasLowercase || !hasDigit || !hasSpecial)
            {
                throw new SemanticoException("Password must contain at least one uppercase letter, one lowercase letter, one digit, and one special character.");
            }
        }
    }
}
