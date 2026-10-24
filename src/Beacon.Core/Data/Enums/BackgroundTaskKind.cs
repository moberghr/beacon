namespace Beacon.Core.Data.Enums;

/// <summary>The kinds of <see cref="Entities.BackgroundTask"/>; each names the kind of resource its subject id addresses.</summary>
public enum BackgroundTaskKind
{
    /// <summary>A forced re-read of a data source's metadata; the subject is the data source.</summary>
    MetadataRefresh = 1,

    /// <summary>A think cycle an AI actor's creator or an Admin asked for now; the subject is the AI actor.</summary>
    AiActorThink = 2
}
