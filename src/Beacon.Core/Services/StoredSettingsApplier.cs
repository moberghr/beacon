using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Beacon.Core.Helpers;

namespace Beacon.Core.Services;

/// <summary>
/// Applies the settings an Admin stored (the base URL and the LLM provider configuration) to the running process.
/// <see cref="AppSettingsService.SaveSettingsAsync"/> applies them when they are saved; after a restart the host's
/// configuration (<c>Beacon:BaseUrl</c>, <c>Beacon:LLM</c>) is in force until this runs. Only what was stored replaces it:
/// an empty stored base URL keeps the configured one, the LLM configuration is replaced only when a provider was stored,
/// and stored LLM limits (concurrency, requests and tokens per minute, whether those are enforced) replace the configured
/// ones only when an Admin stored them (<see cref="AppSettingsData.LlmLimitsStored"/>), also without a stored provider (<see cref="ILlmConfigurationUpdater.UpdateLimits"/>). Without Beacon.AI there is no <see cref="ILlmConfigurationUpdater"/> and only the base URL applies. Settings
/// that cannot be read leave the host's configuration in force and are logged as a Warning naming the exception type
/// only, never a value (§1.11).
/// </summary>
public sealed class StoredSettingsApplier(
    IAppSettingsService settingsService,
    BeaconConfiguration configuration,
    ILogger<StoredSettingsApplier> logger,
    ILlmConfigurationUpdater? llmUpdater = null,
    IHostEnvironment? hostEnvironment = null)
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

            if (BaseUrlPolicy.Validate(configuration.BaseUrl, hostEnvironment?.IsDevelopment() == true) != null)
            {
                logger.LogWarning("The configured base URL is not usable and is left out of the links Beacon sends: {Message}", BaseUrlPolicy.InvalidMessage);
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
                "Stored Admin settings applied at startup: base URL {BaseUrlApplied}, LLM configuration {LlmApplied}, LLM limits {LlmLimitsApplied}",
                baseUrlApplied,
                llmApplied,
                limitsApplied);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                "The stored Admin settings could not be read at startup ({ExceptionType}); the host configuration (Beacon:BaseUrl, Beacon:LLM) stays in effect until they are saved again",
                ex.GetType().Name);
        }
    }
}
