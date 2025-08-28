using Semantico.Core.Data.Entities.Base;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Data.Entities;

internal class Project : ArchivableBaseEntity
{
    public required string Name { get; set; }

    public required string ConnectionString { get; set; }

    public required DatabaseEngineType DatabaseEngineType { get; set; }

    public List<QueryStep> QuerySteps { get; set; } = new();
}
