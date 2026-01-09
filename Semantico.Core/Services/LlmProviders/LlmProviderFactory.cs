using Semantico.Core.Data.Enums;
using Semantico.Core.Models.Configuration;

namespace Semantico.Core.Services.LlmProviders;

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
            AiProvider.Bedrock => CreateBedrockProvider(),
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

    private BedrockProvider CreateBedrockProvider(bool useFastModel = false)
    {
        if (string.IsNullOrEmpty(_config.Region))
        {
            throw new InvalidOperationException("AWS Region is required for Bedrock provider");
        }
        
        var modelId = useFastModel && !string.IsNullOrEmpty(_config.FastModel)
            ? _config.FastModel
            : _config.Model;

        if (string.IsNullOrEmpty(modelId))
        {
            throw new InvalidOperationException("Model ID is required for Bedrock provider");
        }
        
        // If ApiKey contains both access and secret keys (format: "accessKey:secretKey")
        if (!string.IsNullOrEmpty(_config.ApiKey) && _config.ApiKey.Contains(':'))
        {
            var parts = _config.ApiKey.Split(':', 2);
            if (parts.Length != 2)
            {
                throw new InvalidOperationException("ApiKey format should be 'accessKey:secretKey'");
            }

            var accessKey = parts[0];
            var secretKey = parts[1];

            // If session token is provided, use temporary credentials
            if (!string.IsNullOrEmpty(_config.SessionToken))
            {
                return new BedrockProvider(accessKey, secretKey, _config.SessionToken, _config.Region, modelId);
            }

            return new BedrockProvider(accessKey, secretKey, _config.Region, modelId);
        }

        // Otherwise, use default AWS credentials (IAM role, environment variables, etc.)
        return new BedrockProvider(_config.Region, modelId);
    }
}
