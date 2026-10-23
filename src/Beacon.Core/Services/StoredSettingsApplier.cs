using Microsoft.Extensions.Logging;

namespace Beacon.Core.Services;

/// <summary>
/// Applies the settings an Admin stored (the base URL and the LLM provider configuration) to the running process.
/// <see cref="AppSettingsService.SaveSettingsAsync"/> applies them when they are saved; after a restart the host's
/// configuration (<c>Beacon:BaseUrl</c>, <c>Beacon:LLM</c>) is in force until this runs. Only what was stored replaces it:
/// an empty stored base URL keeps the configured one, the LLM configuration is replaced only when a provider was stored,
/// and stored LLM limits (concurrency, requests and tokens per minute, whether those are enforced) replace the configured
/// ones only when an Admin stored them (<see cref="AppSettingsData.LlmLimitsStored"/>), also without a stored provider (<see cref="ILlmConfigurationUpdater.UpdateLimits"/>). Without Beacon.AI there is no <see cref="ILlmConfigurationUpdater"/> and only the base URL applies. Settings
/// that cannot be read leave the host's configuration in force and are logged as a Warning naming the exception type
/// only, never a value (§1.11). A change another process stored is applied again the same way
/// (<see cref="StoredSettingsConvergence"/>).
/// </summary>
public sealed class StoredSettingsApplier(
    IAppSettingsService settingsService,
    BeaconConfiguration configuration,
    ILogger<StoredSettingsApplier> logger,
    ILlmConfigurationUpdater? llmUpdater = null)
{
    public Task ApplyAsync(CancellationToken cancellationToken)
    {
        return ApplyAsync(afterChange: false, cancellationToken);
    }

    /// <summary>
    /// Applies the stored settings again after another process changed them (<see cref="StoredSettingsConvergence"/>),
    /// the same way as at startup.
    /// </summary>
    internal Task ApplyChangedAsync(CancellationToken cancellationToken)
    {
        return ApplyAsync(afterChange: true, cancellationToken);
    }

    private async Task ApplyAsync(bool afterChange, CancellationToken cancellationToken)
    {
        var when = afterChange ? "after a change stored by another process" : "at startup";
        try
        {
            var settings = await settingsService.GetSettingsAsync(cancellationToken);

            var baseUrlApplied = !string.IsNullOrWhiteSpace(settings.BaseUrl);
            if (baseUrlApplied)
            {
                configuration.BaseUrl = settings.BaseUrl;
            }

            var llmApplied = settings.LlmProvider != null && llmUpdater != null;
            var limitsApplied = llmUpdater != null && settings.LlmLimitsStored;
            if (llmApplied)
            {
                llmUpdater!.UpdateConfiguration(settings, applyLimits: settings.LlmLimitsStored);
            }
            else if (limitsApplied)
            {
                llmUpdater!.UpdateLimits(settings);
            }

            logger.LogInformation(
                "Stored Admin settings applied {When}: base URL {BaseUrlApplied}, LLM configuration {LlmApplied}, LLM limits {LlmLimitsApplied}",
                when,
                baseUrlApplied,
                llmApplied,
                limitsApplied);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                "The stored Admin settings could not be read {When} ({ExceptionType}); the settings in effect stay until they are saved again",
                when,
                ex.GetType().Name);
        }
    }
}
