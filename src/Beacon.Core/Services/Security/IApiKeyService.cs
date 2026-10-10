using Beacon.Core.Data.Entities;

namespace Beacon.Core.Services.Security;

public interface IApiKeyService
{
    /// <summary>
    /// Issues a key owned by <paramref name="userId"/> with at least one of the scopes Read and Execute and an expiry:
    /// <c>Beacon:ApiKeys</c> decides the default (90 days) and the maximum lifetime. Throws
    /// <see cref="InvalidOperationException"/> for a missing, unknown or retired scope, or an expiry that is past or
    /// beyond the maximum. Who may issue which scope is the caller's decision (see <c>CreateApiKeyCommand</c>).
    /// <paramref name="ownerGeneration"/> is the owner's <c>BeaconUser.ApiKeyGeneration</c> read together with the
    /// checks that approved the key: the key works only while it is still the owner's, so a key approved before the
    /// owner was disabled, archived or re-enabled, and stored after, never works.
    /// </summary>
    Task<(ApiKeyCredential Credential, string PlainTextKey)> GenerateApiKeyAsync(
        int userId,
        string name,
        string[] scopes,
        int[]? allowedProjectIds = null,
        DateTime? expiresAt = null,
        int ownerGeneration = 0,
        CancellationToken ct = default);
    /// <summary>
    /// The credential for <paramref name="apiKey"/>, or <c>null</c> when it is unknown, revoked or expired, or when its
    /// owner is missing, archived or disabled, or no longer in the API-key generation the key was issued in. A returned
    /// credential has its <c>User</c> loaded with the user's <c>UserRoles</c> and their <c>Role</c>: the authentication
    /// middleware refuses a credential without its user and grants the Execute scope only while that user is a super
    /// admin or holds the Editor role or higher, and the authorization provider grants them write permission.
    /// </summary>
    Task<ApiKeyCredential?> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default);
    Task<List<ApiKeyCredential>> GetApiKeysAsync(int? userId = null, CancellationToken ct = default);
    Task RevokeApiKeyAsync(int keyId, CancellationToken ct = default);
    Task UpdateLastUsedAsync(int keyId, CancellationToken ct = default);
}
