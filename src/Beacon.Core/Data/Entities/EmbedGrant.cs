using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Entities.Projects;

namespace Beacon.Core.Data.Entities;

/// <summary>
/// A stored, revocable permission to embed one approved version of one saved query in one project on the listed frame
/// origins. A host's backend exchanges the grant's secret (shown once, stored as its SHA-256) for short-lived embed
/// tokens; every exchange and render reads this row, so revoking it stops new tokens and every outstanding one. A grant is
/// never deleted: revoked, expired and superseded rows stay for the audit trail.
/// </summary>
public class EmbedGrant : BaseEntity, IAttributedEntity
{
    /// <summary>The random public id (32 lowercase hex characters) URLs and tokens name the grant by.</summary>
    public string PublicId { get; set; } = null!;

    public int ProjectId { get; set; }

    public Project Project { get; set; } = null!;

    public int QueryId { get; set; }

    public Query Query { get; set; } = null!;

    /// <summary>The approved version the grant renders; a newer approved version supersedes the grant.</summary>
    public int QueryVersionId { get; set; }

    public QueryVersion QueryVersion { get; set; } = null!;

    public string Name { get; set; } = null!;

    /// <summary>The exact frame origins (<c>https://host[:port]</c>, lower-case) as a JSON array.</summary>
    public string AllowedOriginsJson { get; set; } = "[]";

    /// <summary>The parameter values the grant fixes, as a JSON object of name to value.</summary>
    public string FixedParametersJson { get; set; } = "{}";

    /// <summary>The parameters the host's backend supplies at each exchange, as a JSON array of names.</summary>
    public string LockedParameterNamesJson { get; set; } = "[]";

    /// <summary>SHA-256 (lowercase hex) of the grant secret; the secret itself is never stored.</summary>
    public string SecretHash { get; set; } = null!;

    public DateTime ExpiresTime { get; set; }

    public DateTime? RevokedTime { get; set; }

    /// <summary>The <c>Users.ExternalId</c> of who revoked the grant.</summary>
    public string? RevokedByUserId { get; set; }

    public DateTime? LastTokenIssuedTime { get; set; }

    public string? CreatedByUserId { get; set; }

    public string? ModifiedByUserId { get; set; }

    public DateTime? ModifiedTime { get; set; }
}
