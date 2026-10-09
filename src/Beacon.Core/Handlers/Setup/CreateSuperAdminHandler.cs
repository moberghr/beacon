using MediatR;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Models;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;

namespace Beacon.Core.Handlers.Setup;

internal sealed class CreateSuperAdminHandler(
    IUserManagementService userService,
    FirstRunSetupToken setupToken,
    ILogger<CreateSuperAdminHandler> logger)
    : IRequestHandler<CreateSuperAdminCommand, CreateSuperAdminResult>
{
    private const string AlreadyCompleted = "Setup has already been completed.";

    public async Task<CreateSuperAdminResult> Handle(CreateSuperAdminCommand request, CancellationToken cancellationToken)
    {
        var isFirstRun = await userService.IsFirstRunAsync(cancellationToken);
        if (!isFirstRun)
        {
            return new CreateSuperAdminResult
            {
                Success = false,
                Error = AlreadyCompleted,
            };
        }

        // Covers a process whose startup check could not reach the database: the token is still announced once.
        setupToken.AnnounceWhileFirstRun();

        if (!setupToken.Verify(request.Request.SetupToken))
        {
            logger.LogWarning("First-run setup rejected: the setup token is missing or invalid.");
            return new CreateSuperAdminResult
            {
                Success = false,
                TokenRejected = true,
                Error = "The setup token is missing or invalid. Use the token from the server console or the configured Beacon:UserManagement:SetupToken.",
            };
        }

        try
        {
            var user = await userService.CreateSuperAdminAsync(request.Request, cancellationToken);

            logger.LogWarning("First-run setup created the super admin with user id {UserId}.", user.Id);

            return new CreateSuperAdminResult
            {
                Success = true,
                UserId = user.Id,
                Message = "Super admin created successfully. You can now log in.",
            };
        }
        catch (BeaconException ex)
        {
            // A business-rule refusal (a weak password, setup already completed): its message is meant for the caller.
            return new CreateSuperAdminResult
            {
                Success = false,
                Error = ex.Message,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (DbSerializationConflict.IsSerializationConflict(ex))
        {
            // The database aborted this attempt in favour of a concurrent first-run request.
            logger.LogWarning(ex, "First-run setup attempt conflicted with a concurrent setup request.");

            return await AfterConflictAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "First-run super admin setup failed.");

            return await AfterFailureAsync(cancellationToken);
        }
    }

    // The winner has usually committed by now: report setup as complete. If it has not (or the state cannot be read),
    // the caller is told to retry rather than shown a server error.
    private async Task<CreateSuperAdminResult> AfterConflictAsync(CancellationToken cancellationToken)
    {
        var stillFirstRun = await TryIsFirstRunAsync(cancellationToken);
        if (stillFirstRun == false)
        {
            return new CreateSuperAdminResult
            {
                Success = false,
                Error = AlreadyCompleted,
            };
        }

        return new CreateSuperAdminResult
        {
            Success = false,
            Conflict = true,
            Error = "Another setup request is in progress. Reload the page and try again.",
        };
    }

    // A request that lost a race in some other way (e.g. a unique violation after the winner committed) is told setup
    // is complete; anything else is a server error whose details stay in the log.
    private async Task<CreateSuperAdminResult> AfterFailureAsync(CancellationToken cancellationToken)
    {
        var stillFirstRun = await TryIsFirstRunAsync(cancellationToken);
        if (stillFirstRun == false)
        {
            logger.LogWarning("First-run setup attempt lost to a concurrent setup that completed first.");
            return new CreateSuperAdminResult
            {
                Success = false,
                Error = AlreadyCompleted,
            };
        }

        return new CreateSuperAdminResult
        {
            Success = false,
            Failed = true,
        };
    }

    // Null when the first-run state cannot be read; the failure is logged.
    private async Task<bool?> TryIsFirstRunAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await userService.IsFirstRunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not re-read the first-run state after a failed setup attempt.");
            return null;
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

    /// <summary>True when the setup token was missing or wrong; the endpoint answers 403.</summary>
    public bool TokenRejected { get; init; }

    /// <summary>
    /// True when the attempt conflicted with a concurrent first-run request that has not visibly completed; the
    /// endpoint answers 409 and the caller may retry.
    /// </summary>
    public bool Conflict { get; init; }

    /// <summary>
    /// True when creation threw unexpectedly — the endpoint maps this to a 500 with a
    /// non-leaking message (details are logged, not returned).
    /// </summary>
    public bool Failed { get; init; }
}
