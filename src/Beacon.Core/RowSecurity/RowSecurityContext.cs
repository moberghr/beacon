using Beacon.Core.Services.Validation;

namespace Beacon.Core.RowSecurity;

/// <summary>
/// What row-level security asks of a statement on one data source. <see cref="Denied"/> is the default value, so a state
/// that was never set refuses rather than runs.
/// </summary>
public enum RowSecurityState
{
    /// <summary>The caller may not read the source's rows: the statement never runs.</summary>
    Denied,

    /// <summary>The source's row policies filter what the caller reads.</summary>
    Filtered,

    /// <summary>No row filter applies: the statement runs as written.</summary>
    NotApplicable
}

/// <summary>
/// Row-level security for one statement on one data source, as <see cref="IRowSecurityResolver"/> resolves it for the scope's
/// caller. Every <see cref="SqlGateRequest"/> carries one, and the gate acts on it: a
/// <see cref="RowSecurityState.Denied"/> context is refused (<see cref="SqlGateCodes.NoEntitlement"/>), and so is a
/// <see cref="RowSecurityState.Filtered"/> one while the gate cannot write the filter into the statement
/// (<see cref="SqlGateCodes.RowSecurityUnrewritable"/>), so a filtered statement never runs unfiltered. The resolver creates
/// one for every statement that runs; <see cref="RowSecurityState.NotApplicable"/> is stated by name only where nothing
/// reaches a source's rows — the in-memory join over step results and the checks that judge SQL before it is stored. The
/// factories are internal, which keeps them from Beacon.AI and the connectors but not from the assemblies Beacon.Core
/// shows its internals to: <c>SqlExecutionPathCoverageTests</c> pins every file that creates one.
/// </summary>
public sealed record RowSecurityContext
{
    /// <summary>The lock <c>Beacon:RowSecurity:Enabled</c> is off.</summary>
    internal const string DisabledReason = "row_security_disabled";

    /// <summary>The lock is on where row security cannot be enforced.</summary>
    internal const string UnavailableReason = "row_security_unavailable";

    /// <summary>The statement joins step results on Beacon's in-memory database; each step was resolved for its own source.</summary>
    internal const string InMemoryJoinReason = "in_memory_join";

    /// <summary>The SQL is judged before it is stored and runs nowhere; it is resolved again wherever it runs.</summary>
    internal const string SaveCheckReason = "save_check";

    private RowSecurityContext(RowSecurityState state, string reason)
    {
        State = state;
        Reason = reason;
    }

    public RowSecurityState State { get; }

    /// <summary>Why, as a stable machine-readable token for logs and tests; never data.</summary>
    public string Reason { get; }

    internal static RowSecurityContext NotApplicable(string reason)
    {
        return new RowSecurityContext(RowSecurityState.NotApplicable, reason);
    }

    internal static RowSecurityContext Filtered(string reason)
    {
        return new RowSecurityContext(RowSecurityState.Filtered, reason);
    }

    internal static RowSecurityContext Denied(string reason)
    {
        return new RowSecurityContext(RowSecurityState.Denied, reason);
    }
}
