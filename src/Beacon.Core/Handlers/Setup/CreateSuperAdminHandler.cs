using MediatR;
using Microsoft.Extensions.Logging;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.Setup;

internal sealed class CreateSuperAdminHandler(
    IUserManagementService userService,
    IRoleService roleService,
    ILogger<CreateSuperAdminHandler> logger)
    : IRequestHandler<CreateSuperAdminCommand, CreateSuperAdminResult>
{
    public async Task<CreateSuperAdminResult> Handle(CreateSuperAdminCommand request, CancellationToken cancellationToken)
    {
        var isFirstRun = await userService.IsFirstRunAsync(cancellationToken);
        if (!isFirstRun)
        {
            return new CreateSuperAdminResult
            {
                Success = false,
                Error = "Setup has already been completed.",
            };
        }

        try
        {
            await roleService.SeedSystemRolesAsync(cancellationToken);

            var user = await userService.CreateSuperAdminAsync(request.Request, cancellationToken);
            return new CreateSuperAdminResult
            {
                Success = true,
                UserId = user.Id,
                Message = "Super admin created successfully. You can now log in.",
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "First-run super admin setup failed.");
            return new CreateSuperAdminResult
            {
                Success = false,
                Failed = true,
            };
        }
    }
}

public record CreateSuperAdminCommand(CreateSuperAdminRequest Request) : IRequest<CreateSuperAdminResult>;

public record CreateSuperAdminResult
{
    public bool Success { get; init; }
    public int? UserId { get; init; }
    public string? Message { get; init; }
    public string? Error { get; init; }

    /// <summary>
    /// True when creation threw unexpectedly — the endpoint maps this to a 500 with a
    /// non-leaking message (details are logged, not returned).
    /// </summary>
    public bool Failed { get; init; }
}
