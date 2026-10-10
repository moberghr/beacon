using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Helpers;

namespace Beacon.Core.Handlers.Projects.Members;

internal sealed class GetProjectMembersHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IProjectAccessEvaluator projectAccess,
    IProjectMembershipSource membershipSource,
    IBeaconActorAccessor actorAccessor,
    ILogger<GetProjectMembersHandler> logger)
    : IRequestHandler<GetProjectMembersQuery, PagedList<ProjectMemberEntry>>
{
    public async Task<PagedList<ProjectMemberEntry>> Handle(GetProjectMembersQuery request, CancellationToken cancellationToken)
    {
        ProjectMembershipRules.EnsureManagedByBeacon(membershipSource);

        var scope = await projectAccess.GetScopeAsync(cancellationToken);
        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await ProjectMembershipRules.RequireManageMembersAsync(context, scope, request.ProjectId, actor, logger, cancellationToken);

        // Display names only (never the user name or e-mail). A disabled or archived member is listed: the row grants
        // nothing until removed.
        var page = await context.ProjectMembers
            .IgnoreQueryFilters()
            .Where(x => x.ProjectId == request.ProjectId)
            .Select(x =>
                new MemberRow
                {
                    UserId = x.UserId,
                    DisplayName = x.User.DisplayName,
                    Role = x.Role,
                    IsActive = x.User.IsEnabled && x.User.ArchivedTime == null,
                    AddedAt = x.CreatedTime,
                    ModifiedAt = x.ModifiedTime
                })
            .ToPagedListAsync(request, cancellationToken, defaultSort: "displayName", tiebreaker: nameof(MemberRow.UserId));

        return page.Map(x => new ProjectMemberEntry(x.UserId, UserLabel.Of(x.UserId, x.DisplayName), x.Role, x.IsActive, x.AddedAt, x.ModifiedAt));
    }

    private sealed class MemberRow
    {
        public int UserId { get; init; }

        public string? DisplayName { get; init; }

        public ProjectRole Role { get; init; }

        public bool IsActive { get; init; }

        public DateTime AddedAt { get; init; }

        public DateTime? ModifiedAt { get; init; }
    }
}

/// <summary>
/// A project's members, by display name unless <c>sort</c> says otherwise. For a ProjectAdmin of the project or an
/// Admin; 409 when the host manages membership.
/// </summary>
public record GetProjectMembersQuery : ListRequest, IRequest<PagedList<ProjectMemberEntry>>, IProjectAuthorizedRequest
{
    public int ProjectId { get; init; }

    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Project(ProjectId), ProjectAction.ManageMembers)
    ];
}

/// <summary>
/// A member of a project, named by display name (or "User {id}" without one). <c>IsActive</c> is false for a disabled or
/// archived user, whose row grants nothing.
/// </summary>
public record ProjectMemberEntry(
    int UserId,
    string DisplayName,
    ProjectRole Role,
    bool IsActive,
    DateTime AddedAt,
    DateTime? ModifiedAt);
