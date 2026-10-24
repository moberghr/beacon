using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Interceptors;

namespace Beacon.Core.SecurityAudit;

/// <summary>
/// What the host checks once it has started, for each sealed audit table beside the MCP audit (<see cref="AuditTableStores"/>):
/// <list type="bullet">
/// <item>whether the database has the table's seal columns (<see cref="AuditTableSealSchema"/>, which logs one Warning while
/// the migration <c>AddAuditTableSeal</c> is not applied; rows are written without their seal until it is);</item>
/// <item>with the columns in place and the context sealing what it saves (<see cref="SecurityAuditInterceptor"/>), the
/// table's sealing-started system event, recorded once: the highest row id written before sealing started (rows up to it
/// may carry no seal), dated by the event, so the verification knows from when a row without a seal is a changed one.</item>
/// </list>
/// Run by Beacon.Api once the host has started. A context without the interceptor is reported by the MCP audit's startup
/// check (<see cref="McpAuditSealingStartup"/>).
/// Identifiers and counts only (§1.11). Never throws for what it finds; the caller runs it off the startup thread.
/// </summary>
internal sealed class AuditTableSealingStartup(
    IDbContextFactory<BeaconContext> contextFactory,
    SecurityAuditIntegrity integrity,
    ISecurityAuditRecorder recorder,
    AuditTableSealSchema sealSchema,
    ILogger<AuditTableSealingStartup> logger)
{
    private const int RecentEventsRead = 20;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await RunAsync(AuditTableStores.ExecutionLog, cancellationToken);
        await RunAsync(AuditTableStores.SettingHistory, cancellationToken);
    }

    private async Task RunAsync<T>(AuditTableStore<T> store, CancellationToken cancellationToken)
        where T : BaseEntity, ISealedAuditRow
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // While the seal columns are missing, the schema logs it once and rows are written without their seal.
        if (await sealSchema.SealColumnsMissingAsync(context, typeof(T), cancellationToken))
        {
            return;
        }

        if (await SealColumnsMissingAsync(context, store, cancellationToken) != false
            || !Sealing(context)
            || await SealingStartedAsync(context, store, cancellationToken))
        {
            return;
        }

        var fromRowId = await store.Rows(context).MaxAsync(x => (int?)x.Id, cancellationToken) ?? 0;
        await recorder.RecordAsync(
            new SecurityAuditEntry(store.SealingStartedAction)
                .On(store.Resource, null)
                .By(SecurityAuditActor.System)
                .With(new SecurityAuditDetails()
                    .Add(AuditTableVerificationService.SealingFromRowIdDetail, fromRowId)
                    .Add("sealVersion", AuditTableSeal.CurrentVersion)),
            cancellationToken);
        logger.LogInformation("Audit table {Store} sealing started after row {FromRowId}", store.Name, fromRowId);
    }

    /// <summary>
    /// True when the table is there and its seal columns are not; false when they are; null when it cannot tell (the table
    /// cannot be read at all).
    /// </summary>
    internal static async Task<bool?> SealColumnsMissingAsync<T>(BeaconContext context, AuditTableStore<T> store, CancellationToken cancellationToken)
        where T : BaseEntity, ISealedAuditRow
    {
        try
        {
            await store.Rows(context)
                .AsNoTracking()
                .Where(x => x.Id == 0)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);
        }
        catch (DbException)
        {
            return null;
        }

        try
        {
            await store.Rows(context)
                .AsNoTracking()
                .Where(x => x.Id == 0)
                .Select(x =>
                    new
                    {
                        x.AuditGuid,
                        x.SealVersion,
                        x.HmacKeyId,
                        x.Hmac
                    })
                .ToListAsync(cancellationToken);

            return false;
        }
        catch (DbException)
        {
            return true;
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

    // Whether an intact sealing-started event of the table was recorded.
    private async Task<bool> SealingStartedAsync<T>(BeaconContext context, AuditTableStore<T> store, CancellationToken cancellationToken)
        where T : BaseEntity, ISealedAuditRow
    {
        var events = await context.SecurityAuditEvents
            .AsNoTracking()
            .Where(x => x.Action == store.SealingStartedAction)
            .Where(x => x.ActorKind == SecurityAuditActorKinds.System)
            .Where(x => x.ActorUserId == null)
            .OrderBy(x => x.Id)
            .Take(RecentEventsRead)
            .ToListAsync(cancellationToken);

        return events.Any(x => integrity.Verify(x) == SecurityAuditVerdict.Intact);
    }
}
