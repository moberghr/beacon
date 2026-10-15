namespace Beacon.Core.Exceptions;

/// <summary>
/// A data contract whose every enabled rule is a Custom SQL rule waiting for approval is not evaluated: nothing would be
/// scored. An <see cref="InvalidOperationException"/>, so the evaluate endpoint answers 400 with this message; the
/// scheduled job recognises it and skips the run (a Warning) instead of failing.
/// </summary>
public sealed class DataContractRulesAwaitApprovalException(int dataContractId, int awaitingRules)
    : InvalidOperationException("Every enabled rule of this data contract is a Custom SQL rule waiting for approval, so it was not evaluated. Approve the change on the Approvals page, or enable a rule that does not wait.")
{
    public int DataContractId { get; } = dataContractId;

    public int AwaitingRules { get; } = awaitingRules;
}
