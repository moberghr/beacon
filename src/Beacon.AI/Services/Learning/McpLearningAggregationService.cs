using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Helpers;
using Beacon.Core.Services;
using Beacon.Core.Services.Retention;

namespace Beacon.AI.Services.Learning;

internal sealed class McpLearningAggregationService(
    IDbContextFactory<BeaconContext> contextFactory,
    IMcpSettingsProvider settingsProvider,
    IGroundingSqlCheck groundingSqlCheck,
    ILogger<McpLearningAggregationService> logger,
    ILessonExtractor? lessonExtractor = null,
    IPatternReplayVerifier? replayVerifier = null) : IMcpLearningAggregationService
{
    // The tool of a signal that answers a natural-language question.
    private const string AskTool = "ask";

    // LLM lesson extractions per project and run; further clusters take the deterministic content.
    internal const int MaxLessonExtractionsPerRun = 20;

    private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromSeconds(1);

    // The sentences the detectors write a pattern's statistics into — a count, or an error rate and a count, the numbers
    // SignalCount and Confidence also hold — matched against the whole content, each with what replaces the statistics
    // when content is compared. Content of any other shape (a lesson, numbers in it included) is compared as written.
    private static readonly (Regex Sentence, string WithoutCounts)[] CountedSentences =
    [
        (new Regex(@"\A(Common query pattern for .+ \()[0-9]+( similar queries\))\z", CountedSentenceOptions, RegexMatchTimeout), "$1#$2"),
        (new Regex(@"\A(Tables .+ are frequently joined together \()[0-9]+( queries\))\z", CountedSentenceOptions, RegexMatchTimeout), "$1#$2"),
        (new Regex(
            @"\A(Table '.+' has a )(?:[0-9]+[\u00A0\u202F ]?%|%[\u00A0\u202F ]?[0-9]+)( error rate across )[0-9]+( queries\. Schema descriptions may be missing or misleading\.)\z",
            CountedSentenceOptions,
            RegexMatchTimeout), "$1#$2#$3")
    ];

    private const RegexOptions CountedSentenceOptions = RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline;

    public async Task AggregateLearnedPatternsAsync(CancellationToken ct = default)
    {
        var settings = await settingsProvider.GetSettingsAsync(ct);
        if (!settings.EnableLearning) return;

        List<int> projectIds;
        {
            await using var context = await contextFactory.CreateDbContextAsync(ct);
            projectIds = await context.McpQuerySignals
                .Where(x => x.ProjectId != null)
                .Select(x => x.ProjectId!.Value)
                .Distinct()
                .ToListAsync(ct);
        }

        // Tallies LLM-extraction attempts vs. null results across every cluster this run so a persistently
        // failing (misconfigured) LLM-primary path surfaces ONCE at the end instead of hiding behind a green
        // job. The run's SQL check reads each data source's dialect and catalog once.
        var extraction = new ExtractionStats { SqlCheck = groundingSqlCheck.StartBatch() };

        foreach (var projectId in projectIds)
        {
            try
            {
                // Per-project effective settings (retention window, replay gate, thresholds); a project that has
                // learning switched off is skipped while the global switch stays the outer gate.
                var projectSettings = await settingsProvider.GetEffectiveSettingsAsync(projectId, ct);
                if (!projectSettings.EnableLearning)
                {
                    continue;
                }

                await using var projectContext = await contextFactory.CreateDbContextAsync(ct);
                await AggregateForProjectAsync(projectContext, projectId, projectSettings, extraction, ct);
            }
            catch (OperationCanceledException)
            {
                // Host shutdown / cancellation must unwind the whole run, not be logged-and-continued per
                // project (the extraction + replay orchestration below correctly rethrows OCE).
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Failed to aggregate patterns for project {ProjectId} ({Failure})", projectId, FailureCodes.Describe(ex));
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

        if (extraction.ReusedExtractions > 0 || extraction.CappedExtractions > 0)
        {
            logger.LogInformation(
                "Lesson extraction this run: {Attempts} LLM call(s), {Reused} cluster(s) reused an extracted lesson, {Capped} cluster(s) past the per-project cap of {Cap} used the deterministic content",
                extraction.Attempts, extraction.ReusedExtractions, extraction.CappedExtractions, MaxLessonExtractionsPerRun);
        }

        if (extraction.BlockedExampleSql > 0)
        {
            logger.LogWarning(
                "{Count} learned-pattern example SQL(s) did not pass the SQL check this run and were not stored",
                extraction.BlockedExampleSql);
        }

        if (extraction.BlockedPromotions > 0)
        {
            logger.LogWarning(
                "{Count} learned pattern(s) were not auto-approved this run: their example SQL did not pass the SQL check",
                extraction.BlockedPromotions);
        }

        if (extraction.Demotions > 0)
        {
            logger.LogInformation(
                "{Count} auto-approved learned pattern(s) went back to NeedsEvidence this run: their content changed",
                extraction.Demotions);
        }
    }

    private async Task AggregateForProjectAsync(
        BeaconContext context, int projectId, McpSettingsData settings, ExtractionStats extraction, CancellationToken ct)
    {
        var retainContent = ContentRetentionDecision.From(settings).RetainQueryContent;
        var globalSettings = await settingsProvider.GetSettingsAsync(ct);
        var globalDays = RetentionDays(globalSettings.LearningSignalRetentionDays, McpSettingsData.DefaultLearningSignalRetentionDays);
        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays(settings.LearningSignalRetentionDays, globalDays));

        // Mine ONLY the taxonomy the detectors expect: ask (NL question → generated SQL) and query
        // (caller SQL executed for real). dry_run signals stay in the table for other analytics, but
        // their Question is the SQL under validation — not natural language — and a gate rejection
        // lands IsSuccessful=false without any execution, so letting them in would pollute NL-pattern
        // mining and inflate documentation-gap error rates (R6-3).
        var signals = await context.McpQuerySignals
            .Where(x => x.ProjectId == projectId)
            .Where(x => x.CreatedTime >= cutoff)
            .Where(x => x.DataSourceId != null)
            .Where(x => x.Tool == "ask" || x.Tool == "query")
            .OrderByDescending(x => x.CreatedTime)
            .ToListAsync(ct);

        if (signals.Count < 3) return; // Not enough data

        // Group by data source
        var byDataSource = signals.GroupBy(s => s.DataSourceId!.Value);

        foreach (var dsGroup in byDataSource)
        {
            var dataSourceId = dsGroup.Key;

            // 1. Schema Corrections: validation failed + retry succeeded
            await DetectSchemaCorrectionsAsync(context, projectId, dataSourceId, dsGroup.ToList(), extraction, retainContent, ct);

            // 2. Common Queries: cluster successful queries. Skipped under the content lock — its
            // exemplar carries the free-text Question and GeneratedSql/CorrectedSql (§ Registry).
            if (retainContent)
            {
                await DetectCommonQueriesAsync(context, projectId, dataSourceId, dsGroup.ToList(), extraction, ct);
            }

            // 3. Join Patterns: multi-table successful queries
            await DetectJoinPatternsAsync(context, projectId, dataSourceId, dsGroup.ToList(), retainContent, extraction, ct);

            // 4. Documentation Gaps: high error rate tables. Skipped under the content lock (§ Registry).
            if (retainContent)
            {
                await DetectDocumentationGapsAsync(context, projectId, dataSourceId, dsGroup.ToList(), extraction, ct);
            }
        }

        // Temporal decay (§ Architecture ⑧): mark schema-corrections stale when their referenced column no
        // longer exists in current metadata. Joins this method's single unit of work (one SaveChanges, §5.7).
        await DetectStalePatternsAsync(context, projectId, ct);

        await context.SaveChangesAsync(ct);

        // Replay-verification gate (§ Architecture ⑥): candidates were upserted as NeedsEvidence above;
        // promote only those that MEASURABLY help against the golden set. Confidence alone never promotes.
        await PromoteVerifiedCandidatesAsync(context, projectId, settings, extraction, ct);
    }

    // Promotes NeedsEvidence candidates for the project via the replay verifier: a candidate becomes
    // AutoApproved ONLY when injecting it flips ≥ LearningReplayMinFlips of its relevant failing golden
    // cases with zero relevant regressions. When replay is disabled or no verifier is wired, candidates
    // stay NeedsEvidence (safe default — never auto-approve without measured evidence). Human Approve/Reject
    // is a separate path and untouched. Like a human approval, a candidate's example SQL passes the grounding SQL check
    // again first (stored without comments); a candidate whose SQL the check refuses is not replayed and stays
    // NeedsEvidence, counted and logged by id. Internal so it can be unit-tested in isolation.
    internal async Task PromoteVerifiedCandidatesAsync(
        BeaconContext context, int projectId, McpSettingsData settings, ExtractionStats? extraction, CancellationToken ct)
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
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .Where(x => x.Status == McpPatternStatus.NeedsEvidence)
            .ToListAsync(ct);

        if (candidates.Count == 0)
        {
            return;
        }

        var sqlCheck = extraction?.SqlCheck ?? groundingSqlCheck;
        var promoted = 0;
        foreach (var candidate in candidates)
        {
            try
            {
                // What the replay measures, as read: the auto-approval is written only while the row still holds it.
                var measured = new MeasuredContent(candidate.PatternContent, candidate.ExampleQuestion, candidate.ExampleSql);
                var replayed = candidate;
                if (!string.IsNullOrWhiteSpace(candidate.ExampleSql))
                {
                    var check = await sqlCheck.CheckAsync(candidate.DataSourceId, candidate.ExampleSql, ct);
                    if (!check.IsAllowed)
                    {
                        if (extraction != null)
                        {
                            extraction.BlockedPromotions++;
                        }

                        logger.LogWarning(
                            "Learned pattern {PatternId} of project {ProjectId} was not replayed for auto-approval: its example SQL did not pass the SQL check",
                            candidate.Id,
                            projectId);
                        continue;
                    }

                    replayed = WithExampleSql(candidate, check.Sql);
                }

                var verdict = await replayVerifier.VerifyAsync(replayed, settings.LearningReplayMinFlips, ct);

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

                if (verdict.Passed && await AutoApproveAsync(context, candidate.Id, candidate.ContentHash, measured, replayed.ExampleSql, ct))
                {
                    promoted++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Replay verification failed for pattern {PatternId}; leaving NeedsEvidence ({Failure})", candidate.Id, FailureCodes.Describe(ex));
            }
        }

        if (promoted > 0)
        {
            // Each promotion was written by its own conditional update, after the detection pass in
            // AggregateForProjectAsync flushed the NeedsEvidence candidates this pass re-reads (including ones created in
            // earlier runs).
            logger.LogInformation("Replay-verified {Count} learned pattern(s) → AutoApproved for project {ProjectId}", promoted, projectId);
        }
    }

    // Auto-approval only from NeedsEvidence, and only of the content the replay measured, compared by its stored digest
    // (exact under any database collation): a person's decision, or a refresh, between the read and this write leaves the
    // row as it is (false). A pattern stored before Beacon kept the digest has it stored first, only while it still holds no
    // digest and the content read.
    private static async Task<bool> AutoApproveAsync(
        BeaconContext context, int patternId, string? storedHash, MeasuredContent measured, string? exampleSql, CancellationToken ct)
    {
        var measuredHash = GroundingContentHash.OfPattern(measured.PatternContent, measured.ExampleQuestion, measured.ExampleSql);
        if (storedHash == null)
        {
            await context.McpLearnedPatterns
                .Where(x => x.Id == patternId)
                .Where(x => x.ContentHash == null)
                .Where(x => x.PatternContent == measured.PatternContent)
                .Where(x => x.ExampleQuestion == measured.ExampleQuestion)
                .Where(x => x.ExampleSql == measured.ExampleSql)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.ContentHash, measuredHash), ct);
        }

        var approvedHash = GroundingContentHash.OfPattern(measured.PatternContent, measured.ExampleQuestion, exampleSql);
        var verifiedAt = DateTime.UtcNow;
        var written = await context.McpLearnedPatterns
            .Where(x => x.Id == patternId)
            .Where(x => x.Status == McpPatternStatus.NeedsEvidence)
            .Where(x => x.ContentHash == measuredHash)
            .ExecuteUpdateAsync(
                x => x
                    .SetProperty(y => y.Status, McpPatternStatus.AutoApproved)
                    .SetProperty(y => y.ExampleSql, exampleSql)
                    .SetProperty(y => y.ContentHash, approvedHash)
                    .SetProperty(y => y.LastVerifiedAt, verifiedAt),
                ct);

        return written > 0;
    }

    // The candidate as the replay sees it: with its example SQL as the check returned it. A copy, so the row read stays the
    // content the conditional write compares with.
    private static McpLearnedPattern WithExampleSql(McpLearnedPattern pattern, string? exampleSql)
    {
        return new McpLearnedPattern
        {
            Id = pattern.Id,
            CreatedTime = pattern.CreatedTime,
            ProjectId = pattern.ProjectId,
            DataSourceId = pattern.DataSourceId,
            SchemaName = pattern.SchemaName,
            TableName = pattern.TableName,
            ColumnName = pattern.ColumnName,
            PatternType = pattern.PatternType,
            PatternContent = pattern.PatternContent,
            ExampleQuestion = pattern.ExampleQuestion,
            ExampleSql = exampleSql,
            ContentHash = pattern.ContentHash,
            SignalCount = pattern.SignalCount,
            Confidence = pattern.Confidence,
            Status = pattern.Status,
            ReviewedByUserId = pattern.ReviewedByUserId,
            ReviewedAt = pattern.ReviewedAt,
            LastRefreshedAt = pattern.LastRefreshedAt,
            SupersededAt = pattern.SupersededAt,
            LastVerifiedAt = pattern.LastVerifiedAt
        };
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
        List<McpQuerySignal> signals, ExtractionStats extraction, bool retainContent, CancellationToken ct)
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
            RegexOptions.IgnoreCase,
            RegexMatchTimeout);

        // PostgreSQL execution error format: '42703: column "created_at" does not exist'
        var pgErrorPattern = new Regex(
            @"column ""(\w+)"" does not exist",
            RegexOptions.IgnoreCase,
            RegexMatchTimeout);

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

        var clusters = new List<SchemaCorrectionCluster>();
        foreach (var group in grouped)
        {
            var first = group.First();
            var correctCol = group
                .Select(m => m.CorrectColumn)
                .FirstOrDefault(c => c != null);

            // The linear count-based confidence is retained ONLY as a prior/tiebreak — it is still passed
            // through regardless of which path builds the pattern (§ Architecture ⑦).
            var cluster = new SchemaCorrectionCluster(first.Schema, first.Table, first.WrongColumn, correctCol, first.Signal, group.Count(),
                Math.Min(1.0, 0.5 + (group.Count() * 0.15)));
            if (retainContent && lessonExtractor is { IsAvailable: true })
            {
                cluster.Lesson = await FindExtractedLessonAsync(context, projectId, dataSourceId, cluster, ct);
            }

            clusters.Add(cluster);
        }

        // The per-project cap is spent on clusters without an extracted lesson first, then on lessons with new evidence,
        // then in the order they have.
        foreach (var cluster in clusters.OrderBy(x => x.Lesson == null ? 0 : x.Lesson.SignalCount == x.SignalCount ? 2 : 1))
        {
            // LLM-PRIMARY: extract a structured lesson from the representative failure. On a non-null
            // lesson use the LLM's content/example/type; on null (or no extractor) fall back below.
            // Under the content lock the extractor is never invoked — its FailureCluster carries
            // Question/GeneratedSql/CorrectedSql/error text and its returned lesson is free text
            // (§ Registry classification: PatternContent is Structural under the lock ONLY as the
            // deterministic template built from schema/table/column names).
            if (retainContent && lessonExtractor is { IsAvailable: true })
            {
                // A lesson already extracted for this column is kept while its cluster has the same number of signals
                // (no new evidence; an equal count is the rule for "unchanged"): only its statistics refresh, no LLM call.
                if (cluster.Lesson is { } kept && kept.SignalCount == cluster.SignalCount)
                {
                    if (await RefreshLessonAsync(context, kept, cluster, advance: true, ct))
                    {
                        extraction.ReusedExtractions++;
                        continue;
                    }

                    cluster.Lesson = null;
                }

                extraction.ExtractionsByProject.TryGetValue(projectId, out var extractedForProject);
                if (extractedForProject < MaxLessonExtractionsPerRun)
                {
                    extraction.ExtractionsByProject[projectId] = extractedForProject + 1;
                    var representative = cluster.Signal;
                    var failure = new FailureCluster(
                        dataSourceId,
                        cluster.Schema,
                        cluster.Table,
                        cluster.WrongColumn,
                        representative.Question,
                        representative.GeneratedSql,
                        representative.SchemaValidationError ?? representative.ExecutionError,
                        representative.CorrectedSql,
                        null)
                    {
                        ProjectId = projectId
                    };

                    extraction.Attempts++;
                    var lesson = await lessonExtractor.ExtractAsync(failure, ct);
                    if (lesson != null)
                    {
                        // An example question only from an answer to a question: a query signal's "question" is SQL.
                        var lessonQuestion = representative.Tool == AskTool ? GroundingText.Question(lesson.ExampleQuestion) : null;
                        await UpsertPatternAsync(context, projectId, dataSourceId, cluster.Schema, cluster.Table, cluster.WrongColumn,
                            lesson.PatternType, lesson.PatternContent, lessonQuestion, lesson.ExampleSql,
                            cluster.SignalCount, cluster.Confidence, extraction, ct);
                        continue;
                    }

                    // Extractor was tried but produced nothing usable — record it so an all-null run is visible.
                    extraction.NullResults++;
                }
                else
                {
                    // Past the per-run cap the cluster takes the deterministic content below.
                    extraction.CappedExtractions++;
                }

                // A cluster that gets no new lesson never replaces the one it has, and its stored signal count does not move, so
                // the next run still sees the new evidence and tries again: only the refresh time is written.
                if (cluster.Lesson is { } existingLesson && await RefreshLessonAsync(context, existingLesson, cluster, advance: false, ct))
                {
                    continue;
                }
            }

            // FALLBACK: deterministic regex + ColumnSimilarity content (unchanged).
            await UpsertPatternAsync(context, projectId, dataSourceId, cluster.Schema, cluster.Table, cluster.WrongColumn,
                McpPatternType.SchemaCorrection, DeterministicContent(cluster.Schema, cluster.Table, cluster.WrongColumn, cluster.CorrectColumn), null, null,
                cluster.SignalCount, cluster.Confidence, extraction, ct);
        }
    }

    internal async Task DetectCommonQueriesAsync(
        BeaconContext context, int projectId, int dataSourceId,
        List<McpQuerySignal> signals, ExtractionStats extraction, CancellationToken ct)
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
            // Prefer a curator-CONFIRMED signal as the exemplar, then a non-failed one.
            var representative = group
                .OrderByDescending(s => IsCuratorVerified(s) ? 1 : 0)
                .ThenBy(s => s.SchemaValidationFailed ? 1 : 0)
                .ThenByDescending(s => s.ResultRowCount ?? 0)
                .First();

            var tables = TryDeserializeJsonArray(group.Key);
            if (tables.Count == 0) continue;

            var primaryTable = tables[0];
            var parts = primaryTable.Split('.');
            var schema = parts.Length > 1 ? parts[0] : "public";
            var table = parts.Length > 1 ? parts[1] : parts[0];

            // Part B: a group containing a curator-CONFIRMED answer is more trustworthy than one resting on
            // execution success alone, so it earns a confidence bonus.
            var curatorVerified = group.Any(s => IsCuratorVerified(s));
            var content = $"Common query pattern for {primaryTable} ({group.Count()} similar queries)";
            var confidence = Math.Min(1.0, 0.4 + (group.Count() * 0.1) + (curatorVerified ? 0.2 : 0.0));

            // Use corrected SQL if the representative had a correction — never store broken SQL
            var exampleSql = representative.CorrectedSql ?? representative.GeneratedSql;

            await UpsertPatternAsync(context, projectId, dataSourceId, schema, table, null,
                McpPatternType.CommonQuery, content, ExampleQuestionOf(representative), exampleSql,
                group.Count(), confidence, extraction, ct);
        }
    }

    internal async Task DetectJoinPatternsAsync(
        BeaconContext context, int projectId, int dataSourceId,
        List<McpQuerySignal> signals, bool retainContent, ExtractionStats extraction, CancellationToken ct)
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
            // Prefer a curator-CONFIRMED join as the exemplar.
            var representative = group
                .OrderByDescending(x => IsCuratorVerified(x.Signal) ? 1 : 0)
                .First().Signal;

            var parts = pair.Split('+');
            var table1Parts = parts[0].Trim().Split('.');
            var schema = table1Parts.Length > 1 ? table1Parts[0] : "public";
            var table = table1Parts.Length > 1 ? table1Parts[1] : table1Parts[0];

            var curatorVerified = group.Any(x => IsCuratorVerified(x.Signal));
            var content = $"Tables {pair} are frequently joined together ({group.Count()} queries)";
            var confidence = Math.Min(1.0, 0.5 + (group.Count() * 0.1) + (curatorVerified ? 0.2 : 0.0));

            // ExampleQuestion/ExampleSql are Content (§ Registry) — under the lock the pattern still
            // records that these tables are joined, but never carries the free-text question or SQL.
            var exampleQuestion = retainContent ? ExampleQuestionOf(representative) : null;
            var exampleSql = retainContent ? representative.GeneratedSql : null;

            await UpsertPatternAsync(context, projectId, dataSourceId, schema, table, null,
                McpPatternType.JoinPattern, content, exampleQuestion, exampleSql,
                group.Count(), confidence, extraction, ct);
        }
    }

    internal async Task DetectDocumentationGapsAsync(
        BeaconContext context, int projectId, int dataSourceId,
        List<McpQuerySignal> signals, ExtractionStats extraction, CancellationToken ct)
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
                tableStats.Total, confidence, extraction, ct);
        }
    }

    // The deterministic content of a schema correction: the one text the fallback branch writes, and the only text
    // that tells a stored pattern apart from a lesson the extractor wrote.
    private static string DeterministicContent(string schema, string table, string wrongColumn, string? correctColumn) =>
        correctColumn != null
            ? $"NEVER use '{wrongColumn}' on {schema}.{table} — correct column is '{correctColumn}'"
            : $"Column '{wrongColumn}' does not exist on {schema}.{table} — check schema";

    // True when the content is the fallback's own text for the cluster's column: with any replacement column (the one
    // found when the pattern was written may be an older one) or without one.
    private static bool IsDeterministicContent(string content, string schema, string table, string wrongColumn)
    {
        if (content == DeterministicContent(schema, table, wrongColumn, null))
        {
            return true;
        }

        var prefix = DeterministicContent(schema, table, wrongColumn, "\u0001");
        var parts = prefix.Split("\u0001");

        return content.Length > parts[0].Length + parts[1].Length
            && content.StartsWith(parts[0], StringComparison.Ordinal)
            && content.EndsWith(parts[1], StringComparison.Ordinal)
            && !content[parts[0].Length..^parts[1].Length].Contains('\'');
    }

    // The lesson the extractor wrote for the cluster's column: a pattern of the column whose content is not the
    // deterministic text for it.
    private static async Task<StoredLesson?> FindExtractedLessonAsync(
        BeaconContext context, int projectId, int dataSourceId, SchemaCorrectionCluster cluster, CancellationToken ct)
    {
        var existing = await context.McpLearnedPatterns
            .Where(x => x.ProjectId == projectId)
            .Where(x => x.DataSourceId == dataSourceId)
            .Where(x => x.TableName == cluster.Table)
            .Where(x => x.SchemaName == cluster.Schema)
            .Where(x => x.ColumnName == cluster.WrongColumn)
            .OrderBy(x => x.Id)
            .Select(x =>
                new StoredLesson(x.Id, x.Status, x.SignalCount, x.PatternContent))
            .ToListAsync(ct);

        return existing
            .Where(x => !IsDeterministicContent(x.Content, cluster.Schema, cluster.Table, cluster.WrongColumn))
            .FirstOrDefault();
    }

    // Refreshes a lesson, conditional on the status it was read with (a person's decision in between is never
    // overwritten): its statistics too when the evidence is the same, only the refresh time otherwise. False when nothing
    // was written.
    private static async Task<bool> RefreshLessonAsync(
        BeaconContext context, StoredLesson lesson, SchemaCorrectionCluster cluster, bool advance, CancellationToken ct)
    {
        var refreshedAt = DateTime.UtcNow;
        var query = context.McpLearnedPatterns
            .Where(x => x.Id == lesson.Id)
            .Where(x => x.Status == lesson.Status);
        var written = advance
            ? await query.ExecuteUpdateAsync(
                x => x
                    .SetProperty(y => y.SignalCount, cluster.SignalCount)
                    .SetProperty(y => y.Confidence, cluster.Confidence)
                    .SetProperty(y => y.LastRefreshedAt, refreshedAt),
                ct)
            : await query.ExecuteUpdateAsync(x => x.SetProperty(y => y.LastRefreshedAt, refreshedAt), ct);

        return written > 0;
    }

    // Every example SQL a pattern stores passes the grounding SQL check first and is stored without its comments; SQL the
    // check refuses is not stored (a new pattern gets none, an existing one keeps the example it has), counted and logged
    // by identifiers only (§1.11). A decision binds to the content it was made on: a pattern a person approved or
    // rejected keeps its content, question and example SQL (only its statistics refresh), and an auto-approved pattern
    // whose content changes goes back to NeedsEvidence, for the replay gate to measure again. Content is compared exactly
    // but for the statistics the detectors write into their own sentences (WithoutCounts), and SQL exactly but for its
    // comments and the whitespace between its tokens (SameSql), so a count that moves, or SQL stored before comments were
    // removed, is no change; a changed number, literal or identifier is. Every write stores the content's digest with it
    // (McpLearnedPattern.ContentHash), and every write to an existing pattern is conditional on the status it was read
    // with: a person's decision between the read and the write is never overwritten.
    private async Task UpsertPatternAsync(
        BeaconContext context, int projectId, int dataSourceId,
        string schema, string table, string? column,
        McpPatternType type, string content, string? exampleQuestion, string? exampleSql,
        int signalCount, double confidence, ExtractionStats extraction, CancellationToken ct)
    {
        var existing = await context.McpLearnedPatterns
            .Where(x => x.ProjectId == projectId)
            .Where(x => x.DataSourceId == dataSourceId)
            .Where(x => x.TableName == table)
            .Where(x => x.SchemaName == schema)
            .Where(x => x.ColumnName == column)
            .Where(x => x.PatternType == type)
            .Select(x =>
                new
                {
                    x.Id,
                    x.Status,
                    x.PatternContent,
                    x.ExampleQuestion,
                    x.ExampleSql
                })
            .FirstOrDefaultAsync(ct);
        var refreshedAt = DateTime.UtcNow;

        if (existing is { Status: McpPatternStatus.Approved or McpPatternStatus.Rejected })
        {
            await context.McpLearnedPatterns
                .Where(x => x.Id == existing.Id)
                .Where(x => x.Status == existing.Status)
                .ExecuteUpdateAsync(
                    x => x
                        .SetProperty(y => y.SignalCount, signalCount)
                        .SetProperty(y => y.Confidence, confidence)
                        .SetProperty(y => y.LastRefreshedAt, refreshedAt),
                    ct);

            return;
        }

        var check = await CheckedExampleSqlAsync(projectId, dataSourceId, type, exampleSql, extraction, ct);
        if (existing == null)
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
                ExampleSql = check?.Sql,
                ContentHash = GroundingContentHash.OfPattern(content, exampleQuestion, check?.Sql),
                SignalCount = signalCount,
                Confidence = confidence,
                Status = McpPatternStatus.NeedsEvidence,
                LastRefreshedAt = refreshedAt
            });

            return;
        }

        var refreshedQuestion = exampleQuestion ?? existing.ExampleQuestion;
        var refreshedSql = check?.Sql ?? existing.ExampleSql;
        var contentChanged = WithoutCounts(existing.PatternContent) != WithoutCounts(content)
            || GroundingText.Question(existing.ExampleQuestion) != GroundingText.Question(refreshedQuestion)
            || !SameSql(existing.ExampleSql, refreshedSql, check?.Dialect);

        // Promotion is decided by the replay-verification gate, NOT confidence: a refresh leaves
        // Pending/NeedsEvidence as they are, and sends an auto-approval whose content changed back for evidence.
        var demoted = contentChanged && existing.Status == McpPatternStatus.AutoApproved;
        var status = demoted ? McpPatternStatus.NeedsEvidence : existing.Status;
        var refreshedHash = GroundingContentHash.OfPattern(content, refreshedQuestion, refreshedSql);
        var written = await context.McpLearnedPatterns
            .Where(x => x.Id == existing.Id)
            .Where(x => x.Status == existing.Status)
            .ExecuteUpdateAsync(
                x => x
                    .SetProperty(y => y.PatternContent, content)
                    .SetProperty(y => y.ExampleQuestion, refreshedQuestion)
                    .SetProperty(y => y.ExampleSql, refreshedSql)
                    .SetProperty(y => y.ContentHash, refreshedHash)
                    .SetProperty(y => y.SignalCount, signalCount)
                    .SetProperty(y => y.Confidence, confidence)
                    .SetProperty(y => y.LastRefreshedAt, refreshedAt)
                    .SetProperty(y => y.Status, status),
                ct);
        if (written > 0 && demoted)
        {
            extraction.Demotions++;
        }
    }

    // The checked example SQL (with the dialect it was read in), or null when there is none or the check refused it.
    private async Task<GroundingSqlCheckResult?> CheckedExampleSqlAsync(
        int projectId, int dataSourceId, McpPatternType type, string? exampleSql, ExtractionStats extraction, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(exampleSql))
        {
            return null;
        }

        var check = await (extraction.SqlCheck ?? groundingSqlCheck).CheckAsync(dataSourceId, exampleSql, ct);
        if (check.IsAllowed)
        {
            return check;
        }

        extraction.BlockedExampleSql++;
        logger.LogWarning(
            "The example SQL of a {PatternType} pattern for project {ProjectId}, data source {DataSourceId} did not pass the SQL check and was not stored",
            type,
            projectId,
            dataSourceId);

        return null;
    }

    // A curator's Correct verdict — recorded by an interactive session holding Curate in the project at that moment
    // (McpQuerySignal.FeedbackFromCurator), never by an API key or an MCP caller: the only verdict that weighs in.
    private static bool IsCuratorVerified(McpQuerySignal signal)
    {
        return signal.UserVerdict == McpUserVerdict.Correct && signal.FeedbackFromCurator;
    }

    // An example question only from an answer to a question (a query signal's "question" is the SQL it ran), on one line
    // and without characters a reviewer cannot see (else none).
    private static string? ExampleQuestionOf(McpQuerySignal signal)
    {
        return signal.Tool == AskTool ? GroundingText.Question(signal.Question) : null;
    }

    // Pattern content with only the statistics the detectors write into their own sentences left out (CountedSentences): a
    // pattern whose count or error rate moved is the same pattern. Any other content is compared as written.
    private static string WithoutCounts(string content)
    {
        foreach (var (sentence, withoutCounts) in CountedSentences)
        {
            if (sentence.IsMatch(content))
            {
                return sentence.Replace(content, withoutCounts);
            }
        }

        return content;
    }

    // The same example SQL: as stored, or the same once comments are removed and the whitespace between the dialect's
    // tokens is one space (GroundingSqlText.ForComparison) — so SQL stored with comments before they were removed is the
    // same as its comment-free form. Literals, quoted identifiers and every other token compare as written; SQL the
    // dialect's tokenizer cannot read so compares as stored.
    private static bool SameSql(string? stored, string? refreshed, string? dialect)
    {
        if (stored == refreshed)
        {
            return true;
        }

        if (stored == null || refreshed == null)
        {
            return false;
        }

        var storedForm = GroundingSqlText.ForComparison(stored, dialect);

        return storedForm != null && storedForm == GroundingSqlText.ForComparison(refreshed, dialect);
    }

    public async Task GenerateDocumentationPatchesAsync(CancellationToken ct = default)
    {
        var settings = await settingsProvider.GetSettingsAsync(ct);
        if (!settings.EnableLearning) return;

        await using var context = await contextFactory.CreateDbContextAsync(ct);

        // Patches are proposed project by project, each from that project's own patterns (§1.12): a data source shared
        // by two projects never carries one project's learned patterns into the other project's patches.
        var projectIds = await context.McpLearnedPatterns
            .Where(x => x.Status == McpPatternStatus.Approved || x.Status == McpPatternStatus.AutoApproved)
            .Where(x => x.PatternType == McpPatternType.SchemaCorrection || x.PatternType == McpPatternType.DocumentationGap)
            .Select(x => x.ProjectId)
            .Distinct()
            .ToListAsync(ct);

        var patchCount = 0;
        foreach (var projectId in projectIds)
        {
            patchCount += await AddDocumentationPatchesForProjectAsync(context, projectId, ct);
        }

        if (patchCount > 0)
        {
            await context.SaveChangesAsync(ct);
            logger.LogInformation("Generated {Count} documentation patches from learned patterns", patchCount);
        }
    }

    // A stored window below one day never means "keep nothing": a window falls back to the next one up (project, then
    // global, then the default).
    internal static int RetentionDays(int days, int fallbackDays)
    {
        return days > 0 ? days : fallbackDays;
    }

    public async Task CleanupOldSignalsAsync(int retentionDays = 0, CancellationToken ct = default)
    {
        // Retention runs whether learning is on or off: signals are still recorded for projects whose own
        // settings turn learning on while the global switch is off, and they must still expire.
        var settings = await settingsProvider.GetSettingsAsync(ct);

        // A stored window below one day (a row written before the settings handlers refused it) never means "delete
        // every signal": the global window falls back to the default, a project's window to the global one.
        var globalDays = RetentionDays(settings.LearningSignalRetentionDays, McpSettingsData.DefaultLearningSignalRetentionDays);

        await using var context = await contextFactory.CreateDbContextAsync(ct);

        // Retention is a per-project setting: delete each project's signals with that project's effective
        // window; signals with no project use the global window. An explicit retentionDays argument wins.
        var projectIds = await context.McpQuerySignals
            .Where(x => x.ProjectId != null)
            .Select(x => x.ProjectId!.Value)
            .Distinct()
            .ToListAsync(ct);

        var deleted = 0;
        var failedProjects = 0;
        foreach (var projectId in projectIds)
        {
            // One project's failure must not keep every other project's signals past their window.
            try
            {
                var projectSettings = await settingsProvider.GetEffectiveSettingsAsync(projectId, ct);
                var projectWindow = RetentionDays(projectSettings.LearningSignalRetentionDays, globalDays);
                var projectDays = retentionDays > 0 ? retentionDays : projectWindow;
                var projectCutoff = DateTime.UtcNow.AddDays(-projectDays);
                deleted += await context.McpQuerySignals
                    .Where(x => x.ProjectId == projectId)
                    .Where(x => x.CreatedTime < projectCutoff)
                    .ExecuteDeleteAsync(ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failedProjects++;
                logger.LogWarning("Failed to clean up old MCP query signals for project {ProjectId} ({Failure})", projectId, FailureCodes.Describe(ex));
            }
        }

        var days = retentionDays > 0 ? retentionDays : globalDays;
        var cutoff = DateTime.UtcNow.AddDays(-days);
        deleted += await context.McpQuerySignals
            .Where(x => x.ProjectId == null)
            .Where(x => x.CreatedTime < cutoff)
            .ExecuteDeleteAsync(ct);

        if (deleted > 0)
        {
            logger.LogInformation("Cleaned up {Count} old MCP query signals (global retention: {Days} days; per-project windows applied)", deleted, days);
        }

        // The run is not a success while a project's signals were left past their window: the job is recorded failed.
        if (failedProjects > 0)
        {
            throw new InvalidOperationException(
                $"Old MCP query signals of {failedProjects} project(s) could not be cleaned up; every other project's signals were.");
        }
    }

    private async Task<int> AddDocumentationPatchesForProjectAsync(BeaconContext context, int projectId, CancellationToken ct)
    {
        // A project proposes patches only for the data sources it links today, as the exemplar index does: a project that
        // unlinked a source no longer speaks for it. Archiving a project keeps its links, so an archived project links none.
        var linkedDataSourceIds = await context.ProjectDataSources
            .Where(x => x.ProjectId == projectId)
            .Where(x => x.Project.ArchivedTime == null)
            .Select(x => x.DataSourceId)
            .ToListAsync(ct);

        if (linkedDataSourceIds.Count == 0)
        {
            return 0;
        }

        var approvedPatterns = await context.McpLearnedPatterns
            .Where(x => x.ProjectId == projectId)
            .Where(x => linkedDataSourceIds.Contains(x.DataSourceId))
            .Where(x => x.Status == McpPatternStatus.Approved || x.Status == McpPatternStatus.AutoApproved)
            .Where(x => x.PatternType == McpPatternType.SchemaCorrection || x.PatternType == McpPatternType.DocumentationGap)
            .ToListAsync(ct);

        if (approvedPatterns.Count == 0)
        {
            return 0;
        }

        // Only this project's own patches decide what is already proposed or applied for it.
        var existingTargets = await context.McpDocumentationPatches
            .Where(x => x.ProjectId == projectId)
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
                            ProjectId = projectId,
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
                            ProjectId = projectId,
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

        return patchCount;
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
                .Where(col => ContainsWord(correctedSql, col))
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

    private static bool ContainsWord(string text, string word)
    {
        try
        {
            return Regex.IsMatch(text, @$"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase, RegexMatchTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static HashSet<string> ExtractSqlIdentifiers(string sql)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(sql, @"\b\w+\b", RegexOptions.None, RegexMatchTimeout))
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

    private sealed record StoredLesson(int Id, McpPatternStatus Status, int SignalCount, string Content);

    private sealed class SchemaCorrectionCluster(
        string schema, string table, string wrongColumn, string? correctColumn, McpQuerySignal signal, int signalCount, double confidence)
    {
        public string Schema { get; } = schema;
        public string Table { get; } = table;
        public string WrongColumn { get; } = wrongColumn;
        public string? CorrectColumn { get; } = correctColumn;
        public McpQuerySignal Signal { get; } = signal;
        public int SignalCount { get; } = signalCount;
        public double Confidence { get; } = confidence;
        public StoredLesson? Lesson { get; set; }
    }

    // A candidate's content as the replay measured it.
    private sealed record MeasuredContent(string PatternContent, string? ExampleQuestion, string? ExampleSql);

    // Run-scoped tally of LLM lesson-extraction attempts vs. null (fell-back) results, threaded through the
    // detectors so a persistently dead LLM-primary path can be reported once at the end of the run, and of the
    // example SQL the grounding SQL check refused (when mined, and before an auto-approval), and of the auto-approved
    // patterns a content change sent back to NeedsEvidence, each reported the same way.
    // It also carries the run's grounding SQL check (IGroundingSqlCheck.StartBatch), which reads each data source's
    // dialect and catalog once per run; without one (a detector called on its own) the service's check is used.
    internal sealed class ExtractionStats
    {
        public int Attempts { get; set; }
        public int NullResults { get; set; }
        public int BlockedExampleSql { get; set; }
        public int BlockedPromotions { get; set; }
        public int Demotions { get; set; }
        public int ReusedExtractions { get; set; }
        public int CappedExtractions { get; set; }
        public Dictionary<int, int> ExtractionsByProject { get; } = [];
        public IGroundingSqlCheck? SqlCheck { get; init; }
    }
}
