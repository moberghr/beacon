using Beacon.Core.Configuration;

namespace Beacon.Core.Services.Security;

/// <summary>
/// Whether a stored API key works — the one rule key validation and the key listings share. A key works while it is
/// not revoked, not expired, and its owner exists, is not archived, is enabled and still has the API-key generation the
/// key was issued in (disabling, archiving and re-enabling the owner advance it). A key expires at its expiry instant.
/// A key stored without an expiry (issued before expiry became mandatory) does not expire unless
/// <see cref="ApiKeyOptions.EnforceMaxLifetimeOnExistingKeys"/> is set; then it expires
/// <see cref="ApiKeyOptions.MaxLifetimeDays"/> days after it was created.
/// </summary>
internal static class ApiKeyStatus
{
    public const string Revoked = "revoked";
    public const string Expired = "expired";
    public const string OwnerMissing = "owner_missing";
    public const string OwnerArchived = "owner_archived";
    public const string OwnerDisabled = "owner_disabled";
    public const string OwnerGenerationChanged = "owner_generation_changed";

    /// <summary>When the key stops working, or <c>null</c> when it does not expire.</summary>
    public static DateTime? EffectiveExpiry(DateTime? expiresAt, DateTime createdTime, ApiKeyOptions options)
    {
        if (expiresAt != null || !options.EnforceMaxLifetimeOnExistingKeys)
        {
            return expiresAt;
        }

        return createdTime.AddDays(options.MaxLifetimeDays);
    }

    /// <summary>Why the key does not work (one of the reason constants), or <c>null</c> when it works.</summary>
    public static string? RefusalReason(
        bool isRevoked,
        DateTime? expiresAt,
        DateTime createdTime,
        ApiKeyOwnerState owner,
        DateTime now,
        ApiKeyOptions options)
    {
        if (isRevoked)
        {
            return Revoked;
        }

        if (EffectiveExpiry(expiresAt, createdTime, options) <= now)
        {
            return Expired;
        }

        if (!owner.Exists)
        {
            return OwnerMissing;
        }

        if (owner.IsArchived)
        {
            return OwnerArchived;
        }

        if (!owner.IsEnabled)
        {
            return OwnerDisabled;
        }

        return owner.KeyGenerationIsCurrent ? null : OwnerGenerationChanged;
    }
}

/// <summary>
/// The state of an API key's owner that decides whether the key works. <see cref="KeyGenerationIsCurrent"/>: the
/// owner's API-key generation is still the one the key was issued in.
/// </summary>
internal readonly record struct ApiKeyOwnerState(bool Exists, bool IsArchived, bool IsEnabled, bool KeyGenerationIsCurrent);
