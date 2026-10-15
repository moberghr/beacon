using Microsoft.Extensions.Logging;

namespace Beacon.Core.Services;

/// <summary>
/// Applies the settings an Admin stored (the base URL and the LLM provider configuration) to the running process.
/// <see cref="AppSettingsService.SaveSettingsAsync"/> applies them when they are saved; after a restart the host's
/// configuration (<c>Beacon:BaseUrl</c>, <c>Beacon:LLM</c>) is in force until this runs. Only what was stored replaces it:
/// an empty stored base URL keeps the configured one, and the LLM configuration is replaced only when a provider was
/// stored. Without Beacon.AI there is no <see cref="ILlmConfigurationUpdater"/> and only the base URL applies. Settings
/// that cannot be read leave the host's configuration in force and are logged as a Warning naming the exception type
/// only, never a value (§1.11).
/// </summary>
public sealed class StoredSettingsApplier(
    IAppSettingsService settingsService,
    BeaconConfiguration configuration,
    ILogger<StoredSettingsApplier> logger,
    ILlmConfigurationUpdater? llmUpdater = null)
{
    public async Task ApplyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await settingsService.GetSettingsAsync(cancellationToken);

            var baseUrlApplied = !string.IsNullOrWhiteSpace(settings.BaseUrl);
            if (baseUrlApplied)
            {
                configuration.BaseUrl = settings.BaseUrl;
            }

            var llmApplied = settings.LlmProvider != null && llmUpdater != null;
            if (llmApplied)
            {
                llmUpdater!.UpdateConfiguration(settings);
            }

            logger.LogInformation(
                "Stored Admin settings applied at startup: base URL {BaseUrlApplied}, LLM configuration {LlmApplied}",
                baseUrlApplied,
                llmApplied);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                "The stored Admin settings could not be read at startup ({ExceptionType}); the host configuration (Beacon:BaseUrl, Beacon:LLM) stays in effect until they are saved again",
                ex.GetType().Name);
        }
    }
}
