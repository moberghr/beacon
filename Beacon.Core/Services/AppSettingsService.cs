using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Settings;

namespace Beacon.Core.Services;

public class AppSettingsService : IAppSettingsService
{
    private const string CacheKey = "AppSettings_All";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    private readonly IDbContextFactory<BeaconContext> _contextFactory;
    private readonly IMemoryCache _cache;
    private readonly IEncryptionService _encryption;
    private readonly BeaconConfiguration _config;
    private readonly ILlmConfigurationUpdater? _llmUpdater;

    public AppSettingsService(
        IDbContextFactory<BeaconContext> contextFactory,
        IMemoryCache cache,
        IEncryptionService encryption,
        BeaconConfiguration config,
        ILlmConfigurationUpdater? llmUpdater = null)
    {
        _contextFactory = contextFactory;
        _cache = cache;
        _encryption = encryption;
        _config = config;
        _llmUpdater = llmUpdater;
    }

    public async Task<AppSettingsData> GetSettingsAsync(CancellationToken ct = default)
    {
        if (_cache.TryGetValue(CacheKey, out AppSettingsData? cached) && cached != null)
            return cached;

        await using var context = await _contextFactory.CreateDbContextAsync(ct);
        var rows = await context.AppSettings.AsNoTracking().ToListAsync(ct);

        var dict = rows.ToDictionary(r => r.Key, r => new { r.Value, r.IsSensitive });

        var data = new AppSettingsData
        {
            BaseUrl = GetValue("General.BaseUrl"),
            LlmProvider = GetEnum<AiProvider>("LLM.Provider"),
            LlmApiKey = GetValue("LLM.ApiKey"),
            LlmEndpoint = GetValue("LLM.Endpoint"),
            LlmRegion = GetValue("LLM.Region"),
            LlmSessionToken = GetValue("LLM.SessionToken"),
            LlmModel = GetValue("LLM.Model"),
            LlmFastModel = GetValue("LLM.FastModel"),
            LlmMaxConcurrentRequests = GetInt("LLM.MaxConcurrentRequests", 50),
            LlmTokensPerMinute = GetInt("LLM.TokensPerMinute", 80000),
            LlmRequestsPerMinute = GetInt("LLM.RequestsPerMinute", 1000),
            LlmMonthlyBudget = GetDecimal("LLM.MonthlyBudget", 100.00m),
        };

        _cache.Set(CacheKey, data, CacheDuration);
        return data;

        string? GetValue(string key)
        {
            if (!dict.TryGetValue(key, out var entry) || entry.Value == null)
                return null;
            return entry.IsSensitive ? _encryption.Decrypt(entry.Value) : entry.Value;
        }

        int GetInt(string key, int defaultValue = 0)
        {
            var v = GetValue(key);
            return v != null && int.TryParse(v, out var i) ? i : defaultValue;
        }

        decimal GetDecimal(string key, decimal defaultValue)
        {
            var v = GetValue(key);
            return v != null && decimal.TryParse(v, CultureInfo.InvariantCulture, out var d) ? d : defaultValue;
        }

        T? GetEnum<T>(string key) where T : struct, Enum
        {
            var v = GetValue(key);
            return v != null && Enum.TryParse<T>(v, true, out var e) ? e : null;
        }

    }

    public async Task SaveSettingsAsync(AppSettingsData settings, string? userId = null, CancellationToken ct = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);
        var rows = await context.AppSettings.ToListAsync(ct);
        var dict = rows.ToDictionary(r => r.Key);
        var historyEntries = new List<AppSettingHistory>();
        var now = DateTime.UtcNow;

        void SetValue(string key, string? newValue, bool isSensitive)
        {
            if (!dict.TryGetValue(key, out var row)) return;

            // Decrypt current value for comparison
            var currentPlain = row.IsSensitive && row.Value != null
                ? _encryption.Decrypt(row.Value)
                : row.Value;

            if (currentPlain == newValue) return;

            // Record history (store plain text for non-sensitive, masked for sensitive)
            historyEntries.Add(new AppSettingHistory
            {
                SettingKey = key,
                OldValue = isSensitive ? "***" : currentPlain,
                NewValue = isSensitive ? "***" : newValue,
                ChangedAt = now,
                ChangedByUserId = userId,
            });

            // Store encrypted if sensitive
            row.Value = isSensitive && newValue != null ? _encryption.Encrypt(newValue) : newValue;
        }

        SetValue("General.BaseUrl", settings.BaseUrl, false);

        SetValue("LLM.Provider", settings.LlmProvider?.ToString(), false);
        SetValue("LLM.ApiKey", settings.LlmApiKey, true);
        SetValue("LLM.Endpoint", settings.LlmEndpoint, true);
        SetValue("LLM.Region", settings.LlmRegion, false);
        SetValue("LLM.SessionToken", settings.LlmSessionToken, true);
        SetValue("LLM.Model", settings.LlmModel, false);
        SetValue("LLM.FastModel", settings.LlmFastModel, false);
        SetValue("LLM.MaxConcurrentRequests", settings.LlmMaxConcurrentRequests.ToString(), false);
        SetValue("LLM.TokensPerMinute", settings.LlmTokensPerMinute.ToString(), false);
        SetValue("LLM.RequestsPerMinute", settings.LlmRequestsPerMinute.ToString(), false);
        SetValue("LLM.MonthlyBudget", settings.LlmMonthlyBudget.ToString(CultureInfo.InvariantCulture), false);

        if (historyEntries.Count > 0)
        {
            context.AppSettingHistory.AddRange(historyEntries);
        }

        await context.SaveChangesAsync(ct);

        // Invalidate cache
        _cache.Remove(CacheKey);

        // Mutate BeaconConfiguration singleton
        _config.BaseUrl = settings.BaseUrl;

        // Update LLM configuration if updater is available
        _llmUpdater?.UpdateConfiguration(settings);
    }

    public async Task<List<AppSettingHistory>> GetHistoryAsync(string? key = null, CancellationToken ct = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);
        var query = context.AppSettingHistory.AsNoTracking().AsQueryable();

        if (!string.IsNullOrEmpty(key))
            query = query.Where(h => h.SettingKey == key);

        return await query.OrderByDescending(h => h.ChangedAt).Take(200).ToListAsync(ct);
    }
}
