using Beacon.Core.Data.Entities.Base;

namespace Beacon.Core.Data.Entities;

/// <summary>
/// The last time a scheduled maintenance job ran to its end, one row per job (<see cref="Name"/>), so a deployment can
/// tell a retention or verification job that was never scheduled from one that runs. Written by the services the jobs
/// call; carries no audit content.
/// </summary>
public class MaintenanceHeartbeat : BaseEntity
{
    public required string Name { get; set; }

    public DateTime LastRunTime { get; set; }
}
