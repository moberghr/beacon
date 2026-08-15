namespace Beacon.Core.Data.Enums;

/// <summary>
/// Where a schema relationship came from. Mirrors pgGraph's split between discovery (declared foreign
/// keys) and explicit registration, with an inference tier Beacon needs because warehouse engines
/// (Snowflake, BigQuery, Databricks) rarely declare enforced foreign keys.
/// </summary>
public enum SchemaRelationshipOrigin
{
    /// <summary>Derived from a declared foreign-key constraint. Ground truth — verified on creation.</summary>
    ForeignKey = 0,

    /// <summary>Guessed from column naming conventions. Unverified until a human confirms it.</summary>
    Inferred = 1,

    /// <summary>Declared by a user. Verified on creation.</summary>
    Manual = 2
}
