using Beacon.Core.Authorization;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Settings;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.AdminSettings;

internal sealed class UpdateAdminSettingsHandler(
    IAppSettingsService settingsService,
    IBeaconUserContext userContext)
    : IRequestHandler<UpdateAdminSettingsCommand>
{
    public async Task Handle(UpdateAdminSettingsCommand request, CancellationToken cancellationToken)
    {
        // The UI sends `null` for "leave secret as is" and a string for "replace".
        // Pull current settings so we can preserve any secret the caller didn't touch.
        var current = await settingsService.GetSettingsAsync(cancellationToken);

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
}

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
