using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;

namespace Semantico.Core.Handlers.ApiKeys;

internal sealed class GetApiKeysHandler(SemanticoContext context)
    : IRequestHandler<GetApiKeysQuery, GetApiKeysResult>
{
    public async Task<GetApiKeysResult> Handle(
        GetApiKeysQuery request,
        CancellationToken cancellationToken)
    {
        var rows = await context.ApiKeyCredentials
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
