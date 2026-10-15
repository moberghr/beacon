using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Handlers.Users;

/// <summary>
/// The live resources of one kind a user owns, created or changed last, for offboarding: queries, subscriptions and data
/// contracts by owner, creator and last modifier; recipients and migration jobs (which have no owner) by creator and last
/// modifier. People are matched by the <c>Users.ExternalId</c> resources record; an archived user is found too, since
/// offboarding often follows the archive. Read across projects, so for an Auditor or a UserAdmin only, and recorded first
/// (<see cref="SecurityAuditActions.OwnedResourcesRead"/>; the read fails when the event cannot be written). Every resource
/// is listed by id and kind; its name only when the caller may read it (an Auditor reads every name; a UserAdmin the names
/// of the resources its projects reach, as the resource's own list shows them; recipients, which belong to no project,
/// always): a UserAdmin offboards a user without reading other projects' resource names.
/// </summary>
internal sealed class GetUserOwnedResourcesHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconPermissionAccessor permissionAccessor,
    IProjectAccessEvaluator projectAccess,
    SecurityAuditReads reads)
    : IRequestHandler<GetUserOwnedResourcesQuery, PagedList<OwnedResourceItem>>
{
    public async Task<PagedList<OwnedResourceItem>> Handle(GetUserOwnedResourcesQuery request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var externalId = await context.Users
            .IgnoreQueryFilters()
            .Where(x => x.Id == request.Id)
            .Select(x => x.ExternalId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconNotFoundException($"User {request.Id} not found.");

        await reads.RecordAsync(
            new SecurityAuditEntry(SecurityAuditActions.OwnedResourcesRead)
                .On(SecurityAuditResources.User, request.Id)
                .With(new SecurityAuditDetails()
                    .Add("kind", request.Kind)
                    .Add("page", request.PageOrDefault)
                    .Add("pageSize", request.PageSizeOrDefault)),
            cancellationToken);

        var page = await PageAsync(context, request, externalId, cancellationToken);
        if ((await permissionAccessor.GetCurrentAsync(cancellationToken)).Holds(BeaconPermissions.Auditor))
        {
            return page;
        }

        var readable = (await projectAccess.GetScopeAsync(cancellationToken)).For(ProjectAction.Read);
        var named = await ReadableIdsAsync(
            context,
            request.Kind,
            page.Items
                .Select(x => x.Id)
                .ToList(),
            readable,
            cancellationToken);

        return page.Map(x => named.Contains(x.Id) ? x : x with { Name = string.Empty, NameWithheld = true });
    }

    // Of the page's resources, those the caller reads as the resource's own list decides; recipients belong to no project.
    private static async Task<HashSet<int>> ReadableIdsAsync(
        BeaconContext context,
        OwnedResourceKind kind,
        List<int> ids,
        ProjectSet readable,
        CancellationToken cancellationToken)
    {
        if (readable.Ids == null || kind == OwnedResourceKind.Recipient || ids.Count == 0)
        {
            return ids.ToHashSet();
        }

        var readableIds = kind switch
        {
            OwnedResourceKind.Query => context.Queries
                .Where(x => ids.Contains(x.Id))
                .WhereAllSourcesInOneProject(context, readable, x => x.Steps.Select(y => y.DataSourceId), x => x.OwnerUserId)
                .Select(x => x.Id),
            OwnedResourceKind.Subscription => context.Subscriptions
                .Where(x => ids.Contains(x.Id))
                .WhereAllSourcesInOneProject(context, readable, x => x.Query.Steps.Select(y => y.DataSourceId), x => x.Query.OwnerUserId)
                .Select(x => x.Id),
            OwnedResourceKind.DataContract => context.DataContracts
                .Where(x => ids.Contains(x.Id))
                .WhereSourceAccessible(context, readable, x => x.DataSourceId)
                .Select(x => x.Id),
            OwnedResourceKind.MigrationJob => context.MigrationJobs
                .Where(x => ids.Contains(x.Id))
                .WhereSourceAccessible(context, readable, x => x.DataSourceId)
                .WhereSourceAccessible(context, readable, x => x.DestinationDataSourceId)
                .Select(x => x.Id),
            _ => throw new InvalidOperationException("Unknown resource kind.")
        };

        return (await readableIds.ToListAsync(cancellationToken)).ToHashSet();
    }

    private static async Task<PagedList<OwnedResourceItem>> PageAsync(
        BeaconContext context,
        GetUserOwnedResourcesQuery request,
        string externalId,
        CancellationToken cancellationToken)
    {
        return request.Kind switch
        {
            OwnedResourceKind.Query => await context.Queries
                .Where(x => x.OwnerUserId == externalId || x.CreatedByUserId == externalId || x.ModifiedByUserId == externalId)
                .Select(x =>
                    new OwnedResourceItem
                    {
                        Id = x.Id,
                        Kind = OwnedResourceKind.Query,
                        Name = x.Name,
                        IsOwner = x.OwnerUserId == externalId,
                        IsCreator = x.CreatedByUserId == externalId,
                        IsLastModifier = x.ModifiedByUserId == externalId,
                        CreatedTime = x.CreatedTime,
                        ModifiedTime = x.ModifiedTime
                    })
                .ToPagedListAsync(request, cancellationToken, defaultSort: "id"),
            OwnedResourceKind.Subscription => await context.Subscriptions
                .Where(x => x.OwnerUserId == externalId || x.CreatedByUserId == externalId || x.ModifiedByUserId == externalId)
                .Select(x =>
                    new OwnedResourceItem
                    {
                        Id = x.Id,
                        Kind = OwnedResourceKind.Subscription,
                        Name = x.Query.Name,
                        IsOwner = x.OwnerUserId == externalId,
                        IsCreator = x.CreatedByUserId == externalId,
                        IsLastModifier = x.ModifiedByUserId == externalId,
                        CreatedTime = x.CreatedTime,
                        ModifiedTime = x.ModifiedTime
                    })
                .ToPagedListAsync(request, cancellationToken, defaultSort: "id"),
            OwnedResourceKind.DataContract => await context.DataContracts
                .Where(x => x.OwnerUserId == externalId || x.CreatedByUserId == externalId || x.ModifiedByUserId == externalId)
                .Select(x =>
                    new OwnedResourceItem
                    {
                        Id = x.Id,
                        Kind = OwnedResourceKind.DataContract,
                        Name = x.Name,
                        IsOwner = x.OwnerUserId == externalId,
                        IsCreator = x.CreatedByUserId == externalId,
                        IsLastModifier = x.ModifiedByUserId == externalId,
                        CreatedTime = x.CreatedTime,
                        ModifiedTime = x.ModifiedTime
                    })
                .ToPagedListAsync(request, cancellationToken, defaultSort: "id"),
            OwnedResourceKind.Recipient => await context.Recipients
                .Where(x => x.CreatedByUserId == externalId || x.ModifiedByUserId == externalId)
                .Select(x =>
                    new OwnedResourceItem
                    {
                        Id = x.Id,
                        Kind = OwnedResourceKind.Recipient,
                        Name = x.Name,
                        IsOwner = false,
                        IsCreator = x.CreatedByUserId == externalId,
                        IsLastModifier = x.ModifiedByUserId == externalId,
                        CreatedTime = x.CreatedTime,
                        ModifiedTime = x.ModifiedTime
                    })
                .ToPagedListAsync(request, cancellationToken, defaultSort: "id"),
            OwnedResourceKind.MigrationJob => await context.MigrationJobs
                .Where(x => x.CreatedByUserId == externalId || x.ModifiedByUserId == externalId)
                .Select(x =>
                    new OwnedResourceItem
                    {
                        Id = x.Id,
                        Kind = OwnedResourceKind.MigrationJob,
                        Name = x.Name,
                        IsOwner = false,
                        IsCreator = x.CreatedByUserId == externalId,
                        IsLastModifier = x.ModifiedByUserId == externalId,
                        CreatedTime = x.CreatedTime,
                        ModifiedTime = x.ModifiedTime
                    })
                .ToPagedListAsync(request, cancellationToken, defaultSort: "id"),
            _ => throw new InvalidOperationException("Unknown resource kind.")
        };
    }
}

/// <summary>The kinds of resource <see cref="GetUserOwnedResourcesQuery"/> lists, one kind per read.</summary>
public enum OwnedResourceKind
{
    Query,
    Subscription,
    DataContract,
    Recipient,
    MigrationJob
}

/// <summary>
/// One page of the live resources of <see cref="Kind"/> that user <see cref="Id"/> owns, created or changed last, oldest
/// first unless <c>sort</c> says otherwise; a resource the caller may not read is listed without its name. 404 for an
/// unknown user. For an Auditor or a UserAdmin; the read is recorded.
/// </summary>
[ProjectScopeExempt("a user's resources across projects, for offboarding; gated by the Auditor or UserAdmin permission", Policy = BeaconPermissions.AuditorOrUserAdminPolicy)]
public record GetUserOwnedResourcesQuery : ListRequest, IRequest<PagedList<OwnedResourceItem>>
{
    /// <summary>The user's <c>Users.Id</c> (from the route).</summary>
    public int Id { get; init; }

    public OwnedResourceKind Kind { get; init; }
}

/// <summary>
/// A resource and how the user is attributed on it: its owner (whose access its scheduled runs act with), its creator,
/// and the last to change it.
/// </summary>
public record OwnedResourceItem
{
    public int Id { get; init; }

    public OwnedResourceKind Kind { get; init; }

    public string Name { get; init; } = string.Empty;

    public bool IsOwner { get; init; }

    public bool IsCreator { get; init; }

    public bool IsLastModifier { get; init; }

    /// <summary>True when the caller may not read the resource (another project's): <see cref="Name"/> is empty.</summary>
    public bool NameWithheld { get; init; }

    public DateTime CreatedTime { get; init; }

    public DateTime? ModifiedTime { get; init; }
}
