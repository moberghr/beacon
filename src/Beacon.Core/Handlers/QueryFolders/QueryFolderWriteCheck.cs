using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;

namespace Beacon.Core.Handlers.QueryFolders;

/// <summary>
/// The rule for changing a query folder. Folders are project-less, but changing one moves, strands or relabels the queries
/// it holds: the caller must be able to write every one of them (403 otherwise). A caller whose write scope is unrestricted
/// (an Admin) is not checked, and a folder that holds no query passes.
/// </summary>
internal static class QueryFolderWriteCheck
{
    /// <param name="withSubfolders">Whether the queries of the folder's descendant folders count too.</param>
    public static async Task DemandWriteOnEveryQueryAsync(
        BeaconContext context,
        IProjectAccessEvaluator projectAccess,
        IBeaconActorAccessor actorAccessor,
        ILogger logger,
        int folderId,
        bool withSubfolders,
        CancellationToken cancellationToken)
    {
        var writable = (await projectAccess.GetScopeAsync(cancellationToken)).For(ProjectAction.Write);
        if (writable.Ids == null)
        {
            return;
        }

        var isWritable = ProjectAccessQueryableExtensions.AllSourcesInOneProjectOf<Query>(context, writable, x => x.Steps.Select(y => y.DataSourceId), x => x.OwnerUserId);
        var isNotWritable = Expression.Lambda<Func<Query, bool>>(Expression.Not(isWritable.Body), isWritable.Parameters);
        var held = withSubfolders
            ? InFolderOrBelow(context, folderId)
            : context.Queries.Where(x => x.FolderId == folderId);
        var holdsUnwritable = await held.AnyAsync(isNotWritable, cancellationToken);
        if (holdsUnwritable)
        {
            var actor = await actorAccessor.GetCurrentAsync(cancellationToken);

            throw AccessRefusal.Of(logger, actor, "query folder", folderId, "The folder holds queries you cannot change.");
        }
    }

    // The queries of the folder and of every folder whose path lies under its path.
    private static IQueryable<Query> InFolderOrBelow(BeaconContext context, int folderId)
    {
        var subtree = context.QueryFolders
            .Where(x => x.Id == folderId)
            .SelectMany(x => context.QueryFolders.Where(y => y.Id == x.Id || y.Path.StartsWith(x.Path + "/")))
            .Select(x => (int?)x.Id);

        return context.Queries
            .Where(x => subtree.Contains(x.FolderId));
    }
}
