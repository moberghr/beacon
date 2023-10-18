using Semantico.Api.Data.Entities.Base;

namespace Semantico.Api.Data.Entities;

public class Account : ArchivableBaseEntity
{
    public required string Username { get; set; }

    public required string Value { get; set; }
}