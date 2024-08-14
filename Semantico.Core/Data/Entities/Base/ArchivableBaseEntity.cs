using Semantico.Core.Models;

namespace Semantico.Core.Data.Entities.Base;

internal abstract class ArchivableBaseEntity : BaseEntity
{
    public DateTime? ArchivedTime { get; set; } = null!;

    public void Archive()
    {
        if (ArchivedTime != null)
        {
            throw new SemanticoException($"Unable to archive already archived entity.");
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
