using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Services.Security;

namespace Beacon.Core.Handlers.Projects.Members;

internal sealed class SearchProjectMemberCandidatesHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IProjectAccessEvaluator projectAccess,
    IProjectMembershipSource membershipSource,
    IBeaconActorAccessor actorAccessor,
    RateLimiter rateLimiter,
    ILogger<SearchProjectMemberCandidatesHandler> logger)
    : IRequestHandler<SearchProjectMemberCandidatesQuery, SearchProjectMemberCandidatesResult>
{
    public const int MinimumSearchLength = 2;
    public const int MaxCandidates = 20;
    public const int MaxSearchesPerMinute = 30;

    public async Task<SearchProjectMemberCandidatesResult> Handle(
        SearchProjectMemberCandidatesQuery request,
        CancellationToken cancellationToken)
    {
        ProjectMembershipRules.EnsureManagedByBeacon(membershipSource);

        var search = request.Search?.Trim() ?? string.Empty;
        if (search.Length < MinimumSearchLength)
        {
            throw new InvalidOperationException($"Search for at least {MinimumSearchLength} characters.");
        }

        var scope = await projectAccess.GetScopeAsync(cancellationToken);
        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await ProjectMembershipRules.RequireManageMembersAsync(context, scope, request.ProjectId, actor, logger, cancellationToken);

        // Per caller, so the user directory cannot be enumerated quickly.
        if (!rateLimiter.IsAllowed($"member-candidates:{actor.BeaconUserId ?? 0}:{actor.UserId}", MaxSearchesPerMinute, TimeSpan.FromMinutes(1)))
        {
            throw new BeaconRateLimitedException("Too many member searches; try again in a minute.");
        }

        // Active users only, matched by display name alone (case-insensitive); the display name and id only, never the
        // user name or e-mail.
        var lowered = search.ToLowerInvariant();
        var candidates = await context.Users
            .Where(x => x.IsEnabled)
            .Where(x => x.DisplayName != null)
            .Where(x => x.DisplayName!.ToLower().Contains(lowered))
            .OrderBy(x => x.DisplayName)
            .ThenBy(x => x.Id)
            .Select(x =>
                new
                {
                    x.Id,
                    x.DisplayName,
                    IsMember = context.ProjectMembers
                        .Where(y => y.ProjectId == request.ProjectId)
                        .Any(y => y.UserId == x.Id)
                })
            .Take(MaxCandidates)
            .ToListAsync(cancellationToken);

        return new SearchProjectMemberCandidatesResult(candidates
            .Select(x => new MemberCandidate(x.Id, UserLabel.Of(x.Id, x.DisplayName), x.IsMember))
            .ToList());
    }
}

/// <summary>
/// Active users to add to a project, matched by display name (case-insensitive; never by user name or e-mail): at least
/// 2 characters, at most 20 results, 30 searches a minute per caller. For a ProjectAdmin of the project or an Admin; 409 when the host manages membership.
/// </summary>
public record SearchProjectMemberCandidatesQuery(int ProjectId, string? Search) : IRequest<SearchProjectMemberCandidatesResult>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Project(ProjectId), ProjectAction.ManageMembers)
    ];
}

public record SearchProjectMemberCandidatesResult(List<MemberCandidate> Candidates);

/// <summary>A user who may be added to a project; <c>IsMember</c> when they already are.</summary>
public record MemberCandidate(int UserId, string DisplayName, bool IsMember);
