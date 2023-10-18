namespace Semantico.Api.Data.Entities.Base;

public class ArchivableBaseEntity : BaseEntity
{
    public DateTime? ArchivedTime { get; set; } = null!;

    public void Archive()
    {
        ArchivedTime = DateTime.UtcNow;
    }
}
