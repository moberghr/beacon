using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Data.Interceptors;

namespace Beacon.Core.SecurityAudit;

/// <summary>
/// What the host checks once it has started, for the MCP audit seal (<see cref="McpAuditSeal"/>):
/// <list type="bullet">
/// <item>a Warning when the Beacon data context runs without <see cref="SecurityAuditInterceptor"/> (registered by the
/// provider's <c>UsePostgreSql</c> / <c>UseSqlServer</c>): neither MCP audit rows nor security audit events are sealed;</item>
/// <item>an Error while the database lacks the seal columns (<see cref="IMcpAuditSealSchema"/>): rows are written without
/// their seal until the host applies the Beacon migrations;</item>
/// <item>with both in place, the <see cref="SecurityAuditActions.McpAuditSealingStarted"/> system event, recorded once:
/// the highest row id written before sealing started (rows up to it may carry no seal), dated by the event, so the
/// verification knows from when a row without a seal is a changed one;</item>
/// <item>a Warning when sealing started more than <see cref="VerificationOverdue"/> ago, MCP audit rows exist, and no MCP
/// audit verification was recorded within that time: the host does not schedule the verification.</item>
/// </list>
/// Identifiers and counts only (§1.11). Never throws for what it finds; the caller runs it off the startup thread.
/// </summary>
internal sealed class McpAuditSealingStartup(
    IDbContextFactory<BeaconContext> contextFactory,
    IMcpAuditSealSchema sealSchema,
    SecurityAuditIntegrity integrity,
    ISecurityAuditRecorder recorder,
    TimeProvider timeProvider,
    ILogger<McpAuditSealingStartup> logger)
{
    /// <summary>How long without a recorded MCP audit verification counts as the verification not being scheduled.</summary>
    internal static readonly TimeSpan VerificationOverdue = TimeSpan.FromDays(2);

    private const int RecentEventsRead = 20;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var sealing = Sealing(context);
        if (!sealing)
        {
            logger.LogWarning(
                "The Beacon data context runs without {Interceptor}: MCP audit rows and security audit events are not sealed, and the verification cannot tell a changed row from one never sealed. Register the context through UsePostgreSql or UseSqlServer, or add the interceptor",
                nameof(SecurityAuditInterceptor));
        }

        var missing = await sealSchema.SealColumnsMissingAsync(cancellationToken);
        if (missing == true)
        {
            logger.LogError(
                "The MCP audit table has no seal columns: MCP audit rows are written without their seal until the Beacon migrations are applied (migration AddMcpAuditSeal)");

            return;
        }

        if (missing == null || !sealing)
        {
            return;
        }

        var started = await SealingStartedAsync(context, cancellationToken);
        if (started == null)
        {
            var fromAuditId = await context.McpAuditLogs.MaxAsync(x => (int?)x.Id, cancellationToken) ?? 0;
            await recorder.RecordAsync(
                new SecurityAuditEntry(SecurityAuditActions.McpAuditSealingStarted)
                    .On(SecurityAuditResources.McpAudit, null)
                    .By(SecurityAuditActor.System)
                    .With(new SecurityAuditDetails()
                        .Add(McpAuditVerificationService.SealingFromAuditIdDetail, fromAuditId)
                        .Add("sealVersion", McpAuditSeal.CurrentVersion)),
                cancellationToken);
            logger.LogInformation("MCP audit sealing started after row {FromAuditId}", fromAuditId);

            return;
        }

        var overdueBefore = timeProvider.GetUtcNow().UtcDateTime - VerificationOverdue;
        if (started.Value > overdueBefore || !await context.McpAuditLogs.AnyAsync(cancellationToken))
        {
            return;
        }

        var verified = await context.SecurityAuditEvents
            .AsNoTracking()
            .Where(x => x.Action == SecurityAuditActions.McpAuditVerified)
            .Where(x => x.ActorKind == SecurityAuditActorKinds.System)
            .Where(x => x.ActorUserId == null)
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (verified == null || verified.CreatedTime < overdueBefore || integrity.Verify(verified) != SecurityAuditVerdict.Intact)
        {
            logger.LogWarning(
                "MCP audit rows have not been verified within {OverdueDays} days: schedule ISecurityAuditVerificationService.VerifyNewEventsAsync (hourly; the sample host's Warp job security-audit-verify), which verifies the MCP audit too",
                VerificationOverdue.TotalDays);
        }
    }

    // Whether the context seals what it saves: the security audit interceptor is among its interceptors.
    private static bool Sealing(BeaconContext context)
    {
        var interceptors = context.GetService<IDbContextOptions>()
            .FindExtension<CoreOptionsExtension>()?
            .Interceptors;

        return interceptors?.Any(x => x is SecurityAuditInterceptor) == true;
    }

    // When the earliest intact sealing-started event was recorded; null when none was.
    private async Task<DateTime?> SealingStartedAsync(BeaconContext context, CancellationToken cancellationToken)
    {
        var events = await context.SecurityAuditEvents
            .AsNoTracking()
            .Where(x => x.Action == SecurityAuditActions.McpAuditSealingStarted)
            .Where(x => x.ActorKind == SecurityAuditActorKinds.System)
            .Where(x => x.ActorUserId == null)
            .OrderBy(x => x.Id)
            .Take(RecentEventsRead)
            .ToListAsync(cancellationToken);
        foreach (var auditEvent in events)
        {
            if (integrity.Verify(auditEvent) == SecurityAuditVerdict.Intact)
            {
                return auditEvent.CreatedTime;
            }
        }

        return null;
    }
}
