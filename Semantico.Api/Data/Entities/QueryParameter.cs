using Semantico.Api.Data.Entities.Base;

namespace Semantico.Api.Data.Entities;

public class QueryParameter : BaseEntity
{
    public required int QueryId { get; set; }

    public Query Query { get; set; } = null!;
    
    public required string Name { get; set; }
    
    public required string Type { get; set; }
    
    public required string Description { get; set; }
}
