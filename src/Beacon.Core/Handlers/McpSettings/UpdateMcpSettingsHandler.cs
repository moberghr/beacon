using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using Beacon.Core.Authorization;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Handlers.McpSettings;

internal sealed class UpdateMcpSettingsHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IMcpSettingsProvider settingsProvider,
    IOptions<McpDeploymentOptions> deploymentOptions,
    IBeaconPermissionAccessor permissionAccessor,
    IBeaconActorAccessor actorAccessor,
    ISecurityAuditRecorder securityAudit,
    ILogger<UpdateMcpSettingsHandler> logger)
    : IRequestHandler<UpdateMcpSettingsCommand>
{
    public async Task Handle(UpdateMcpSettingsCommand request, CancellationToken cancellationToken)
    {
        var data = request.Data;

        // Deployment locks win over any stored value (§1.5, R1/R2). A write that contradicts one is refused
        // before touching the DB — the API maps SettingLockedException to 409.
        var activeLocks = McpLockPolicy.ActiveRules(deploymentOptions.Value);
        foreach (var rule in activeLocks)
        {
            if (rule.Contradicts(data))
            {
                throw SettingLockedException.For(rule.FieldName, rule.LockName);
            }
        }

        // Every numeric setting in its range (McpSettingsRanges): a row cap below one row would read as no cap at all, a
        // retention window below one day would delete every learning signal on each cleanup run, a statement timeout of 0
        // would cancel every statement.
        McpSettingsRanges.Check(data);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.McpSettings.FirstOrDefaultAsync(cancellationToken);

        // A custom PII pattern the save adds must be valid, at most 500 characters and buildable on the linear-time
        // engine (CustomPiiPattern). The page re-sends the whole list, so a pattern already stored is not checked again:
        // one stored before these checks keeps working (evaluated fail closed) and never blocks an unrelated save.
        CustomPiiPattern.CheckAdded(data.CustomPiiPatterns, CustomPiiPattern.Stored(entity?.CustomPiiPatterns));

        if (entity == null)
        {
            entity = new Data.Entities.McpSettings();
            context.McpSettings.Add(entity);
        }

        // A row that did not exist yet held the defaults every consumer read, so those are the values a change replaces.
        var before = McpSettingsChangeAudit.Snapshot(entity);

        // Turning read-only enforcement or PII detection off is an Admin's; a SecurityAdmin turns them on. A locked one is
        // never turned off here: a value that contradicts its lock was refused above.
        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        if (McpSafetySettings.Loosens(entity.EnforceReadOnly, data.EnforceReadOnly)
            || McpSafetySettings.Loosens(entity.EnablePiiDetection, data.EnablePiiDetection))
        {
            McpSafetySettings.DemandAdmin(
                await permissionAccessor.GetCurrentAsync(cancellationToken),
                actor,
                logger,
                null);
        }

        // Every setting is written as sent: the global GET hands the page the STORED values next to the resolved ones, so
        // a value above a deployment ceiling is what the admin configured and is kept, to apply again when the ceiling is
        // raised. A field a deployment lock pins is not written (a value contradicting the lock was refused above), so
        // lifting the lock restores the stored value.
        var lockedFields = activeLocks
            .Select(x => x.FieldName)
            .ToHashSet(StringComparer.Ordinal);
        McpSettingFields.Store(data, entity, lockedFields);

        // Every change is kept in the settings history: who, which setting, the value before and after.
        var changes = McpSettingsChangeAudit.Changes(
            McpSettingsChangeAudit.GlobalKeyPrefix,
            before,
            McpSettingsChangeAudit.Snapshot(entity),
            actor.LogId,
            DateTime.UtcNow);
        foreach (var change in changes)
        {
            context.AppSettingHistory.Add(change);
        }

        await securityAudit.SaveWithEventsAsync(
            context,
            () => SettingsAuditEntries.Changed(SecurityAuditActions.McpSettingsChanged, null, changes),
            cancellationToken);
        settingsProvider.InvalidateCache();
        McpSettingsChangeAudit.Log(logger, changes, actor.LogId, null);
    }
}

/// <summary>
/// Saves the global MCP settings. For an Admin or a SecurityAdmin; only an Admin turns read-only enforcement or PII
/// detection off (403, <see cref="McpSafetySettings"/>), and only while the deployment does not lock them on. A value that
/// contradicts a deployment lock is refused (409), an Admin's included. Every change is recorded in the settings history
/// (<see cref="McpSettingsChangeAudit"/>).
/// </summary>
[ProjectScopeExempt("security configuration, gated by the SecurityAdmin permission", Policy = BeaconPermissions.SecurityAdminPolicy)]
public record UpdateMcpSettingsCommand(McpSettingsData Data) : IRequest;
