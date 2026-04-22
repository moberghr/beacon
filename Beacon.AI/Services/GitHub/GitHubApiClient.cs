using System.Net.Http.Headers;
using System.Text.Json;

namespace Beacon.AI.Services.GitHub;

internal sealed class GitHubApiClient(IHttpClientFactory httpClientFactory)
{
    private const string GitHubApiBase = "https://api.github.com";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<string> GetDefaultBranchAsync(string owner, string repo, string? accessToken, CancellationToken ct)
    {
        var client = CreateClient(accessToken);
        var response = await client.GetAsync($"{GitHubApiBase}/repos/{owner}/{repo}", ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"GitHub API returned {(int)response.StatusCode} for {owner}/{repo}. " +
                $"If this is a private repo, ensure the token has the 'repo' scope. Response: {body}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        var repoInfo = JsonSerializer.Deserialize<GitHubRepoResponse>(json, JsonOptions);
        return repoInfo?.DefaultBranch ?? "main";
    }

    public async Task<List<GitHubTreeItem>> GetRepositoryTreeAsync(string owner, string repo, string branch, string? accessToken, CancellationToken ct)
    {
        var client = CreateClient(accessToken);
        // Use recursive tree API to get all files
        var response = await client.GetAsync($"{GitHubApiBase}/repos/{owner}/{repo}/git/trees/{branch}?recursive=1", ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var tree = JsonSerializer.Deserialize<GitHubTreeResponse>(json, JsonOptions);
        return tree?.Tree?.Where(t => t.Type == "blob").ToList() ?? new();
    }

    public async Task<string> GetFileContentAsync(string owner, string repo, string path, string branch, string? accessToken, CancellationToken ct)
    {
        var client = CreateClient(accessToken);
        var response = await client.GetAsync($"{GitHubApiBase}/repos/{owner}/{repo}/contents/{path}?ref={branch}", ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var file = JsonSerializer.Deserialize<GitHubFileResponse>(json, JsonOptions);

        if (file?.Content == null) return string.Empty;
        // GitHub returns base64-encoded content
        var bytes = Convert.FromBase64String(file.Content.Replace("\n", ""));
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    public async Task<List<GitHubCommit>> GetCommitsSinceAsync(string owner, string repo, string branch, DateTime since, string? accessToken, CancellationToken ct)
    {
        var client = CreateClient(accessToken);
        var sinceStr = since.ToString("o");
        var response = await client.GetAsync($"{GitHubApiBase}/repos/{owner}/{repo}/commits?sha={branch}&since={sinceStr}", ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<GitHubCommit>>(json, JsonOptions) ?? new();
    }

    private HttpClient CreateClient(string? accessToken)
    {
        var client = httpClientFactory.CreateClient("GitHub");
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Beacon", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
        if (!string.IsNullOrEmpty(accessToken))
        {
            // Classic PATs (ghp_) use "token" scheme; fine-grained (github_pat_) use "Bearer"
            var scheme = accessToken.StartsWith("github_pat_") ? "Bearer" : "token";
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(scheme, accessToken);
        }
        return client;
    }
}

// GitHub API response models
public record GitHubRepoResponse(string? DefaultBranch);
public record GitHubTreeResponse(List<GitHubTreeItem>? Tree, bool Truncated);
public record GitHubTreeItem(string Path, string Type, string? Sha, int? Size);
public record GitHubFileResponse(string? Content, string? Encoding, string? Sha);
public record GitHubCommit(string Sha, GitHubCommitInfo? Commit);
public record GitHubCommitInfo(string Message, GitHubCommitAuthor? Author);
public record GitHubCommitAuthor(string Name, DateTime Date);
