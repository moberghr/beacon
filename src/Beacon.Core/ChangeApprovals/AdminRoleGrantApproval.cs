using System.Globalization;
using System.Text.Json;
using Beacon.Core.Authentication;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.Approvals;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.ChangeApprovals;

/// <summary>
/// A grant of the Admin role under the approval workflow (<see cref="ApprovalWorkflowOptions.CoversAdminRoleGrants"/>,
/// kind <see cref="ChangeApprovalKind.AdminRoleGrant"/>). A save that gives a user the Admin role — a user created with it,
/// or it added to a user's roles — applies everything else it changes and submits the grant instead of applying it: a
/// request of the user (<c>ResourceId</c> is <c>Users.Id</c>) whose proposal holds ids and the role's name only, never the
/// user's name, e-mail or external id. One pending request per user and role: saving the same grant again while one waits
/// adds none. The requester must be a user Beacon can name, whatever <see cref="ApprovalWorkflowOptions.AllowSelfApproval"/>
/// says. Only a live Admin other than the requester reads and decides the request; approving it adds the role (recorded as
/// assigned by the requester), renews the user's security stamp and records the role change, while the user still exists
/// and does not hold the Admin role. Removing the Admin role, and granting a functional role, are not reviewed.
/// </summary>
internal sealed class AdminRoleGrantApproval(
    IBeaconActorAccessor actorAccessor,
    BeaconConfiguration configuration,
    ILogger<AdminRoleGrantApproval> logger)
{
    /// <summary>What a request records as its change: the role's name, never the user's.</summary>
    internal const string ChangeSummary = "Admin role granted";

    /// <summary>Why a grant of the Admin role needs a requester Beacon can name.</summary>
    internal const string UnidentifiedRequesterMessage =
        "A grant of the Admin role waits for another Admin's approval and needs a requester Beacon can name: sign in as a Beacon user to make it.";

    /// <summary>Why a grant of the Admin role to a user who already holds it is not approved.</summary>
    internal const string AlreadyAdminMessage = "The user already holds the Admin role: reject the request.";

    /// <summary>What a save that submitted a grant of the Admin role answers.</summary>
    internal const string GrantWaitsMessage = "The Admin role waits for another Admin's approval.";

    /// <summary>The state of a user who holds the Admin role already (or is a super admin): no grant of it applies.</summary>
    internal const string AlreadyAdminState = "admin";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>True when the workflow is on and reviews grants of the Admin role.</summary>
    public bool Covers => configuration.ApprovalWorkflow.Covers(ChangeApprovalKind.AdminRoleGrant);

    /// <summary>True for the Admin role's name (ignoring case, so no casing of it escapes review).</summary>
    public static bool IsAdminRole(string? roleName)
    {
        return string.Equals(roleName?.Trim(), RoleService.RoleNames.Admin, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The definition a grant of role <paramref name="roleId"/> to user <paramref name="userId"/> is recorded by.</summary>
    public static string Hash(int userId, int roleId)
    {
        return ChangeApprovalHashes.Sha256(string.Create(CultureInfo.InvariantCulture, $"admin-role-grant:v1:{userId}:{roleId}"));
    }

    /// <summary>
    /// The caller as the requester of a grant of the Admin role to user <paramref name="userId"/> (null for a user not
    /// created yet). Refuses (403) a caller without a user id, also when the host allows self-approval: nobody could tell
    /// their grant from the approver's own. Called before anything is written.
    /// </summary>
    public async Task<ChangeRequester> RequesterAsync(int? userId, CancellationToken cancellationToken)
    {
        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        if (actor.UserId == null)
        {
            throw AccessRefusal.Of(logger, actor, "user roles", userId, UnidentifiedRequesterMessage);
        }

        return new ChangeRequester(actor.UserId, null);
    }

    /// <summary>
    /// The user's row, by a no-op update that holds its lock until the transaction ends, as a decision takes it: a
    /// submission and a decision on one user run one after the other. 0 for a user that does not exist or is archived.
    /// </summary>
    public static Task<int> LockUserAsync(BeaconContext context, int userId, CancellationToken cancellationToken)
    {
        return context.Users
            .Where(x => x.Id == userId)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.SecurityStamp, y => y.SecurityStamp), cancellationToken);
    }

    /// <summary>
    /// Adds (unsaved) the request of a grant of the Admin role <paramref name="adminRoleId"/> to user
    /// <paramref name="userId"/>, in the unit of work that writes the user (its row taken first, or saved first for a new
    /// user); none when a pending request already proposes that grant.
    /// </summary>
    public async Task<ChangeSubmission> SubmitAsync(
        BeaconContext context,
        ChangeRequester requester,
        int userId,
        int adminRoleId,
        CancellationToken cancellationToken)
    {
        var hash = Hash(userId, adminRoleId);
        var alreadyProposed = await context.ChangeApprovalRequests
            .Where(x => x.Kind == ChangeApprovalKind.AdminRoleGrant)
            .Where(x => x.ResourceId == userId)
            .Where(x => x.Status == ApprovalStatus.Pending)
            .Where(x => x.DefinitionHash == hash)
            .AnyAsync(cancellationToken);
        if (alreadyProposed)
        {
            return new ChangeSubmission([], null);
        }

        var proposed = new AdminRoleGrantView
        {
            UserId = userId,
            RoleId = adminRoleId,
            RoleName = RoleService.RoleNames.Admin
        };
        var proposedJson = JsonSerializer.Serialize(proposed, JsonOptions);
        var request = new ChangeApprovalRequest
        {
            Kind = ChangeApprovalKind.AdminRoleGrant,
            ResourceId = userId,
            ResourceName = UserLabel.Of(userId, null),
            Status = ApprovalStatus.Pending,
            DefinitionHash = hash,
            PayloadHash = ChangeApprovalHashes.Sha256(proposedJson),
            ProposedJson = proposedJson,
            RequestedByUserId = requester.UserId,
            RequestedByUserName = await ChangeAuthorLabel.OfAsync(context, requester.UserId, requester.DisplayName, cancellationToken),
            ChangeSummary = ChangeSummary
        };

        context.ChangeApprovalRequests.Add(request);

        return new ChangeSubmission([], request);
    }

    /// <summary>
    /// The user a grant names, as it is now: null when it does not exist or is archived; else its label
    /// (<see cref="UserLabel"/>), whether it holds the Admin role already (a super admin does), and the Admin role's id
    /// (null when there is none).
    /// </summary>
    public static async Task<AdminRoleGrantState?> StateAsync(BeaconContext context, int userId, CancellationToken cancellationToken)
    {
        var user = await context.Users
            .Where(x => x.Id == userId)
            .Select(x =>
                new
                {
                    x.Id,
                    x.DisplayName,
                    HoldsAdmin = x.IsSuperAdmin || x.UserRoles.Any(y => y.Role.Name == RoleService.RoleNames.Admin)
                })
            .FirstOrDefaultAsync(cancellationToken);
        if (user == null)
        {
            return null;
        }

        var adminRoleId = await context.Roles
            .Where(x => x.Name == RoleService.RoleNames.Admin)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return new AdminRoleGrantState(UserLabel.Of(user.Id, user.DisplayName), user.HoldsAdmin, adminRoleId);
    }

    /// <summary>
    /// Approved: user <paramref name="userId"/> holds role <paramref name="roleId"/>, recorded as assigned by
    /// <paramref name="assignedByUserId"/> (the requester), and every session they hold ends (a new security stamp), in the
    /// decision's unit of work; returns the role change's security event, which the caller stages. Ids only.
    /// </summary>
    public static async Task<SecurityAuditEntry> ApplyAsync(
        BeaconContext context,
        int userId,
        int roleId,
        string? assignedByUserId,
        DateTime assignedAt,
        CancellationToken cancellationToken)
    {
        var user = await context.Users
            .Where(x => x.Id == userId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconConflictException(ChangeApprovalService.ResourceGoneMessage);

        // A role change signs out every session the user holds, in the same save.
        user.SecurityStamp = SecurityStamps.New();
        context.UserRoles.Add(new BeaconUserRole
        {
            UserId = userId,
            RoleId = roleId,
            AssignedByUserId = assignedByUserId,
            AssignedAt = assignedAt
        });

        return new SecurityAuditEntry(SecurityAuditActions.RolesChanged)
            .On(SecurityAuditResources.User, userId)
            .With(new SecurityAuditDetails()
                .Add("addedRoleIds", [roleId])
                .Add("removedRoleIds", Array.Empty<int>()));
    }

    /// <summary>Logs a grant of the Admin role submitted for approval: ids only.</summary>
    public void LogSubmitted(ChangeSubmission submission, int userId)
    {
        if (submission.Request is not { } request)
        {
            return;
        }

        logger.LogInformation(
            "The Admin role's grant to user {UserId} waits for approval: change request {RequestId}",
            userId,
            request.Id);
    }
}

/// <summary>A user a grant of the Admin role names, as it is now.</summary>
internal sealed record AdminRoleGrantState(string Label, bool HoldsAdmin, int? AdminRoleId);
