using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Beacon.Core.SecurityAudit;

namespace Beacon.Api;

/// <summary>
/// Notes once at startup how the security audit trail is kept: the id of the key its events are sealed under and where
/// that key comes from (never the key), with a Warning when <c>Beacon:Audit:IntegrityKey</c> is unset (the derived key
/// changes when the caller-hash key or the encryption key does, and events sealed before then verify only while the old
/// key stays listed); and whether a retention is set, which deletes nothing until the host schedules the purge.
/// Configuration names, key ids and counts only (§1.11).
/// </summary>
internal sealed class SecurityAuditStartupNote(
    ILogger<SecurityAuditStartupNote> logger,
    SecurityAuditIntegrity? integrity = null,
    IOptions<SecurityAuditOptions>? options = null) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return builder =>
        {
            Note();
            next(builder);
        };
    }

    internal void Note()
    {
        if (integrity == null)
        {
            return;
        }

        logger.LogInformation(
            "Security audit events are sealed under integrity key {KeyId} ({KeySource})",
            integrity.CurrentKeyId,
            integrity.KeySource);

        if (integrity.KeySource != SecurityAuditKeySources.IntegrityKey)
        {
            var source = integrity.KeySource == SecurityAuditKeySources.DerivedFromEncryptionKey ? "Beacon:EncryptionKey" : "Beacon:Mcp:CallerHashKey";
            logger.LogWarning(
                "{Setting} is not set: the security audit integrity key is derived from {Source} and changes when it does. Events sealed before such a change verify only while the old key stays listed ({PreviousIntegrityKeys}, or {PreviousEncryptionKeys} for the encryption key). Set the integrity key before changing {SourceToChange}",
                $"{SecurityAuditOptions.SectionName}:{nameof(SecurityAuditOptions.IntegrityKey)}",
                source,
                $"{SecurityAuditOptions.SectionName}:{nameof(SecurityAuditOptions.PreviousIntegrityKeys)}",
                "Beacon:PreviousEncryptionKeys",
                source);
        }

        var retentionDays = options?.Value.RetentionDays;
        if (retentionDays == null)
        {
            logger.LogInformation("Security audit events are kept forever ({Setting} is not set)", $"{SecurityAuditOptions.SectionName}:{nameof(SecurityAuditOptions.RetentionDays)}");

            return;
        }

        logger.LogInformation(
            "Security audit events older than {RetentionDays} days are deleted by the retention purge, which runs only where the host schedules it: ISecurityAuditRetentionService.PurgeExpiredAsync daily (the sample host's Warp job security-audit-retention). The scheduled verification (ISecurityAuditVerificationService.VerifyNewEventsAsync, hourly; security-audit-verify) checks new events between purges",
            retentionDays);
    }
}
