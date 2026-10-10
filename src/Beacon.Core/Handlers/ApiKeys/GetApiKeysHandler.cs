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

internal sealed class GetApiKeysHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IHttpContextAccessor httpContextAccessor,
    IUserManagementService userManagementService,
    IOptions<ApiKeyOptions> options,
    TimeProvider timeProvider)
    : IRequestHandler<GetApiKeysQuery, PagedList<ApiKeyEntry>>
{
    public async Task<PagedList<ApiKeyEntry>> Handle(
        GetApiKeysQuery request,
        CancellationToken cancellationToken)
    {
        // API keys are scoped to the user who minted them (§1.4) — only ever return the
        // current user's own keys, never the whole table.
        var user = await ApiKeyManagementCaller.ResolveAsync(httpContextAccessor, userManagementService, cancellationToken);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Scopes and project ids are stored as JSON, so the page is read into arrays after it is loaded.
        var page = await context.ApiKeyCredentials
            .Where(x => x.UserId == user.Id)
            .Select(x =>
                new ApiKeyRow
                {
                    Id = x.Id,
                    Name = x.Name,
                    Prefix = x.KeyPrefix,
                    Scopes = x.Scopes,
                    AllowedProjectIds = x.AllowedProjectIds,
                    CreatedAt = x.CreatedTime,
                    LastUsedAt = x.LastUsedAt,
                    ExpiresAt = x.ExpiresAt,
                    IsRevoked = x.IsRevoked,
                })
            .ToPagedListAsync(request, cancellationToken, defaultSort: "-createdAt");

        // Active by the rule key validation applies; the owner is the caller, found, so not archived.
        var owner = new ApiKeyOwnerState(Exists: true, IsArchived: false, user.IsEnabled);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        return page.Map(x =>
            new ApiKeyEntry(
                x.Id,
                x.Name,
                x.Prefix,
                ApiKeyGrants.ReadScopes(x.Scopes) ?? [],
                x.CreatedAt,
                x.LastUsedAt,
                x.ExpiresAt,
                ApiKeyStatus.RefusalReason(x.IsRevoked, x.ExpiresAt, x.CreatedAt, owner, now, options.Value) == null,
                ApiKeyGrants.ReadProjectIds(x.AllowedProjectIds)));
    }

    private sealed class ApiKeyRow
    {
        public int Id { get; init; }

        public string Name { get; init; } = string.Empty;

        public string Prefix { get; init; } = string.Empty;

        public string? Scopes { get; init; }

        public string? AllowedProjectIds { get; init; }

        public DateTime CreatedAt { get; init; }

        public DateTime? LastUsedAt { get; init; }

        public DateTime? ExpiresAt { get; init; }

        public bool IsRevoked { get; init; }
    }
}

/// <summary>The caller's own keys, newest first; sortable by name, prefix, createdAt, lastUsedAt, expiresAt.</summary>
public record GetApiKeysQuery : ListRequest, IRequest<PagedList<ApiKeyEntry>>;

/// <summary>
/// One API key. <see cref="Scopes"/> are the scopes it was issued with (a key stored with the retired Admin scope shows
/// Execute). <see cref="IsActive"/> is whether it works now: not revoked, not expired, and its owner enabled and not
/// archived. <see cref="AllowedProjectIds"/> is <c>null</c> when the key is not restricted to projects.
/// </summary>
public record ApiKeyEntry(
    int Id,
    string Name,
    string Prefix,
    string[] Scopes,
    DateTime CreatedAt,
    DateTime? LastUsedAt,
    DateTime? ExpiresAt,
    bool IsActive,
    int[]? AllowedProjectIds = null);
