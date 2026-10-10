using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;

namespace Beacon.Core.Services.Security;

internal sealed class ApiKeyService(
    IDbContextFactory<BeaconContext> contextFactory,
    IOptions<ApiKeyOptions> options,
    TimeProvider timeProvider,
    ILogger<ApiKeyService> logger) : IApiKeyService
{
    private const string KeyPrefix = "sk-sem_";

    // Keys stored without an expiry that have already been warned about in this process: one warning per key.
    private static readonly ConcurrentDictionary<int, byte> NonExpiringKeysWarned = new();

    /// <summary>
    /// Issues a key for <paramref name="userId"/> with at least one valid scope (Read, Execute) and an expiry:
    /// <see cref="ApiKeyOptions.DefaultLifetimeDays"/> when none is requested, at most
    /// <see cref="ApiKeyOptions.MaxLifetimeDays"/>. Throws <see cref="InvalidOperationException"/> otherwise.
    /// </summary>
    public async Task<(ApiKeyCredential Credential, string PlainTextKey)> GenerateApiKeyAsync(
        int userId, string name, string[] scopes, int[]? allowedProjectIds = null, DateTime? expiresAt = null, CancellationToken ct = default)
    {
        var grantedScopes = ApiKeyGrants.ParseRequestedScopes(scopes);
        var expiry = ResolveExpiry(expiresAt);

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
            Scopes = JsonSerializer.Serialize(grantedScopes),
            AllowedProjectIds = allowedProjectIds != null ? JsonSerializer.Serialize(allowedProjectIds) : null,
            ExpiresAt = expiry
        };

        context.ApiKeyCredentials.Add(credential);
        await context.SaveChangesAsync(ct);

        logger.LogInformation("Generated API key {ApiKeyId} for user {UserId}", credential.Id, userId);
        return (credential, plainTextKey);
    }

    public async Task<ApiKeyCredential?> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            return null;
        }

        var keyHash = HashKey(apiKey);
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        // One query loads the key, its owner and the owner's roles: the Execute scope follows the owner's current write
        // permission. IgnoreQueryFilters so an archived owner is loaded (and refused below) rather than hidden by the
        // soft-delete filter.
        var credential = await context.ApiKeyCredentials
            .IgnoreQueryFilters()
            .Include(x => x.User)
            .ThenInclude(x => x!.UserRoles)
            .ThenInclude(x => x.Role)
            .Where(x => x.KeyHash == keyHash)
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync(ct);

        // An unknown key is not logged: there is no id to log, and the presented value never is (§1.3).
        if (credential == null)
        {
            return null;
        }

        var lifetimes = options.Value;
        var owner = credential.User;
        var reason = ApiKeyStatus.RefusalReason(
            credential.IsRevoked,
            credential.ExpiresAt,
            credential.CreatedTime,
            new ApiKeyOwnerState(owner != null, owner?.ArchivedTime != null, owner?.IsEnabled == true),
            timeProvider.GetUtcNow().UtcDateTime,
            lifetimes);
        if (reason != null)
        {
            logger.LogWarning("API key {ApiKeyId} refused: {Reason}", credential.Id, reason);
            return null;
        }

        if (credential.ExpiresAt == null
            && !lifetimes.EnforceMaxLifetimeOnExistingKeys
            && NonExpiringKeysWarned.TryAdd(credential.Id, 0))
        {
            logger.LogWarning(
                "API key {ApiKeyId} does not expire: it was issued before expiry became mandatory. Rotate it, or set " +
                "{Setting} to expire such keys {MaxLifetimeDays} days after creation.",
                credential.Id,
                $"{ApiKeyOptions.SectionName}:{nameof(ApiKeyOptions.EnforceMaxLifetimeOnExistingKeys)}",
                lifetimes.MaxLifetimeDays);
        }

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

        // Revoking twice keeps the first revocation time.
        if (key.IsRevoked)
        {
            return;
        }

        key.IsRevoked = true;
        key.RevokedAt = timeProvider.GetUtcNow().UtcDateTime;
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

    private DateTime ResolveExpiry(DateTime? requested)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var maxLifetimeDays = options.Value.MaxLifetimeDays;
        if (requested == null)
        {
            return now.AddDays(Math.Min(ApiKeyOptions.DefaultLifetimeDays, maxLifetimeDays));
        }

        // An unspecified kind is read as UTC, as before.
        var expiry = requested.Value.Kind == DateTimeKind.Local
            ? requested.Value.ToUniversalTime()
            : DateTime.SpecifyKind(requested.Value, DateTimeKind.Utc);

        if (expiry <= now)
        {
            throw new InvalidOperationException("An API key's expiry date must be in the future.");
        }

        if (expiry > now.AddDays(maxLifetimeDays))
        {
            throw new InvalidOperationException($"An API key can be valid for at most {maxLifetimeDays} days.");
        }

        return expiry;
    }

    private static string HashKey(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
