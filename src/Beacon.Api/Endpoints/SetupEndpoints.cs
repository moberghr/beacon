using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Beacon.Core.Handlers.Setup;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Api.Endpoints;

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
            CreateSuperAdminRequest request,
            IMediator m,
            CancellationToken ct) =>
        {
            var result = await m.Send(new CreateSuperAdminCommand(request), ct);
            if (result.Failed)
            {
                return Results.Text("Setup failed. Check server logs.", statusCode: StatusCodes.Status500InternalServerError);
            }

            if (!result.Success)
            {
                return Results.BadRequest(new { error = result.Error });
            }

            return Results.Ok(new
            {
                success = true,
                userId = result.UserId,
                message = result.Message
            });
        }).WithName("CreateSuperAdmin").AllowAnonymous();

        // Get available roles (for setup reference)
        group.MapGet("/roles", async (IRoleService roleService, CancellationToken ct) =>
        {
            var roles = await roleService.GetRolesAsync(ct);
            return Results.Ok(roles);
        }).AllowAnonymous();
    }
}
