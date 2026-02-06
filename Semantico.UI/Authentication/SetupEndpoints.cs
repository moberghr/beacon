using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Semantico.Core.Models.UserManagement;
using Semantico.Core.Services;

namespace Semantico.UI.Authentication;

/// <summary>
/// API endpoints for the first-run setup process.
/// </summary>
public static class SetupEndpoints
{
    public static void MapSetupEndpoints(this WebApplication app, string basePath)
    {
        var group = app.MapGroup($"{basePath}/api/setup");

        // Check if setup is needed
        group.MapGet("/status", async (IUserManagementService userService, CancellationToken ct) =>
        {
            var isFirstRun = await userService.IsFirstRunAsync(ct);
            return Results.Ok(new { isFirstRun });
        }).AllowAnonymous();

        // Create super admin
        group.MapPost("/superadmin", async (
            IUserManagementService userService,
            IRoleService roleService,
            CreateSuperAdminRequest request,
            CancellationToken ct) =>
        {
            // Ensure this is actually first run
            var isFirstRun = await userService.IsFirstRunAsync(ct);
            if (!isFirstRun)
            {
                return Results.BadRequest(new { error = "Setup has already been completed." });
            }

            try
            {
                // Ensure system roles exist
                await roleService.SeedSystemRolesAsync(ct);

                // Create super admin
                var user = await userService.CreateSuperAdminAsync(request, ct);
                return Results.Ok(new
                {
                    success = true,
                    userId = user.Id,
                    message = "Super admin created successfully. You can now log in."
                });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).AllowAnonymous();

        // Get available roles (for setup reference)
        group.MapGet("/roles", async (IRoleService roleService, CancellationToken ct) =>
        {
            var roles = await roleService.GetRolesAsync(ct);
            return Results.Ok(roles);
        }).AllowAnonymous();
    }
}
