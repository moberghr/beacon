using Beacon.Core.Data.Entities;
using Beacon.Core.Models.Settings;

namespace Beacon.Core.Services;

public interface IAppSettingsService
{
    Task<AppSettingsData> GetSettingsAsync(CancellationToken ct = default);
    Task SaveSettingsAsync(AppSettingsData settings, string? userId = null, CancellationToken ct = default);
    Task<List<AppSettingHistory>> GetHistoryAsync(string? key = null, CancellationToken ct = default);
}
