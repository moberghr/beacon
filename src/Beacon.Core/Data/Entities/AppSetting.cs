using Beacon.Core.Data.Entities.Base;

namespace Beacon.Core.Data.Entities;

public class AppSetting : BaseEntity
{
    public string Key { get; set; } = null!;
    public string? Value { get; set; }
    public string Category { get; set; } = null!;
    public bool IsSensitive { get; set; }
}
