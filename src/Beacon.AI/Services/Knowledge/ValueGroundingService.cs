using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Helpers;
using Beacon.Core.RowSecurity;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.Core.Mcp;
using Beacon.Core.Services.Retention;

namespace Beacon.AI.Services.Knowledge;

/// <summary>
/// A ranked candidate column for a single literal: string-typed, not the primary key, drawn from the
/// tables the caller already retrieved for the ask context.
/// </summary>
internal sealed record CandidateColumn(string Schema, string Table, ValueGroundingColumn Column);

// One candidate of a literal, in rank order: resolved from its samples, or the index of its probe.
internal sealed record GroundingSlot(string? SampleHit, int? ProbeIndex);

internal sealed class ValueGroundingService(
    IDbContextFactory<BeaconContext> contextFactory,
    IDataSourceProviderFactory providerFactory,
    ISqlExecutionGate gate,
    IConfiguredDatabaseResolver configuredDatabases,
    IRowSecurityResolver rowSecurityResolver,
    IQueryGuardrailService guardrail,
    IMcpStatementLog statementLog,
    ILogger<ValueGroundingService> logger,
    IProbeConcurrencyLeases? probeLeases = null) : IValueGroundingService
{
    private const int MaxLiterals = 4;
    private const int MinLiteralLength = 2;
    private const int MaxLiteralLength = 64;
    private const int MaxDomainCandidateLength = 100;
    private const int ProbeResultLimit = 3;

    /// <summary>How long one probe may run, counted from when it starts; a probe that times out matches nothing.</summary>
    internal static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(5);

    // At most this many probes of one ask run at once: the one the call runs in, plus the per-key query slots it can take
    // (IProbeConcurrencyLeases). Without free slots the probes run one after another, as one call's statements always did.
    private const int MaxParallelProbes = 3;

    /// <summary>
    /// A safety net over all the probes of one ask: a probe still running then is cut, and one not started matches nothing.
    /// Each probe keeps its own <see cref="DefaultProbeTimeout"/>; the net only bounds a question whose probes all run slow.
    /// </summary>
    internal static readonly TimeSpan DefaultProbeSafetyCap = TimeSpan.FromSeconds(15);

    // Sentence-starting question words are excluded from the capitalised-token rule even when they are
    // capitalised (they are how the question is phrased, not a value in the data) — spec item 5 step 1.
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Show", "List", "How", "What", "Which", "Give", "Find", "Count", "Total", "Average", "Top", "All", "Last", "This", "Next"
    };

    // Same short-string type rule as item 4's complete-value-domain probe (ColumnValueSampler).
    private static readonly string[] StringTypeFragments =
    [
        "char", "varchar", "nvarchar", "text", "character varying", "enum", "name", "bpchar"
    ];

    // Column-name hints ranked ahead of the rest of the candidate columns (spec item 5 step 2).
    private static readonly string[] PreferredColumnNameFragments =
    [
        "status", "type", "state", "code", "country", "category", "name", "kind", "label"
    ];

    // A quote counts as a delimiter only when it is not glued to a letter/digit on the outside, so a
    // possessive/contraction apostrophe ("customer's orders from 'Berlin'") cannot open a bogus span.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly Regex QuotedSpanRegex = new(@"(?<![\p{L}\p{N}])[""']([^""']+)[""'](?![\p{L}\p{N}])", RegexOptions.Compiled, MatchTimeout);
    private static readonly Regex SentenceSplitRegex = new(@"(?<=[.!?])\s+", RegexOptions.Compiled, MatchTimeout);
    private static readonly Regex DateLikeRegex = new(@"^\d{1,4}[-/]\d{1,2}[-/]\d{1,4}$", RegexOptions.Compiled, MatchTimeout);
    private static readonly Regex AllowedCharsetRegex = new(@"^[\p{L}\p{N} .\-_/&']+$", RegexOptions.Compiled, MatchTimeout);

    internal TimeSpan ProbeTimeout { get; init; } = DefaultProbeTimeout;

    internal TimeSpan ProbeSafetyCap { get; init; } = DefaultProbeSafetyCap;

    public async Task<string> BuildValueMatchesBlockAsync(
        int dataSourceId, int projectId, string question, IReadOnlyList<ValueGroundingTable> tables, McpSettingsData settings, CancellationToken ct)
    {
        if (!settings.EnableValueGrounding || tables.Count == 0)
        {
            return "";
        }

        IReadOnlyList<string> literals;
        try
        {
            literals = ExtractLiterals(question);
        }
        catch (RegexMatchTimeoutException)
        {
            logger.LogWarning("Value grounding literal extraction timed out for data source {DataSourceId}; injecting no value-matches block.", dataSourceId);
            return "";
        }

        if (literals.Count == 0)
        {
            return "";
        }

        // A column whose values are never read to describe it (a PII, secret-like or financial-amount name) is neither
        // matched against its samples nor probed.
        var groundable = WithoutExcludedColumns(tables, settings);

        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(ct);

            // Only through the caller's project: a data source it does not link is not found, so nothing is probed.
            var projectSourceIds = context.ProjectDataSources
                .WhereReadable()
                .Where(x => x.ProjectId == projectId)
                .Select(x => x.DataSourceId);
            var dataSource = await context.DataSources
                .Where(x => x.Id == dataSourceId)
                .Where(x => projectSourceIds.Contains(x.Id))
                .FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException($"Data source {dataSourceId} not found.");

            if (dataSource.DataSourceType == DataSourceType.Api)
            {
                return "";
            }

            var dialect = DataSourceSqlDialect.Of(dataSource);
            var configuredDatabase = configuredDatabases.Resolve(dataSource);
            var rowSecurity = await rowSecurityResolver.ResolveAsync(dataSource.Id, ct);
            var provider = providerFactory.GetProvider(dataSource.DataSourceType);

            // Which candidates resolve from their samples and which are probed is decided in order, as before; the probes
            // then run a few at a time within one deadline, and their hits are put back in that order.
            var plan = new List<(string Literal, List<GroundingSlot> Slots)>();
            var probes = new List<(CandidateColumn Candidate, string Literal)>();
            foreach (var literal in literals)
            {
                ct.ThrowIfCancellationRequested();

                var slots = new List<GroundingSlot>();
                var candidates = RankCandidateColumns(groundable, literal);

                foreach (var candidate in candidates)
                {
                    var sampleHit = TryResolveFromSampleValues(candidate, literal);
                    if (sampleHit != null)
                    {
                        // Sample-value hit — resolved WITHOUT a probe, does not count against the cap.
                        slots.Add(new GroundingSlot(sampleHit, ProbeIndex: null));
                        continue;
                    }

                    if (probes.Count >= settings.ValueGroundingMaxProbes)
                    {
                        continue;
                    }

                    slots.Add(new GroundingSlot(SampleHit: null, probes.Count));
                    probes.Add((candidate, literal));
                }

                plan.Add((literal, slots));
            }

            var probeHits = await RunProbesAsync(provider, dataSource, dialect, configuredDatabase, rowSecurity, probes, settings, ct);

            var matchLines = new List<string>();
            foreach (var (literal, slots) in plan)
            {
                var hits = new List<string>();
                foreach (var slot in slots)
                {
                    if (slot.SampleHit != null)
                    {
                        hits.Add(slot.SampleHit);
                    }
                    else if (probeHits[slot.ProbeIndex!.Value] is { } found)
                    {
                        hits.AddRange(found);
                    }
                }

                if (hits.Count > 0)
                {
                    matchLines.Add($"- \"{literal}\" → {string.Join("; ", hits)}");
                }
            }

            return RenderBlock(matchLines);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never log the literal or probe SQL (§1.11) — a count is enough to diagnose.
            logger.LogWarning("Value grounding probe failed for data source {DataSourceId}; injecting no value-matches block ({Failure}).", dataSourceId, FailureCodes.Describe(ex));
            return "";
        }
    }

    /// <summary>
    /// Extracts at most <see cref="MaxLiterals"/> literal-looking tokens from the question (spec item 5
    /// step 1): quoted spans; capitalised tokens/multi-word runs that are not sentence-initial and not in
    /// <see cref="StopWords"/>; tokens mixing letters and digits (e.g. <c>ACME-42</c>, <c>SKU123</c>).
    /// Pure numbers and dates are excluded — they are values, not labels. Each candidate is trimmed and
    /// restricted to <c>[\p{L}\p{N} .\-_/&amp;']</c>; anything else is DROPPED, never escaped (R13). Pure,
    /// internal for direct unit testing.
    /// </summary>
    internal static IReadOnlyList<string> ExtractLiterals(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return [];
        }

        var literals = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string raw)
        {
            if (literals.Count >= MaxLiterals)
            {
                return;
            }

            var trimmed = raw.Trim();
            if (trimmed.Length is < MinLiteralLength or > MaxLiteralLength)
            {
                return;
            }

            if (!AllowedCharsetRegex.IsMatch(trimmed))
            {
                return;
            }

            if (!seen.Add(trimmed))
            {
                return;
            }

            literals.Add(trimmed);
        }

        foreach (Match quoted in QuotedSpanRegex.Matches(question))
        {
            if (literals.Count >= MaxLiterals)
            {
                return literals;
            }

            TryAdd(quoted.Groups[1].Value);
        }

        foreach (var sentence in SentenceSplitRegex.Split(question).Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            if (literals.Count >= MaxLiterals)
            {
                return literals;
            }

            var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var i = 0;
            while (i < words.Length)
            {
                if (literals.Count >= MaxLiterals)
                {
                    return literals;
                }

                var word = TrimPunctuation(words[i]);
                if (word.Length == 0)
                {
                    i++;
                    continue;
                }

                if (IsAlnumMixed(word))
                {
                    TryAdd(word);
                    i++;
                    continue;
                }

                if (IsPureNumberOrDate(word))
                {
                    i++;
                    continue;
                }

                var isSentenceInitial = i == 0;
                if (char.IsUpper(word[0]) && !isSentenceInitial && !StopWords.Contains(word))
                {
                    var runWords = new List<string> { word };
                    var j = i + 1;
                    while (j < words.Length)
                    {
                        var next = TrimPunctuation(words[j]);
                        if (next.Length == 0 || !char.IsUpper(next[0]) || StopWords.Contains(next))
                        {
                            break;
                        }

                        runWords.Add(next);
                        j++;
                    }

                    TryAdd(string.Join(" ", runWords));
                    i = j;
                    continue;
                }

                i++;
            }
        }

        return literals;
    }

    /// <summary>
    /// Sanitises a literal for use inside a <c>LIKE</c> pattern: lower-cased (probes match
    /// case-insensitively via <c>LOWER(column)</c>), <c>%</c>/<c>_</c>/<c>\</c>/<c>[</c> stripped so the
    /// literal cannot inject its own wildcard, then <c>'</c> doubled so it cannot close the string literal.
    /// Pure, internal for direct unit testing.
    /// </summary>
    internal static string SanitizeLiteral(string literal)
    {
        var lowered = literal.ToLowerInvariant();
        var stripped = new string(lowered.Where(x => x is not ('%' or '_' or '\\' or '[')).ToArray());
        return stripped.Replace("'", "''");
    }

    /// <summary>
    /// Builds the bounded <c>LIKE</c> probe for one candidate column. Identifiers are validated against
    /// the §1.10 whitelist (they come from the metadata catalog, never from the question) before being
    /// interpolated; the literal is sanitised via <see cref="SanitizeLiteral"/>. Pure, internal for direct
    /// unit testing.
    /// </summary>
    internal static string BuildProbeSql(DatabaseEngineType? engine, string schema, string table, string column, string literal)
    {
        var validSchema = SqlIdentifierGuard.Validate(schema, "schema");
        var validTable = SqlIdentifierGuard.Validate(table, "table");
        var validColumn = SqlIdentifierGuard.Validate(column, "column");
        var pattern = $"%{SanitizeLiteral(literal)}%";

        if (engine is DatabaseEngineType.MSSQL or DatabaseEngineType.AzureSynapse)
        {
            var bracketSchema = SqlIdentifierGuard.EscapeQuote(validSchema, ']');
            var bracketTable = SqlIdentifierGuard.EscapeQuote(validTable, ']');
            var bracketColumn = SqlIdentifierGuard.EscapeQuote(validColumn, ']');
            return $"SELECT DISTINCT TOP {ProbeResultLimit} [{bracketColumn}] FROM [{bracketSchema}].[{bracketTable}] WHERE LOWER([{bracketColumn}]) LIKE '{pattern}'";
        }

        // MySQL only treats "..." as an identifier under ANSI_QUOTES; default sql_mode reads it as a
        // string literal, so quote with backticks (same rule as ColumnValueSampler's probes).
        var quoteChar = engine == DatabaseEngineType.MySQL ? '`' : '"';
        var quotedSchema = $"{quoteChar}{SqlIdentifierGuard.EscapeQuote(validSchema, quoteChar)}{quoteChar}";
        var quotedTable = $"{quoteChar}{SqlIdentifierGuard.EscapeQuote(validTable, quoteChar)}{quoteChar}";
        var quotedColumn = $"{quoteChar}{SqlIdentifierGuard.EscapeQuote(validColumn, quoteChar)}{quoteChar}";
        return $"SELECT DISTINCT {quotedColumn} FROM {quotedSchema}.{quotedTable} WHERE LOWER({quotedColumn}) LIKE '{pattern}' LIMIT {ProbeResultLimit}";
    }

    /// <summary>
    /// Ranks the candidate string columns (not PK) across all supplied tables for one literal: columns
    /// whose <c>SampleValues</c> already contain the literal case-insensitively first (resolved without a
    /// probe), then columns named like <c>status|type|state|code|country|category|name|kind|label</c>,
    /// then the rest. Internal for direct unit testing.
    /// </summary>
    internal static IReadOnlyList<CandidateColumn> RankCandidateColumns(IReadOnlyList<ValueGroundingTable> tables, string literal)
    {
        var candidates = new List<CandidateColumn>();
        foreach (var table in tables)
        {
            foreach (var column in table.Columns)
            {
                if (column.IsPrimaryKey || !IsStringColumn(column))
                {
                    continue;
                }

                candidates.Add(new CandidateColumn(table.Schema, table.Table, column));
            }
        }

        return candidates
            .OrderByDescending(x => ColumnSampleContainsLiteral(x.Column, literal))
            .ThenByDescending(x => IsPreferredColumnName(x.Column.ColumnName))
            .ToList();
    }

    // The probes of one ask: one at a time in the call's own slot, plus one more for each per-key query slot the call can
    // take now (at most MaxParallelProbes together), held until every probe is done. Each probe has its own ProbeTimeout,
    // counted from its start, as before; ProbeSafetyCap bounds them all, and a probe it cuts, or that has not started by
    // then, matches nothing (logged as one count). The caller's cancellation still cancels.
    private async Task<IReadOnlyList<string>?[]> RunProbesAsync(
        IDataSourceProvider provider, DataSource dataSource, string? dialect, string? configuredDatabase, RowSecurityContext rowSecurity,
        List<(CandidateColumn Candidate, string Literal)> probes, McpSettingsData settings, CancellationToken ct)
    {
        if (probes.Count == 0)
        {
            return [];
        }

        var leases = new List<IDisposable>();
        try
        {
            while (probeLeases != null && leases.Count + 1 < Math.Min(MaxParallelProbes, probes.Count))
            {
                var lease = probeLeases.TryAcquire(settings.MaxConcurrentQueriesPerKey);
                if (lease == null)
                {
                    break;
                }

                leases.Add(lease);
            }

            return await RunProbesInSlotsAsync(provider, dataSource, dialect, configuredDatabase, rowSecurity, probes, settings, 1 + leases.Count, ct);
        }
        finally
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
        }
    }

    private async Task<IReadOnlyList<string>?[]> RunProbesInSlotsAsync(
        IDataSourceProvider provider, DataSource dataSource, string? dialect, string? configuredDatabase, RowSecurityContext rowSecurity,
        List<(CandidateColumn Candidate, string Literal)> probes, McpSettingsData settings, int slotCount, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ProbeSafetyCap);
        using var slots = new SemaphoreSlim(slotCount);
        var cut = 0;

        async Task<IReadOnlyList<string>?> ProbeAsync(CandidateColumn candidate, string literal)
        {
            await slots.WaitAsync(ct);
            try
            {
                IReadOnlyList<string>? hits = null;
                if (!deadline.IsCancellationRequested)
                {
                    try
                    {
                        hits = await ProbeColumnAsync(provider, dataSource, dialect, configuredDatabase, rowSecurity, candidate, literal, settings, deadline.Token, ct);
                    }
                    catch (Exception) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
                    {
                        // A driver may report the cut as an error of its own: still a cut, not a failure of the block.
                    }
                }

                if (hits == null && deadline.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    Interlocked.Increment(ref cut);
                }

                return hits;
            }
            finally
            {
                slots.Release();
            }
        }

        var results = await Task.WhenAll(probes.Select(x => ProbeAsync(x.Candidate, x.Literal)));
        if (cut > 0)
        {
            // §1.11 — counts only, never a literal, column or probe SQL.
            logger.LogWarning(
                "Value-grounding probes for data source {DataSourceId} reached their safety cap of {DeadlineMs} ms: {CutProbes} of {Probes} probe(s) matched nothing",
                dataSource.Id,
                (int)ProbeSafetyCap.TotalMilliseconds,
                cut,
                probes.Count);
        }

        return results;
    }

    private async Task<IReadOnlyList<string>?> ProbeColumnAsync(
        IDataSourceProvider provider, DataSource dataSource, string? dialect, string? configuredDatabase, RowSecurityContext rowSecurity, CandidateColumn candidate,
        string literal, McpSettingsData settings, CancellationToken deadline, CancellationToken ct)
    {
        var sql = BuildProbeSql(dataSource.DatabaseEngineType, candidate.Schema, candidate.Table, candidate.Column.ColumnName, literal);

        // §1.5 — every probe passes the shared execution gate (regex guardrail, read-only AST, the source's own
        // database, the host policy) before it ever reaches the provider, and executes through the read-only variant
        // (a READ ONLY transaction where the engine has one). The probe carries its own LIMIT/TOP, which the gate's row cap
        // holds it to.
        var report = gate.Evaluate(new SqlGateRequest(
            sql,
            dialect,
            EnforceReadOnly: true,
            DetectPii: false,
            CustomPiiPatterns: null,
            Catalog: null,
            BlockOnSchemaFailure: false,
            LintContext: null,
            EnableSemanticLint: false,
            MaxRows: ProbeResultLimit,
            rowSecurity)
        {
            HostManagedKey = dataSource.HostManagedKey,
            ConfiguredDatabase = configuredDatabase
        });
        if (report.Blocked)
        {
            return null;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(deadline);
        timeoutCts.CancelAfter(ProbeTimeout);

        ProviderQueryResult result;
        try
        {
            // The probe reaches the data source with a literal of the question: on the call's audit row as a probe.
            result = await statementLog.RecordAsync(
                dataSource.Id,
                McpStatementPurposes.Probe,
                report.FinalSql,
                () => provider.ExecuteReadOnlyQueryAsync(dataSource, report.FinalSql, new Dictionary<string, object?>(), timeoutCts.Token),
                x => (x.Success ? x.Rows.Count : null, x.Success ? null : x.ErrorMessage ?? McpContentRedactor.ClassExecution));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The probe's own timeout or the ask's safety cap fired, not the caller's cancellation — treat as no match. A
            // cut by the cap is counted by the caller.
            if (deadline.IsCancellationRequested)
            {
                return null;
            }

            // §1.11 — data source id and column name only, never the literal or the probe SQL.
            logger.LogWarning(
                "Value-grounding probe timed out for data source {DataSourceId} (column {Table}.{Column})",
                dataSource.Id,
                candidate.Table,
                candidate.Column.ColumnName);
            return null;
        }

        if (!result.Success || result.Rows.Count == 0)
        {
            return null;
        }

        var values = ExtractColumnValues(result.Rows);
        if (values.Count == 0 || ColumnValueExclusions.ContainsExcludedValue(values, settings.CustomPiiPatterns))
        {
            return null;
        }

        return values
            .Select(x => $"{candidate.Schema}.{candidate.Table}.{candidate.Column.ColumnName} = '{EscapeForRender(x)}'")
            .ToList();
    }

    private IReadOnlyList<ValueGroundingTable> WithoutExcludedColumns(IReadOnlyList<ValueGroundingTable> tables, McpSettingsData settings)
    {
        return tables
            .Select(x => x with
            {
                Columns = x.Columns
                    .Where(y => !ColumnValueExclusions.IsExcludedColumn(guardrail, y.ColumnName, settings.CustomPiiPatterns))
                    .ToList()
            })
            .ToList();
    }

    private static string? TryResolveFromSampleValues(CandidateColumn candidate, string literal)
    {
        var values = DeserializeSampleValues(candidate.Column.SampleValuesJson);
        var match = values?.FirstOrDefault(x => string.Equals(x, literal, StringComparison.OrdinalIgnoreCase));
        return match == null
            ? null
            : $"{candidate.Schema}.{candidate.Table}.{candidate.Column.ColumnName} = '{EscapeForRender(match)}'";
    }

    private static bool ColumnSampleContainsLiteral(ValueGroundingColumn column, string literal)
    {
        var values = DeserializeSampleValues(column.SampleValuesJson);
        return values != null && values.Any(x => string.Equals(x, literal, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPreferredColumnName(string columnName)
    {
        return PreferredColumnNameFragments.Any(x => columnName.Contains(x, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsStringColumn(ValueGroundingColumn column)
    {
        if (column.MaxLength is > MaxDomainCandidateLength)
        {
            return false;
        }

        var normalized = NormalizeDataType(column.DataType);
        return StringTypeFragments.Any(x => normalized.Contains(x, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeDataType(string dataType)
    {
        var parenIndex = dataType.IndexOf('(');
        return (parenIndex > 0 ? dataType[..parenIndex] : dataType).Trim();
    }

    private static IReadOnlyList<string>? DeserializeSampleValues(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ExtractColumnValues(List<Dictionary<string, object?>> rows)
    {
        var values = new List<string>();
        foreach (var row in rows)
        {
            var raw = row.Values.FirstOrDefault();
            var text = raw?.ToString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                values.Add(text);
            }
        }

        return values;
    }

    private static string RenderBlock(IReadOnlyList<string> matchLines)
    {
        if (matchLines.Count == 0)
        {
            return "";
        }

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("## Value matches (from live data — use these exact values in filters)");
        foreach (var line in matchLines)
        {
            sb.AppendLine(line);
        }

        return sb.ToString();
    }

    private static string TrimPunctuation(string word)
    {
        return word.Trim('"', '\'', ',', '.', '!', '?', ';', ':', '(', ')');
    }

    private static bool IsAlnumMixed(string word)
    {
        return word.Any(char.IsLetter) && word.Any(char.IsDigit);
    }

    private static bool IsPureNumberOrDate(string word)
    {
        if (word.All(x => char.IsDigit(x) || x is ',' or '.'))
        {
            return true;
        }

        return DateLikeRegex.IsMatch(word);
    }

    private static string EscapeForRender(string value)
    {
        return value.Replace("'", "''");
    }
}
