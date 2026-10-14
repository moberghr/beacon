namespace Beacon.Core.Data.Entities.Base;

/// <summary>
/// A resource that records who created it and who changed it last: <c>Users.ExternalId</c> values (as the resource's owner
/// is recorded), taken from the request's caller and never from a request body, and the time of the last change. Null for
/// resources created or changed before the attribution was recorded.
/// </summary>
public interface IAttributedEntity
{
    string? CreatedByUserId { get; set; }

    string? ModifiedByUserId { get; set; }

    DateTime? ModifiedTime { get; set; }
}
