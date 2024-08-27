namespace Semantico.Core.Data.Entities.Base;

internal abstract class BaseEntity
{
    public int Id { get; set; }

    public DateTime CreatedTime { get; set; } = DateTime.UtcNow;
}
