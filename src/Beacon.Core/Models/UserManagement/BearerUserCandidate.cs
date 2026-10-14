namespace Beacon.Core.Models.UserManagement;

/// <summary>
/// An external Beacon user a bearer token may be bound to, with the soft-delete state the binding refuses on (archived
/// users are returned rather than hidden, so an archived match is refused instead of overlooked).
/// </summary>
public sealed class BearerUserCandidate
{
    public required BeaconUserData User { get; init; }

    public bool IsArchived { get; init; }
}
