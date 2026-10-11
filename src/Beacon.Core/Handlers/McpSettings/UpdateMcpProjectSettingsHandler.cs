using System.Text.Json;
using System.Text.RegularExpressions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.McpSettings;

internal sealed class UpdateMcpProjectSettingsHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IMcpSettingsProvider settingsProvider,
    IOptions<McpDeploymentOptions> deploymentOptions,
    IBeaconPermissionAccessor permissionAccessor,
    IBeaconActorAccessor actorAccessor,
    ILogger<UpdateMcpProjectSettingsHandler> logger)
    : IRequestHandler<UpdateMcpProjectSettingsCommand>
{
    public async Task Handle(UpdateMcpProjectSettingsCommand request, CancellationToken cancellationToken)
    {
        var data = request.Data;

        // Deployment locks win over any stored value (§1.5, R1/R2). ANY non-null override of a locked field is
        // refused — even one that agrees with the lock today, because it would silently take effect the day the
        // lock is lifted. Null means "inherit" and is always allowed: the inherited value is pinned by the lock.
        foreach (var rule in McpLockPolicy.ActiveRules(deploymentOptions.Value))
        {
            if (rule.HasOverride(data))
            {
                throw SettingLockedException.For(rule.FieldName, rule.LockName);
            }
        }

        // Same write-time regex validation as the global handler — an unparseable pattern must never be persisted.
        foreach (var pattern in data.CustomPiiPatterns ?? [])
        {
            try
            {
                _ = Regex.Match(string.Empty, pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException($"Custom PII pattern is not a valid regular expression: {ex.Message}");
            }
        }

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

        // Turning read-only enforcement or PII detection off for the project is an Admin's; a SecurityAdmin turns them
        // on. An override left empty inherits the global value, read only when that decides it.
        if (await LoosensSafetyAsync(context, entity, data, cancellationToken))
        {
            McpSafetySettings.DemandAdmin(
                await permissionAccessor.GetCurrentAsync(cancellationToken),
                await actorAccessor.GetCurrentAsync(cancellationToken),
                logger,
                request.ProjectId);
        }

        if (entity == null)
        {
            entity = new McpProjectSettings { ProjectId = request.ProjectId };
            context.McpProjectSettings.Add(entity);
        }

        entity.MaxRowLimit = data.MaxRowLimit;
        entity.EnforceReadOnly = data.EnforceReadOnly;
        entity.EnablePiiDetection = data.EnablePiiDetection;
        // null = inherit; a non-null EMPTY list is a real override ("no custom patterns for this project") and
        // must round-trip as "[]", otherwise the UI's override toggle turns itself off on the next GET.
        entity.CustomPiiPatterns = data.CustomPiiPatterns != null
            ? JsonSerializer.Serialize(data.CustomPiiPatterns)
            : null;
        entity.EnableSampleValueCollection = data.EnableSampleValueCollection;

        entity.EnableLearning = data.EnableLearning;
        entity.LearningAutoApproveThreshold = data.LearningAutoApproveThreshold;
        entity.LearningInjectionBudgetChars = data.LearningInjectionBudgetChars;
        entity.LearningSignalRetentionDays = data.LearningSignalRetentionDays;

        entity.EnableSelfConsistency = data.EnableSelfConsistency;
        entity.SelfConsistencyCandidateCount = data.SelfConsistencyCandidateCount;
        entity.EnableEvalJudge = data.EnableEvalJudge;
        entity.EnableSemanticRetrieval = data.EnableSemanticRetrieval;
        entity.ExemplarTopK = data.ExemplarTopK;

        entity.EnableReplayVerification = data.EnableReplayVerification;
        entity.LearningReplayMinFlips = data.LearningReplayMinFlips;

        entity.EnableContextualRetrieval = data.EnableContextualRetrieval;
        entity.DocChunkWindowSentences = data.DocChunkWindowSentences;
        entity.DocChunkOverlapSentences = data.DocChunkOverlapSentences;
        entity.GlossaryTopK = data.GlossaryTopK;
        entity.DocChunkTopK = data.DocChunkTopK;

        entity.EnableGoldenExemplars = data.EnableGoldenExemplars;
        entity.GoldenExemplarTopK = data.GoldenExemplarTopK;
        entity.GoldenExemplarBudgetChars = data.GoldenExemplarBudgetChars;

        entity.EnableValueGrounding = data.EnableValueGrounding;
        entity.ValueGroundingMaxProbes = data.ValueGroundingMaxProbes;
        entity.EnableSemanticLint = data.EnableSemanticLint;
        entity.SelfConsistencyMinTables = data.SelfConsistencyMinTables;

        entity.RetainQueryContent = data.RetainQueryContent;
        entity.StatementTimeoutSeconds = data.StatementTimeoutSeconds;
        entity.MaxResultBytes = data.MaxResultBytes;
        entity.MaxExplainCost = data.MaxExplainCost;
        entity.MaxConcurrentQueriesPerKey = data.MaxConcurrentQueriesPerKey;
        entity.AllowExplicitFeedbackContent = data.AllowExplicitFeedbackContent;

        await context.SaveChangesAsync(cancellationToken);
        settingsProvider.InvalidateCache();
    }

    private static async Task<bool> LoosensSafetyAsync(
        BeaconContext context,
        McpProjectSettings? entity,
        McpProjectSettingsData data,
        CancellationToken cancellationToken)
    {
        var readOnly = McpSafetySettings.Loosens(entity?.EnforceReadOnly, data.EnforceReadOnly);
        var pii = McpSafetySettings.Loosens(entity?.EnablePiiDetection, data.EnablePiiDetection);
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
/// <see cref="McpSafetySettings"/>). An override of a field a deployment lock pins is refused (409).
/// </summary>
[ProjectScopeExempt("security configuration, gated by the SecurityAdmin permission", Policy = BeaconPermissions.SecurityAdminPolicy)]
public record UpdateMcpProjectSettingsCommand(int ProjectId, McpProjectSettingsData Data) : IRequest;
