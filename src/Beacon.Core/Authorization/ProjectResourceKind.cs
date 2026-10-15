namespace Beacon.Core.Authorization;

/// <summary>
/// The kinds of resource a <see cref="ProjectResource"/> names. Each resolves to the project it lives in (a project's
/// documentation), to the data sources it reads (a query, a run, a contract), or to its parent resource (a task to its
/// subscription, a plan to its AI actor).
/// </summary>
public enum ProjectResourceKind
{
    /// <summary>A project. An archived project is missing.</summary>
    Project = 1,

    /// <summary>A data source.</summary>
    DataSource = 2,

    /// <summary>
    /// The data sources a request names directly (a draft's steps, a new query's sources): reachable through one project
    /// holding all of them. Each id must exist.
    /// </summary>
    DataSources = 3,

    /// <summary>A saved query, by its steps' data sources; its lock holder is its recorded owner.</summary>
    Query = 4,

    /// <summary>A query version, by its recorded data sources and its query's.</summary>
    QueryVersion = 5,

    /// <summary>An approval request, as its query version.</summary>
    ApprovalRequest = 6,

    /// <summary>A subscription, as its query; its <c>OwnerUserId</c> is its recorded owner.</summary>
    Subscription = 7,

    /// <summary>A subscription run, by the data sources it recorded (a run recorded before them: its subscription's query).</summary>
    Run = 8,

    /// <summary>A notification, as its run.</summary>
    Notification = 9,

    /// <summary>A task, as its subscription.</summary>
    Task = 10,

    /// <summary>A data contract, by its data source; its <c>OwnerUserId</c> is its recorded owner.</summary>
    DataContract = 11,

    /// <summary>An AI actor, by its data source; its creator is its recorded owner.</summary>
    AiActor = 12,

    /// <summary>An AI actor's plan, as its actor.</summary>
    AiActorPlan = 13,

    /// <summary>A schema relationship, by its data source.</summary>
    SchemaRelationship = 14,

    /// <summary>A project's generated documentation.</summary>
    Documentation = 15,

    /// <summary>A section of a project's generated documentation.</summary>
    DocumentationSection = 16,

    /// <summary>A document imported into a project.</summary>
    ImportedDocument = 17,

    /// <summary>A learned query pattern, in its project.</summary>
    LearnedPattern = 18,

    /// <summary>A documentation patch, in its project; applying it writes its data source's shared metadata.</summary>
    DocumentationPatch = 19,

    /// <summary>A recorded MCP query signal, in its project; one recorded without a project is an Admin's.</summary>
    QuerySignal = 20
}
