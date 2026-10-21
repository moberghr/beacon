using Beacon.AI.Models.Configuration;
using Beacon.Core.Data.Enums;
using Microsoft.Extensions.Logging;

namespace Beacon.AI.Services.LlmProviders;

/// <summary>
/// Builds the Bedrock provider with the credentials the configured <see cref="BedrockAuthMode"/> names. Bedrock needs no
/// API key: the access key modes use their own fields, and the IAM role mode uses the default AWS credential chain.
/// </summary>
internal sealed class BedrockRegistration(ILogger<BedrockRegistration>? logger = null) : ILlmProviderRegistration
{
    public AiProvider Provider => AiProvider.Bedrock;

    public bool RequiresApiKey => false;

    public ILlmProvider Create(LlmConfiguration configuration, bool fast)
    {
        if (string.IsNullOrEmpty(configuration.Region))
        {
            throw new InvalidOperationException("AWS Region is required for Bedrock provider");
        }

        var modelId = (fast && !string.IsNullOrEmpty(configuration.FastModel) ? configuration.FastModel : configuration.Model)
            ?? throw new InvalidOperationException("Model ID is required for Bedrock provider");

        switch (configuration.BedrockAuthMode)
        {
            case BedrockAuthMode.AccessKey:
                {
                    var (accessKeyId, secretAccessKey) = RequireAccessKeyPair(configuration);

                    return new BedrockProvider(accessKeyId, secretAccessKey, configuration.Region, modelId);
                }

            case BedrockAuthMode.TemporaryCredentials:
                {
                    var (accessKeyId, secretAccessKey) = RequireAccessKeyPair(configuration);
                    if (string.IsNullOrWhiteSpace(configuration.SessionToken))
                    {
                        throw new InvalidOperationException("Bedrock temporary credentials require a session token");
                    }

                    return new BedrockProvider(accessKeyId, secretAccessKey, configuration.SessionToken, configuration.Region, modelId);
                }

            case BedrockAuthMode.IamRole:
                // Installs that stored the pair as "accessKeyId:secretAccessKey" in ApiKey keep working, with a warning:
                // the client then uses those static keys, not the default AWS credential chain the mode names.
                if (LegacyPairOf(configuration) is { } legacy)
                {
                    logger?.LogWarning(
                        "Bedrock is set to the IAM role mode but uses the static access keys stored in the legacy API key field, not the default AWS credential chain. Store the keys in the access key mode, or clear the API key field to use the IAM role.");

                    return !string.IsNullOrEmpty(configuration.SessionToken)
                        ? new BedrockProvider(legacy.AccessKeyId, legacy.SecretAccessKey, configuration.SessionToken, configuration.Region, modelId)
                        : new BedrockProvider(legacy.AccessKeyId, legacy.SecretAccessKey, configuration.Region, modelId);
                }

                // Default AWS credentials (IAM role, environment variables, etc.)
                break;

            default:
                throw new InvalidOperationException($"Bedrock authentication mode {configuration.BedrockAuthMode} is not supported");
        }

        return new BedrockProvider(configuration.Region, modelId);
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
