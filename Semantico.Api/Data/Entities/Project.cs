using Semantico.Api.Data.Entities.Base;
using Semantico.Api.Data.Enums;

namespace Semantico.Api.Data.Entities;

public class Project : ArchivableBaseEntity
{
    public required string Name { get; set; }

    public required string ConnectionString { get; set; }

    public required DatabaseEngineType DatabaseEngine { get; set; }

    public List<Query> Queries { get; set; } = new();
}
