using Beacon.Core.Data;
using Beacon.Core.Helpers;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.Users;

internal sealed class GetUsersHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<GetUsersQuery, PagedList<UserEntry>>
{
    public async Task<PagedList<UserEntry>> Handle(GetUsersQuery request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = context.Users.AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            query = query.Where(x =>
                x.UserName.Contains(request.Search) ||
                (x.Email != null && x.Email.Contains(request.Search)) ||
                (x.DisplayName != null && x.DisplayName.Contains(request.Search)));
        }

        return await query
            .Select(x =>
                new UserEntry
                {
                    Id = x.Id,
                    UserName = x.UserName,
                    Email = x.Email,
                    DisplayName = x.DisplayName,
                    IsInternalUser = x.IsInternalUser,
                    IsSuperAdmin = x.IsSuperAdmin,
                    IsEnabled = x.IsEnabled,
                    LastLoginAt = x.LastLoginAt,
                    Roles = x.UserRoles
                        .Select(y => new UserRoleEntry(y.Role.Id, y.Role.Name, y.Role.Level))
                        .ToList(),
                })
            .ToPagedListAsync(request, cancellationToken, defaultSort: "userName");
    }
}

/// <summary>Alphabetical by user name unless <c>sort</c> says otherwise; <c>search</c> matches name, e-mail and display name.</summary>
public record GetUsersQuery : ListRequest, IRequest<PagedList<UserEntry>>
{
    public string? Search { get; init; }
}

public record UserEntry
{
    public int Id { get; init; }

    public string UserName { get; init; } = string.Empty;

    public string? Email { get; init; }

    public string? DisplayName { get; init; }

    public bool IsInternalUser { get; init; }

    public bool IsSuperAdmin { get; init; }

    public bool IsEnabled { get; init; }

    public DateTime? LastLoginAt { get; init; }

    public List<UserRoleEntry> Roles { get; init; } = [];
}

public record UserRoleEntry(int Id, string Name, int Level);
