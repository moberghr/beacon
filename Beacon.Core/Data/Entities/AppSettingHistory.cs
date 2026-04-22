using Beacon.Core.Data.Entities.Base;

namespace Beacon.Core.Data.Entities;

public class AppSettingHistory : BaseEntity
{
    public string SettingKey { get; set; } = null!;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public DateTime ChangedAt { get; set; }
    public string? ChangedByUserId { get; set; }
}
