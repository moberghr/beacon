using Beacon.AI.Models.Configuration;

namespace Beacon.AI.Services.LlmProviders;

public class LlmProviderFactory
{
    private readonly LlmConfiguration _config;

    public LlmProviderFactory(LlmConfiguration config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public ILlmProvider CreateProvider()
    {
        return _config.Provider switch
        {
            AiProvider.OpenAI => new OpenAiProvider(_config.ApiKey, _config.Model),
            AiProvider.Claude => new ClaudeProvider(_config.ApiKey, _config.Model),
            AiProvider.AzureOpenAI => CreateAzureProvider(),
            AiProvider.Bedrock => CreateBedrockProvider(useFastModel: false),
            _ => throw new NotSupportedException($"Provider {_config.Provider} is not supported")
        };
    }

    public ILlmProvider CreateFastProvider()
    {
        var fastModel = _config.FastModel ?? "gpt-4o-mini";

        return _config.Provider switch
        {
            AiProvider.OpenAI => new OpenAiProvider(_config.ApiKey, fastModel),
            AiProvider.Claude => new ClaudeProvider(_config.ApiKey, "claude-haiku-4-20250514"),
            AiProvider.AzureOpenAI => CreateAzureProvider(),
            AiProvider.Bedrock => CreateBedrockProvider(useFastModel: true),
            _ => throw new NotSupportedException($"Provider {_config.Provider} is not supported")
        };
    }

    private AzureOpenAiProvider CreateAzureProvider()
    {
        if (string.IsNullOrEmpty(_config.Endpoint))
        {
            throw new InvalidOperationException("Azure OpenAI endpoint is required");
        }

        return new AzureOpenAiProvider(_config.Endpoint, _config.ApiKey, _config.Model);
    }

    private BedrockProvider CreateBedrockProvider(bool useFastModel)
    {
        if (string.IsNullOrEmpty(_config.Region))
        {
            throw new InvalidOperationException("AWS Region is required for Bedrock provider");
        }

        var modelId = (useFastModel && !string.IsNullOrEmpty(_config.FastModel) ? _config.FastModel : _config.Model)
            ?? throw new InvalidOperationException("Model ID is required for Bedrock provider");

        return _config.BedrockAuthMode switch
        {
            BedrockAuthMode.IamRole => new BedrockProvider(_config.Region, modelId),
            BedrockAuthMode.AccessKey => CreateBedrockWithAccessKey(modelId),
            BedrockAuthMode.TemporaryCredentials => CreateBedrockWithTempCreds(modelId),
            _ => throw new NotSupportedException($"Unsupported Bedrock auth mode: {_config.BedrockAuthMode}")
        };
    }

    private BedrockProvider CreateBedrockWithAccessKey(string modelId)
    {
        if (string.IsNullOrEmpty(_config.AwsAccessKeyId) || string.IsNullOrEmpty(_config.AwsSecretAccessKey))
        {
            throw new InvalidOperationException("AWS access key ID and secret access key are required for AccessKey auth mode");
        }

        return new BedrockProvider(_config.AwsAccessKeyId, _config.AwsSecretAccessKey, _config.Region!, modelId);
    }

    private BedrockProvider CreateBedrockWithTempCreds(string modelId)
    {
        if (string.IsNullOrEmpty(_config.AwsAccessKeyId)
            || string.IsNullOrEmpty(_config.AwsSecretAccessKey)
            || string.IsNullOrEmpty(_config.SessionToken))
        {
            throw new InvalidOperationException("AWS access key ID, secret access key, and session token are required for TemporaryCredentials auth mode");
        }

        return new BedrockProvider(_config.AwsAccessKeyId, _config.AwsSecretAccessKey, _config.SessionToken, _config.Region!, modelId);
    }
}
