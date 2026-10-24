namespace Beacon.Core.Data.Entities.Base;

/// <summary>
/// A row of an audit table that is sealed as it is saved: a keyed HMAC-SHA256 over its fields under the security audit
/// trail's keys (<c>AuditTableSeal</c>), verified by the scheduled audit verification. Every seal property is null on rows
/// written before the table's rows were sealed.
/// </summary>
public interface ISealedAuditRow
{
    /// <summary>The row's own id within the seal, unique across its table, so a sealed row cannot be copied into a second one.</summary>
    Guid? AuditGuid { get; set; }

    /// <summary>Which field list the seal covers, so a field added later is sealed without breaking the rows sealed before it.</summary>
    int? SealVersion { get; set; }

    /// <summary>The id of the integrity key the row was sealed under (the security audit trail's key ids).</summary>
    string? HmacKeyId { get; set; }

    /// <summary>The keyed HMAC-SHA256 over the row's fields, written when the row is saved.</summary>
    string? Hmac { get; set; }
}
