using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Services;

namespace Beacon.AI.Services.Learning;

internal sealed class McpLearningAggregationService(
    IDbContextFactory<BeaconContext> contextFactory,
    IMcpSettingsProvider settingsProvider,
    ILogger<McpLearningAggregationService> logger,
    ILessonExtractor? lessonExtractor = null,
    IPatternReplayVerifier? replayVerifier = null) : IMcpLearningAggregationService
{
    public async Task AggregateLearnedPatternsAsync(CancellationToken ct = default)
    {
        var settings = await settingsProvider.GetSettingsAsync(ct);
        if (!settings.EnableLearning) return;

        List<int> projectIds;
        {
            await using var context = await contextFactory.CreateDbContextAsync(ct);
            projectIds = await context.McpQuerySignals
                .Where(s => s.ProjectId != null)
                .Select(s => s.ProjectId!.Value)
                .Distinct()
                .ToListAsync(ct);
        }

        // Tallies LLM-extraction attempts vs. null results across every cluster this run so a persistently
        // failing (misconfigured) LLM-primary path surfaces ONCE at the end instead of hiding behind a green
        // job (§ silent-failure F7).
        var extraction = new ExtractionStats();

        foreach (var projectId in projectIds)
        {
            try
            {
                await using var projectContext = await contextFactory.CreateDbContextAsync(ct);
                await AggregateForProjectAsync(projectContext, projectId, settings, extraction, ct);
            }
            catch (OperationCanceledException)
            {
                // Host shutdown / cancellation must unwind the whole run, not be logged-and-continued per
                // project (the extraction + replay orchestration below correctly rethrows OCE).
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to aggregate patterns for project {ProjectId}", projectId);
            }
        }

        // LLM-primary path health: if the extractor was tried at least once but returned null EVERY time,
        // the provider is likely misconfigured and the system is silently running on the regex fallback.
        if (extraction.Attempts > 0 && extraction.NullResults == extraction.Attempts)
        {
            logger.LogError(
                "LLM lesson extraction returned no result for all {Count} cluster(s) this run — LLM-primary path may be misconfigured; using regex fallback",
                extraction.Attempts);
        }
    }

    private async Task AggregateForProjectAsync(
        BeaconContext context, int projectId, McpSettingsData settings, ExtractionStats extraction, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-settings.LearningSignalRetentionDays);
        var signals = await context.McpQuerySignals
            .Where(s => s.ProjectId == projectId && s.CreatedTime >= cutoff && s.DataSourceId != null)
            .OrderByDescending(s => s.CreatedTime)
            .ToListAsync(ct);

        if (signals.Count < 3) return; // Not enough data

        // Group by data source
        var byDataSource = signals.GroupBy(s => s.DataSourceId!.Value);

        foreach (var dsGroup in byDataSource)
        {
            var dataSourceId = dsGroup.Key;

            // 1. Schema Corrections: validation failed + retry succeeded
            await DetectSchemaCorrectionsAsync(context, projectId, dataSourceId, dsGroup.ToList(), extraction, ct);

            // 2. Common Queries: cluster successful queries
            await DetectCommonQueriesAsync(context, projectId, dataSourceId, dsGroup.ToList(), ct);

            // 3. Join Patterns: multi-table successful queries
            await DetectJoinPatternsAsync(context, projectId, dataSourceId, dsGroup.ToList(), ct);

            // 4. Documentation Gaps: high error rate tables
            await DetectDocumentationGapsAsync(context, projectId, dataSourceId, dsGroup.ToList(), ct);
        }

        // Temporal decay (§ Architecture ⑧): mark schema-corrections stale when their referenced column no
        // longer exists in current metadata. Joins this method's single unit of work (one SaveChanges, §5.7).
        await DetectStalePatternsAsync(context, projectId, ct);

        await context.SaveChangesAsync(ct);

        // Replay-verification gate (§ Architecture ⑥): candidates were upserted as NeedsEvidence above;
        // promote only those that MEASURABLY help against the golden set. Confidence alone never promotes.
        await PromoteVerifiedCandidatesAsync(context, projectId, settings, ct);
    }

    // Promotes NeedsEvidence candidates for the project via the replay verifier: a candidate becomes
    // AutoApproved ONLY when injecting it flips ≥ LearningReplayMinFlips of its relevant failing golden
    // cases with zero relevant regressions. When replay is disabled or no verifier is wired, candidates
    // stay NeedsEvidence (safe default — never auto-approve without measured evidence). Human Approve/Reject
    // is a separate path and untouched. Internal so it can be unit-tested in isolation.
    internal async Task PromoteVerifiedCandidatesAsync(
        BeaconContext context, int projectId, McpSettingsData settings, CancellationToken ct)
    {
        // Intentional disable stays quiet (operator turned replay off on purpose).
        if (!settings.EnableReplayVerification)
        {
            return;
        }

        // Enabled-but-unwired is a DI gap that silently stalls ALL promotion (candidates never leave
        // NeedsEvidence) — surface it so it is noticed rather than mistaken for "nothing to promote" (T2-2).
        if (replayVerifier is null)
        {
            logger.LogWarning(
                "Replay verification is enabled but no verifier is wired — candidates will remain NeedsEvidence");
            return;
        }

        var candidates = await context.McpLearnedPatterns
            .Where(x => x.ProjectId == projectId)
            .Where(x => x.Status == McpPatternStatus.NeedsEvidence)
            .ToListAsync(ct);

        if (candidates.Count == 0)
        {
            return;
        }

        var promoted = 0;
        foreach (var candidate in candidates)
        {
            try
            {
                var verdict = await replayVerifier.VerifyAsync(candidate, settings.LearningReplayMinFlips, ct);

                logger.LogDebug(
                    "Replay verdict for candidate {PatternId}: relevant={Relevant} baselineFailing={BaselineFailing} flipped={Flipped} regressions={Regressions} errored={Errored} passed={Passed}",
                    candidate.Id, verdict.RelevantCases, verdict.BaselineFailing, verdict.Flipped,
                    verdict.Regressions, verdict.Errored, verdict.Passed);

                // Persistent replay failure (every relevant case errored, e.g. a data-source outage) is
                // distinct from "measured, no improvement" — make it visible so promotion isn't silently
                // deferred forever.
                if (verdict.Errored == verdict.RelevantCases && verdict.RelevantCases > 0)
                {
                    logger.LogWarning(
                        "Replay could not measure candidate {PatternId}: all {Count} relevant case(s) errored — promotion deferred",
                        candidate.Id, verdict.RelevantCases);
                }

                if (verdict.Passed)
                {
                    candidate.Status = McpPatternStatus.AutoApproved;
                    candidate.LastVerifiedAt = DateTime.UtcNow;
                    promoted++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Replay verification failed for pattern {PatternId}; leaving NeedsEvidence", candidate.Id);
            }
        }

        if (promoted > 0)
        {
            // Second save of the deliberate two-phase flow (§5.7, T2-1): the detection pass in
            // AggregateForProjectAsync already flushed NeedsEvidence candidates, and promotion re-queries those
            // persisted candidates (including ones created in earlier runs), so the detection save MUST land
            // before this promotion save — they cannot be collapsed into one unit of work.
            await context.SaveChangesAsync(ct);
            logger.LogInformation("Replay-verified {Count} learned pattern(s) → AutoApproved for project {ProjectId}", promoted, projectId);
        }
    }

    // Temporal decay / staleness pass (§ Architecture ⑧): a schema-correction points at a specific column
    // (McpLearnedPattern.ColumnName). When that column is no longer present in the current ColumnMetadata for
    // the correction's (DataSourceId, SchemaName, TableName) — e.g. the schema was refreshed and the column
    // was renamed/removed — the correction has gone stale and must never be injected again. We set
    // SupersededAt (keeping the row for audit — never delete, §Rejected alternatives) rather than deleting.
    // No SaveChanges here: the tracked mutations are flushed by AggregateForProjectAsync's single unit of
    // work (§5.7). Internal so it can be unit-tested in isolation against a mocked context.
    internal async Task DetectStalePatternsAsync(BeaconContext context, int projectId, CancellationToken ct)
    {
        var corrections = await context.McpLearnedPatterns
            .Where(x => x.ProjectId == projectId)
            .Where(x => x.PatternType == McpPatternType.SchemaCorrection)
            .Where(x => x.Status == McpPatternStatus.Approved || x.Status == McpPatternStatus.AutoApproved)
            .Where(x => x.SupersededAt == null)
            .Where(x => x.ColumnName != null)
            .ToListAsync(ct);

        if (corrections.Count == 0)
        {
            return;
        }

        foreach (var correction in corrections)
        {
            var columnExists = await context.ColumnMetadata
                .Where(x => x.DatabaseMetadata.DataSourceId == correction.DataSourceId)
                .Where(x => x.DatabaseMetadata.SchemaName == correction.SchemaName)
                .Where(x => x.DatabaseMetadata.TableName == correction.TableName)
                .Where(x => x.ColumnName == correction.ColumnName)
                .AnyAsync(ct);

            if (!columnExists)
            {
                correction.SupersededAt = DateTime.UtcNow;
            }
        }
    }

    // LLM-PRIMARY (§ Architecture ⑦): per correction cluster, the LLM lesson extractor is tried first;
    // its structured lesson is used when present, otherwise the deterministic regex + ColumnSimilarity
    // path below runs unchanged as the fallback. Instance method (not static) so it can reach the
    // optional lessonExtractor. The clustering and every other detector stay untouched.
    internal async Task DetectSchemaCorrectionsAsync(
        BeaconContext context, int projectId, int dataSourceId,
        List<McpQuerySignal> signals, ExtractionStats extraction, CancellationToken ct)
    {
        // Capture corrections from both schema validation failures AND execution column errors
        var corrections = signals
            .Where(s => s.RetrySucceeded && !string.IsNullOrEmpty(s.CorrectedSql))
            .Where(s =>
                (s.SchemaValidationFailed && !string.IsNullOrEmpty(s.SchemaValidationError)) ||
                (s.ExecutionFailed && !string.IsNullOrEmpty(s.ExecutionError)))
            .ToList();

        if (corrections.Count == 0) return;

        // Schema validation format: "Column 'created_at' does not exist on 'l'. Available: col1, col2, ..."
        var schemaErrorPattern = new Regex(
            @"Column '(\w+)' does not exist on '[\w.]+'\.\s*Available:\s*([^;]+)",
            RegexOptions.IgnoreCase);

        // PostgreSQL execution error format: '42703: column "created_at" does not exist'
        var pgErrorPattern = new Regex(
            @"column ""(\w+)"" does not exist",
            RegexOptions.IgnoreCase);

        var mappings = new List<(string WrongColumn, string? CorrectColumn, string Schema, string Table, McpQuerySignal Signal)>();

        foreach (var signal in corrections)
        {
            var tables = TryDeserializeJsonArray(signal.TablesUsed ?? "[]");
            if (tables.Count == 0) continue;

            // Use the real table name from TablesUsed, not the alias from the error message
            var primaryTable = tables.FirstOrDefault(t => t.Contains('.')) ?? tables[0];
            var tableParts = primaryTable.Split('.');
            var schema = tableParts.Length > 1 ? tableParts[0] : "public";
            var table = tableParts.Length > 1 ? tableParts[1] : tableParts[0];
            var captured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Schema validation errors have the Available columns list — best source for mapping
            if (!string.IsNullOrEmpty(signal.SchemaValidationError))
            {
                foreach (Match match in schemaErrorPattern.Matches(signal.SchemaValidationError))
                {
                    var wrongCol = match.Groups[1].Value;
                    var availableCsv = match.Groups[2].Value;
                    var correctCol = FindBestColumnMatch(wrongCol, availableCsv, signal.CorrectedSql);
                    mappings.Add((wrongCol, correctCol, schema, table, signal));
                    captured.Add(wrongCol);
                }
            }

            // Execution errors (PostgreSQL column-not-found) — fallback when schema validation missed it
            if (!string.IsNullOrEmpty(signal.ExecutionError))
            {
                var pgMatch = pgErrorPattern.Match(signal.ExecutionError);
                if (pgMatch.Success)
                {
                    var wrongCol = pgMatch.Groups[1].Value;
                    if (!captured.Contains(wrongCol))
                    {
                        var correctCol = FindReplacementInCorrectedSql(
                            wrongCol, signal.GeneratedSql, signal.CorrectedSql);
                        mappings.Add((wrongCol, correctCol, schema, table, signal));
                    }
                }
            }
        }

        // Group by schema.table.wrongColumn and aggregate
        var grouped = mappings
            .GroupBy(m => $"{m.Schema}.{m.Table}.{m.WrongColumn}", StringComparer.OrdinalIgnoreCase);

        foreach (var group in grouped)
        {
            var first = group.First();
            var correctCol = group
                .Select(m => m.CorrectColumn)
                .FirstOrDefault(c => c != null);

            // The linear count-based confidence is retained ONLY as a prior/tiebreak — it is still passed
            // through regardless of which path builds the pattern (§ Architecture ⑦).
            var confidence = Math.Min(1.0, 0.5 + (group.Count() * 0.15));

            // LLM-PRIMARY: extract a structured lesson from the representative failure. On a non-null
            // lesson use the LLM's content/example/type; on null (or no extractor) fall back below.
            if (lessonExtractor is { IsAvailable: true })
            {
                var representative = first.Signal;
                var cluster = new FailureCluster(
                    dataSourceId,
                    first.Schema,
                    first.Table,
                    first.WrongColumn,
                    representative.Question,
                    representative.GeneratedSql,
                    representative.SchemaValidationError ?? representative.ExecutionError,
                    representative.CorrectedSql,
                    null);

                extraction.Attempts++;
                var lesson = await lessonExtractor.ExtractAsync(cluster, ct);
                if (lesson != null)
                {
                    await UpsertPatternAsync(context, projectId, dataSourceId, first.Schema, first.Table, first.WrongColumn,
                        lesson.PatternType, lesson.PatternContent, lesson.ExampleQuestion, lesson.ExampleSql,
                        group.Count(), confidence, ct);
                    continue;
                }

                // Extractor was tried but produced nothing usable — record it so an all-null run is visible.
                extraction.NullResults++;
            }

            // FALLBACK: deterministic regex + ColumnSimilarity content (unchanged).
            var content = correctCol != null
                ? $"NEVER use '{first.WrongColumn}' on {first.Schema}.{first.Table} — correct column is '{correctCol}'"
                : $"Column '{first.WrongColumn}' does not exist on {first.Schema}.{first.Table} — check schema";

            await UpsertPatternAsync(context, projectId, dataSourceId, first.Schema, first.Table, first.WrongColumn,
                McpPatternType.SchemaCorrection, content, null, null,
                group.Count(), confidence, ct);
        }
    }

    internal static async Task DetectCommonQueriesAsync(
        BeaconContext context, int projectId, int dataSourceId,
        List<McpQuerySignal> signals, CancellationToken ct)
    {
        var successful = signals
            .Where(s => s.IsSuccessful && !string.IsNullOrEmpty(s.GeneratedSql) && !string.IsNullOrEmpty(s.TablesUsed))
            // Part B: a human-verified INCORRECT answer is worse than no signal — execution success is not
            // correctness. Never mine an explicitly-wrong query into a "common query" pattern.
            .Where(s => s.UserVerdict != McpUserVerdict.Incorrect)
            .ToList();

        if (successful.Count < 3) return;

        // Group by tables used (as a proxy for semantic similarity)
        var byTables = successful
            .GroupBy(s => s.TablesUsed!)
            .Where(g => g.Count() >= 3);

        foreach (var group in byTables)
        {
            // Prefer a human-CONFIRMED signal as the exemplar, then a non-failed one.
            var representative = group
                .OrderByDescending(s => s.UserVerdict == McpUserVerdict.Correct ? 1 : 0)
                .ThenBy(s => s.SchemaValidationFailed ? 1 : 0)
                .ThenByDescending(s => s.ResultRowCount ?? 0)
                .First();

            var tables = TryDeserializeJsonArray(group.Key);
            if (tables.Count == 0) continue;

            var primaryTable = tables[0];
            var parts = primaryTable.Split('.');
            var schema = parts.Length > 1 ? parts[0] : "public";
            var table = parts.Length > 1 ? parts[1] : parts[0];

            // Part B: a group containing a human-CONFIRMED answer is more trustworthy than one resting on
            // execution success alone, so it earns a confidence bonus.
            var humanVerified = group.Any(s => s.UserVerdict == McpUserVerdict.Correct);
            var content = $"Common query pattern for {primaryTable} ({group.Count()} similar queries)";
            var confidence = Math.Min(1.0, 0.4 + (group.Count() * 0.1) + (humanVerified ? 0.2 : 0.0));

            // Use corrected SQL if the representative had a correction — never store broken SQL
            var exampleSql = representative.CorrectedSql ?? representative.GeneratedSql;

            await UpsertPatternAsync(context, projectId, dataSourceId, schema, table, null,
                McpPatternType.CommonQuery, content, representative.Question, exampleSql,
                group.Count(), confidence, ct);
        }
    }

    internal static async Task DetectJoinPatternsAsync(
        BeaconContext context, int projectId, int dataSourceId,
        List<McpQuerySignal> signals, CancellationToken ct)
    {
        var multiTable = signals
            .Where(s => s.IsSuccessful && !string.IsNullOrEmpty(s.TablesUsed))
            // Part B: exclude human-verified-incorrect answers from join mining (execution success ≠ correct join).
            .Where(s => s.UserVerdict != McpUserVerdict.Incorrect)
            .Select(s => new { Signal = s, Tables = TryDeserializeJsonArray(s.TablesUsed!) })
            .Where(x => x.Tables.Count >= 2)
            .ToList();

        if (multiTable.Count < 2) return;

        // Group by the table pair
        var joinGroups = multiTable
            .SelectMany(x => GetTablePairs(x.Tables).Select(pair => new { Pair = pair, x.Signal }))
            .GroupBy(x => x.Pair)
            .Where(g => g.Count() >= 2);

        foreach (var group in joinGroups)
        {
            var pair = group.Key;
            // Prefer a human-CONFIRMED join as the exemplar.
            var representative = group
                .OrderByDescending(x => x.Signal.UserVerdict == McpUserVerdict.Correct ? 1 : 0)
                .First().Signal;

            var parts = pair.Split('+');
            var table1Parts = parts[0].Trim().Split('.');
            var schema = table1Parts.Length > 1 ? table1Parts[0] : "public";
            var table = table1Parts.Length > 1 ? table1Parts[1] : table1Parts[0];

            var humanVerified = group.Any(x => x.Signal.UserVerdict == McpUserVerdict.Correct);
            var content = $"Tables {pair} are frequently joined together ({group.Count()} queries)";
            var confidence = Math.Min(1.0, 0.5 + (group.Count() * 0.1) + (humanVerified ? 0.2 : 0.0));

            await UpsertPatternAsync(context, projectId, dataSourceId, schema, table, null,
                McpPatternType.JoinPattern, content, representative.Question, representative.GeneratedSql,
                group.Count(), confidence, ct);
        }
    }

    private static async Task DetectDocumentationGapsAsync(
        BeaconContext context, int projectId, int dataSourceId,
        List<McpQuerySignal> signals, CancellationToken ct)
    {
        var byTable = signals
            .Where(s => !string.IsNullOrEmpty(s.TablesUsed))
            .SelectMany(s => TryDeserializeJsonArray(s.TablesUsed!).Select(t => new { Table = t, s.IsSuccessful }))
            .GroupBy(x => x.Table)
            .Where(g => g.Count() >= 5)
            .Select(g => new
            {
                Table = g.Key,
                Total = g.Count(),
                Errors = g.Count(x => !x.IsSuccessful),
                ErrorRate = (double)g.Count(x => !x.IsSuccessful) / g.Count()
            })
            .Where(x => x.ErrorRate > 0.3)
            .ToList();

        foreach (var tableStats in byTable)
        {
            var parts = tableStats.Table.Split('.');
            var schema = parts.Length > 1 ? parts[0] : "public";
            var table = parts.Length > 1 ? parts[1] : parts[0];

            var content = $"Table '{tableStats.Table}' has a {tableStats.ErrorRate:P0} error rate across {tableStats.Total} queries. Schema descriptions may be missing or misleading.";
            var confidence = Math.Min(1.0, tableStats.ErrorRate);

            await UpsertPatternAsync(context, projectId, dataSourceId, schema, table, null,
                McpPatternType.DocumentationGap, content, null, null,
                tableStats.Total, confidence, ct);
        }
    }

    private static async Task UpsertPatternAsync(
        BeaconContext context, int projectId, int dataSourceId,
        string schema, string table, string? column,
        McpPatternType type, string content, string? exampleQuestion, string? exampleSql,
        int signalCount, double confidence, CancellationToken ct)
    {
        var existing = await context.McpLearnedPatterns
            .FirstOrDefaultAsync(p =>
                p.ProjectId == projectId
                && p.DataSourceId == dataSourceId
                && p.TableName == table
                && p.SchemaName == schema
                && p.ColumnName == column
                && p.PatternType == type, ct);

        if (existing != null)
        {
            existing.PatternContent = content;
            existing.ExampleQuestion = exampleQuestion ?? existing.ExampleQuestion;
            existing.ExampleSql = exampleSql ?? existing.ExampleSql;
            existing.SignalCount = signalCount;
            existing.Confidence = confidence;
            existing.LastRefreshedAt = DateTime.UtcNow;

            // Promotion is now decided by the replay-verification gate (§ Architecture ⑥), NOT confidence.
            // A refresh leaves the status alone: Pending/NeedsEvidence await evidence; a human-reviewed
            // Approved/Rejected/AutoApproved verdict is preserved.
        }
        else
        {
            // New candidates land in NeedsEvidence — never AutoApproved on confidence alone. The aggregation
            // job's replay gate promotes them to AutoApproved only when they measurably help (§ Architecture ⑥).
            context.McpLearnedPatterns.Add(new McpLearnedPattern
            {
                ProjectId = projectId,
                DataSourceId = dataSourceId,
                SchemaName = schema,
                TableName = table,
                ColumnName = column,
                PatternType = type,
                PatternContent = content,
                ExampleQuestion = exampleQuestion,
                ExampleSql = exampleSql,
                SignalCount = signalCount,
                Confidence = confidence,
                Status = McpPatternStatus.NeedsEvidence,
                LastRefreshedAt = DateTime.UtcNow
            });
        }
    }

    public async Task GenerateDocumentationPatchesAsync(CancellationToken ct = default)
    {
        var settings = await settingsProvider.GetSettingsAsync(ct);
        if (!settings.EnableLearning) return;

        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var approvedPatterns = await context.McpLearnedPatterns
            .Where(x => x.Status == McpPatternStatus.Approved || x.Status == McpPatternStatus.AutoApproved)
            .Where(x => x.PatternType == McpPatternType.SchemaCorrection || x.PatternType == McpPatternType.DocumentationGap)
            .ToListAsync(ct);

        if (approvedPatterns.Count == 0) return;

        var existingTargets = await context.McpDocumentationPatches
            .Where(x => x.Status == McpDocPatchStatus.Proposed || x.Status == McpDocPatchStatus.Applied)
            .Select(x => new { x.DataSourceId, x.TargetType, x.TargetIdentifier })
            .ToListAsync(ct);

        var existingSet = existingTargets
            .Select(x => $"{x.DataSourceId}:{x.TargetType}:{x.TargetIdentifier}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var patchCount = 0;

        foreach (var pattern in approvedPatterns)
        {
            switch (pattern.PatternType)
            {
                case McpPatternType.SchemaCorrection:
                    {
                        var targetId = $"{pattern.SchemaName}.{pattern.TableName}.{pattern.ColumnName}";
                        var key = $"{pattern.DataSourceId}:{McpDocPatchTarget.ColumnDescription}:{targetId}";
                        if (existingSet.Contains(key)) continue;

                        var currentDesc = await context.ColumnMetadata
                            .Where(c => c.DatabaseMetadata.DataSourceId == pattern.DataSourceId)
                            .Where(c => c.DatabaseMetadata.SchemaName == pattern.SchemaName)
                            .Where(c => c.DatabaseMetadata.TableName == pattern.TableName)
                            .Where(c => c.ColumnName == pattern.ColumnName)
                            .Select(c => c.Description)
                            .FirstOrDefaultAsync(ct);

                        context.McpDocumentationPatches.Add(new McpDocumentationPatch
                        {
                            ProjectId = pattern.ProjectId,
                            DataSourceId = pattern.DataSourceId,
                            TargetType = McpDocPatchTarget.ColumnDescription,
                            TargetIdentifier = targetId,
                            CurrentContent = currentDesc,
                            ProposedContent = pattern.PatternContent,
                            Reasoning = $"Learned from {pattern.SignalCount} query signals (confidence: {pattern.Confidence:P0}). {pattern.PatternContent}",
                            SupportingSignalCount = pattern.SignalCount,
                            Status = McpDocPatchStatus.Proposed
                        });
                        existingSet.Add(key);
                        patchCount++;
                        break;
                    }
                case McpPatternType.DocumentationGap:
                    {
                        var targetId = $"{pattern.SchemaName}.{pattern.TableName}";
                        var key = $"{pattern.DataSourceId}:{McpDocPatchTarget.TableDescription}:{targetId}";
                        if (existingSet.Contains(key)) continue;

                        var currentDesc = await context.DatabaseMetadata
                            .Where(m => m.DataSourceId == pattern.DataSourceId)
                            .Where(m => m.SchemaName == pattern.SchemaName)
                            .Where(m => m.TableName == pattern.TableName)
                            .Select(m => m.TableDescription)
                            .FirstOrDefaultAsync(ct);

                        var proposed = string.IsNullOrEmpty(currentDesc)
                            ? $"[Auto-generated] {pattern.PatternContent}"
                            : $"{currentDesc}\n\n⚠️ {pattern.PatternContent}";

                        context.McpDocumentationPatches.Add(new McpDocumentationPatch
                        {
                            ProjectId = pattern.ProjectId,
                            DataSourceId = pattern.DataSourceId,
                            TargetType = McpDocPatchTarget.TableDescription,
                            TargetIdentifier = targetId,
                            CurrentContent = currentDesc,
                            ProposedContent = proposed,
                            Reasoning = $"Learned from {pattern.SignalCount} query signals. {pattern.PatternContent}",
                            SupportingSignalCount = pattern.SignalCount,
                            Status = McpDocPatchStatus.Proposed
                        });
                        existingSet.Add(key);
                        patchCount++;
                        break;
                    }
            }
        }

        if (patchCount > 0)
        {
            await context.SaveChangesAsync(ct);
            logger.LogInformation("Generated {Count} documentation patches from learned patterns", patchCount);
        }
    }

    public async Task CleanupOldSignalsAsync(int retentionDays = 0, CancellationToken ct = default)
    {
        try
        {
            var settings = await settingsProvider.GetSettingsAsync(ct);
            if (!settings.EnableLearning) return;

            var days = retentionDays > 0 ? retentionDays : settings.LearningSignalRetentionDays;
            var cutoff = DateTime.UtcNow.AddDays(-days);

            await using var context = await contextFactory.CreateDbContextAsync(ct);
            var deleted = await context.McpQuerySignals
                .Where(s => s.CreatedTime < cutoff)
                .ExecuteDeleteAsync(ct);

            if (deleted > 0)
                logger.LogInformation("Cleaned up {Count} old MCP query signals (retention: {Days} days)", deleted, days);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to cleanup old MCP query signals");
        }
    }

    private static List<string> TryDeserializeJsonArray(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<string> GetTablePairs(List<string> tables)
    {
        for (var i = 0; i < tables.Count; i++)
            for (var j = i + 1; j < tables.Count; j++)
            {
                var a = string.Compare(tables[i], tables[j], StringComparison.OrdinalIgnoreCase) <= 0
                    ? tables[i] : tables[j];
                var b = a == tables[i] ? tables[j] : tables[i];
                yield return $"{a} + {b}";
            }
    }

    /// <summary>
    /// Given a wrong column name, the list of available columns, and the corrected SQL,
    /// finds the most likely correct column by similarity + presence in corrected SQL.
    /// </summary>
    private static string? FindBestColumnMatch(string wrongColumn, string availableCsv, string? correctedSql)
    {
        var available = availableCsv.Split(',')
            .Select(c => c.Trim())
            .Where(c => c.Length > 0)
            .ToList();

        if (available.Count == 0) return null;

        // Narrow to columns that actually appear in the corrected SQL
        var candidates = available;
        if (!string.IsNullOrEmpty(correctedSql))
        {
            var inSql = available
                .Where(col => Regex.IsMatch(correctedSql, @$"\b{Regex.Escape(col)}\b", RegexOptions.IgnoreCase))
                .ToList();
            if (inSql.Count > 0)
            {
                candidates = inSql;
            }
        }

        return candidates
            .Select(c => (Column: c, Score: ColumnSimilarity(wrongColumn, c)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Column)
            .FirstOrDefault();
    }

    /// <summary>
    /// Finds the replacement column by diffing identifiers between generated and corrected SQL.
    /// Used when we don't have the Available columns list (execution error path).
    /// </summary>
    private static string? FindReplacementInCorrectedSql(string wrongColumn, string? generatedSql, string? correctedSql)
    {
        if (string.IsNullOrEmpty(generatedSql) || string.IsNullOrEmpty(correctedSql)) return null;

        var genWords = ExtractSqlIdentifiers(generatedSql);
        var corWords = ExtractSqlIdentifiers(correctedSql);
        var newWords = corWords.Except(genWords, StringComparer.OrdinalIgnoreCase).ToList();

        return newWords
            .Select(c => (Column: c, Score: ColumnSimilarity(wrongColumn, c)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Column)
            .FirstOrDefault();
    }

    private static double ColumnSimilarity(string wrong, string candidate)
    {
        var w = wrong.ToLowerInvariant();
        var c = candidate.ToLowerInvariant();
        if (w == c) return 0; // Same name isn't a correction

        // One contains the other, but only if the contained part is substantial (>= 4 chars)
        // to avoid false matches like "paid_up_date" containing "id"
        if (c.Length >= 4 && w.Contains(c)) return 0.9;
        if (w.Length >= 4 && c.Contains(w)) return 0.85;

        // Common prefix (paid_up_date vs paid_date share "paid_")
        var prefixLen = 0;
        for (var i = 0; i < Math.Min(w.Length, c.Length); i++)
        {
            if (w[i] == c[i]) prefixLen++;
            else break;
        }

        if (prefixLen >= 3)
            return 0.5 + (0.3 * prefixLen / Math.Max(w.Length, c.Length));

        // Shared underscore-delimited parts (e.g., "loan_date" vs "approval_date" share "date")
        var wParts = w.Split('_').ToHashSet();
        var cParts = c.Split('_').ToHashSet();
        var shared = wParts.Intersect(cParts).Count();
        if (shared > 0)
            return 0.3 + (0.3 * shared / Math.Max(wParts.Count, cParts.Count));

        return 0;
    }

    private static HashSet<string> ExtractSqlIdentifiers(string sql)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(sql, @"\b\w+\b"))
        {
            if (m.Value.Length > 1 && !SqlKeywords.Contains(m.Value))
            {
                result.Add(m.Value);
            }
        }

        return result;
    }

    private static readonly HashSet<string> SqlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "IN", "ON", "AS",
        "JOIN", "LEFT", "RIGHT", "INNER", "OUTER", "CROSS", "FULL",
        "GROUP", "BY", "ORDER", "HAVING", "LIMIT", "OFFSET",
        "INSERT", "UPDATE", "DELETE", "INTO", "VALUES", "SET",
        "CREATE", "ALTER", "DROP", "TABLE", "INDEX",
        "CASE", "WHEN", "THEN", "ELSE", "END",
        "IS", "NULL", "TRUE", "FALSE", "BETWEEN", "LIKE", "EXISTS",
        "ASC", "DESC", "DISTINCT", "ALL", "ANY", "SOME",
        "UNION", "INTERSECT", "EXCEPT", "WITH", "RECURSIVE",
        "OVER", "PARTITION", "WINDOW", "FILTER",
        "DATE", "INTERVAL", "CURRENT_DATE", "CURRENT_TIMESTAMP",
        "COALESCE", "NULLIF", "CAST", "EXTRACT",
        "SUM", "COUNT", "AVG", "MIN", "MAX", "ROUND",
        "DATE_TRUNC", "GENERATE_SERIES", "LAG", "LEAD",
        "ROW_NUMBER", "RANK", "DENSE_RANK"
    };

    // Run-scoped tally of LLM lesson-extraction attempts vs. null (fell-back) results, threaded through the
    // detectors so a persistently dead LLM-primary path can be reported once at the end of the run (F7).
    internal sealed class ExtractionStats
    {
        public int Attempts { get; set; }
        public int NullResults { get; set; }
    }
}
