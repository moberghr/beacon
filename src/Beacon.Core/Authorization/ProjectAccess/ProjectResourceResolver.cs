using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;

namespace Beacon.Core.Authorization.ProjectAccess;

/// <summary>Reads where a <see cref="ProjectResource"/> lives (§6.2): one database command per resource.</summary>
internal interface IProjectResourceResolver
{
    Task<ResolvedProjectResource> ResolveAsync(BeaconContext context, ProjectResource resource, CancellationToken cancellationToken);
}

/// <summary>
/// The default <see cref="IProjectResourceResolver"/>: a project's resources resolve to the project, data-source-addressed
/// resources to the data sources they read (a query to its steps' sources, a run to the sources it recorded, a contract or
/// AI actor to its source), and child resources to their parent (a task to its subscription's query, a plan to its AI
/// actor, a notification to its run). Archived parents make the resource missing, as their handlers do. A resource from a
/// query without steps carries the query's owner, who alone (with Admins) reaches it; a run whose data sources cannot be
/// told (none recorded and none on its live query, or its subscription or query archived) is an Admin's.
/// </summary>
internal sealed class ProjectResourceResolver : IProjectResourceResolver
{
    public async Task<ResolvedProjectResource> ResolveAsync(
        BeaconContext context,
        ProjectResource resource,
        CancellationToken cancellationToken)
    {
        var id = resource.Id;

        return resource.Kind switch
        {
            ProjectResourceKind.Project => await ProjectAsync(context, id, cancellationToken),
            ProjectResourceKind.DataSource => await DataSourceAsync(context, id, cancellationToken),
            ProjectResourceKind.DataSources => await DataSourcesAsync(context, resource.Ids, cancellationToken),
            ProjectResourceKind.Query => await QueryAsync(context, id, cancellationToken),
            ProjectResourceKind.QueryVersion => await QueryVersionAsync(context, id, cancellationToken),
            ProjectResourceKind.ApprovalRequest => await ApprovalRequestAsync(context, id, cancellationToken),
            ProjectResourceKind.Subscription => await SubscriptionAsync(context, id, cancellationToken),
            ProjectResourceKind.Run => await RunAsync(context, id, cancellationToken),
            ProjectResourceKind.Notification => await NotificationAsync(context, id, cancellationToken),
            ProjectResourceKind.Task => await TaskAsync(context, id, cancellationToken),
            ProjectResourceKind.DataContract => await DataContractAsync(context, id, cancellationToken),
            ProjectResourceKind.AiActor => await AiActorAsync(context, id, cancellationToken),
            ProjectResourceKind.AiActorPlan => await AiActorPlanAsync(context, id, cancellationToken),
            ProjectResourceKind.SchemaRelationship => await SchemaRelationshipAsync(context, id, cancellationToken),
            ProjectResourceKind.Documentation => await DocumentationAsync(context, id, cancellationToken),
            ProjectResourceKind.DocumentationSection => await DocumentationSectionAsync(context, id, cancellationToken),
            ProjectResourceKind.ImportedDocument => await ImportedDocumentAsync(context, id, cancellationToken),
            ProjectResourceKind.LearnedPattern => await LearnedPatternAsync(context, id, cancellationToken),
            ProjectResourceKind.DocumentationPatch => await DocumentationPatchAsync(context, id, cancellationToken),
            ProjectResourceKind.QuerySignal => await QuerySignalAsync(context, id, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(resource), resource.Kind, "Unknown project resource kind.")
        };
    }

    private static async Task<ResolvedProjectResource> ProjectAsync(BeaconContext context, int id, CancellationToken cancellationToken)
    {
        var exists = await context.Projects
            .Where(x => x.Id == id)
            .AnyAsync(cancellationToken);

        return exists ? ResolvedProjectResource.InProject(id) : ResolvedProjectResource.Missing;
    }

    private static async Task<ResolvedProjectResource> DataSourceAsync(BeaconContext context, int id, CancellationToken cancellationToken)
    {
        var exists = await context.DataSources
            .Where(x => x.Id == id)
            .AnyAsync(cancellationToken);

        return exists ? ResolvedProjectResource.ThroughSources([id]) : ResolvedProjectResource.Missing;
    }

