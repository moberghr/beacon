namespace Beacon.Core.Data.Enums;

/// <summary>The kinds of <see cref="Entities.BackgroundTask"/>; each names the kind of resource its subject id addresses.</summary>
public enum BackgroundTaskKind
{
    /// <summary>A forced re-read of a data source's metadata; the subject is the data source.</summary>
    MetadataRefresh = 1,

    /// <summary>A manual run of a migration job; the subject is the migration job.</summary>
    MigrationRun = 3,

    /// <summary>A scan of every repository of a project for code references; the subject is the project.</summary>
    RepositoryScan = 4
}
