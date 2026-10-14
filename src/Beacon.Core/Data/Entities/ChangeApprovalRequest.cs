using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Data.Entities;

/// <summary>
/// A request to approve a change of a governed resource other than a query (<see cref="Kind"/>): a migration job's
/// definition, a data contract's Custom SQL rules, a recipient's destination. The change is stored on the resource when it
/// is submitted, and the resource stays inactive (no run, no evaluation of those rules, no delivery) until a second person
/// approves exactly the definition the request records (<see cref="DefinitionHash"/>). Decided once, from
/// <see cref="ApprovalStatus.Pending"/>; a newer submission of the same requester for the same resource supersedes it.
/// </summary>
public class ChangeApprovalRequest : BaseEntity
{
    public ChangeApprovalKind Kind { get; set; }

    /// <summary>The governed resource's id: a migration job, a data contract or a recipient, by <see cref="Kind"/>.</summary>
    public int ResourceId { get; set; }

    /// <summary>The resource's name when the change was submitted.</summary>
    public string ResourceName { get; set; } = string.Empty;

    public ApprovalStatus Status { get; set; }

    /// <summary>
    /// The SHA-256 of the definition the change submits, as the resource is checked against it: its approval applies only
    /// while the resource still holds exactly this definition. Never returned by the API (a recipient's covers its
    /// secrets).
    /// </summary>
    public string DefinitionHash { get; set; } = string.Empty;

    /// <summary>The SHA-256 of <see cref="ProposedJson"/>: what a reviewer is shown, and names when approving.</summary>
    public string PayloadHash { get; set; } = string.Empty;

    /// <summary>The change as a reviewer sees it (destinations masked, never a secret).</summary>
    public string ProposedJson { get; set; } = string.Empty;

    /// <summary>The resource before the change, as a reviewer sees it; null for a resource the change creates.</summary>
    public string? PreviousJson { get; set; }

    public string? RequestedByUserId { get; set; }

    public string? RequestedByUserName { get; set; }

    public string? ReviewedByUserId { get; set; }

    public string? ReviewedByUserName { get; set; }

    public DateTime? ReviewedAt { get; set; }

    public string? ReviewComment { get; set; }

    public string? ChangeSummary { get; set; }
}
