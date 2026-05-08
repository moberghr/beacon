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
        // Pull current settings so we can preserve existing secrets the caller
        // didn't change. The UI sends `null` for "leave as is" and a string
        // for "replace".
        var current = await settingsService.GetSettingsAsync(cancellationToken);

        var next = new AppSettingsData
        {
            BaseUrl = request.BaseUrl,
            LlmProvider = request.LlmProvider,
            LlmModel = request.LlmModel,
            LlmFastModel = request.LlmFastModel,
            LlmRegion = request.LlmRegion,
            LlmApiKey = request.LlmApiKey ?? current.LlmApiKey,
            LlmEndpoint = request.LlmEndpoint ?? current.LlmEndpoint,
            LlmSessionToken = request.LlmSessionToken ?? current.LlmSessionToken,
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
    string? LlmApiKey,
    string? LlmEndpoint,
    string? LlmSessionToken,
    int LlmMaxConcurrentRequests,
    int LlmTokensPerMinute,
    int LlmRequestsPerMinute,
    decimal LlmMonthlyBudget) : IRequest;
