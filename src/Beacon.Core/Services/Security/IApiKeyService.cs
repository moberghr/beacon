using Beacon.Core.Data.Entities;

namespace Beacon.Core.Services.Security;

public interface IApiKeyService
{
    /// <summary>
    /// Issues a key owned by <paramref name="userId"/> with at least one of the scopes Read and Execute and an expiry:
    /// <c>Beacon:ApiKeys</c> decides the default (90 days) and the maximum lifetime. Throws
    /// <see cref="InvalidOperationException"/> for a missing, unknown or retired scope, or an expiry that is past or
    /// beyond the maximum. Who may issue which scope is the caller's decision (see <c>CreateApiKeyCommand</c>).
    /// </summary>
    Task<(ApiKeyCredential Credential, string PlainTextKey)> GenerateApiKeyAsync(int userId, string name, string[] scopes, int[]? allowedProjectIds = null, DateTime? expiresAt = null, CancellationToken ct = default);
    /// <summary>
    /// The credential for <paramref name="apiKey"/>, or <c>null</c> when it is unknown, revoked or expired, or when its
    /// owner is missing, archived or disabled. A returned credential has its <c>User</c> loaded with the user's
    /// <c>UserRoles</c> and their <c>Role</c>: the authentication middleware refuses a credential without its user and
    /// grants the Execute scope only while that user is a super admin or holds the Editor role or higher.
    /// </summary>
    Task<ApiKeyCredential?> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default);
    Task<List<ApiKeyCredential>> GetApiKeysAsync(int? userId = null, CancellationToken ct = default);
    Task RevokeApiKeyAsync(int keyId, CancellationToken ct = default);
    Task UpdateLastUsedAsync(int keyId, CancellationToken ct = default);
}
