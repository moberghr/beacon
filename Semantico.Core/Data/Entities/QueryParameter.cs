using Semantico.Core.Data.Entities.Base;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Data.Entities;

public class QueryParameter : ArchivableBaseEntity
{
    public required int QueryId { get; set; }

    public Query Query { get; set; } = null!;
    
    public required string Name { get; set; }
    
    public required ParameterType Type { get; set; }
    
    public required string Description { get; set; }
    
    public required string Placeholder { get; set; }
}
