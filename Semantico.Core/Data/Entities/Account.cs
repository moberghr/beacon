using Semantico.Core.Data.Entities.Base;

namespace Semantico.Core.Data.Entities;

public class Account : ArchivableBaseEntity
{
    public required string Username { get; set; }

    public required string Value { get; set; }
}