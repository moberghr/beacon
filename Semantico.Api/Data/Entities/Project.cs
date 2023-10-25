using Semantico.Api.Data.Entities.Base;

namespace Semantico.Api.Data.Entities;

public class Project : ArchivableBaseEntity
{
    public required string Name { get; set; }

    public required string ConnectionString { get; set; }

    public List<Query> Queries { get; set; } = new();
}
