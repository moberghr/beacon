using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Helpers;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.ApiKeys;

/// <summary>
/// Every user's API keys, or one user's, for an Admin, an Auditor or a UserAdmin in a signed-in session: the route's
/// policy admits them, and they hold the role now (<see cref="ApiKeyManagementCaller.ResolveAdministratorAsync"/>). Each
/// key says whether the caller may revoke it, by the rules of <see cref="AdminRevokeApiKeyHandler"/>.
/// </summary>
internal sealed class GetAllApiKeysHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IUserManagementService userManagementService,
    IBeaconPermissionAccessor permissionAccessor,
    IOptions<ApiKeyOptions> options,
    IOptions<RoleMappingOptions> roleMapping,
    TimeProvider timeProvider,
    IHttpContextAccessor? httpContextAccessor = null)
    : IRequestHandler<GetAllApiKeysQuery, PagedList<AdminApiKeyEntry>>
{
    public async Task<PagedList<AdminApiKeyEntry>> Handle(
        GetAllApiKeysQuery request,
        CancellationToken cancellationToken)
    {
        var administrator = await ApiKeyManagementCaller.ResolveAdministratorAsync(
            httpContextAccessor,
            userManagementService,
            permissionAccessor,
            [BeaconPermissions.Auditor, BeaconPermissions.UserAdmin],
            cancellationToken);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // IgnoreQueryFilters keeps archived owners in the owner columns: their keys are listed under their name.
        var query = context.ApiKeyCredentials.IgnoreQueryFilters();
        if (request.UserId != null)
        {
            query = query.Where(x => x.UserId == request.UserId);
        }

        // Scopes and project ids are stored as JSON, so the page is read into arrays after it is loaded.
        var page = await query
            .Select(x =>
                new AdminApiKeyRow
                {
                    Id = x.Id,
                    Name = x.Name,
                    Prefix = x.KeyPrefix,
                    Scopes = x.Scopes,
                    AllowedProjectIds = x.AllowedProjectIds,
                    CreatedAt = x.CreatedTime,
                    LastUsedAt = x.LastUsedAt,
                    ExpiresAt = x.ExpiresAt,
                    RevokedAt = x.RevokedAt,
                    IsRevoked = x.IsRevoked,
                    UserId = x.UserId,
                    UserName = x.User != null ? x.User.UserName : null,
                    OwnerExists = x.User != null,
                    OwnerArchived = x.User != null && x.User.ArchivedTime != null,
                    OwnerEnabled = x.User != null && x.User.IsEnabled,
                    OwnerGenerationIsCurrent = x.User != null && x.User.ApiKeyGeneration == x.OwnerGeneration,
                    OwnerLastSignInAt = x.User != null ? (DateTime?)(x.User.LastLoginAt ?? x.User.CreatedTime) : null,
                })
            .ToPagedListAsync(request, cancellationToken, defaultSort: "-createdAt");

        // An Admin revokes every key, a UserAdmin any but those of a user only an Admin may change (its own excepted), an
        // Auditor none.
        var permissions = await permissionAccessor.GetCurrentAsync(cancellationToken);
        var revokesAny = ApiKeyManagementCaller.IsAdmin(administrator, permissions);
        var revokesUnprotected = revokesAny || permissions.Holds(BeaconPermissions.UserAdmin);
        var protectedOwners = revokesUnprotected && !revokesAny
            ? await ProtectedUsers.ProtectedAmongAsync(
                context,
                page.Items
                    .Where(x => x.UserId != null)
                    .Where(x => x.UserId != administrator.Id)
                    .Select(x => x.UserId!.Value)
                    .ToList(),
                roleMapping.Value,
                cancellationToken)
            : new HashSet<int>();

        // Active by the rule key validation applies: not revoked, not expired, owner present, enabled, not archived,
        // still in the owner's API-key generation, and the owner not inactive for too long (when that rule is on).
        var now = timeProvider.GetUtcNow().UtcDateTime;

        return page.Map(x =>
            new AdminApiKeyEntry(
                x.Id,
                x.Name,
                x.Prefix,
                ApiKeyGrants.ReadScopes(x.Scopes) ?? [],
                ApiKeyGrants.ReadProjectIds(x.AllowedProjectIds),
                x.CreatedAt,
                x.LastUsedAt,
                x.ExpiresAt,
                x.RevokedAt,
                ApiKeyStatus.RefusalReason(
                    x.IsRevoked,
                    x.ExpiresAt,
                    x.CreatedAt,
                    new ApiKeyOwnerState(
                        x.OwnerExists,
                        x.OwnerArchived,
                        x.OwnerEnabled,
                        x.OwnerGenerationIsCurrent,
                        x.OwnerLastSignInAt),
                    now,
                    options.Value) == null,
                x.UserId,
                x.UserName,
                revokesAny || (revokesUnprotected && (x.UserId == null || !protectedOwners.Contains(x.UserId.Value)))));
    }

    private sealed class AdminApiKeyRow
    {
        public int Id { get; init; }

        public string Name { get; init; } = string.Empty;

        public string Prefix { get; init; } = string.Empty;

        public string? Scopes { get; init; }

        public string? AllowedProjectIds { get; init; }

        public DateTime CreatedAt { get; init; }

        public DateTime? LastUsedAt { get; init; }

        public DateTime? ExpiresAt { get; init; }

        public DateTime? RevokedAt { get; init; }

        public bool IsRevoked { get; init; }

        public int? UserId { get; init; }

        public string? UserName { get; init; }

        public bool OwnerExists { get; init; }

        public bool OwnerArchived { get; init; }

        public bool OwnerEnabled { get; init; }

        public bool OwnerGenerationIsCurrent { get; init; }

        public DateTime? OwnerLastSignInAt { get; init; }
    }
}

/// <summary>
/// All API keys, newest first, optionally only those of <see cref="UserId"/>; sortable by name, prefix, createdAt,
/// lastUsedAt, expiresAt, revokedAt and userName.
/// </summary>
[ProjectScopeExempt("key administration, gated by the Auditor and UserAdmin permissions; no project data", Policy = BeaconPermissions.AuditorOrUserAdminPolicy)]
public record GetAllApiKeysQuery : ListRequest, IRequest<PagedList<AdminApiKeyEntry>>
{
    public int? UserId { get; init; }
}

/// <summary>
/// An API key with its owner, as an administrator sees it. Scopes, projects and <see cref="IsActive"/> as in
/// <see cref="ApiKeyEntry"/>. <see cref="CanRevoke"/>: whether the caller may revoke it (an Auditor revokes none; a
/// UserAdmin who is not an Admin none of an Admin's, a super admin's or a functional role holder's but its own).
/// </summary>
public record AdminApiKeyEntry(
    int Id,
    string Name,
    string Prefix,
    string[] Scopes,
    int[]? AllowedProjectIds,
    DateTime CreatedAt,
    DateTime? LastUsedAt,
    DateTime? ExpiresAt,
    DateTime? RevokedAt,
    bool IsActive,
    int? UserId,
    string? UserName,
    bool CanRevoke);
