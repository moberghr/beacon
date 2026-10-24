using Beacon.Core.Authorization;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Settings;
using Beacon.Core.Services;
using Microsoft.Extensions.Logging;
using Beacon.Core.Helpers;
using Microsoft.Extensions.Hosting;
using MediatR;

namespace Beacon.Core.Handlers.AdminSettings;

internal sealed class UpdateAdminSettingsHandler(
    IAppSettingsService settingsService,
    IBeaconUserContext userContext,
    IBeaconActorAccessor actorAccessor,
    IBeaconPermissionAccessor permissionAccessor,
    ILlmProviderCatalog providerCatalog,
    LlmConnectionApproval llmConnections,
    ILogger<UpdateAdminSettingsHandler> logger,
    IHostEnvironment? hostEnvironment = null)
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

        var baseUrlError = BaseUrlPolicy.Validate(request.BaseUrl, hostEnvironment?.IsDevelopment() == true);
        if (baseUrlError != null && !SameBaseUrl(current.BaseUrl, request.BaseUrl))
        {
            throw new InvalidOperationException(baseUrlError);
        }

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

        // The LLM connection is judged against the stored one, never this process's cached copy, which can predate a change
        // another process stored; the credentials left as they are come from it too, so a save never writes back a
        // credential that was replaced elsewhere.
        var connection = await llmConnections.StoredAsync(cancellationToken);
        var connectionRequest = new LlmConnectionRequest(
            request.LlmProvider,
            request.LlmRegion,
            request.LlmBedrockAuthMode,
            request.LlmApiKey,
            request.LlmEndpoint,
            request.LlmSessionToken,
            request.LlmAwsAccessKeyId,
            request.LlmAwsSecretAccessKey);
        LlmConnectionSettings.DemandAdminForChanges(permissions, actor, logger, connection, connectionRequest, nullKeepsStored: false);
        LlmConnectionSettings.DemandCredentialsForNewDestination(connection, connectionRequest);

        LlmProviderInstallation.DemandInstalled(providerCatalog, request.LlmProvider, connection.LlmProvider);

        var next = new AppSettingsData
        {
            BaseUrl = request.BaseUrl,
            LlmProvider = request.LlmProvider,
            LlmModel = request.LlmModel,
            LlmFastModel = request.LlmFastModel,
            LlmRegion = request.LlmRegion,
            LlmBedrockAuthMode = request.LlmBedrockAuthMode,
            LlmApiKey = request.LlmApiKey ?? connection.LlmApiKey,
            LlmEndpoint = request.LlmEndpoint ?? connection.LlmEndpoint,
            LlmSessionToken = request.LlmSessionToken ?? connection.LlmSessionToken,
            LlmAwsAccessKeyId = request.LlmAwsAccessKeyId ?? connection.LlmAwsAccessKeyId,
            LlmAwsSecretAccessKey = request.LlmAwsSecretAccessKey ?? connection.LlmAwsSecretAccessKey,
            LlmMaxConcurrentRequests = request.LlmMaxConcurrentRequests,
            LlmTokensPerMinute = request.LlmTokensPerMinute,
            LlmRequestsPerMinute = request.LlmRequestsPerMinute,
            LlmMonthlyBudget = request.LlmMonthlyBudget,
            LlmRateLimitsEnabled = rateLimitsEnabled,
        };

        // Only an Admin's save stores and applies the limits: anyone else's leaves the stored or host-configured ones in
        // force, also when an Admin changed them after this request read them. A change of the provider, endpoint, region
        // or authentication mode the approval workflow covers is staged for a live Admin's approval, the rest saved with it.
        await llmConnections.SaveAsync(connection, next, userContext.UserId, saveLimits: permissions.IsAdmin, cancellationToken);
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
/// recorded only and stays a SecurityAdmin's too). A SecurityAdmin sends the displayed limits back; the save neither
/// stores nor applies them. While the approval workflow covers the LLM connection
/// (<see cref="ApprovalWorkflowOptions.CoversLlmConnection"/>), a change of the provider, endpoint, region or
/// authentication mode (or a first stored provider over the host's configuration) waits for a live Admin's approval: the
/// stored connection and models stay in use, the other settings are saved at once with the request (409 while another
/// person's change of the connection waits). A change of credentials or models alone applies at once.
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

    // The compiler-generated ToString prints every member; the secrets are printed redacted.
    protected virtual bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"BaseUrl = {BaseUrl}, LlmProvider = {LlmProvider}, LlmModel = {LlmModel}, LlmFastModel = {LlmFastModel}, LlmRegion = {LlmRegion}, LlmBedrockAuthMode = {LlmBedrockAuthMode}, LlmApiKey = {RedactedPrint.Of(LlmApiKey)}, LlmEndpoint = {RedactedPrint.Of(LlmEndpoint)}, LlmSessionToken = {RedactedPrint.Of(LlmSessionToken)}, LlmAwsAccessKeyId = {RedactedPrint.Of(LlmAwsAccessKeyId)}, LlmAwsSecretAccessKey = {RedactedPrint.Of(LlmAwsSecretAccessKey)}, LlmMaxConcurrentRequests = {LlmMaxConcurrentRequests}, LlmTokensPerMinute = {LlmTokensPerMinute}, LlmRequestsPerMinute = {LlmRequestsPerMinute}, LlmMonthlyBudget = {LlmMonthlyBudget}, LlmRateLimitsEnabled = {LlmRateLimitsEnabled}");

        return true;
    }
}
