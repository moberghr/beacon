using Semantico.Core.Data.Enums;

namespace Semantico.AI.Services.GitHub;

public interface IGitHubScannerService
{
    Task ScanRepositoryAsync(int repositoryId, CancellationToken cancellationToken = default);
    Task<ScanProgressInfo> GetScanProgressAsync(int repositoryId, CancellationToken cancellationToken = default);
}

public record ScanProgressInfo(ScanStatus Status, int FilesScanned, int ReferencesFound, string? Error);
