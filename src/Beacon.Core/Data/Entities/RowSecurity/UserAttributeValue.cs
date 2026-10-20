using Beacon.Core.Data.Entities.Base;

namespace Beacon.Core.Data.Entities.RowSecurity;

/// <summary>
/// One value of an attribute a user holds; a user's values of an attribute are the set a policy's clause matches a column
/// against. Deleted when it is taken away: the security audit trail records the change.
/// </summary>
public class UserAttributeValue : BaseEntity
{
    /// <summary>The user holding the value (<c>Users.Id</c>).</summary>
    public int UserId { get; set; }

    public int AttributeDefinitionId { get; set; }

    /// <summary>
    /// The value in the canonical text of the attribute's type: as entered for text (compared ordinally), invariant decimal
    /// digits for an integer, the lowercase "D" form for a guid.
    /// </summary>
    public required string Value { get; set; }

    /// <summary>Who assigned the value: the caller's <c>Users.ExternalId</c>.</summary>
    public string? CreatedByUserId { get; set; }

    public BeaconUser User { get; set; } = null!;

    public AttributeDefinition AttributeDefinition { get; set; } = null!;
}
