namespace Beacon.Core.Authorization;

public enum ResourceType
{
    DataSource = 1,
    Query = 2,
    QueryFolder = 3,
    Subscription = 4,
    Recipient = 5,
    QueryTask = 6,
    MigrationJob = 7,
    ProjectDocumentation = 8,
    AiActor = 9,
    AiActorPlan = 10,
    AiAlertConfiguration = 11,
    QueryApprovalRequest = 12
}

public enum PermissionAction
{
    Read = 1,
    Create = 2,
    Update = 3,
    Delete = 4,
    Execute = 5,      // For queries/subscriptions
    Archive = 6,      // For archivable entities
    Approve = 7,      // For AI Actor plans
    Lock = 8,         // For query locking
    Export = 9        // For documentation/data export
}

public sealed class AuthorizationResult
{
    public bool IsAuthorized { get; init; }
    public string? FailureReason { get; init; }

    public static AuthorizationResult Success() =>
        new() { IsAuthorized = true };

    public static AuthorizationResult Failure(string reason) =>
        new() { IsAuthorized = false, FailureReason = reason };
}
