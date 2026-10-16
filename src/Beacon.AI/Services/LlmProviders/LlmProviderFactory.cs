using Beacon.AI.Models.Configuration;
using Beacon.Core.Data.Enums;
using Microsoft.Extensions.Logging;

namespace Beacon.AI.Services.LlmProviders;

/// <summary>
/// Builds concrete <see cref="ILlmProvider"/> instances from the current
/// <see cref="LlmConfiguration"/> snapshot returned by the supplied accessor.
/// Each call reads the latest configuration so hot-swaps pick up new values.
/// </summary>
public class LlmProviderFactory
{
    private readonly Func<LlmConfiguration> _configAccessor;
    private readonly ILogger? _logger;

    public LlmProviderFactory(Func<LlmConfiguration> configAccessor, ILogger<LlmProviderFactory>? logger = null)
    {
        _configAccessor = configAccessor ?? throw new ArgumentNullException(nameof(configAccessor));
        _logger = logger;
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

    private BedrockProvider CreateBedrockProvider(LlmConfiguration config, bool useFastModel)
    {
        if (string.IsNullOrEmpty(config.Region))
        {
            throw new InvalidOperationException("AWS Region is required for Bedrock provider");
        }

        var modelId = (useFastModel && !string.IsNullOrEmpty(config.FastModel) ? config.FastModel : config.Model)
            ?? throw new InvalidOperationException("Model ID is required for Bedrock provider");

        switch (config.BedrockAuthMode)
        {
            case BedrockAuthMode.AccessKey:
                {
                    var (accessKeyId, secretAccessKey) = RequireAccessKeyPair(config);

                    return new BedrockProvider(accessKeyId, secretAccessKey, config.Region, modelId);
                }

            case BedrockAuthMode.TemporaryCredentials:
                {
                    var (accessKeyId, secretAccessKey) = RequireAccessKeyPair(config);
                    if (string.IsNullOrWhiteSpace(config.SessionToken))
                    {
                        throw new InvalidOperationException("Bedrock temporary credentials require a session token");
                    }

                    return new BedrockProvider(accessKeyId, secretAccessKey, config.SessionToken, config.Region, modelId);
                }

            case BedrockAuthMode.IamRole:
                // Installs that stored the pair as "accessKeyId:secretAccessKey" in ApiKey keep working, with a warning:
                // the client then uses those static keys, not the default AWS credential chain the mode names.
                if (LegacyPairOf(config) is { } legacy)
                {
                    _logger?.LogWarning(
                        "Bedrock is set to the IAM role mode but uses the static access keys stored in the legacy API key field, not the default AWS credential chain. Store the keys in the access key mode, or clear the API key field to use the IAM role.");

                    return !string.IsNullOrEmpty(config.SessionToken)
                        ? new BedrockProvider(legacy.AccessKeyId, legacy.SecretAccessKey, config.SessionToken, config.Region, modelId)
                        : new BedrockProvider(legacy.AccessKeyId, legacy.SecretAccessKey, config.Region, modelId);
                }

                // Default AWS credentials (IAM role, environment variables, etc.)
                break;

            default:
                throw new InvalidOperationException($"Bedrock authentication mode {config.BedrockAuthMode} is not supported");
        }

        return new BedrockProvider(config.Region, modelId);
    }

    /// <summary>
    /// The access key fields when both are set. When both are empty, the pair an older install stored as
    /// <c>accessKeyId:secretAccessKey</c> in ApiKey; a missing half, or neither, fails instead of reaching the default chain.
    /// </summary>
    private static (string AccessKeyId, string SecretAccessKey) RequireAccessKeyPair(LlmConfiguration config)
    {
        var hasAccessKeyId = !string.IsNullOrWhiteSpace(config.AwsAccessKeyId);
        var hasSecretAccessKey = !string.IsNullOrWhiteSpace(config.AwsSecretAccessKey);
        if (hasAccessKeyId && hasSecretAccessKey)
        {
            return (config.AwsAccessKeyId!, config.AwsSecretAccessKey!);
        }

        if (!hasAccessKeyId && !hasSecretAccessKey && LegacyPairOf(config) is { } legacy)
        {
            return legacy;
        }

        throw new InvalidOperationException(
            $"Bedrock {config.BedrockAuthMode} authentication requires both an AWS access key id and a secret access key");
    }

    private static (string AccessKeyId, string SecretAccessKey)? LegacyPairOf(LlmConfiguration config)
    {
        if (string.IsNullOrEmpty(config.ApiKey) || !config.ApiKey.Contains(':'))
        {
            return null;
        }

        var parts = config.ApiKey.Split(':', 2);

        return (parts[0], parts[1]);
    }
}
