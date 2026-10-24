using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using Beacon.Core.Authorization;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Handlers.McpSettings;

internal sealed class UpdateMcpProjectSettingsHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IMcpSettingsProvider settingsProvider,
    IOptions<McpDeploymentOptions> deploymentOptions,
    IBeaconPermissionAccessor permissionAccessor,
    IBeaconActorAccessor actorAccessor,
    ISecurityAuditRecorder securityAudit,
    ILogger<UpdateMcpProjectSettingsHandler> logger)
    : IRequestHandler<UpdateMcpProjectSettingsCommand>
{
    public async Task Handle(UpdateMcpProjectSettingsCommand request, CancellationToken cancellationToken)
    {
        var data = request.Data;

        // Deployment locks win over any stored value (§1.5, R1/R2). ANY non-null override of a locked field is
        // refused — even one that agrees with the lock today, because it would silently take effect the day the
        // lock is lifted. Null is always allowed and keeps the stored override of a locked field (below), which the
        // lock overrides until it is lifted.
        var activeLocks = McpLockPolicy.ActiveRules(deploymentOptions.Value);
        foreach (var rule in activeLocks)
        {
            if (rule.HasOverride(data))
            {
                throw SettingLockedException.For(rule.FieldName, rule.LockName);
            }
        }

        // The same ranges as the global handler; null keeps the global value.
        McpSettingsRanges.Check(data);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var projectExists = await context.Projects
            .Where(x => x.Id == request.ProjectId)
            .AnyAsync(cancellationToken);

        if (!projectExists)
        {
            throw new InvalidOperationException($"Project {request.ProjectId} not found.");
        }

        var entity = await context.McpProjectSettings
            .Where(x => x.ProjectId == request.ProjectId)
            .FirstOrDefaultAsync(cancellationToken);

        // The same checks as the global handler on a custom PII pattern the save adds; one already stored, for the project
        // or globally (the page may start an override from the global list), is not checked again.
        if (data.CustomPiiPatterns is { Count: > 0 } patterns)
        {
            var added = patterns
                .Except(CustomPiiPattern.Stored(entity?.CustomPiiPatterns), StringComparer.Ordinal)
                .ToList();
            if (added.Count > 0)
            {
                var global = await context.McpSettings
                    .Select(x => x.CustomPiiPatterns)
                    .FirstOrDefaultAsync(cancellationToken);
                CustomPiiPattern.CheckAdded(added, CustomPiiPattern.Stored(global));
            }
        }

        // The page never sends a locked field (null), so writing it would clear its stored override: it is not written,
        // so lifting the lock restores the project's configuration (as the global handler keeps the global row's value).
        var lockedFields = activeLocks
            .Select(x => x.FieldName)
            .ToHashSet(StringComparer.Ordinal);

        // Turning read-only enforcement or PII detection off for the project is an Admin's; a SecurityAdmin turns them
        // on. An override left empty inherits the global value, read only when that decides it; a locked one is unchanged.
        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        if (await LoosensSafetyAsync(context, entity, data, lockedFields, cancellationToken))
        {
            McpSafetySettings.DemandAdmin(
                await permissionAccessor.GetCurrentAsync(cancellationToken),
                actor,
                logger,
                request.ProjectId);
        }

        if (entity == null)
        {
            entity = new McpProjectSettings { ProjectId = request.ProjectId };
            context.McpProjectSettings.Add(entity);
        }

        // A project without a row inherited every value (all overrides empty).
        var before = McpSettingsChangeAudit.Snapshot(entity);

        McpSettingFields.Store(data, entity, lockedFields);

        // Every change is kept in the settings history: who, which override, the value before and after (empty = inherit).
        var changes = McpSettingsChangeAudit.Changes(
            McpSettingsChangeAudit.ProjectKeyPrefix(request.ProjectId),
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
            () => SettingsAuditEntries.Changed(SecurityAuditActions.McpProjectSettingsChanged, request.ProjectId, changes),
            cancellationToken);
        settingsProvider.InvalidateCache();
        McpSettingsChangeAudit.Log(logger, changes, actor.LogId, request.ProjectId);
    }

    private static async Task<bool> LoosensSafetyAsync(
        BeaconContext context,
        McpProjectSettings? entity,
        McpProjectSettingsData data,
        IReadOnlySet<string> lockedFields,
        CancellationToken cancellationToken)
    {
        var readOnly = McpSafetySettings.Loosens(
            entity?.EnforceReadOnly,
            lockedFields.Contains(nameof(McpProjectSettingsData.EnforceReadOnly)) ? entity?.EnforceReadOnly : data.EnforceReadOnly);
        var pii = McpSafetySettings.Loosens(
            entity?.EnablePiiDetection,
            lockedFields.Contains(nameof(McpProjectSettingsData.EnablePiiDetection)) ? entity?.EnablePiiDetection : data.EnablePiiDetection);
        if (readOnly == true || pii == true)
        {
            return true;
        }

        if (readOnly == null || pii == null)
        {
            var global = await context.McpSettings
                .Select(x =>
                    new
                    {
                        x.EnforceReadOnly,
                        x.EnablePiiDetection
                    })
                .FirstOrDefaultAsync(cancellationToken);

            return (readOnly == null && !(global?.EnforceReadOnly ?? true))
                || (pii == null && !(global?.EnablePiiDetection ?? true));
        }

        return false;
    }
}

/// <summary>
/// Saves a project's MCP setting overrides (null inherits the global value). For an Admin or a SecurityAdmin; only an
/// Admin turns read-only enforcement or PII detection off for the project, or pins either off (403,
/// <see cref="McpSafetySettings"/>), and only while the deployment does not lock them on. An override of a field a
/// deployment lock pins is refused (409), an Admin's included, and a save leaves the override such a field held before the
/// lock as it is. Every change is recorded in the settings history
/// (<see cref="McpSettingsChangeAudit"/>).
/// </summary>
[ProjectScopeExempt("security configuration, gated by the SecurityAdmin permission", Policy = BeaconPermissions.SecurityAdminPolicy)]
public record UpdateMcpProjectSettingsCommand(int ProjectId, McpProjectSettingsData Data) : IRequest;
