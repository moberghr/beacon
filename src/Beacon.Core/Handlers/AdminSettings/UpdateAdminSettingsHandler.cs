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
    ILlmProviderCatalog providerCatalog,
    ILogger<UpdateAdminSettingsHandler> logger)
    : IRequestHandler<UpdateAdminSettingsCommand>
{
    public const string BaseUrlAdminOnlyMessage = "Only an Admin can change the base URL.";

    public const string LimitsAdminOnlyMessage =
        "Only an Admin can change the LLM concurrency limit, the requests or tokens per minute, or whether they are enforced.";

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

        // The limits decide how much LLM traffic runs at all, so they are an Admin's like the connection.
        var rateLimitsEnabled = request.LlmRateLimitsEnabled ?? current.LlmRateLimitsEnabled;
        if (!permissions.IsAdmin && !SameLimits(current, request, rateLimitsEnabled))
        {
            throw AccessRefusal.Of(logger, actor, "LLM limits", null, LimitsAdminOnlyMessage);
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

        LlmProviderInstallation.DemandInstalled(providerCatalog, request.LlmProvider, current.LlmProvider);

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
            LlmRateLimitsEnabled = rateLimitsEnabled,
        };

        await settingsService.SaveSettingsAsync(next, userContext.UserId, cancellationToken);
    }

    private static bool SameLimits(AppSettingsData current, UpdateAdminSettingsCommand request, bool rateLimitsEnabled)
    {
        return request.LlmMaxConcurrentRequests == current.LlmMaxConcurrentRequests
            && request.LlmRequestsPerMinute == current.LlmRequestsPerMinute
            && request.LlmTokensPerMinute == current.LlmTokensPerMinute
            && rateLimitsEnabled == current.LlmRateLimitsEnabled;
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
/// Saves the admin settings. For an Admin or a SecurityAdmin; only an Admin changes the base URL, the LLM provider,
/// endpoint, region, Bedrock authentication mode and credentials (403 otherwise, <see cref="LlmConnectionSettings"/>), and
/// the LLM concurrency limit, requests and tokens per minute and whether those are enforced (the monthly budget is
/// recorded only and stays a SecurityAdmin's too).
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
    decimal LlmMonthlyBudget) : IRequest
{
    /// <summary>
    /// Whether the requests and tokens per minute are enforced; off (the default) leaves them recorded only. Null keeps
    /// the stored value, so a caller that does not know the setting never turns enforcement off.
    /// </summary>
    public bool? LlmRateLimitsEnabled { get; init; }
}
