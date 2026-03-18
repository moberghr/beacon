using Semantico.Core.Data.Entities.Projects;

namespace Semantico.AI.Services.Documentation;

public interface IProjectDocumentationService
{
    Task<ProjectDocumentation> GenerateDocumentationAsync(
        int projectId, int userId, CancellationToken ct = default);

    Task<string> ExportToMarkdownAsync(int documentationId, CancellationToken ct = default);

    Task<string> ExportToHtmlAsync(int documentationId, CancellationToken ct = default);

    Task<string?> ExportLatestToMarkdownAsync(int projectId, CancellationToken ct = default);
}
