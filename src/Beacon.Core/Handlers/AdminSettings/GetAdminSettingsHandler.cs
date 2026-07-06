using Beacon.Core.Data.Enums;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.AdminSettings;

internal sealed class GetAdminSettingsHandler(IAppSettingsService settingsService)
    : IRequestHandler<GetAdminSettingsQuery, GetAdminSettingsResult>
{
    public async Task<GetAdminSettingsResult> Handle(GetAdminSettingsQuery request, CancellationToken cancellationToken)
    {
        var settings = await settingsService.GetSettingsAsync(cancellationToken);
        var history = await settingsService.GetHistoryAsync(null, cancellationToken);

        var entries = history
            .Select(x =>
                new AdminSettingHistoryEntry(
                    x.SettingKey,
                    x.OldValue,
                    x.NewValue,
                    x.ChangedAt,
                    x.ChangedByUserId))
            .ToList();

        // Mask sensitive secrets — write-only — never echo back the stored value.
        var view = new AdminSettingsView(
            settings.BaseUrl,
            settings.LlmProvider,
            !string.IsNullOrEmpty(settings.LlmApiKey),
            !string.IsNullOrEmpty(settings.LlmEndpoint),
            settings.LlmRegion,
            !string.IsNullOrEmpty(settings.LlmSessionToken),
            !string.IsNullOrEmpty(settings.LlmAwsAccessKeyId),
            !string.IsNullOrEmpty(settings.LlmAwsSecretAccessKey),
            settings.LlmBedrockAuthMode,
            settings.LlmModel,
            settings.LlmFastModel,
            settings.LlmMaxConcurrentRequests,
            settings.LlmTokensPerMinute,
            settings.LlmRequestsPerMinute,
            settings.LlmMonthlyBudget);

        return new GetAdminSettingsResult(view, entries);
    }
}

public record GetAdminSettingsQuery : IRequest<GetAdminSettingsResult>;

public record GetAdminSettingsResult(AdminSettingsView Settings, List<AdminSettingHistoryEntry> History);

public record AdminSettingsView(
    string? BaseUrl,
    AiProvider? LlmProvider,
    bool LlmApiKeySet,
    bool LlmEndpointSet,
    string? LlmRegion,
    bool LlmSessionTokenSet,
    bool LlmAwsAccessKeyIdSet,
    bool LlmAwsSecretAccessKeySet,
    BedrockAuthMode LlmBedrockAuthMode,
    string? LlmModel,
    string? LlmFastModel,
    int LlmMaxConcurrentRequests,
    int LlmTokensPerMinute,
    int LlmRequestsPerMinute,
    decimal LlmMonthlyBudget);

public record AdminSettingHistoryEntry(
    string SettingKey,
    string? OldValue,
    string? NewValue,
    DateTime ChangedAt,
    string? ChangedByUserId);
