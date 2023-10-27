using Semantico.Api.Types;

namespace Semantico.Api.Data.Entities.Base;

public abstract class ArchivableBaseEntity : BaseEntity
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
