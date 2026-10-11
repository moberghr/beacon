using Beacon.Core.Data.Enums;

namespace Beacon.Core.Models.DataQuality;

public record DataContractData
{
    public int Id { get; init; }
    public int DataSourceId { get; init; }
    public string DataSourceName { get; init; } = null!;
    public string SchemaName { get; init; } = null!;
    public string TableName { get; init; } = null!;
    public string Name { get; init; } = null!;
    public string? Description { get; init; }
    public string CronExpression { get; init; } = null!;
    public bool IsEnabled { get; init; }

    /// <summary>An owner is recorded: the contract's scheduled evaluations run as the owner, checked at every run.</summary>
    public bool HasOwner { get; init; }

    /// <summary>
    /// The owner's display name, or a neutral "User {id}" label without one (never the user name or external id); null
    /// when there is no owner or no matching user.
    /// </summary>
    public string? OwnerDisplayName { get; init; }

    /// <summary>
    /// A scheduled evaluation was skipped because the owner may no longer run the contract's rules, and no evaluation has
    /// run since: the contract's scheduled checks are not happening until the owner regains access or an Admin changes
    /// the owner.
    /// </summary>
    public bool OwnerNotAuthorized { get; init; }

    public bool AlertOnFailure { get; init; }
    public int FailureThresholdScore { get; init; }
    public DateTime CreatedTime { get; init; }
    public double? LatestScore { get; init; }
    public List<DataContractRuleData> Rules { get; init; } = new();
    public List<DataContractRecipientData> Recipients { get; init; } = new();
}

public record DataContractRecipientData
{
    public int Id { get; init; }
    public string Name { get; init; } = null!;
    /// <summary>Always null: destinations are secrets, shown masked to admins on the recipients list only.</summary>
    public string? Destination { get; init; }
    public NotificationType NotificationType { get; init; }
}
