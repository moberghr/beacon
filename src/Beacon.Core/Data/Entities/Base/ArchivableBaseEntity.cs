using Beacon.Core.Models;

namespace Beacon.Core.Data.Entities.Base;

public abstract class ArchivableBaseEntity : BaseEntity
{
    public DateTime? ArchivedTime { get; set; } = null!;

    public void Archive()
    {
        if (ArchivedTime != null)
        {
            throw new BeaconException($"Unable to archive already archived entity.");
        }

        ArchivedTime = DateTime.UtcNow;
    }

    public void Unarchive()
    {
        if (ArchivedTime != null)
        {
            ArchivedTime = null;
        }
    }
}
