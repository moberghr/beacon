using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Helpers;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.ApiKeys;

internal sealed class GetApiKeysHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconUserContext userContext,
    IUserManagementService userManagementService)
    : IRequestHandler<GetApiKeysQuery, PagedList<ApiKeyEntry>>
{
    public async Task<PagedList<ApiKeyEntry>> Handle(
        GetApiKeysQuery request,
        CancellationToken cancellationToken)
    {
        // API keys are scoped to the user who minted them (§1.4) — only ever return the
        // current user's own keys, never the whole table.
        var externalId = userContext.UserId
            ?? throw new InvalidOperationException("Cannot list API keys without an authenticated user.");

        var user = await userManagementService.GetUserByExternalIdAsync(externalId, cancellationToken)
            ?? throw new InvalidOperationException($"Authenticated user '{externalId}' was not found.");

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Scopes are stored comma-joined, so the page is split into arrays after it is read.
        var page = await context.ApiKeyCredentials
            .Where(x => x.UserId == user.Id)
            .Select(x =>
                new ApiKeyRow
                {
                    Id = x.Id,
                    Name = x.Name,
                    Prefix = x.KeyPrefix,
                    Scopes = x.Scopes,
                    CreatedAt = x.CreatedTime,
                    LastUsedAt = x.LastUsedAt,
                    ExpiresAt = x.ExpiresAt,
                    IsRevoked = x.IsRevoked,
                })
            .ToPagedListAsync(request, cancellationToken, defaultSort: "-createdAt");

        return page.Map(x =>
            new ApiKeyEntry(
                x.Id,
                x.Name,
                x.Prefix,
                (x.Scopes ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                x.CreatedAt,
                x.LastUsedAt,
                x.ExpiresAt,
                !x.IsRevoked));
    }

    private sealed class ApiKeyRow
    {
        public int Id { get; init; }

        public string Name { get; init; } = string.Empty;

        public string Prefix { get; init; } = string.Empty;

        public string? Scopes { get; init; }

        public DateTime CreatedAt { get; init; }

        public DateTime? LastUsedAt { get; init; }

        public DateTime? ExpiresAt { get; init; }

        public bool IsRevoked { get; init; }
    }
}

/// <summary>The caller's own keys, newest first; sortable by name, prefix, createdAt, lastUsedAt, expiresAt.</summary>
public record GetApiKeysQuery : ListRequest, IRequest<PagedList<ApiKeyEntry>>;

public record ApiKeyEntry(
    int Id,
    string Name,
    string Prefix,
    string[] Scopes,
    DateTime CreatedAt,
    DateTime? LastUsedAt,
    DateTime? ExpiresAt,
    bool IsActive);
