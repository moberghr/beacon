using Beacon.Core.Data.Entities;

namespace Beacon.Core.Services.Security;

public interface IApiKeyService
{
    Task<(ApiKeyCredential Credential, string PlainTextKey)> GenerateApiKeyAsync(int? userId, string name, string[]? scopes = null, int[]? allowedProjectIds = null, DateTime? expiresAt = null, CancellationToken ct = default);
    Task<ApiKeyCredential?> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default);
    Task<List<ApiKeyCredential>> GetApiKeysAsync(int? userId = null, CancellationToken ct = default);
    Task RevokeApiKeyAsync(int keyId, CancellationToken ct = default);
    Task UpdateLastUsedAsync(int keyId, CancellationToken ct = default);
}
