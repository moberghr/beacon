namespace Beacon.Core.Authorization;

/// <summary>
/// A resource a request addresses by id, for a <see cref="ProjectCheck"/>. <see cref="Kind"/> says how it resolves to a
/// project or to data sources; <see cref="Ids"/> holds its id (or, for <see cref="ProjectResourceKind.DataSources"/>,
/// the data source ids).
/// </summary>
public sealed class ProjectResource
{
    private ProjectResource(ProjectResourceKind kind, IReadOnlyList<int> ids)
    {
        Kind = kind;
        Ids = ids;
    }

    public ProjectResourceKind Kind { get; }

    /// <summary>The resource's id; the distinct data source ids for <see cref="ProjectResourceKind.DataSources"/>.</summary>
    public IReadOnlyList<int> Ids { get; }

    /// <summary>The resource's id (the first data source id for <see cref="ProjectResourceKind.DataSources"/>).</summary>
    public int Id => Ids.Count == 0 ? 0 : Ids[0];

    public static ProjectResource Project(int id) => new(ProjectResourceKind.Project, [id]);

    public static ProjectResource DataSource(int id) => new(ProjectResourceKind.DataSource, [id]);

    /// <summary>Data sources a request names directly: reachable through one project holding every one of them.</summary>
    public static ProjectResource DataSources(IEnumerable<int>? ids) =>
        new(
            ProjectResourceKind.DataSources,
            (ids ?? [])
                .Distinct()
                .Order()
                .ToList());

    public static ProjectResource Query(int id) => new(ProjectResourceKind.Query, [id]);

    public static ProjectResource QueryVersion(int id) => new(ProjectResourceKind.QueryVersion, [id]);

    public static ProjectResource ApprovalRequest(int id) => new(ProjectResourceKind.ApprovalRequest, [id]);

    public static ProjectResource Subscription(int id) => new(ProjectResourceKind.Subscription, [id]);

    public static ProjectResource Run(int id) => new(ProjectResourceKind.Run, [id]);

    public static ProjectResource Notification(int id) => new(ProjectResourceKind.Notification, [id]);

    public static ProjectResource Task(int id) => new(ProjectResourceKind.Task, [id]);

    public static ProjectResource DataContract(int id) => new(ProjectResourceKind.DataContract, [id]);

    public static ProjectResource AiActor(int id) => new(ProjectResourceKind.AiActor, [id]);

    public static ProjectResource AiActorPlan(int id) => new(ProjectResourceKind.AiActorPlan, [id]);

    public static ProjectResource SchemaRelationship(int id) => new(ProjectResourceKind.SchemaRelationship, [id]);

    public static ProjectResource Documentation(int id) => new(ProjectResourceKind.Documentation, [id]);

    public static ProjectResource DocumentationSection(int id) => new(ProjectResourceKind.DocumentationSection, [id]);

    public static ProjectResource ImportedDocument(int id) => new(ProjectResourceKind.ImportedDocument, [id]);

    public static ProjectResource LearnedPattern(int id) => new(ProjectResourceKind.LearnedPattern, [id]);

    public static ProjectResource DocumentationPatch(int id) => new(ProjectResourceKind.DocumentationPatch, [id]);

    public static ProjectResource QuerySignal(int id) => new(ProjectResourceKind.QuerySignal, [id]);

    public override string ToString() => $"{Kind} {string.Join(",", Ids)}";
}
