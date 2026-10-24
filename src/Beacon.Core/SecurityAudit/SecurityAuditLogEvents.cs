using Microsoft.Extensions.Logging;
using Beacon.Core.Data.Entities;

namespace Beacon.Core.SecurityAudit;

/// <summary>
/// The security audit trail's events on the <c>Beacon.Audit</c> log stream (<see cref="Telemetry.BeaconTelemetry.AuditLogCategory"/>),
/// for shipping to a SIEM: a copy of every event once its transaction committed (9104, <c>Persisted=True</c>), a read or
/// verification of the trail (9105), a retention purge (9106), an event that could not be written (9107, Error: the same
/// fields as 9104 with <c>Persisted=False</c>, so the stream still holds what the database lost), stored events that fail
/// verification (9108, Error), the checkpoint each scheduled verification writes (9109), a retention purge that did not
/// run (9110, Error), MCP audit rows that fail verification (9111, Error), the MCP audit checkpoint (9112), rows of the other sealed audit
/// tables (the execution log, the settings history) that fail verification (9113, Error) and their checkpoint (9114). Identifiers, codes, counts and digests only, like the events themselves.
/// </summary>
public static class SecurityAuditLogEvents
{
    public static readonly EventId Recorded = new(9104, "SecurityAuditRecorded");
    public static readonly EventId Read = new(9105, "SecurityAuditRead");
    public static readonly EventId Purged = new(9106, "SecurityAuditPurged");
    public static readonly EventId WriteFailed = new(9107, "SecurityAuditWriteFailed");
    public static readonly EventId VerificationFailed = new(9108, "SecurityAuditVerificationFailed");
    public static readonly EventId Checkpoint = new(9109, "SecurityAuditCheckpoint");
    public static readonly EventId PurgeFailed = new(9110, "SecurityAuditPurgeFailed");
    public static readonly EventId McpAuditVerificationFailed = new(9111, "McpAuditVerificationFailed");
    public static readonly EventId McpAuditCheckpoint = new(9112, "McpAuditCheckpoint");
    public static readonly EventId AuditTableVerificationFailed = new(9113, "AuditTableVerificationFailed");
    public static readonly EventId AuditTableCheckpoint = new(9114, "AuditTableCheckpoint");

    private const string EventTemplate =
        "Security audit event: Action={Action} Outcome={Outcome} EventGuid={EventGuid} AuditId={AuditId} Persisted={Persisted} " +
        "ActorKind={ActorKind} ActorUserId={ActorUserId} ApiKeyId={ApiKeyId} CallerHash={CallerHash} " +
        "ProjectId={ProjectId} ResourceType={ResourceType} ResourceId={ResourceId} Details={Details} TraceId={TraceId}";

    /// <summary>The copy of <paramref name="auditEvent"/> once it is durable in the database: 9104 at Information.</summary>
    internal static void LogPersisted(ILogger logger, SecurityAuditEvent auditEvent)
    {
        logger.Log(
            LogLevel.Information,
            Recorded,
            EventTemplate,
            auditEvent.Action,
            auditEvent.Outcome,
            auditEvent.EventGuid,
            auditEvent.Id,
            true,
            auditEvent.ActorKind,
            auditEvent.ActorUserId,
            auditEvent.ApiKeyId,
            auditEvent.CallerHash,
            auditEvent.ProjectId,
            auditEvent.ResourceType,
            auditEvent.ResourceId,
            auditEvent.DetailsJson,
            auditEvent.TraceId);
    }

    /// <summary>
    /// <paramref name="auditEvent"/>, which could not be written (<paramref name="failure"/> names why, by type and code
    /// only): 9107 at Error with every field the stored event would have carried and <c>Persisted=False</c>.
    /// </summary>
    internal static void LogNotPersisted(ILogger logger, SecurityAuditEvent auditEvent, string failure)
    {
        logger.Log(
            LogLevel.Error,
            WriteFailed,
            EventTemplate + " Failure={Failure}",
            auditEvent.Action,
            auditEvent.Outcome,
            auditEvent.EventGuid,
            null,
            false,
            auditEvent.ActorKind,
            auditEvent.ActorUserId,
            auditEvent.ApiKeyId,
            auditEvent.CallerHash,
            auditEvent.ProjectId,
            auditEvent.ResourceType,
            auditEvent.ResourceId,
            auditEvent.DetailsJson,
            auditEvent.TraceId,
            failure);
    }
}
