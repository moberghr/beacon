using System.Net.Http.Headers;
using System.Text.Json;
using Beacon.Core.Http;
using Microsoft.Extensions.Options;

namespace Beacon.AI.Services.GitHub;

internal sealed class GitHubApiClient(IHttpClientFactory httpClientFactory, IOptions<GitHubScanOptions> options)
{
    private const string GitHubApiBase = "https://api.github.com";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<string> GetDefaultBranchAsync(string owner, string repo, string? accessToken, CancellationToken ct)
    {
        var client = CreateClient(accessToken);
        var response = await client.GetAsync($"{GitHubApiBase}/repos/{owner}/{repo}", HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"GitHub API returned {(int)response.StatusCode} for {owner}/{repo}. " +
                "If this is a private repo, ensure the token can read the repository contents.");
        }

        var json = await ReadLimitedAsync(response, 1024 * 1024, ct);
        var repoInfo = JsonSerializer.Deserialize<GitHubRepoResponse>(json, JsonOptions);
        return repoInfo?.DefaultBranch ?? "main";
    }

    public async Task<(List<GitHubTreeItem> Items, bool Truncated)> GetRepositoryTreeAsync(string owner, string repo, string branch, string? accessToken, CancellationToken ct)
    {
        var client = CreateClient(accessToken);
        // Use recursive tree API to get all files
        var response = await client.GetAsync($"{GitHubApiBase}/repos/{owner}/{repo}/git/trees/{branch}?recursive=1", HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var json = await ReadLimitedAsync(response, options.Value.MaxTreeResponseBytes, ct);
        var tree = JsonSerializer.Deserialize<GitHubTreeResponse>(json, JsonOptions);
        return (tree?.Tree?.Where(t => t.Type == "blob").ToList() ?? new(), tree?.Truncated ?? false);
    }

    public async Task<string> GetFileContentAsync(string owner, string repo, string path, string branch, string? accessToken, CancellationToken ct)
    {
        var client = CreateClient(accessToken);
        var response = await client.GetAsync($"{GitHubApiBase}/repos/{owner}/{repo}/contents/{path}?ref={branch}", HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        // The JSON wraps base64 content: about 4/3 of the file plus line breaks and fields
        var json = await ReadLimitedAsync(response, (long)options.Value.MaxFileBytes * 2 + 4096, ct);
        var file = JsonSerializer.Deserialize<GitHubFileResponse>(json, JsonOptions);

        if (file?.Content == null) return string.Empty;
        // GitHub returns base64-encoded content
        var bytes = Convert.FromBase64String(file.Content.Replace("\n", ""));
        if (bytes.Length > options.Value.MaxFileBytes)
        {
            throw new InvalidOperationException($"File is larger than the {options.Value.MaxFileBytes}-byte scan limit.");
        }

        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, long maxBytes, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > maxBytes)
        {
            throw new InvalidOperationException($"The GitHub response is larger than the {maxBytes}-byte scan limit.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new InvalidOperationException($"The GitHub response is larger than the {maxBytes}-byte scan limit.");
            }

            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private HttpClient CreateClient(string? accessToken)
    {
        var client = httpClientFactory.CreateClient(BeaconHttpClients.GitHub);
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
