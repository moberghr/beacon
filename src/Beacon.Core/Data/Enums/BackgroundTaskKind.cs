namespace Beacon.Core.Data.Enums;

/// <summary>The kinds of <see cref="Entities.BackgroundTask"/>; each names the kind of resource its subject id addresses.</summary>
public enum BackgroundTaskKind
{
    /// <summary>A forced re-read of a data source's metadata; the subject is the data source.</summary>
    MetadataRefresh = 1,

    /// <summary>A think cycle an AI actor's creator or an Admin asked for now; the subject is the AI actor.</summary>
    AiActorThink = 2,

    /// <summary>A manual run of a migration job; the subject is the migration job.</summary>
    MigrationRun = 3,

    /// <summary>A scan of every repository of a project for code references; the subject is the project.</summary>
    RepositoryScan = 4,

    /// <summary>A regeneration of a project's documentation; the subject is the project.</summary>
    DocumentationGeneration = 5
}
