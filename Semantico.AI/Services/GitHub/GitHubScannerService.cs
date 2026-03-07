using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities.Projects;
using Semantico.Core.Data.Enums;
using Semantico.Core.Services;

namespace Semantico.AI.Services.GitHub;

internal sealed class GitHubScannerService(
    IDbContextFactory<SemanticoContext> contextFactory,
    GitHubApiClient gitHubApiClient,
    IEnumerable<ICodeAnalyzer> codeAnalyzers,
    IEncryptionService encryptionService,
    ILogger<GitHubScannerService> logger) : IGitHubScannerService
{
    // File extensions we care about (extensible per language)
    private static readonly HashSet<string> RelevantExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".py", ".java", ".ts", ".js", ".go", ".rb", ".rs", ".sql"
    };

    public async Task ScanRepositoryAsync(int repositoryId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var repo = await context.GitHubRepositories
            .FirstOrDefaultAsync(r => r.Id == repositoryId, cancellationToken)
            ?? throw new InvalidOperationException($"Repository {repositoryId} not found");

        try
        {
            repo.ScanStatus = ScanStatus.InProgress;
            repo.LastScanError = null;
            await context.SaveChangesAsync(cancellationToken);

            var (owner, repoName) = ParseRepositoryUrl(repo.RepositoryUrl);
            var accessToken = repo.EncryptedAccessToken != null
                ? encryptionService.Decrypt(repo.EncryptedAccessToken)
                : null;

            // Get all files in the repo
            var tree = await gitHubApiClient.GetRepositoryTreeAsync(owner, repoName, repo.Branch, accessToken, cancellationToken);
            var relevantFiles = tree
                .Where(f => RelevantExtensions.Any(ext => f.Path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            logger.LogInformation("Found {FileCount} relevant files in {Owner}/{Repo}", relevantFiles.Count, owner, repoName);

            // Clear old references
            var oldRefs = await context.CodeReferences
                .Where(r => r.GitHubRepositoryId == repositoryId)
                .ToListAsync(cancellationToken);
            context.CodeReferences.RemoveRange(oldRefs);

            var allReferences = new List<CodeReference>();
            var filesScanned = 0;

            // Process files in batches
            foreach (var batch in relevantFiles.Chunk(20))
            {
                var tasks = batch.Select(async file =>
                {
                    try
                    {
                        var content = await gitHubApiClient.GetFileContentAsync(owner, repoName, file.Path, repo.Branch, accessToken, cancellationToken);
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
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to analyze file {Path}", file.Path);
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

            // Save all references
            context.CodeReferences.AddRange(allReferences);

            repo.ScanStatus = ScanStatus.Completed;
            repo.LastScanAt = DateTime.UtcNow;
            repo.TotalFilesScanned = filesScanned;
            repo.TotalReferencesFound = allReferences.Count;
            await context.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Scan completed: {Files} files, {Refs} references found", filesScanned, allReferences.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to scan repository {RepoId}", repositoryId);
            repo.ScanStatus = ScanStatus.Failed;
            repo.LastScanError = ex.Message;
            await context.SaveChangesAsync(cancellationToken);
            throw;
        }
    }

    public async Task<ScanProgressInfo> GetScanProgressAsync(int repositoryId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var repo = await context.GitHubRepositories
            .FirstOrDefaultAsync(r => r.Id == repositoryId, cancellationToken)
            ?? throw new InvalidOperationException($"Repository {repositoryId} not found");

        return new ScanProgressInfo(repo.ScanStatus, repo.TotalFilesScanned, repo.TotalReferencesFound, repo.LastScanError);
    }

    private static (string Owner, string Repo) ParseRepositoryUrl(string url)
    {
        // Handle formats: https://github.com/owner/repo, https://github.com/owner/repo.git, owner/repo
        url = url.TrimEnd('/').Replace(".git", "");

        if (url.Contains("github.com"))
        {
            var parts = new Uri(url).AbsolutePath.Trim('/').Split('/');
            if (parts.Length >= 2) return (parts[0], parts[1]);
        }

        var segments = url.Split('/');
        if (segments.Length >= 2) return (segments[^2], segments[^1]);

        throw new ArgumentException($"Cannot parse GitHub repository URL: {url}");
    }
}
