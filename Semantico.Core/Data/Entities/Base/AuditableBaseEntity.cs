namespace Semantico.Core.Data.Entities.Base;

/// <summary>
/// Base entity with optional audit trail fields.
/// These fields are nullable and will remain null until audit trail is enabled.
/// </summary>
public abstract class AuditableBaseEntity : BaseEntity
{
    /// <summary>
    /// User ID who created this entity (null if audit trail disabled).
    /// </summary>
    public string? CreatedByUserId { get; set; }

    /// <summary>
    /// User name who created this entity (null if audit trail disabled).
    /// </summary>
    public string? CreatedByUserName { get; set; }

    /// <summary>
    /// Last modified timestamp (null if never modified).
    /// </summary>
    public DateTime? ModifiedTime { get; set; }

    /// <summary>
    /// User ID who last modified this entity (null if audit trail disabled).
    /// </summary>
    public string? ModifiedByUserId { get; set; }

    /// <summary>
    /// User name who last modified this entity (null if audit trail disabled).
    /// </summary>
    public string? ModifiedByUserName { get; set; }
}
