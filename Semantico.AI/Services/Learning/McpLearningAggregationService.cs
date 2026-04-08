using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Semantico.Core.Data.Entities.Metadata;
using Semantico.Core.Services;

namespace Semantico.AI.Services.Learning;

internal sealed class McpLearningAggregationService(
    IDbContextFactory<SemanticoContext> contextFactory,
    IMcpSettingsProvider settingsProvider,
    ILogger<McpLearningAggregationService> logger) : IMcpLearningAggregationService
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

        foreach (var projectId in projectIds)
        {
            try
            {
                await using var projectContext = await contextFactory.CreateDbContextAsync(ct);
                await AggregateForProjectAsync(projectContext, projectId, settings.LearningAutoApproveThreshold, settings.LearningSignalRetentionDays, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to aggregate patterns for project {ProjectId}", projectId);
            }
        }
    }

    private async Task AggregateForProjectAsync(
        SemanticoContext context, int projectId, double autoApproveThreshold, int retentionDays, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
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
            await DetectSchemaCorrectionsAsync(context, projectId, dataSourceId, dsGroup.ToList(), autoApproveThreshold, ct);

            // 2. Common Queries: cluster successful queries
            await DetectCommonQueriesAsync(context, projectId, dataSourceId, dsGroup.ToList(), autoApproveThreshold, ct);

            // 3. Join Patterns: multi-table successful queries
            await DetectJoinPatternsAsync(context, projectId, dataSourceId, dsGroup.ToList(), autoApproveThreshold, ct);

            // 4. Documentation Gaps: high error rate tables
            await DetectDocumentationGapsAsync(context, projectId, dataSourceId, dsGroup.ToList(), autoApproveThreshold, ct);
        }

        await context.SaveChangesAsync(ct);
    }

    private static async Task DetectSchemaCorrectionsAsync(
        SemanticoContext context, int projectId, int dataSourceId,
        List<McpQuerySignal> signals, double threshold, CancellationToken ct)
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

        var mappings = new List<(string WrongColumn, string? CorrectColumn, string Schema, string Table)>();

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
                    mappings.Add((wrongCol, correctCol, schema, table));
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
                        mappings.Add((wrongCol, correctCol, schema, table));
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

            var content = correctCol != null
                ? $"NEVER use '{first.WrongColumn}' on {first.Schema}.{first.Table} — correct column is '{correctCol}'"
                : $"Column '{first.WrongColumn}' does not exist on {first.Schema}.{first.Table} — check schema";

            var confidence = Math.Min(1.0, 0.5 + (group.Count() * 0.15));

            await UpsertPatternAsync(context, projectId, dataSourceId, first.Schema, first.Table, first.WrongColumn,
                McpPatternType.SchemaCorrection, content, null, null,
                group.Count(), confidence, threshold, ct);
        }
    }

    private static async Task DetectCommonQueriesAsync(
        SemanticoContext context, int projectId, int dataSourceId,
        List<McpQuerySignal> signals, double threshold, CancellationToken ct)
    {
        var successful = signals
            .Where(s => s.IsSuccessful && !string.IsNullOrEmpty(s.GeneratedSql) && !string.IsNullOrEmpty(s.TablesUsed))
            .ToList();

        if (successful.Count < 3) return;

        // Group by tables used (as a proxy for semantic similarity)
        var byTables = successful
            .GroupBy(s => s.TablesUsed!)
            .Where(g => g.Count() >= 3);

        foreach (var group in byTables)
        {
            // Prefer non-failed signals for the representative example
            var representative = group
                .OrderBy(s => s.SchemaValidationFailed ? 1 : 0)
                .ThenByDescending(s => s.ResultRowCount ?? 0)
                .First();

            var tables = TryDeserializeJsonArray(group.Key);
            if (tables.Count == 0) continue;

            var primaryTable = tables[0];
            var parts = primaryTable.Split('.');
            var schema = parts.Length > 1 ? parts[0] : "public";
            var table = parts.Length > 1 ? parts[1] : parts[0];

            var content = $"Common query pattern for {primaryTable} ({group.Count()} similar queries)";
            var confidence = Math.Min(1.0, 0.4 + (group.Count() * 0.1));

            // Use corrected SQL if the representative had a correction — never store broken SQL
            var exampleSql = representative.CorrectedSql ?? representative.GeneratedSql;

            await UpsertPatternAsync(context, projectId, dataSourceId, schema, table, null,
                McpPatternType.CommonQuery, content, representative.Question, exampleSql,
                group.Count(), confidence, threshold, ct);
        }
    }

    private static async Task DetectJoinPatternsAsync(
        SemanticoContext context, int projectId, int dataSourceId,
        List<McpQuerySignal> signals, double threshold, CancellationToken ct)
    {
        var multiTable = signals
            .Where(s => s.IsSuccessful && !string.IsNullOrEmpty(s.TablesUsed))
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
            var representative = group.First().Signal;

            var parts = pair.Split('+');
            var table1Parts = parts[0].Trim().Split('.');
            var schema = table1Parts.Length > 1 ? table1Parts[0] : "public";
            var table = table1Parts.Length > 1 ? table1Parts[1] : table1Parts[0];

            var content = $"Tables {pair} are frequently joined together ({group.Count()} queries)";
            var confidence = Math.Min(1.0, 0.5 + (group.Count() * 0.1));

            await UpsertPatternAsync(context, projectId, dataSourceId, schema, table, null,
                McpPatternType.JoinPattern, content, representative.Question, representative.GeneratedSql,
                group.Count(), confidence, threshold, ct);
        }
    }

    private static async Task DetectDocumentationGapsAsync(
        SemanticoContext context, int projectId, int dataSourceId,
        List<McpQuerySignal> signals, double threshold, CancellationToken ct)
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
                tableStats.Total, confidence, threshold, ct);
        }
    }

    private static async Task UpsertPatternAsync(
        SemanticoContext context, int projectId, int dataSourceId,
        string schema, string table, string? column,
        McpPatternType type, string content, string? exampleQuestion, string? exampleSql,
        int signalCount, double confidence, double autoApproveThreshold, CancellationToken ct)
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

            // Auto-approve if above threshold and currently pending
            if (existing.Status == McpPatternStatus.Pending && confidence >= autoApproveThreshold)
                existing.Status = McpPatternStatus.AutoApproved;
        }
        else
        {
            var status = confidence >= autoApproveThreshold
                ? McpPatternStatus.AutoApproved
                : McpPatternStatus.Pending;

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
                Status = status,
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
}
