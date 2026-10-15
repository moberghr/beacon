using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Data;

namespace Beacon.Core.SecurityAudit;

/// <summary>
/// Records a read of audit data (the security audit trail, the MCP audit export, a user's owned resources) before the
/// read returns anything, and fails the read when the event cannot be written: audit data is never handed out without a
/// record of who read it. An identical read by the same actor within <see cref="SecurityAuditReadGate.CollapseWindow"/>
/// is recorded once.
/// </summary>
internal sealed class SecurityAuditReads(
    IDbContextFactory<BeaconContext> contextFactory,
    ISecurityAuditRecorder recorder,
    IBeaconActorAccessor actorAccessor,
    SecurityAuditReadGate gate)
{
    /// <summary>Writes <paramref name="entry"/> in a unit of work of its own; throws when it cannot (fail closed).</summary>
    public async Task RecordAsync(SecurityAuditEntry entry, CancellationToken cancellationToken)
    {
        var key = KeyOf(await actorAccessor.GetCurrentAsync(cancellationToken), entry);
        if (gate.RecordedRecently(key))
        {
            return;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await recorder.StageAsync(context, entry, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        gate.Recorded(key);
    }

    // Who read what, as a digest kept in memory only: the actor's identity and every field the event would carry.
    private static string KeyOf(BeaconActor actor, SecurityAuditEntry entry)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new object?[]
        {
            actor.Kind.ToString(),
            actor.BeaconUserId,
            actor.ApiKeyId,
            actor.UserId,
            entry.Action,
            entry.Outcome,
            entry.ResourceType,
            entry.ResourceId,
            entry.ProjectId,
            entry.Details?.ToJson()
        });

        return Convert.ToHexStringLower(SHA256.HashData(canonical));
    }
}
