using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Projects;
using Beacon.Core.Services;

namespace Beacon.AI.Services.GitHub;

internal sealed class GitHubScannerService(
    IDbContextFactory<BeaconContext> contextFactory,
    GitHubApiClient gitHubApiClient,
    IEnumerable<ICodeAnalyzer> codeAnalyzers,
    IEncryptionService encryptionService,
    IOptions<GitHubScanOptions> options,
    ILogger<GitHubScannerService> logger) : IGitHubScannerService
{
    // File extensions we care about (extensible per language)
    private static readonly HashSet<string> RelevantExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".py", ".java", ".ts", ".js", ".go", ".rb", ".rs", ".sql"
    };

    public async Task ScanRepositoryAsync(int projectId, int repositoryId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var repo = await context.GitHubRepositories
            .Where(x => x.Id == repositoryId)
            .Where(x => x.ProjectId == projectId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Repository {repositoryId} not found");

        var limits = options.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limits.ScanTimeout);
        var scanToken = timeout.Token;
        var notes = new List<string>();

        try
        {
            repo.ScanStatus = ScanStatus.InProgress;
            repo.LastScanError = null;
            await context.SaveChangesAsync(cancellationToken);

            var (owner, repoName) = ParseRepositoryUrl(repo.RepositoryUrl);
            var accessToken = repo.EncryptedAccessToken != null
                ? encryptionService.Decrypt(repo.EncryptedAccessToken)
                : null;

            var (branch, tree, treeTruncated) = await ReadFileListAsync(owner, repoName, accessToken, limits, scanToken, cancellationToken);
            if (repo.Branch != branch)
            {
                repo.Branch = branch;
                await context.SaveChangesAsync(cancellationToken);
            }

            var relevantFiles = tree
                .Where(f => RelevantExtensions.Any(ext => f.Path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (treeTruncated)
            {
                notes.Add("GitHub returned a truncated file list for this repository");
            }

            var oversized = relevantFiles.Count(f => f.Size > limits.MaxFileBytes);
            if (oversized > 0)
            {
                relevantFiles = relevantFiles.Where(f => !(f.Size > limits.MaxFileBytes)).ToList();
                notes.Add($"{oversized} files over {limits.MaxFileBytes} bytes were skipped");
            }

            if (relevantFiles.Count > limits.MaxFilesPerRepository)
            {
                notes.Add($"only the first {limits.MaxFilesPerRepository} of {relevantFiles.Count} files were scanned");
                relevantFiles = relevantFiles.Take(limits.MaxFilesPerRepository).ToList();
            }

            logger.LogInformation("Found {FileCount} relevant files in {Owner}/{Repo}", relevantFiles.Count, owner, repoName);

            var allReferences = new List<CodeReference>();
            var filesScanned = 0;
            var filesFailed = 0;

            // Process files in batches
            foreach (var batch in relevantFiles.Chunk(20))
            {
                if (scanToken.IsCancellationRequested)
                {
                    break;
                }

                var tasks = batch.Select(async file =>
                {
                    try
                    {
                        var content = await gitHubApiClient.GetFileContentAsync(owner, repoName, file.Path, repo.Branch, accessToken, scanToken);
                        var refs = new List<CodeReference>();

                        foreach (var analyzer in codeAnalyzers)
                        {
                            if (analyzer.CanAnalyze(file.Path))
                            {
                                var fileRefs = analyzer.AnalyzeFile(file.Path, content);
                                foreach (var r in fileRefs)
                                    r.GitHubRepositoryId = repositoryId;
                                refs.AddRange(fileRefs);
                            }
                        }
                        return refs;
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        // Cut short by the scan's own time limit is reported once as the limit, not as a file failure
                        if (!scanToken.IsCancellationRequested)
                        {
                            logger.LogWarning(ex, "Failed to analyze file {Path}", file.Path);
                            Interlocked.Increment(ref filesFailed);
                        }

                        return new List<CodeReference>();
                    }
                });

                var batchResults = await Task.WhenAll(tasks);
                foreach (var refs in batchResults)
                    allReferences.AddRange(refs);

                filesScanned += batch.Length;

                // Update progress periodically
                repo.TotalFilesScanned = filesScanned;
                repo.TotalReferencesFound = allReferences.Count;
                await context.SaveChangesAsync(cancellationToken);
            }

            // The caller's cancellation fails the scan; the scan's own time limit ends it with what it found
            cancellationToken.ThrowIfCancellationRequested();
            if (scanToken.IsCancellationRequested)
            {
                notes.Add($"the {limits.ScanTimeout.TotalSeconds:0}-second scan time limit was reached after {filesScanned} of {relevantFiles.Count} files");
            }

            if (filesFailed > 0)
            {
                notes.Add($"{filesFailed} files could not be read or analyzed");
            }

            // The old references are replaced only now, in the same save as the result, so a failed scan keeps them
            var oldRefs = await context.CodeReferences
                .Where(x => x.GitHubRepositoryId == repositoryId)
                .ToListAsync(cancellationToken);
            context.CodeReferences.RemoveRange(oldRefs);
            context.CodeReferences.AddRange(allReferences);

            // A partial scan is still completed, with the reason in the error text so it is never mistaken for a full one
            repo.LastScanError = RepositoryScanNotes.Format(notes);
            repo.ScanStatus = ScanStatus.Completed;
            repo.LastScanAt = DateTime.UtcNow;
            repo.TotalFilesScanned = filesScanned;
            repo.TotalReferencesFound = allReferences.Count;
            await context.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Scan completed: {Files} files, {Refs} references found, partial {Partial}", filesScanned, allReferences.Count, notes.Count > 0);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to scan repository {RepoId}", repositoryId);

            // Through a context of its own: nothing the scan staged on its context is written with the failure
            await using var failureContext = await contextFactory.CreateDbContextAsync(CancellationToken.None);
            var failed = await failureContext.GitHubRepositories
                .Where(x => x.Id == repositoryId)
                .FirstOrDefaultAsync(CancellationToken.None);
            if (failed != null)
            {
                failed.ScanStatus = ScanStatus.Failed;
                failed.LastScanError = ex.Message;
                await failureContext.SaveChangesAsync(CancellationToken.None);
            }

            throw;
        }
    }

    public async Task<ScanProgressInfo> GetScanProgressAsync(int projectId, int repositoryId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var repo = await context.GitHubRepositories
            .Where(x => x.Id == repositoryId)
            .Where(x => x.ProjectId == projectId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Repository {repositoryId} not found");

        var partialReasons = RepositoryScanNotes.PartialReasons(repo.ScanStatus, repo.LastScanError);
        return new ScanProgressInfo(repo.ScanStatus, repo.TotalFilesScanned, repo.TotalReferencesFound, repo.LastScanError)
        {
            PartialScan = partialReasons.Count > 0,
            PartialScanReasons = partialReasons
        };
    }

    internal static (string Owner, string Repo) ParseRepositoryUrl(string url)
    {
        // Accepted: https://github.com/owner/repo, https://github.com/owner/repo.git, owner/repo
        var text = url.Trim();
        string[] segments;
        if (text.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || !(uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("Only repositories on https://github.com can be scanned; other Git hosts are not supported.");
            }

            segments = uri.AbsolutePath.Trim('/').Split('/');
        }
        else
        {
            segments = text.Trim('/').Split('/');
            if (segments.Length != 2)
            {
                throw new InvalidOperationException("Only repositories on https://github.com can be scanned; use https://github.com/owner/repo or owner/repo.");
            }
        }

        if (segments.Length < 2)
        {
            throw new InvalidOperationException("The repository URL must name an owner and a repository.");
        }

        var name = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[1][..^4] : segments[1];
        var valid = IdentifierPattern.IsMatch(segments[0]) && IdentifierPattern.IsMatch(name);
        if (!valid)
        {
            throw new InvalidOperationException("The repository URL must name an owner and a repository.");
        }

        return (segments[0], name);
    }

    private static readonly System.Text.RegularExpressions.Regex IdentifierPattern =
        new(@"^[A-Za-z0-9_.-]+$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));

    // The repository's metadata and file list are read under the scan's time limit, response bodies included
    private async Task<(string Branch, List<GitHubTreeItem> Tree, bool Truncated)> ReadFileListAsync(
        string owner,
        string repoName,
        string? accessToken,
        GitHubScanOptions limits,
        CancellationToken scanToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var branch = await gitHubApiClient.GetDefaultBranchAsync(owner, repoName, accessToken, scanToken);
            var (tree, truncated) = await gitHubApiClient.GetRepositoryTreeAsync(owner, repoName, branch, accessToken, scanToken);
            return (branch, tree, truncated);
        }
        catch (OperationCanceledException) when (scanToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The {limits.ScanTimeout.TotalSeconds:0}-second scan time limit was reached before the repository's file list was read.");
        }
    }
}
