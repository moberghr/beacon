using Beacon.Core.Authorization;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Settings;
using Beacon.Core.Services;
using Microsoft.Extensions.Logging;
using MediatR;

namespace Beacon.Core.Handlers.AdminSettings;

internal sealed class UpdateAdminSettingsHandler(
    IAppSettingsService settingsService,
    IBeaconUserContext userContext,
    IBeaconActorAccessor actorAccessor,
    IBeaconPermissionAccessor permissionAccessor,
    ILogger<UpdateAdminSettingsHandler> logger)
    : IRequestHandler<UpdateAdminSettingsCommand>
{
    public const string BaseUrlAdminOnlyMessage = "Only an Admin can change the base URL.";

    public async Task Handle(UpdateAdminSettingsCommand request, CancellationToken cancellationToken)
    {
        // The UI sends `null` for "leave secret as is" and a string for "replace".
        // Pull current settings so we can preserve any secret the caller didn't touch.
        var current = await settingsService.GetSettingsAsync(cancellationToken);

        // A SecurityAdmin changes the rest; the provider, endpoint, region and credentials stay an Admin's, and so does
        // the base URL, the address every link Beacon sends out points to.
        var permissions = await permissionAccessor.GetCurrentAsync(cancellationToken);
        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        if (!permissions.IsAdmin && !SameBaseUrl(current.BaseUrl, request.BaseUrl))
        {
            throw AccessRefusal.Of(logger, actor, "base URL", null, BaseUrlAdminOnlyMessage);
        }

        LlmConnectionSettings.DemandAdminForChanges(
            permissions,
            actor,
            logger,
            current,
            new LlmConnectionRequest(
                request.LlmProvider,
                request.LlmRegion,
                request.LlmBedrockAuthMode,
                request.LlmApiKey,
                request.LlmEndpoint,
                request.LlmSessionToken,
                request.LlmAwsAccessKeyId,
                request.LlmAwsSecretAccessKey),
            nullKeepsStored: false);

        var next = new AppSettingsData
        {
            BaseUrl = request.BaseUrl,
            LlmProvider = request.LlmProvider,
            LlmModel = request.LlmModel,
            LlmFastModel = request.LlmFastModel,
            LlmRegion = request.LlmRegion,
            LlmBedrockAuthMode = request.LlmBedrockAuthMode,
            LlmApiKey = request.LlmApiKey ?? current.LlmApiKey,
            LlmEndpoint = request.LlmEndpoint ?? current.LlmEndpoint,
            LlmSessionToken = request.LlmSessionToken ?? current.LlmSessionToken,
            LlmAwsAccessKeyId = request.LlmAwsAccessKeyId ?? current.LlmAwsAccessKeyId,
            LlmAwsSecretAccessKey = request.LlmAwsSecretAccessKey ?? current.LlmAwsSecretAccessKey,
            LlmMaxConcurrentRequests = request.LlmMaxConcurrentRequests,
            LlmTokensPerMinute = request.LlmTokensPerMinute,
            LlmRequestsPerMinute = request.LlmRequestsPerMinute,
            LlmMonthlyBudget = request.LlmMonthlyBudget,
        };

        await settingsService.SaveSettingsAsync(next, userContext.UserId, cancellationToken);
    }

    private static bool SameBaseUrl(string? stored, string? requested)
    {
        return string.Equals(
            string.IsNullOrWhiteSpace(stored) ? null : stored.Trim(),
            string.IsNullOrWhiteSpace(requested) ? null : requested.Trim(),
            StringComparison.Ordinal);
    }
}

/// <summary>
/// Saves the admin settings. For an Admin or a SecurityAdmin; only an Admin changes the base URL and the LLM provider,
/// endpoint, region, Bedrock authentication mode and credentials (403 otherwise, <see cref="LlmConnectionSettings"/>).
/// </summary>
[ProjectScopeExempt("gated by the SecurityAdmin permission: settings, no project data; the LLM connection stays an Admin's", Policy = BeaconPermissions.SecurityAdminPolicy)]
public record UpdateAdminSettingsCommand(
    string? BaseUrl,
    AiProvider? LlmProvider,
    string? LlmModel,
    string? LlmFastModel,
    string? LlmRegion,
    BedrockAuthMode LlmBedrockAuthMode,
    string? LlmApiKey,
    string? LlmEndpoint,
    string? LlmSessionToken,
    string? LlmAwsAccessKeyId,
    string? LlmAwsSecretAccessKey,
    int LlmMaxConcurrentRequests,
    int LlmTokensPerMinute,
    int LlmRequestsPerMinute,
    decimal LlmMonthlyBudget) : IRequest;
