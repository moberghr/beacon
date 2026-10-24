using Beacon.Core.Data.Entities.Base;

namespace Beacon.Core.Data.Entities;

/// <summary>One changed setting of a settings save: its key, the values before and after (sensitive ones as a mask), and who saved it.</summary>
public class AppSettingHistory : BaseEntity, ISealedAuditRow
{
    public string SettingKey { get; set; } = null!;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public DateTime ChangedAt { get; set; }
    public string? ChangedByUserId { get; set; }

    /// <inheritdoc />
    public Guid? AuditGuid { get; set; }

    /// <inheritdoc />
    public int? SealVersion { get; set; }

    /// <inheritdoc />
    public string? HmacKeyId { get; set; }

    /// <inheritdoc />
    public string? Hmac { get; set; }
}
