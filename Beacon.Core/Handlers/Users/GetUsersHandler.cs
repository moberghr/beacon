using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.Users;

internal sealed class GetUsersHandler(IUserManagementService userService)
    : IRequestHandler<GetUsersQuery, GetUsersResult>
{
    public async Task<GetUsersResult> Handle(GetUsersQuery request, CancellationToken cancellationToken)
    {
        var users = await userService.GetUsersAsync(request.Search, cancellationToken);

        var entries = users
            .Select(x =>
                new UserEntry(
                    x.Id,
                    x.UserName,
                    x.Email,
                    x.DisplayName,
                    x.IsInternalUser,
                    x.IsSuperAdmin,
                    x.IsEnabled,
                    x.LastLoginAt,
                    x.Roles.Select(y => new UserRoleEntry(y.Id, y.Name, y.Level)).ToList()))
            .ToList();

        return new GetUsersResult(entries);
    }
}

public record GetUsersQuery(string? Search) : IRequest<GetUsersResult>;

public record GetUsersResult(List<UserEntry> Entries);

public record UserEntry(
    int Id,
    string UserName,
    string? Email,
    string? DisplayName,
    bool IsInternalUser,
    bool IsSuperAdmin,
    bool IsEnabled,
    DateTime? LastLoginAt,
    List<UserRoleEntry> Roles);

public record UserRoleEntry(int Id, string Name, int Level);
