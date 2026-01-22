using Semantico.Core.Data.Entities;
using Semantico.Core.Models.Ai;

namespace Semantico.AI.Services.Ai;

/// <summary>
/// No-op implementation of IAiDocumentationService for when AI features are disabled.
/// This allows handlers to have their dependencies satisfied without requiring LLM configuration.
/// </summary>
internal sealed class NullAiDocumentationService : IAiDocumentationService
{
    private const string AiNotConfiguredMessage =
        "AI features are not enabled. To use documentation generation, configure LLM settings in appsettings.json:\n\n" +
        "{\n" +
        "  \"Semantico\": {\n" +
        "    \"LLM\": {\n" +
        "      \"Provider\": \"OpenAI\",\n" +
        "      \"ApiKey\": \"your-api-key\",\n" +
        "      \"Model\": \"gpt-4o\"\n" +
        "    }\n" +
        "  }\n" +
        "}";

    public Task<List<DataSourceDocumentation>> GetDocumentationsAsync(
        int dataSourceId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new List<DataSourceDocumentation>());
    }

    public Task<DataSourceDocumentation> GenerateDocumentationAsync(
        int dataSourceId,
        int userId,
        GenerationOptions options,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(AiNotConfiguredMessage);
    }

    public Task<DataSourceDocumentation> RegenerateDocumentationAsync(
        int documentationId,
        int userId,
        GenerationOptions options,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(AiNotConfiguredMessage);
    }

    public Task<DocumentationSection> RegenerateSectionAsync(
        int sectionId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(AiNotConfiguredMessage);
    }

    public Task<string> ExportToMarkdownAsync(
        int documentationId,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(AiNotConfiguredMessage);
    }

    public Task<string> ExportToHtmlAsync(
        int documentationId,
        string? customCss = null,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(AiNotConfiguredMessage);
    }

    public Task<byte[]> ExportToPdfAsync(
        int documentationId,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(AiNotConfiguredMessage);
    }
}
