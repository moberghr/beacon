using Beacon.Core.Data.Enums;

namespace Beacon.AI.Services.GitHub;

/// <summary>
/// Scans a project's repositories for code references. A repository belongs to one project, and its references are read
/// through that project only (§1.12): each call names the project, and a repository of another project is not found.
/// </summary>
public interface IGitHubScannerService
{
    Task ScanRepositoryAsync(int projectId, int repositoryId, CancellationToken cancellationToken = default);
    Task<ScanProgressInfo> GetScanProgressAsync(int projectId, int repositoryId, CancellationToken cancellationToken = default);
}

public record ScanProgressInfo(ScanStatus Status, int FilesScanned, int ReferencesFound, string? Error);
