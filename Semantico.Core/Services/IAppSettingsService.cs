using Semantico.Core.Data.Entities;
using Semantico.Core.Models.Settings;

namespace Semantico.Core.Services;

public interface IAppSettingsService
{
    Task<AppSettingsData> GetSettingsAsync(CancellationToken ct = default);
    Task SaveSettingsAsync(AppSettingsData settings, string? userId = null, CancellationToken ct = default);
    Task<List<AppSettingHistory>> GetHistoryAsync(string? key = null, CancellationToken ct = default);
}
