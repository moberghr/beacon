using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.ApiKeys;

internal sealed class GetApiKeysHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconUserContext userContext,
    IUserManagementService userManagementService)
    : IRequestHandler<GetApiKeysQuery, GetApiKeysResult>
{
    public async Task<GetApiKeysResult> Handle(
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

        var rows = await context.ApiKeyCredentials
            .Where(x => x.UserId == user.Id)
            .OrderByDescending(k => k.CreatedTime)
            .Select(k => new
            {
                k.Id,
                k.Name,
                k.KeyPrefix,
                k.Scopes,
                k.CreatedTime,
                k.LastUsedAt,
                k.ExpiresAt,
                k.IsRevoked
            })
            .ToListAsync(cancellationToken);

        var entries = rows.Select(r => new ApiKeyEntry(
            r.Id,
            r.Name,
            r.KeyPrefix,
            (r.Scopes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            r.CreatedTime,
            r.LastUsedAt,
            r.ExpiresAt,
            !r.IsRevoked)).ToList();

        return new GetApiKeysResult(entries);
    }
}

public record GetApiKeysQuery : IRequest<GetApiKeysResult>;

public record GetApiKeysResult(List<ApiKeyEntry> Entries);

public record ApiKeyEntry(
    int Id,
    string Name,
    string Prefix,
    string[] Scopes,
    DateTime CreatedAt,
    DateTime? LastUsedAt,
    DateTime? ExpiresAt,
    bool IsActive);
