using Semantico.Core.Data.Entities;
using Semantico.Core.Models.Ai;

namespace Semantico.AI.Services.Ai;

/// <summary>
/// No-op implementation of IAiAlertGenerationService for when AI features are disabled.
/// This allows handlers to have their dependencies satisfied without requiring LLM configuration.
/// </summary>
internal sealed class NullAiAlertGenerationService : IAiAlertGenerationService
{
    private const string AiNotConfiguredMessage =
        "AI features are not enabled. To use AI alert generation, configure LLM settings in appsettings.json:\n\n" +
        "{\n" +
        "  \"Semantico\": {\n" +
        "    \"LLM\": {\n" +
        "      \"Provider\": \"OpenAI\",\n" +
        "      \"ApiKey\": \"your-api-key\",\n" +
        "      \"Model\": \"gpt-4o\"\n" +
        "    }\n" +
        "  }\n" +
        "}";

    public Task<AiAlertConfiguration> GenerateAlertAsync(
        int dataSourceId,
        string naturalLanguageDescription,
        string createdBy,
        AlertGenerationOptions options,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(AiNotConfiguredMessage);
    }

    public Task<AiAlertConfiguration> RefineAlertAsync(
        int alertConfigurationId,
        string userFeedback,
        string modifiedBy,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(AiNotConfiguredMessage);
    }

    public Task<bool> ValidateQuerySyntaxAsync(
        int dataSourceId,
        string sql,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(AiNotConfiguredMessage);
    }

    public Task<AiAlertConfiguration> ApproveAndActivateAlertAsync(
        int alertConfigurationId,
        string approvedBy,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(AiNotConfiguredMessage);
    }
}
