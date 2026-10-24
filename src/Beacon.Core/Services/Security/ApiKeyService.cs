using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Services.Security;

internal sealed class ApiKeyService(
    IDbContextFactory<BeaconContext> contextFactory,
    IOptions<ApiKeyOptions> options,
    TimeProvider timeProvider,
    ISecurityAuditRecorder securityAudit,
    IBeaconActorAccessor actorAccessor,
    ILogger<ApiKeyService> logger,
    ApiKeyLastUsedThrottle? lastUsedThrottle = null) : IApiKeyService
{
    private const string KeyPrefix = "sk-sem_";

    private static readonly TimeSpan LastUsedWriteInterval = TimeSpan.FromMinutes(1);

    // When this process last wrote each key's LastUsedAt. The host shares one throttle (singleton); an instance built
    // without one (a test) gets its own, so no state outlives it.
    private readonly ApiKeyLastUsedThrottle _lastUsedThrottle = lastUsedThrottle ?? new ApiKeyLastUsedThrottle();

    // Keys stored without an expiry that have already been warned about in this process: one warning per key.
    private static readonly ConcurrentDictionary<int, byte> NonExpiringKeysWarned = new();

    /// <summary>
    /// Issues a key for <paramref name="userId"/> with at least one valid scope (Read, Execute) and an expiry:
    /// <see cref="ApiKeyOptions.DefaultLifetimeDays"/> when none is requested, at most
    /// <see cref="ApiKeyOptions.MaxLifetimeDays"/>. Throws <see cref="InvalidOperationException"/> otherwise. The key is
    /// stamped with <paramref name="ownerGeneration"/>.
    /// </summary>
    public async Task<(ApiKeyCredential Credential, string PlainTextKey)> GenerateApiKeyAsync(
        int userId,
        string name,
        string[] scopes,
        int[]? allowedProjectIds = null,
        DateTime? expiresAt = null,
        int ownerGeneration = 0,
        CancellationToken ct = default)
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
            ExpiresAt = expiry,
            OwnerGeneration = ownerGeneration
        };

        // Scopes, projects and expiry: what the key may do, never key material (§1.3).
        context.ApiKeyCredentials.Add(credential);
        await securityAudit.SaveWithEventsAsync(
            context,
            () =>
            [
                new SecurityAuditEntry(SecurityAuditActions.ApiKeyCreated)
                    .On(SecurityAuditResources.ApiKey, credential.Id)
                    .With(new SecurityAuditDetails()
                        .Add("ownerUserId", userId)
                        .Add("readScope", grantedScopes.Contains(BeaconScopes.Read))
                        .Add("executeScope", grantedScopes.Contains(BeaconScopes.Execute))
                        .Add("allowedProjectIds", allowedProjectIds)
                        .Add("expiresAt", expiry))
            ],
            ct);

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
        // soft-delete filter. Only the columns the checks and the caller read are selected: the owner's password hash
        // and salt are never loaded, and nothing is tracked.
        var row = await context.ApiKeyCredentials
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.KeyHash == keyHash)
            .OrderBy(x => x.Id)
            .Select(x =>
                new ApiKeyRow
                {
                    Id = x.Id,
                    UserId = x.UserId,
                    Name = x.Name,
                    KeyHash = x.KeyHash,
                    KeyPrefix = x.KeyPrefix,
                    Scopes = x.Scopes,
                    AllowedProjectIds = x.AllowedProjectIds,
                    ExpiresAt = x.ExpiresAt,
                    LastUsedAt = x.LastUsedAt,
                    IsRevoked = x.IsRevoked,
                    RevokedAt = x.RevokedAt,
                    OwnerGeneration = x.OwnerGeneration,
                    CreatedTime = x.CreatedTime,
                    Owner = x.User == null
                        ? null
                        : new OwnerRow
                        {
                            Id = x.User.Id,
                            ExternalId = x.User.ExternalId,
                            UserName = x.User.UserName,
                            Email = x.User.Email,
                            DisplayName = x.User.DisplayName,
                            IsSuperAdmin = x.User.IsSuperAdmin,
                            IsEnabled = x.User.IsEnabled,
                            ArchivedTime = x.User.ArchivedTime,
                            ApiKeyGeneration = x.User.ApiKeyGeneration,
                            LastLoginAt = x.User.LastLoginAt,
                            CreatedTime = x.User.CreatedTime,
                            Roles = x.User.UserRoles
                                .Select(y =>
                                    new OwnerRoleRow
                                    {
                                        Name = y.Role.Name,
                                        Level = y.Role.Level
                                    })
                                .ToList()
                        }
                })
            .FirstOrDefaultAsync(ct);

        var credential = row == null ? null : row.ToCredential();

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
            new ApiKeyOwnerState(
                owner != null,
                owner?.ArchivedTime != null,
                owner?.IsEnabled == true,
                owner?.ApiKeyGeneration == credential.OwnerGeneration,
                owner?.LastLoginAt ?? owner?.CreatedTime),
            timeProvider.GetUtcNow().UtcDateTime,
            lifetimes);
        if (reason != null)
        {
            logger.LogWarning("API key {ApiKeyId} refused: {Reason}", credential.Id, reason);

            // The one refusal the caller is told about: the key is genuine and works again once its owner signs in.
            if (reason == ApiKeyStatus.OwnerInactive)
            {
                throw new ApiKeyOwnerInactiveException(lifetimes.MaxOwnerInactivityDays);
            }

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

        // Revoking a key of another user is an administrator's revocation.
        var actor = await actorAccessor.GetCurrentAsync(ct);
        var revokedByOwner = actor.BeaconUserId != null && actor.BeaconUserId == key.UserId;
        await securityAudit.StageAsync(
            context,
            new SecurityAuditEntry(revokedByOwner ? SecurityAuditActions.ApiKeyRevoked : SecurityAuditActions.ApiKeyRevokedByAdministrator)
                .On(SecurityAuditResources.ApiKey, key.Id)
                .With(new SecurityAuditDetails().Add("ownerUserId", key.UserId)),
            ct);
        await context.SaveChangesAsync(ct);

        logger.LogInformation("Revoked API key {KeyId}", keyId);
    }

    public async Task UpdateLastUsedAsync(int keyId, CancellationToken ct = default)
    {
        // LastUsedAt is bookkeeping, not an audit record: it moves at most once per interval, so a busy key costs one
        // write a minute instead of one per request. The write is conditional, so concurrent requests (and processes)
        // that all pass the check still land one update.
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (_lastUsedThrottle.Writes.TryGetValue(keyId, out var written) && written <= now && now - written < LastUsedWriteInterval)
        {
            return;
        }

        var staleBefore = now - LastUsedWriteInterval;
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        await context.ApiKeyCredentials
            .IgnoreQueryFilters()
            .Where(x => x.Id == keyId)
            .Where(x => x.LastUsedAt == null || x.LastUsedAt < staleBefore)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.LastUsedAt, now), ct);

        _lastUsedThrottle.Writes[keyId] = now;
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

    private sealed class OwnerRoleRow
    {
        public required string Name { get; init; }
        public int Level { get; init; }
    }

    private sealed class OwnerRow
    {
        public int Id { get; init; }
        public required string ExternalId { get; init; }
        public required string UserName { get; init; }
        public string? Email { get; init; }
        public string? DisplayName { get; init; }
        public bool IsSuperAdmin { get; init; }
        public bool IsEnabled { get; init; }
        public DateTime? ArchivedTime { get; init; }
        public int ApiKeyGeneration { get; init; }
        public DateTime? LastLoginAt { get; init; }
        public DateTime CreatedTime { get; init; }
        public List<OwnerRoleRow> Roles { get; init; } = [];
    }

    private sealed class ApiKeyRow
    {
        public int Id { get; init; }
        public int? UserId { get; init; }
        public required string Name { get; init; }
        public required string KeyHash { get; init; }
        public required string KeyPrefix { get; init; }
        public string? Scopes { get; init; }
        public string? AllowedProjectIds { get; init; }
        public DateTime? ExpiresAt { get; init; }
        public DateTime? LastUsedAt { get; init; }
        public bool IsRevoked { get; init; }
        public DateTime? RevokedAt { get; init; }
        public int OwnerGeneration { get; init; }
        public DateTime CreatedTime { get; init; }
        public OwnerRow? Owner { get; init; }

        public ApiKeyCredential ToCredential() =>
            new()
            {
                Id = Id,
                UserId = UserId,
                Name = Name,
                KeyHash = KeyHash,
                KeyPrefix = KeyPrefix,
                Scopes = Scopes,
                AllowedProjectIds = AllowedProjectIds,
                ExpiresAt = ExpiresAt,
                LastUsedAt = LastUsedAt,
                IsRevoked = IsRevoked,
                RevokedAt = RevokedAt,
                OwnerGeneration = OwnerGeneration,
                CreatedTime = CreatedTime,
                User = Owner == null
                    ? null
                    : new BeaconUser
                    {
                        Id = Owner.Id,
                        ExternalId = Owner.ExternalId,
                        UserName = Owner.UserName,
                        Email = Owner.Email,
                        DisplayName = Owner.DisplayName,
                        IsSuperAdmin = Owner.IsSuperAdmin,
                        IsEnabled = Owner.IsEnabled,
                        ArchivedTime = Owner.ArchivedTime,
                        ApiKeyGeneration = Owner.ApiKeyGeneration,
                        LastLoginAt = Owner.LastLoginAt,
                        CreatedTime = Owner.CreatedTime,
                        UserRoles = Owner.Roles
                            .Select(x =>
                                new BeaconUserRole
                                {
                                    Role = new BeaconRole
                                    {
                                        Name = x.Name,
                                        Level = x.Level
                                    }
                                })
                            .ToList()
                    }
            };
    }
}

/// <summary>When each API key's LastUsedAt was last written by this process; registered as a singleton.</summary>
internal sealed class ApiKeyLastUsedThrottle
{
    public ConcurrentDictionary<int, DateTime> Writes { get; } = new();
}
