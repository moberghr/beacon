using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Beacon.Api.Authentication;
using Beacon.Core.Handlers.Setup;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using MediatR;

namespace Beacon.Api.Endpoints;

/// <summary>
/// API endpoints for the first-run setup process. Needs user management (<c>EnableUserManagement</c>). Creating the
/// first super admin requires the setup token (the configured <c>Beacon:UserManagement:SetupToken</c>, or the one
/// written to the server console at startup), and every setup endpoint is throttled per remote IP.
/// </summary>
public static class SetupEndpoints
{
    public static void MapSetupEndpoints(this WebApplication app, string basePath)
    {
        var group = app.MapGroup($"{basePath}/api/setup")
            .AddEndpointFilter<SetupRateLimitFilter>();

        // While setup is open, the generated setup token is written to the console once at startup. Off the startup
        // thread: the check needs the database. Bounded by the host's lifetime, so shutdown cancels it.
        app.Lifetime.ApplicationStarted.Register(
            () => _ = Task.Run(() => AnnounceSetupTokenAsync(app.Services, app.Lifetime.ApplicationStopping)));

        // Check if setup is needed
        group.MapGet("/status", async (
            IUserManagementService userService,
            [FromServices] FirstRunSetupToken setupToken,
            CancellationToken ct) =>
        {
            var isFirstRun = await userService.IsFirstRunAsync(ct);
            if (isFirstRun)
            {
                setupToken.AnnounceWhileFirstRun();
            }

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

            if (result.TokenRejected)
            {
                return Results.Problem(detail: result.Error, statusCode: StatusCodes.Status403Forbidden);
            }

            if (result.Conflict)
            {
                return Results.Problem(detail: result.Error, statusCode: StatusCodes.Status409Conflict);
            }

            if (!result.Success)
            {
                return Results.Problem(detail: result.Error, statusCode: StatusCodes.Status400BadRequest);
            }

            return Results.Ok(new
            {
                success = true,
                userId = result.UserId,
                message = result.Message
            });
        }).WithName("CreateSuperAdmin").AllowAnonymous();

        // Available roles: anonymous only while setup is open; afterwards only for a signed-in user.
        group.MapGet("/roles", async (
            HttpContext http,
            IUserManagementService userService,
            IRoleService roleService,
            CancellationToken ct) =>
        {
            if (http.User.Identity?.IsAuthenticated != true && !await userService.IsFirstRunAsync(ct))
            {
                return Results.Unauthorized();
            }

            var roles = await roleService.GetRolesAsync(ct);
            return Results.Ok(roles);
        }).AllowAnonymous();
    }

    private static async Task AnnounceSetupTokenAsync(IServiceProvider services, CancellationToken stopping)
    {
        var logger = services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(SetupEndpoints));
        try
        {
            await using var scope = services.CreateAsyncScope();
            var userService = scope.ServiceProvider.GetService<IUserManagementService>();
            if (userService == null)
            {
                logger.LogWarning(
                    "First-run setup endpoints are mapped, but user management is not enabled: setup cannot run. Call " +
                    "EnableUserManagement() in AddBeaconServices, or do not map the setup endpoints.");
                return;
            }

            if (await userService.IsFirstRunAsync(stopping))
            {
                scope.ServiceProvider.GetRequiredService<FirstRunSetupToken>().AnnounceWhileFirstRun();
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // The host is shutting down; nothing to announce.
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Could not check the first-run state at startup; the setup token is announced when setup is first requested.");
        }
    }
}