    private static async Task<ResolvedProjectResource> DataSourcesAsync(
        BeaconContext context,
        IReadOnlyList<int> ids,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return ResolvedProjectResource.ThroughSources([]);
        }

        var found = await context.DataSources
            .Where(x => ids.Contains(x.Id))
            .CountAsync(cancellationToken);

        return found == ids.Count ? ResolvedProjectResource.ThroughSources(ids) : ResolvedProjectResource.Missing;
    }

    private static async Task<ResolvedProjectResource> QueryAsync(BeaconContext context, int id, CancellationToken cancellationToken)
    {
        var query = await context.Queries
            .Where(x => x.Id == id)
            .Select(x =>
                new
                {
                    x.LockedByUserId,
                    x.OwnerUserId,
                    Sources = x.Steps
                        .Select(y => y.DataSourceId)
                        .ToList()
                })
            .FirstOrDefaultAsync(cancellationToken);

        return query == null
            ? ResolvedProjectResource.Missing
            : ResolvedProjectResource.ThroughQuery(Distinct(query.Sources), query.OwnerUserId, query.LockedByUserId);
    }

    private static async Task<ResolvedProjectResource> QueryVersionAsync(BeaconContext context, int id, CancellationToken cancellationToken)
    {
        var version = await context.QueryVersions
            .Where(x => x.Id == id)
            .Select(x =>
                new VersionRow
                {
                    DataSourceIds = x.DataSourceIds,
                    StepsJson = x.DataSourceIds == null ? x.StepsJson : null,
                    QueryOwnerUserId = x.Query.OwnerUserId,
                    QuerySources = x.Query.Steps
                        .Select(y => y.DataSourceId)
                        .ToList()
                })
            .FirstOrDefaultAsync(cancellationToken);

        return OfVersion(version);
    }

    private static async Task<ResolvedProjectResource> ApprovalRequestAsync(BeaconContext context, int id, CancellationToken cancellationToken)
    {
        var version = await context.QueryApprovalRequests
            .Where(x => x.Id == id)
            .Select(x =>
                new VersionRow
                {
                    DataSourceIds = x.QueryVersion.DataSourceIds,
                    StepsJson = x.QueryVersion.DataSourceIds == null ? x.QueryVersion.StepsJson : null,
                    QueryOwnerUserId = x.Query.OwnerUserId,
                    QuerySources = x.Query.Steps
                        .Select(y => y.DataSourceId)
                        .ToList()
                })
            .FirstOrDefaultAsync(cancellationToken);

        return OfVersion(version);
    }

    private static async Task<ResolvedProjectResource> SubscriptionAsync(BeaconContext context, int id, CancellationToken cancellationToken)
    {
        var subscription = await context.Subscriptions
            .Where(x => x.Id == id)
            .Select(x =>
                new
                {
                    x.OwnerUserId,
                    QueryOwnerUserId = x.Query.OwnerUserId,
                    Sources = x.Query.Steps
                        .Select(y => y.DataSourceId)
                        .ToList()
                })
            .FirstOrDefaultAsync(cancellationToken);

        return subscription == null
            ? ResolvedProjectResource.Missing
            : ResolvedProjectResource.ThroughQuery(Distinct(subscription.Sources), subscription.QueryOwnerUserId, subscription.OwnerUserId);
    }

    private static async Task<ResolvedProjectResource> RunAsync(BeaconContext context, int id, CancellationToken cancellationToken)
    {
        var run = await context.QueryExecutionHistory
            .Where(x => x.Id == id)
            .Select(x =>
                new RunRow
                {
                    DataSourceIds = x.DataSourceIds,
                    HasLiveQuery = context.Subscriptions
                        .Where(y => y.Id == x.SubscriptionId)
                        .Any(y => y.Query.ArchivedTime == null),
                    LegacySources = context.Subscriptions
                        .Where(y => y.Id == x.SubscriptionId)
                        .SelectMany(y => y.Query.Steps)
                        .Select(y => y.DataSourceId)
                        .ToList()
                })
            .FirstOrDefaultAsync(cancellationToken);

        return OfRun(run);
    }

    private static async Task<ResolvedProjectResource> NotificationAsync(BeaconContext context, int id, CancellationToken cancellationToken)
    {
        var run = await context.Notifications
            .Where(x => x.Id == id)
            .Select(x =>
                new RunRow
                {
                    DataSourceIds = x.QueryExecutionHistory.DataSourceIds,
                    HasLiveQuery = context.Subscriptions
                        .Where(y => y.Id == x.QueryExecutionHistory.SubscriptionId)
                        .Any(y => y.Query.ArchivedTime == null),
                    LegacySources = context.Subscriptions
                        .Where(y => y.Id == x.QueryExecutionHistory.SubscriptionId)
                        .SelectMany(y => y.Query.Steps)
                        .Select(y => y.DataSourceId)
                        .ToList()
                })
            .FirstOrDefaultAsync(cancellationToken);

        return OfRun(run);
    }

    private static async Task<ResolvedProjectResource> TaskAsync(BeaconContext context, int id, CancellationToken cancellationToken)
    {
        var task = await context.QueryTasks
            .Where(x => x.Id == id)
            .Select(x =>
                new
                {
                    QueryOwnerUserId = x.Subscription.Query.OwnerUserId,
                    Sources = x.Subscription.Query.Steps
                        .Select(y => y.DataSourceId)
                        .ToList()
                })
            .FirstOrDefaultAsync(cancellationToken);

        return task == null ? ResolvedProjectResource.Missing : ResolvedProjectResource.ThroughQuery(Distinct(task.Sources), task.QueryOwnerUserId);
    }

    private static async Task<ResolvedProjectResource> DataContractAsync(BeaconContext context, int id, CancellationToken cancellationToken)
    {
        var contract = await context.DataContracts
            .Where(x => x.Id == id)
            .Select(x =>
                new
                {
                    x.DataSourceId,
                    x.OwnerUserId
                })
            .FirstOrDefaultAsync(cancellationToken);

        return contract == null
            ? ResolvedProjectResource.Missing
            : ResolvedProjectResource.ThroughSources([contract.DataSourceId], contract.OwnerUserId);
    }

    private static async Task<ResolvedProjectResource> AiActorAsync(BeaconContext context, int id, CancellationToken cancellationToken)
    {
        var actor = await context.AiActors
            .Where(x => x.Id == id)
            .Select(x =>
                new
                {
                    x.DataSourceId,
                    x.CreatedByUserId
                })
            .FirstOrDefaultAsync(cancellationToken);

        return actor == null
            ? ResolvedProjectResource.Missing
            : ResolvedProjectResource.ThroughSources([actor.DataSourceId], actor.CreatedByUserId);
    }

    private static async Task<ResolvedProjectResource> AiActorPlanAsync(BeaconContext context, int id, CancellationToken cancellationToken)
    {
        var actor = await context.AiActorPlans
            .Where(x => x.Id == id)
            .Select(x =>
                new
                {
                    x.AiActor.DataSourceId,
                    x.AiActor.CreatedByUserId
                })
            .FirstOrDefaultAsync(cancellationToken);

        return actor == null
            ? ResolvedProjectResource.Missing
            : ResolvedProjectResource.ThroughSources([actor.DataSourceId], actor.CreatedByUserId);
    }

    private static async Task<ResolvedProjectResource> SchemaRelationshipAsync(BeaconContext context, int id, CancellationToken cancellationToken)
    {
        var dataSourceId = await context.SchemaRelationships
            .Where(x => x.Id == id)
            .Select(x => (int?)x.DataSourceId)
            .FirstOrDefaultAsync(cancellationToken);

        return dataSourceId == null ? ResolvedProjectResource.Missing : ResolvedProjectResource.ThroughSources([dataSourceId.Value]);
    }

    private static async Task<ResolvedProjectResource> DocumentationAsync(BeaconContext context, int id, CancellationToken cancellationToken)
    {
        var projectId = await context.ProjectDocumentations
            .Where(x => x.Id == id)
            .Select(x => (int?)x.ProjectId)
            .FirstOrDefaultAsync(cancellationToken);

        return InProject(projectId);
    }

    private static async Task<ResolvedProjectResource> DocumentationSectionAsync(BeaconContext context, int id, CancellationToken cancellationToken)
    {
        var projectId = await context.ProjectDocumentationSections
            .Where(x => x.Id == id)
            .Select(x => (int?)x.Documentation.ProjectId)
            .FirstOrDefaultAsync(cancellationToken);

        return InProject(projectId);
    }

    private static async Task<ResolvedProjectResource> ImportedDocumentAsync(BeaconContext context, int id, CancellationToken cancellationToken)
    {
        var projectId = await context.ProjectImportedDocuments
            .Where(x => x.Id == id)
            .Select(x => (int?)x.ProjectId)
            .FirstOrDefaultAsync(cancellationToken);

        return InProject(projectId);
    }

    private static async Task<ResolvedProjectResource> LearnedPatternAsync(BeaconContext context, int id, CancellationToken cancellationToken)
    {
        var projectId = await context.McpLearnedPatterns
            .Where(x => x.Id == id)
            .Select(x => (int?)x.ProjectId)
            .FirstOrDefaultAsync(cancellationToken);

        return InProject(projectId);
    }

    private static async Task<ResolvedProjectResource> DocumentationPatchAsync(BeaconContext context, int id, CancellationToken cancellationToken)
    {
        var patch = await context.McpDocumentationPatches
            .Where(x => x.Id == id)
            .Select(x =>
                new
                {
                    x.ProjectId,
                    x.DataSourceId
                })
            .FirstOrDefaultAsync(cancellationToken);

        return patch == null
            ? ResolvedProjectResource.Missing
            : ResolvedProjectResource.InProject(patch.ProjectId, [patch.DataSourceId]);
    }

    // A signal recorded without a project is an Admin's alone.
    private static async Task<ResolvedProjectResource> QuerySignalAsync(BeaconContext context, int id, CancellationToken cancellationToken)
    {
        var signal = await context.McpQuerySignals
            .Where(x => x.Id == id)
            .Select(x => new { x.ProjectId })
            .FirstOrDefaultAsync(cancellationToken);
        if (signal == null)
        {
            return ResolvedProjectResource.Missing;
        }

        return signal.ProjectId == null ? ResolvedProjectResource.AdminOnly : ResolvedProjectResource.InProject(signal.ProjectId.Value);
    }

    private static ResolvedProjectResource InProject(int? projectId)
    {
        return projectId == null ? ResolvedProjectResource.Missing : ResolvedProjectResource.InProject(projectId.Value);
    }

    // A version is reachable when its own data sources are and its query's are; a legacy version whose snapshot cannot be
    // read is an Admin's.
    private static ResolvedProjectResource OfVersion(VersionRow? version)
    {
        if (version == null)
        {
            return ResolvedProjectResource.Missing;
        }

        var sources = QueryVersionSources.Of(version.DataSourceIds, version.StepsJson);
        if (sources == null)
        {
            return ResolvedProjectResource.AdminOnly;
        }

        return new ResolvedProjectResource
        {
            SourceSets = [Distinct(sources), Distinct(version.QuerySources)],
            QueryOwnerUserId = version.QueryOwnerUserId
        };
    }

    // A run is reachable by the data sources it read: the recorded ones, or for a run recorded before Beacon kept them,
    // its live query's. Without any (an empty record, a query without steps, an archived or missing subscription or
    // query) it is an Admin's, never open.
    private static ResolvedProjectResource OfRun(RunRow? run)
    {
        if (run == null)
        {
            return ResolvedProjectResource.Missing;
        }

        if (run.DataSourceIds != null)
        {
            return run.DataSourceIds.Length == 0
                ? ResolvedProjectResource.AdminOnly
                : ResolvedProjectResource.ThroughSources(Distinct(run.DataSourceIds));
        }

        return !run.HasLiveQuery || run.LegacySources.Count == 0
            ? ResolvedProjectResource.AdminOnly
            : new ResolvedProjectResource { SourceSets = [Distinct(run.LegacySources)], IsLegacyRun = true };
    }

    private static IReadOnlyList<int> Distinct(IEnumerable<int> ids)
    {
        return ids
            .Distinct()
            .Order()
            .ToList();
    }

    private sealed class VersionRow
    {
        public int[]? DataSourceIds { get; init; }

        public string? StepsJson { get; init; }

        public string? QueryOwnerUserId { get; init; }

        public List<int> QuerySources { get; init; } = [];
    }

    private sealed class RunRow
    {
        public int[]? DataSourceIds { get; init; }

        public bool HasLiveQuery { get; init; }

        public List<int> LegacySources { get; init; } = [];
    }
}
