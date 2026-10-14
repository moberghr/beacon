using Beacon.Core.Configuration;
using Beacon.Core.Models;

namespace Beacon.Core.Services.Security;

/// <summary>
/// The API key is valid, but its owner has not signed in to Beacon with their own identity for
/// <see cref="ApiKeyOptions.MaxOwnerInactivityDays"/> days: the key is refused until the owner signs in again. Thrown by
/// <see cref="IApiKeyService.ValidateApiKeyAsync"/>; the authentication middleware answers 401 with
/// <see cref="Exception.Message"/>, which names neither the key nor its owner.
/// </summary>
public sealed class ApiKeyOwnerInactiveException(int maxOwnerInactivityDays)
    : BeaconException(
        $"This API key is inactive: its owner has not signed in to Beacon for {maxOwnerInactivityDays} days. "
        + "The key works again after the owner signs in.")
{
    public int MaxOwnerInactivityDays { get; } = maxOwnerInactivityDays;
}
