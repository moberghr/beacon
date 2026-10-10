using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Helpers;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;

namespace Beacon.Core.Handlers.ApiKeys;

/// <summary>
/// Every user's API keys, or one user's, for an administrator in a signed-in session: the Admin role on the route, and
/// an administrator in the user store now (<see cref="ApiKeyManagementCaller.ResolveAdministratorAsync"/>).
/// </summary>
internal sealed class GetAllApiKeysHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IHttpContextAccessor httpContextAccessor,
    IUserManagementService userManagementService,
    IOptions<ApiKeyOptions> options,
    TimeProvider timeProvider)
    : IRequestHandler<GetAllApiKeysQuery, PagedList<AdminApiKeyEntry>>
{
    public async Task<PagedList<AdminApiKeyEntry>> Handle(
        GetAllApiKeysQuery request,
        CancellationToken cancellationToken)
    {
        await ApiKeyManagementCaller.ResolveAdministratorAsync(httpContextAccessor, userManagementService, cancellationToken);

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
                })
            .ToPagedListAsync(request, cancellationToken, defaultSort: "-createdAt");

        // Active by the rule key validation applies: not revoked, not expired, owner present, enabled, not archived,
        // and still in the owner's API-key generation.
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
                    new ApiKeyOwnerState(x.OwnerExists, x.OwnerArchived, x.OwnerEnabled, x.OwnerGenerationIsCurrent),
                    now,
                    options.Value) == null,
                x.UserId,
                x.UserName));
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
    }
}

/// <summary>
/// All API keys, newest first, optionally only those of <see cref="UserId"/>; sortable by name, prefix, createdAt,
/// lastUsedAt, expiresAt, revokedAt and userName.
/// </summary>
public record GetAllApiKeysQuery : ListRequest, IRequest<PagedList<AdminApiKeyEntry>>
{
    public int? UserId { get; init; }
}

/// <summary>
/// An API key with its owner, as an administrator sees it. Scopes, projects and <see cref="IsActive"/> as in
/// <see cref="ApiKeyEntry"/>.
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
    string? UserName);
