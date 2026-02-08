using Semantico.AI.Models.Configuration;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models.Settings;
using Semantico.Core.Services;

namespace Semantico.AI.Services.LlmProviders;

/// <summary>
/// Manages the current LLM provider and supports hot-swapping when configuration changes.
/// </summary>
public class LlmProviderManager : ILlmConfigurationUpdater
{
    private readonly LlmConfiguration _llmConfig;
    private readonly LlmProviderFactory _factory;
    private readonly object _lock = new();

    public LlmProviderManager(LlmConfiguration llmConfig, LlmProviderFactory factory)
    {
        _llmConfig = llmConfig;
        _factory = factory;

        // Create initial provider if configuration is valid
        if (!string.IsNullOrEmpty(llmConfig.ApiKey) || llmConfig.Provider == AiProvider.Bedrock)
        {
            try
            {
                CurrentProvider = factory.CreateProvider();
            }
            catch
            {
                // Provider creation may fail if config is incomplete at startup — that's OK
            }
        }
    }

    public ILlmProvider? CurrentProvider { get; private set; }

    public void UpdateConfiguration(AppSettingsData settings)
    {
        lock (_lock)
        {
            // Mutate the existing LlmConfiguration singleton
            if (settings.LlmProvider.HasValue)
                _llmConfig.Provider = settings.LlmProvider.Value;

            _llmConfig.ApiKey = settings.LlmApiKey ?? string.Empty;
            _llmConfig.Endpoint = settings.LlmEndpoint;
            _llmConfig.Region = settings.LlmRegion;
            _llmConfig.SessionToken = settings.LlmSessionToken;
            _llmConfig.Model = settings.LlmModel ?? string.Empty;
            _llmConfig.FastModel = settings.LlmFastModel;
            _llmConfig.Limits.MaxConcurrentRequests = settings.LlmMaxConcurrentRequests;
            _llmConfig.Limits.TokensPerMinute = settings.LlmTokensPerMinute;
            _llmConfig.Limits.RequestsPerMinute = settings.LlmRequestsPerMinute;
            _llmConfig.Limits.MonthlyBudget = settings.LlmMonthlyBudget;

            // Recreate provider with updated config
            if (!string.IsNullOrEmpty(_llmConfig.ApiKey) || _llmConfig.Provider == AiProvider.Bedrock)
            {
                try
                {
                    CurrentProvider = _factory.CreateProvider();
                }
                catch
                {
                    // If provider creation fails, keep the old one
                }
            }
        }
    }
}
