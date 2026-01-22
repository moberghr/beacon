using Semantico.Core.Data.Entities;
using Semantico.Core.Models.Ai;

namespace Semantico.AI.Services.Ai;

public interface IAiDocumentationService
{
    Task<List<DataSourceDocumentation>> GetDocumentationsAsync(
        int dataSourceId,
        CancellationToken cancellationToken = default);

    Task<DataSourceDocumentation> GenerateDocumentationAsync(
        int dataSourceId,
        int userId,
        GenerationOptions options,
        CancellationToken cancellationToken = default);

    Task<DataSourceDocumentation> RegenerateDocumentationAsync(
        int documentationId,
        int userId,
        GenerationOptions options,
        CancellationToken cancellationToken = default);

    Task<DocumentationSection> RegenerateSectionAsync(
        int sectionId,
        int userId,
        CancellationToken cancellationToken = default);

    Task<string> ExportToMarkdownAsync(
        int documentationId,
        CancellationToken cancellationToken = default);

    Task<string> ExportToHtmlAsync(
        int documentationId,
        string? customCss = null,
        CancellationToken cancellationToken = default);

    Task<byte[]> ExportToPdfAsync(
        int documentationId,
        CancellationToken cancellationToken = default);
}
