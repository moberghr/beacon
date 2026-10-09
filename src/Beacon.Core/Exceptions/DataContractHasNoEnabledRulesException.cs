namespace Beacon.Core.Exceptions;

/// <summary>
/// A data contract without an enabled rule is not evaluated: scoring no rules would report 100, overwrite the
/// contract's real score and keep its alerts quiet. An <see cref="InvalidOperationException"/>, so the evaluate endpoint
/// answers 400 with this message; the scheduled job recognises it and skips the run instead of failing.
/// </summary>
public sealed class DataContractHasNoEnabledRulesException(int dataContractId)
    : InvalidOperationException("This data contract has no enabled rules, so it was not evaluated. Enable a rule and save the contract first.")
{
    public int DataContractId { get; } = dataContractId;
}
