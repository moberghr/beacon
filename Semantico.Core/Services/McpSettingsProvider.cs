using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities;
using Semantico.Core.Models;

namespace Semantico.Core.Services;

public interface IMcpSettingsProvider
{
    Task<McpSettingsData> GetSettingsAsync(CancellationToken ct = default);
    void InvalidateCache();
}

internal sealed class McpSettingsProvider(
    IDbContextFactory<SemanticoContext> contextFactory,
    IMemoryCache cache) : IMcpSettingsProvider
{
    private const string CacheKey = "McpSettings";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public async Task<McpSettingsData> GetSettingsAsync(CancellationToken ct = default)
    {
        return await cache.GetOrCreateAsync(CacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;

            await using var context = await contextFactory.CreateDbContextAsync(ct);
            var entity = await context.McpSettings.FirstOrDefaultAsync(ct);

            return entity != null ? MapToData(entity) : new McpSettingsData();
        }) ?? new McpSettingsData();
    }

    public void InvalidateCache()
    {
        cache.Remove(CacheKey);
    }

    private static McpSettingsData MapToData(McpSettings entity)
    {
        List<string> piiPatterns = [];
        if (!string.IsNullOrWhiteSpace(entity.CustomPiiPatterns))
        {
            try
            {
                piiPatterns = JsonSerializer.Deserialize<List<string>>(entity.CustomPiiPatterns) ?? [];
            }
            catch
            {
                // Invalid JSON — treat as empty
            }
        }

        return new McpSettingsData
        {
            AskSystemPrompt = entity.AskSystemPrompt,
            GlobalInstruction = entity.GlobalInstruction,
            GetContextDescription = entity.GetContextDescription,
            SearchDescription = entity.SearchDescription,
            QueryDescription = entity.QueryDescription,
            GetDocumentationDescription = entity.GetDocumentationDescription,
            AskDescription = entity.AskDescription,
            MaxRowLimit = entity.MaxRowLimit,
            EnforceReadOnly = entity.EnforceReadOnly,
            EnablePiiDetection = entity.EnablePiiDetection,
            CustomPiiPatterns = piiPatterns
        };
    }
}
