using System.Net.Http.Headers;
using System.Text.Json;

namespace Semantico.AI.Services.GitHub;

internal sealed class GitHubApiClient(IHttpClientFactory httpClientFactory)
{
    private const string GitHubApiBase = "https://api.github.com";

    public async Task<List<GitHubTreeItem>> GetRepositoryTreeAsync(string owner, string repo, string branch, string? accessToken, CancellationToken ct)
    {
        var client = CreateClient(accessToken);
        // Use recursive tree API to get all files
        var response = await client.GetAsync($"{GitHubApiBase}/repos/{owner}/{repo}/git/trees/{branch}?recursive=1", ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var tree = JsonSerializer.Deserialize<GitHubTreeResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return tree?.Tree?.Where(t => t.Type == "blob").ToList() ?? new();
    }

    public async Task<string> GetFileContentAsync(string owner, string repo, string path, string branch, string? accessToken, CancellationToken ct)
    {
        var client = CreateClient(accessToken);
        var response = await client.GetAsync($"{GitHubApiBase}/repos/{owner}/{repo}/contents/{path}?ref={branch}", ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var file = JsonSerializer.Deserialize<GitHubFileResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

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
        return JsonSerializer.Deserialize<List<GitHubCommit>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
    }

    private HttpClient CreateClient(string? accessToken)
    {
        var client = httpClientFactory.CreateClient("GitHub");
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Semantico", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
        if (!string.IsNullOrEmpty(accessToken))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }
}

// GitHub API response models
public record GitHubTreeResponse(List<GitHubTreeItem>? Tree, bool Truncated);
public record GitHubTreeItem(string Path, string Type, string? Sha, int? Size);
public record GitHubFileResponse(string? Content, string? Encoding, string? Sha);
public record GitHubCommit(string Sha, GitHubCommitInfo? Commit);
public record GitHubCommitInfo(string Message, GitHubCommitAuthor? Author);
public record GitHubCommitAuthor(string Name, DateTime Date);
