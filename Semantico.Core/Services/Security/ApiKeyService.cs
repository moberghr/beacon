using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities;

namespace Semantico.Core.Services.Security;

internal sealed class ApiKeyService(
    IDbContextFactory<SemanticoContext> contextFactory,
    ILogger<ApiKeyService> logger) : IApiKeyService
{
    private const string KeyPrefix = "sk-sem_";

    public async Task<(ApiKeyCredential Credential, string PlainTextKey)> GenerateApiKeyAsync(
        int? userId, string name, string[]? scopes = null, int[]? allowedDataSourceIds = null, DateTime? expiresAt = null, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        // Generate a secure random key
        var keyBytes = RandomNumberGenerator.GetBytes(32);
        var plainTextKey = KeyPrefix + Convert.ToBase64String(keyBytes).Replace("+", "").Replace("/", "").Replace("=", "");
        var keyHash = HashKey(plainTextKey);
        var keyPrefixStr = plainTextKey[..Math.Min(16, plainTextKey.Length)];

        var credential = new ApiKeyCredential
        {
            UserId = userId,
            Name = name,
            KeyHash = keyHash,
            KeyPrefix = keyPrefixStr,
            Scopes = scopes != null ? JsonSerializer.Serialize(scopes) : null,
            AllowedDataSourceIds = allowedDataSourceIds != null ? JsonSerializer.Serialize(allowedDataSourceIds) : null,
            ExpiresAt = expiresAt.HasValue ? DateTime.SpecifyKind(expiresAt.Value, DateTimeKind.Utc) : null
        };

        context.ApiKeyCredentials.Add(credential);
        await context.SaveChangesAsync(ct);

        logger.LogInformation("Generated API key '{Name}' for user {UserId}", name, userId);
        return (credential, plainTextKey);
    }

    public async Task<ApiKeyCredential?> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(apiKey)) return null;

        var keyHash = HashKey(apiKey);
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var credential = await context.ApiKeyCredentials
            .Include(k => k.User)
            .Where(k => k.KeyHash == keyHash)
            .FirstOrDefaultAsync(ct);

        if (credential == null) return null;
        if (credential.IsRevoked) return null;
        if (credential.ExpiresAt.HasValue && credential.ExpiresAt < DateTime.UtcNow) return null;

        return credential;
    }

    public async Task<List<ApiKeyCredential>> GetApiKeysAsync(int? userId = null, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var query = context.ApiKeyCredentials.Include(k => k.User).AsQueryable();
        if (userId.HasValue)
            query = query.Where(k => k.UserId == userId);
        return await query.OrderByDescending(k => k.CreatedTime).ToListAsync(ct);
    }

    public async Task RevokeApiKeyAsync(int keyId, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var key = await context.ApiKeyCredentials.FindAsync([keyId], ct)
            ?? throw new InvalidOperationException($"API key {keyId} not found");

        key.IsRevoked = true;
        key.RevokedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(ct);

        logger.LogInformation("Revoked API key {KeyId}", keyId);
    }

    public async Task UpdateLastUsedAsync(int keyId, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var key = await context.ApiKeyCredentials.FindAsync([keyId], ct);
        if (key != null)
        {
            key.LastUsedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(ct);
        }
    }

    private static string HashKey(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
