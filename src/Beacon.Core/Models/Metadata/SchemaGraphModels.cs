using Beacon.Core.Data.Enums;

namespace Beacon.Core.Models.Metadata;

/// <summary>
/// A table in the schema graph. <see cref="IsJunction"/> follows pgGraph's <c>classify_as_junction()</c>:
/// every primary-key column is also a foreign-key column and the table points at two or more distinct
/// tables. Unlike pgGraph — which removes junctions and registers a direct edge between their targets —
/// Beacon keeps them, because the junction table has to appear in the generated SQL. Instead it traverses
/// them at zero cost, so a two-hop many-to-many ranks alongside a one-hop direct relationship.
/// </summary>
public record SchemaGraphNode(
    string SchemaName,
    string TableName,
    bool IsJunction)
{
    public string QualifiedName => $"{SchemaName}.{TableName}";
}

/// <summary>
/// A registered relationship in the shape the graph consumes, decoupled from the persistence entity so
/// the graph stays pure and testable without a database.
/// </summary>
public record SchemaRelationshipEdge(
    string SourceSchema,
    string SourceTable,
    string SourceColumn,
    string TargetSchema,
    string TargetTable,
    string TargetColumn,
    string Label,
    SchemaRelationshipOrigin Origin,
    bool IsVerified,
    double Confidence);

/// <summary>
/// One hop of a join path, carrying everything needed to write the ON clause plus the provenance the
/// prompt needs in order to label an unverified join honestly.
/// </summary>
public record SchemaJoinStep(
    string FromQualifiedName,
    string FromColumn,
    string ToQualifiedName,
    string ToColumn,
    string Label,
    SchemaRelationshipOrigin Origin,
    bool IsVerified,
    double Confidence,
    bool ToIsJunction);

/// <summary>
/// An ordered chain of joins between two tables. <see cref="IsFullyVerified"/> is false when any step
/// rests on an inferred relationship, which is what forces the path into the prompt's unverified block.
/// </summary>
public record SchemaJoinPath(
    string FromQualifiedName,
    string ToQualifiedName,
    IReadOnlyList<SchemaJoinStep> Steps)
{
    public bool IsFullyVerified => Steps.All(x => x.IsVerified);

    public double MinimumConfidence => Steps.Count == 0 ? 1.0 : Steps.Min(x => x.Confidence);

    public IEnumerable<string> IntermediateQualifiedNames => Steps
        .Take(Steps.Count - 1)
        .Select(x => x.ToQualifiedName);
}

/// <summary>
/// Result of expanding a set of seed tables into the set worth describing in full. <see cref="Capped"/>
/// mirrors pgGraph's <c>capped := true</c> contract — a truncated neighbourhood must never be mistaken
/// for a complete one.
/// </summary>
public record SchemaExpansion(
    IReadOnlyList<string> DetailedTables,
    IReadOnlyList<SchemaJoinPath> JoinPaths,
    bool Capped,
    int OmittedTableCount);

/// <summary>
/// Connectivity report for a data source's schema graph. Disconnected islands and isolated tables are
/// where a user needs to declare relationships by hand — on a warehouse with no enforced foreign keys
/// that is most of the schema until inference or manual registration fills it in.
/// </summary>
public record SchemaHealthReport(
    int TableCount,
    int RelationshipCount,
    int VerifiedRelationshipCount,
    int UnverifiedRelationshipCount,
    int ComponentCount,
    int LargestComponentSize,
    IReadOnlyList<string> IsolatedTables,
    IReadOnlyList<string> JunctionTables);
