using Semantico.Core.Data.Entities.Base;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Data.Entities;

public class Comment : BaseEntity
{
    public required EntityType EntityType { get; set; }
    public required int EntityId { get; set; }
    public required string Content { get; set; }
    public string? UserId { get; set; }
    public string? UserName { get; set; }
}
