namespace Beacon.Core.Data.Enums;

/// <summary>The kinds of change, besides query changes, that the change approval workflow reviews.</summary>
public enum ChangeApprovalKind
{
    /// <summary>A migration job created, or its definition changed: it runs only once the definition is approved.</summary>
    MigrationJob = 1,

    /// <summary>A data contract's Custom SQL rules added or changed: the rules waiting for approval are not evaluated.</summary>
    CustomSqlRules = 2,

    /// <summary>A recipient created, or where it delivers changed: deliveries skip it until approved.</summary>
    RecipientDestination = 3
}
