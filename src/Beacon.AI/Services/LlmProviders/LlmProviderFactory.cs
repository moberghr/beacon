using Beacon.AI.Models.Configuration;
using Beacon.Core.Data.Enums;

namespace Beacon.AI.Services.LlmProviders;

/// <summary>
/// Builds concrete <see cref="ILlmProvider"/> instances from the current
/// <see cref="LlmConfiguration"/> snapshot returned by the supplied accessor.
/// Each call reads the latest configuration so hot-swaps pick up new values.
/// </summary>
public class LlmProviderFactory
{
    private readonly Func<LlmConfiguration> _configAccessor;

    public LlmProviderFactory(Func<LlmConfiguration> configAccessor)
    {
        _configAccessor = configAccessor ?? throw new ArgumentNullException(nameof(configAccessor));
    }

    public ILlmProvider CreateProvider()
    {
        var config = _configAccessor();
        return config.Provider switch
        {
            AiProvider.OpenAI => new OpenAiProvider(config.ApiKey, config.Model),
            AiProvider.Claude => new ClaudeProvider(config.ApiKey, config.Model),
            AiProvider.AzureOpenAI => CreateAzureProvider(config, config.Model),
            AiProvider.Bedrock => CreateBedrockProvider(config, useFastModel: false),
            _ => throw new NotSupportedException($"Provider {config.Provider} is not supported")
        };
    }

    public ILlmProvider CreateFastProvider()
    {
        var config = _configAccessor();
        var configuredFast = config.FastModel;
        var hasConfiguredFast = !string.IsNullOrWhiteSpace(configuredFast);

        return config.Provider switch
        {
            AiProvider.OpenAI => new OpenAiProvider(
                config.ApiKey,
                hasConfiguredFast ? configuredFast! : "gpt-4o-mini"),
            AiProvider.Claude => new ClaudeProvider(
                config.ApiKey,
                hasConfiguredFast ? configuredFast! : "claude-haiku-4-20250514"),
            AiProvider.AzureOpenAI => CreateAzureProvider(
                config,
                hasConfiguredFast ? configuredFast! : config.Model),
            AiProvider.Bedrock => CreateBedrockProvider(config, useFastModel: true),
            _ => throw new NotSupportedException($"Provider {config.Provider} is not supported")
        };
    }

    private static AzureOpenAiProvider CreateAzureProvider(LlmConfiguration config, string model)
    {
        if (string.IsNullOrEmpty(config.Endpoint))
        {
            throw new InvalidOperationException("Azure OpenAI endpoint is required");
        }

        return new AzureOpenAiProvider(config.Endpoint, config.ApiKey, model);
    }

    private static BedrockProvider CreateBedrockProvider(LlmConfiguration config, bool useFastModel)
    {
        if (string.IsNullOrEmpty(config.Region))
        {
            throw new InvalidOperationException("AWS Region is required for Bedrock provider");
        }

        var modelId = (useFastModel && !string.IsNullOrEmpty(config.FastModel) ? config.FastModel : config.Model)
            ?? throw new InvalidOperationException("Model ID is required for Bedrock provider");

        // If ApiKey contains both access and secret keys (format: "accessKey:secretKey")
        if (!string.IsNullOrEmpty(config.ApiKey) && config.ApiKey.Contains(':'))
        {
            var parts = config.ApiKey.Split(':', 2);
            if (parts.Length != 2)
            {
                throw new InvalidOperationException("ApiKey format should be 'accessKey:secretKey'");
            }

            return !string.IsNullOrEmpty(config.SessionToken)
                ? new BedrockProvider(parts[0], parts[1], config.SessionToken, config.Region, modelId)
                : new BedrockProvider(parts[0], parts[1], config.Region, modelId);
        }

        // Use default AWS credentials (IAM role, environment variables, etc.)
        return new BedrockProvider(config.Region, modelId);
    }
}
