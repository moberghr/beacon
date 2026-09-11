using System.Text.Json;
using System.Text.RegularExpressions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Models;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.McpSettings;

internal sealed class UpdateMcpProjectSettingsHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IMcpSettingsProvider settingsProvider,
    IOptions<McpDeploymentOptions> deploymentOptions)
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
}

public record UpdateMcpProjectSettingsCommand(int ProjectId, McpProjectSettingsData Data) : IRequest;
