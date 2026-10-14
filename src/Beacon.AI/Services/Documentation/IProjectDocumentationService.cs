using Beacon.Core.Data.Entities.Projects;

namespace Beacon.AI.Services.Documentation;

public interface IProjectDocumentationService
{
    Task<ProjectDocumentation> GenerateDocumentationAsync(
        int projectId, int userId, CancellationToken ct = default);

    Task<string> ExportToMarkdownAsync(int documentationId, CancellationToken ct = default);

    Task<string> ExportToHtmlAsync(int documentationId, CancellationToken ct = default);

    Task<string?> ExportLatestToMarkdownAsync(int projectId, CancellationToken ct = default);

    /// <summary>
    /// The latest documentation of <paramref name="projectId"/> as markdown without the sections whose type is in
    /// <paramref name="leaveOut"/> — under the row-data lock, the sections whose stored text can list values discovered in
    /// the data (<see cref="ProjectDocSectionTypes.ListingValues"/>).
    /// </summary>
    Task<string?> ExportLatestToMarkdownAsync(int projectId, IReadOnlyCollection<ProjectDocSectionType> leaveOut, CancellationToken ct = default);
}
