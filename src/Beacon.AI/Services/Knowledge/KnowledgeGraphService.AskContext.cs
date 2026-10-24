using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.AI.Services.Embeddings;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Services;
using Beacon.Core.Services.Metadata;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Beacon.AI.Services.Knowledge;

internal sealed partial class KnowledgeGraphService
{
    public async Task<SmartSchemaContext> GetSmartContextForAskAsync(int dataSourceId, int projectId, string question, bool liveValueProbes, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var dataSource = await LinkedDataSourceAsync(context, dataSourceId, projectId, ct);

        var isApi = dataSource.DataSourceType == DataSourceType.Api;

        // Grounding reads the data source under the strictest guardrails of the live projects that link it: value
        // grounding and its probe budget, stored sample values, and the custom patterns of the grounding PII screen.
        var mcpSettings = await sharedSourceSettings.GetEffectiveSettingsAsync(projectId, [dataSourceId], ct);
        var valueGroundingSettings = ValueGroundingSettings(mcpSettings, liveValueProbes);

        // The stored catalog (from the snapshot cache, read only after the link check above); the stored samples are
        // screened under this project's settings on every ask, never cached screened.
        var storedCatalog = await StoredCatalogAsync(context, dataSourceId, projectId, ct);
        var allTables = storedCatalog.Tables
            .Select(x => x with { Columns = WithStoredSamples(x.Columns, mcpSettings) })
            .ToList();

        // Whether a prompt built from this context, or from a table context of the same source, can carry sample values.
        var samplesShown = allTables.Any(x => x.Columns.Any(y => y.SampleValuesJson != null));

        var totalColumns = allTables.Sum(t => t.Columns.Count);
        var catalog = BuildSchemaCatalog(allTables.Select(x => (x.SchemaName, x.TableName, x.Columns.Select(y => y.ColumnName))));
        var primaryKeyCatalog = BuildPrimaryKeyCatalog(allTables.Select(t => (t.SchemaName, t.TableName, (IEnumerable<SchemaColumn>)t.Columns)));
        var dialect = DataSourceSqlDialect.Of(dataSource);

        // Small schema fast path: send everything
        if (allTables.Count <= 40 && totalColumns <= 300)
        {
            var head = new StringBuilder();
            head.AppendLine(isApi
                ? $"# REST API: {dataSource.Name}"
                : $"# Data Source: {dataSource.Name} ({dataSource.DatabaseEngineType})");
            head.AppendLine($"Tables: {allTables.Count}\n");

            var tableBlocks = allTables
                .Select(x => TableBlock(x.SchemaName, x.TableName, x.TableDescription, x.Columns, isApi, fullColumns: true))
                .ToList();

            // Small schemas send every table, but they still need the relationship graph: on a warehouse
            // that declares no foreign keys the column listing carries no join information at all, so
            // inferred relationships are the only thing telling the model how these tables connect.
            var allPaths = await GetJoinPathsForAllTablesAsync(dataSourceId, ct);
            var joinPaths = new StringBuilder();
            SchemaContextFormatter.AppendJoinPaths(joinPaths, allPaths);

            // The grounding blocks are built at once (each on its own context), the masked question embedded once for all.
            var questionVector = new MaskedQuestionVector(embeddingService, question);

            // Item 5: ask-time value grounding — real values found for literal-looking words in the
            // question, injected right before the golden exemplars so the model sees them alongside the
            // schema. Skipped for API sources (no SQL to probe) and gated by EnableValueGrounding.
            var valueMatchesTask = Task.FromResult("");
            if (mcpSettings.EnableValueGrounding && !isApi)
            {
                var valueGroundingTables = allTables
                    .Select(t => new ValueGroundingTable(t.SchemaName, t.TableName, ToValueGroundingColumns(t.Columns)))
                    .ToList();
                valueMatchesTask = BuildValueMatchesBlockAsync(dataSourceId, projectId, question, valueGroundingTables, valueGroundingSettings, ct);
            }

            // Part A: inject human-verified golden query examples ABOVE the mined learned patterns — they are
            // human-verified, so they outrank machine-mined patterns. Empty (behaviour-preserving) when
            // disabled, the embedder is off, or no golden case is near.
            var goldenExemplarsTask = BuildGoldenExemplarBlockAsync(dataSourceId, projectId, question, mcpSettings, dialect, questionVector, ct);

            // Inject learned patterns
            var learnedPatternsTask = Task.FromResult("");
            if (mcpSettings.EnableLearning)
            {
                var allTableNames = allTables.Select(t => $"{t.SchemaName}.{t.TableName}").ToList();
                learnedPatternsTask = LearnedPatternsBlockAsync(dataSourceId, projectId, allTableNames, question, mcpSettings, dialect, questionVector, ct);
            }

            // Tier-3 ⑪: append the business-glossary block after the learned patterns. Empty (nothing
            // injected) when the embedder is off, semantic retrieval is disabled, or no term is near —
            // behaviour-preserving relative to pre-glossary output.
            var glossaryTask = BuildGlossaryBlockAsync(projectId, question, mcpSettings, dialect, questionVector, ct);

            await Task.WhenAll(valueMatchesTask, goldenExemplarsTask, learnedPatternsTask, glossaryTask);
            var valueMatches = await valueMatchesTask;
            var goldenExemplars = await goldenExemplarsTask;
            var learnedPatternsBlock = await learnedPatternsTask;
            var glossary = await glossaryTask;

            // Over the budget the tables the question names come first, then the catalogue order.
            var budgeted = BudgetedContext(
                dataSourceId,
                head.ToString(),
                [new SchemaContextSection("", tableBlocks, HeadingWhenEmpty: false)],
                [
                    new SchemaContextPart(SchemaContextPartKind.JoinPaths, joinPaths.ToString()),
                    new SchemaContextPart(SchemaContextPartKind.ValueMatches, valueMatches),
                    new SchemaContextPart(SchemaContextPartKind.GoldenExemplars, goldenExemplars),
                    new SchemaContextPart(SchemaContextPartKind.LearnedPatterns, learnedPatternsBlock),
                    new SchemaContextPart(SchemaContextPartKind.Glossary, glossary)
                ],
                AskSchemaContextBudget.TablesNamedIn(question, tableBlocks));

            return new SmartSchemaContext
            {
                FullContext = budgeted.Text,
                TablesShown = budgeted.TablesShown,
                TablesNotShown = budgeted.TablesNotShown,
                UsedSmartRetrieval = false,
                TotalTableCount = allTables.Count,
                DatabaseDialect = dialect,
                HostManagedKey = dataSource.HostManagedKey,
                ConfiguredDatabase = configuredDatabases.Resolve(dataSource),
                SchemaCatalog = catalog,
                PrimaryKeyCatalog = primaryKeyCatalog,
                JoinPaths = allPaths,
                RowDataCategories = RowDataCategoriesOf(samplesShown, valueMatches)
            };
        }

        // Smart path: search for relevant tables
        var searchResults = await SearchAsync(question, projectId, dataSourceId, maxResults: 15, ct);

        var matchedTableNames = searchResults
            .Where(r => r.Type is "table" or "column")
            .Select(r => $"{r.SchemaName}.{r.TableName}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // In retrieval rank order, named as the context names them: the budget keeps these first.
        var rankedTableNames = searchResults
            .Where(x => x.Type is "table" or "column")
            .Select(x => AskSchemaContextBudget.BlockName(x.SchemaName, x.TableName, isApi))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Expand through the registered relationship graph: join paths between matched tables, the
        // intermediate tables those paths need (including link tables), then direct neighbours. Falls
        // back to the pre-graph one-hop expansion if the graph cannot be built.
        var tableShapes = allTables
            .Select(x => (x.SchemaName, x.TableName, Columns: (IReadOnlyList<SchemaColumn>)x.Columns))
            .ToList();

        var expansion = await ExpandRelatedTablesAsync(dataSourceId, matchedTableNames, tableShapes, ct);
        var detailedTables = new HashSet<string>(expansion.DetailedTables, StringComparer.OrdinalIgnoreCase);
        detailedTables.UnionWith(matchedTableNames);

        // Build two-section context
        var smartHead = new StringBuilder();
        smartHead.AppendLine(isApi
            ? $"# REST API: {dataSource.Name}"
            : $"# Data Source: {dataSource.Name} ({dataSource.DatabaseEngineType})");
        smartHead.AppendLine($"Total tables: {allTables.Count}\n");

        // Section 1: Relevant tables with full schema
        var relevantHeading = new StringBuilder();
        relevantHeading.AppendLine("## Relevant Tables (full schema)\n");
        var relevantBlocks = allTables
            .Where(x => detailedTables.Contains($"{x.SchemaName}.{x.TableName}"))
            .Select(x => TableBlock(x.SchemaName, x.TableName, x.TableDescription, x.Columns, isApi, fullColumns: true))
            .ToList();

        // Section 2: Other tables compact
        var otherHeading = new StringBuilder();
        otherHeading.AppendLine("\n## Other Tables (name and primary keys only)\n");
        var otherBlocks = allTables
            .Where(x => !detailedTables.Contains($"{x.SchemaName}.{x.TableName}"))
            .Select(x => TableBlock(x.SchemaName, x.TableName, x.TableDescription, x.Columns, isApi, fullColumns: false))
            .ToList();

        // Explicit join chains for the retrieved tables, verified and inferred kept apart so an inferred
        // join is never presented as fact.
        var smartJoinPaths = new StringBuilder();
        SchemaContextFormatter.AppendJoinPaths(smartJoinPaths, expansion.JoinPaths);
        var coverage = new StringBuilder();
        SchemaContextFormatter.AppendCoverage(coverage, expansion.Capped, expansion.OmittedTableCount, detailedTables.Count);

        // Item 5: ask-time value grounding, scoped to the detailed tables only (same set rendered with
        // full columns above) — same gating as the fast path.
        // Built at once, the masked question embedded once for all (as on the fast path).
        var smartQuestionVector = new MaskedQuestionVector(embeddingService, question);
        var smartValueMatchesTask = Task.FromResult("");
        if (mcpSettings.EnableValueGrounding && !isApi)
        {
            var valueGroundingTables = allTables
                .Where(t => detailedTables.Contains($"{t.SchemaName}.{t.TableName}"))
                .Select(t => new ValueGroundingTable(t.SchemaName, t.TableName, ToValueGroundingColumns(t.Columns)))
                .ToList();
            smartValueMatchesTask = BuildValueMatchesBlockAsync(dataSourceId, projectId, question, valueGroundingTables, valueGroundingSettings, ct);
        }

        // Part A: inject human-verified golden query examples ABOVE the mined learned patterns (same
        // behaviour-preserving fallback as the fast path — empty string when nothing to inject).
        var smartGoldenExemplarsTask = BuildGoldenExemplarBlockAsync(dataSourceId, projectId, question, mcpSettings, dialect, smartQuestionVector, ct);

        // Inject learned patterns for relevant tables
        var smartPatternsTask = Task.FromResult("");
        if (mcpSettings.EnableLearning)
        {
            var relevantTableNames = detailedTables.ToList();
            smartPatternsTask = LearnedPatternsBlockAsync(dataSourceId, projectId, relevantTableNames, question, mcpSettings, dialect, smartQuestionVector, ct);
        }

        // Tier-3 ⑪: append the business-glossary block after the learned patterns (same behaviour-preserving
        // fallback as the fast path — empty string when nothing to inject).
        var smartGlossaryTask = BuildGlossaryBlockAsync(projectId, question, mcpSettings, dialect, smartQuestionVector, ct);

        await Task.WhenAll(smartValueMatchesTask, smartGoldenExemplarsTask, smartPatternsTask, smartGlossaryTask);
        var smartValueMatches = await smartValueMatchesTask;
        var smartGoldenExemplars = await smartGoldenExemplarsTask;
        var smartPatternsBlock = await smartPatternsTask;
        var smartGlossary = await smartGlossaryTask;

        // Over the budget the tables the question names come first, then the tables retrieval ranked for the question, then
        // the relevant tables and the other tables in catalogue order.
        IReadOnlyList<SchemaContextTableBlock> allBlocks = [.. relevantBlocks, .. otherBlocks];
        var smartBudgeted = BudgetedContext(
            dataSourceId,
            smartHead.ToString(),
            [
                new SchemaContextSection(relevantHeading.ToString(), relevantBlocks, HeadingWhenEmpty: true),
                new SchemaContextSection(otherHeading.ToString(), otherBlocks, HeadingWhenEmpty: false)
            ],
            [
                new SchemaContextPart(SchemaContextPartKind.JoinPaths, smartJoinPaths.ToString()),
                new SchemaContextPart(SchemaContextPartKind.Coverage, coverage.ToString()),
                new SchemaContextPart(SchemaContextPartKind.ValueMatches, smartValueMatches),
                new SchemaContextPart(SchemaContextPartKind.GoldenExemplars, smartGoldenExemplars),
                new SchemaContextPart(SchemaContextPartKind.LearnedPatterns, smartPatternsBlock),
                new SchemaContextPart(SchemaContextPartKind.Glossary, smartGlossary)
            ],
            [.. AskSchemaContextBudget.TablesNamedIn(question, allBlocks), .. rankedTableNames]);

        return new SmartSchemaContext
        {
            FullContext = smartBudgeted.Text,
            TablesShown = smartBudgeted.TablesShown,
            TablesNotShown = smartBudgeted.TablesNotShown,
            UsedSmartRetrieval = true,
            RelevantTables = [.. detailedTables],
            TotalTableCount = allTables.Count,
            DatabaseDialect = dialect,
            HostManagedKey = dataSource.HostManagedKey,
            ConfiguredDatabase = configuredDatabases.Resolve(dataSource),
            SchemaCatalog = catalog,
            PrimaryKeyCatalog = primaryKeyCatalog,
            Capped = expansion.Capped,
            OmittedTableCount = expansion.OmittedTableCount,
            JoinPaths = expansion.JoinPaths,
            RowDataCategories = RowDataCategoriesOf(samplesShown, smartValueMatches)
        };
    }

    // The ask context within Beacon:Mcp:Ceilings:AskSchemaContextChars. A context that had to leave out grounding sections, or
    // that is over the budget with one table, is logged as a warning (counts only); one that left out only tables as
    // information.
    private BudgetedSchemaContext BudgetedContext(
        int dataSourceId,
        string head,
        IReadOnlyList<SchemaContextSection> sections,
        IReadOnlyList<SchemaContextPart> parts,
        IReadOnlyList<string> rankedNames)
    {
        var budgetChars = (deploymentOptions?.Value.Ceilings ?? new McpCeilingOptions()).EffectiveAskSchemaContextChars;
        var budgeted = AskSchemaContextBudget.Assemble(head, sections, parts, rankedNames, budgetChars);
        if (budgeted.PartsLeftOut.Count > 0 || budgeted.TopTableCut)
        {
            logger.LogWarning(
                "Ask schema context for data source {DataSourceId} does not fit its budget of {BudgetChars} characters: "
                    + "{GroundingSectionsLeftOut} grounding section(s) left out, {TopTableCut} top table cut, {TablesShown} table(s) shown, {TablesNotShown} left out, {ContextChars} characters sent.",
                dataSourceId,
                budgetChars,
                budgeted.PartsLeftOut.Count,
                budgeted.TopTableCut,
                budgeted.TablesShown,
                budgeted.TablesNotShown.Count,
                budgeted.Text.Length);
        }
        else if (budgeted.TablesNotShown.Count > 0)
        {
            logger.LogInformation(
                "Ask schema context for data source {DataSourceId} is over its budget of {BudgetChars} characters: {TablesShown} table(s) shown, {TablesNotShown} left out.",
                dataSourceId,
                budgetChars,
                budgeted.TablesShown,
                budgeted.TablesNotShown.Count);
        }

        return budgeted;
    }

    // One table rendered as the ask context shows it: in full, or compact (name, primary key and column names).
    private static SchemaContextTableBlock TableBlock(
        string schemaName, string tableName, string? description, IReadOnlyList<SchemaColumn> columns, bool isApi, bool fullColumns)
    {
        var sb = new StringBuilder();
        if (fullColumns)
        {
            SchemaContextFormatter.AppendTableWithFullColumns(sb, schemaName, tableName, description, columns, isApi);
        }
        else
        {
            SchemaContextFormatter.AppendTableCompact(sb, schemaName, tableName, description, columns, isApi);
        }

        return new SchemaContextTableBlock(AskSchemaContextBudget.BlockName(schemaName, tableName, isApi), schemaName, tableName, sb.ToString());
    }

    public async Task<Dictionary<string, HashSet<string>>> GetSchemaCatalogAsync(int dataSourceId, int projectId, CancellationToken ct = default)
    {
        // Same catalog GetSmartContextForAskAsync computes (shared BuildSchemaCatalog), from the same stored-catalog
        // snapshot — none of the LLM-context assembly (search, join paths, exemplars). Empty when the caller's project
        // does not link the data source (§1.12); the link is checked live, before the snapshot is read.
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var linked = await ProjectSourceIds(context, projectId)
            .Where(x => x == dataSourceId)
            .AnyAsync(ct);
        if (!linked)
        {
            return BuildSchemaCatalog([]);
        }

        var storedCatalog = await StoredCatalogAsync(context, dataSourceId, projectId, ct);

        return BuildSchemaCatalog(storedCatalog.Tables.Select(x => (x.SchemaName, x.TableName, x.Columns.Select(y => y.ColumnName))));
    }

    public async Task<IReadOnlyList<DocChunkHit>> GetRelevantDocChunksAsync(int projectId, string question, int topK, CancellationToken ct = default)
    {
        // No embedder / semantic retrieval disabled → no dense retrieval; the caller falls back to
        // char-truncated documentation. (Gated on the setting too, matching the glossary path, so an
        // operator disabling semantic retrieval stops chunk serving consistently.)
        if (!embeddingService.IsAvailable)
        {
            return [];
        }

        var settings = await settingsProvider.GetEffectiveSettingsAsync(projectId, ct);
        if (!settings.EnableSemanticRetrieval)
        {
            return [];
        }

        // Fail CLOSED like the metadata dense arm (SearchAsync) and the semantic exemplar path
        // (GetRelevantPatternsAsync): a transient embedding/vector-store error must NOT fail the whole
        // knowledge answer (this runs inside KnowledgeAnswerService's Task.WhenAll). Rethrow OCE so a
        // shutdown/timeout unwinds; otherwise log and return [] so the caller falls back to char-truncated
        // documentation, exactly as the documented no-hits path does.
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(ct);

            var limit = Math.Max(1, topK);

            // Embed the RAW question — doc chunks are prose embedded RAW at index time (blurb + chunk, no
            // masking). Masking strips literals/numbers, which is right for SQL-exemplar matching but would
            // put the query vector in a different representation region than the raw chunk vectors and hurt
            // recall. Symmetry with index-time embedding is what matters here (Stage-1 finding, 2026-07-13).
            var queryVector = await embeddingService.EmbedAsync(question, ct);

            // Same provider branch as the metadata dense arm, but scoped by project_id (not data_source_id)
            // and filtered to the DocChunk owner type.
            var hits = context.Database.ProviderName == NpgsqlProviderName
                ? await GetNearestPostgresAsync(context, queryVector, dataSourceIds: [], DocChunkOwnerTypes, limit, ct, projectId: projectId)
                : await GetNearestInMemoryAsync(context, queryVector, dataSourceIds: [], DocChunkOwnerTypes, limit, ct, projectId: projectId);

            if (hits.Count == 0)
            {
                return [];
            }

            // DocChunk OwnerId == McpDocChunk.Id. Load the hit chunks' text/blurb, then re-project in the
            // vector-similarity order the NN helper returned.
            var ownerIds = hits
                .Select(x => x.OwnerId)
                .ToList();

            var chunks = await context.McpDocChunks
                .Where(x => x.ProjectId == projectId)
                .Where(x => ownerIds.Contains(x.Id))
                .Select(x =>
                    new
                    {
                        x.Id,
                        x.ChunkText,
                        x.ContextualBlurb,
                        x.SourceSectionId
                    })
                .ToListAsync(ct);

            // Each chunk of generated documentation carries its section's type, which says whether its text can list
            // values discovered in the data; an imported document's chunk has no section.
            var sectionIds = chunks
                .Select(x => x.SourceSectionId)
                .Where(x => x != 0)
                .Distinct()
                .ToList();
            var sectionTypes = sectionIds.Count == 0
                ? new Dictionary<int, ProjectDocSectionType>()
                : await context.ProjectDocumentationSections
                    .Where(x => sectionIds.Contains(x.Id))
                    .Select(x =>
                        new
                        {
                            x.Id,
                            x.SectionType
                        })
                    .ToDictionaryAsync(x => x.Id, x => x.SectionType, ct);

            var chunksById = chunks.ToDictionary(x => x.Id);

            var results = new List<DocChunkHit>();
            foreach (var hit in hits)
            {
                if (chunksById.TryGetValue(hit.OwnerId, out var chunk))
                {
                    results.Add(new DocChunkHit(chunk.ChunkText, chunk.ContextualBlurb)
                    {
                        SectionType = sectionTypes.TryGetValue(chunk.SourceSectionId, out var sectionType) ? sectionType : null
                    });
                }
            }

            return results;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Dense doc-chunk retrieval failed for project {ProjectId}; falling back to char-truncated documentation ({Failure}).", projectId, FailureCodes.Describe(ex));
            return [];
        }
    }

    /// <summary>
    /// Fail-closed wrapper around <see cref="IValueGroundingService.BuildValueMatchesBlockAsync"/>, mirroring
    /// <see cref="BuildGlossaryBlockAsync"/>: a transient probe/embedding/provider error must NOT fail the
    /// whole `ask` (this is called from <see cref="GetSmartContextForAskAsync"/>). Rethrows
    /// <see cref="OperationCanceledException"/> so a shutdown/timeout unwinds; any other exception is logged
    /// (count only — never the literal or probe SQL, §1.11) and yields "" (2026-07-13 lesson). Internal so it
    /// can be unit-tested directly.
    /// </summary>
    internal async Task<string> BuildValueMatchesBlockAsync(
        int dataSourceId, int projectId, string question, IReadOnlyList<ValueGroundingTable> tables, McpSettingsData settings, CancellationToken ct)
    {
        try
        {
            return await valueGroundingService.BuildValueMatchesBlockAsync(dataSourceId, projectId, question, tables, settings, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Value grounding failed for data source {DataSourceId}; injecting no value-matches block ({Failure}).", dataSourceId, FailureCodes.Describe(ex));
            return "";
        }
    }

    /// <summary>
    /// Projects the internal <see cref="SchemaColumn"/> shape into the public <see cref="ValueGroundingColumn"/>
    /// shape <see cref="IValueGroundingService"/> takes — kept internal-type-free so the interface can stay
    /// public (Moq's proxy generation for a mocked seam needs a public or Castle-visible internal interface).
    /// </summary>
    private static IReadOnlyList<ValueGroundingColumn> ToValueGroundingColumns(IEnumerable<SchemaColumn> columns)
    {
        return columns
            .Select(x => new ValueGroundingColumn(x.ColumnName, x.DataType, x.IsPrimaryKey, x.MaxLength, x.SampleValuesJson))
            .ToList();
    }

    /// <summary>
    /// Builds the "## Business Glossary" block injected into the smart context (Tier-3 ⑪): the top-K glossary
    /// terms nearest to the masked question, each rendered as term → definition → target column/metric.
    /// This is now the mechanism that supersedes the dead <c>McpPatternType.BusinessTermMapping</c> learned-
    /// pattern path (that enum value is left in place but no longer produced). Glossary terms are
    /// project-scoped, and the project is the caller's AUTHORIZED <paramref name="projectId"/> — passed
    /// explicitly, never derived from the data source, because a data source shared by several projects
    /// must never leak one project's terms into another's ask (codex PR-11 R4 fix). Returns "" — injecting
    /// nothing, behaviour-preserving — when the embedder is unavailable, semantic retrieval is disabled,
    /// or no active term is near. Internal so it can be unit-tested directly without standing up the full
    /// <see cref="GetSmartContextForAskAsync"/> schema path.
    /// </summary>
    internal Task<string> BuildGlossaryBlockAsync(int projectId, string question, McpSettingsData settings, CancellationToken ct)
    {
        return BuildGlossaryBlockAsync(projectId, question, settings, dialect: null, ct);
    }

    // A term's metric expression is SQL the model copies into its answer: it is rendered without its comments, read in the
    // dialect of the data source being asked (<paramref name="dialect"/>; without one, an expression holding a comment marker
    // is left out). An expression that cannot be shown that way is left out — the term keeps its definition — and logged by
    // term id, once per interval.
    internal Task<string> BuildGlossaryBlockAsync(int projectId, string question, McpSettingsData settings, string? dialect, CancellationToken ct)
    {
        return BuildGlossaryBlockAsync(projectId, question, settings, dialect, new MaskedQuestionVector(embeddingService, question), ct);
    }

    private async Task<string> BuildGlossaryBlockAsync(
        int projectId, string question, McpSettingsData settings, string? dialect, MaskedQuestionVector questionVector, CancellationToken ct)
    {
        if (!embeddingService.IsAvailable || !settings.EnableSemanticRetrieval)
        {
            return "";
        }

        // Fail CLOSED like the sibling retrieval paths: a transient embedding/vector-store error must NOT
        // fail the whole `ask` (this is called from GetSmartContextForAskAsync). Rethrow OCE so a
        // shutdown/timeout unwinds; otherwise log and return "" so no glossary block is injected — the same
        // behaviour-preserving fallback as the unavailable-embedder / no-hits paths.
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(ct);

            var limit = Math.Max(1, settings.GlossaryTopK);

            // Mask the question exactly as the terms were embedded at index time (Mask(term + synonyms)) so
            // similarity keys on structure, not literals.
            var maskedVector = await questionVector.GetAsync(ct);

            // Same provider branch as the doc-chunk path, scoped by project_id and filtered to GlossaryTerm.
            var hits = context.Database.ProviderName == NpgsqlProviderName
                ? await GetNearestPostgresAsync(context, maskedVector, dataSourceIds: [], GlossaryOwnerTypes, limit, ct, projectId: projectId)
                : await GetNearestInMemoryAsync(context, maskedVector, dataSourceIds: [], GlossaryOwnerTypes, limit, ct, projectId: projectId);

            if (hits.Count == 0)
            {
                return "";
            }

            // GlossaryTerm OwnerId == McpGlossaryTerm.Id. Load the hit terms (ACTIVE only — a stale vector for a
            // deactivated term maps to no active row and is skipped), then re-project in similarity order.
            var ownerIds = hits
                .Select(x => x.OwnerId)
                .ToList();

            var terms = await context.McpGlossaryTerms
                .Where(x => x.ProjectId == projectId)
                .Where(x => x.IsActive)
                .Where(x => ownerIds.Contains(x.Id))
                .Select(x =>
                    new GlossaryHit
                    {
                        Id = x.Id,
                        Term = x.Term,
                        Definition = x.Definition,
                        TargetSchema = x.TargetSchema,
                        TargetTable = x.TargetTable,
                        TargetColumn = x.TargetColumn,
                        MetricExpression = x.MetricExpression
                    })
                .ToListAsync(ct);

            var termsById = terms.ToDictionary(x => x.Id);

            var sb = new StringBuilder();
            var leftOut = new List<int>();
            foreach (var hit in hits)
            {
                if (!termsById.TryGetValue(hit.OwnerId, out var term))
                {
                    continue;
                }

                if (sb.Length == 0)
                {
                    sb.AppendLine("\n## Business Glossary\n");
                }

                var metricExpression = string.IsNullOrWhiteSpace(term.MetricExpression)
                    ? null
                    : GroundingSqlText.WithoutComments(term.MetricExpression, dialect);
                if (!string.IsNullOrWhiteSpace(term.MetricExpression) && string.IsNullOrEmpty(metricExpression))
                {
                    leftOut.Add(term.Id);
                }

                sb.Append($"- **{term.Term}**: {term.Definition}");
                var target = FormatGlossaryTarget(term, metricExpression);
                if (target != null)
                {
                    sb.Append($" (→ {target})");
                }

                sb.AppendLine();
            }

            var withheldTerms = DueToLog("glossary-metric", leftOut);
            if (withheldTerms.Count > 0)
            {
                logger.LogWarning(
                    "{Count} glossary metric expression(s) of project {ProjectId} were left out of the prompt: they hold a comment that cannot be removed or characters that do not show (terms {TermIds})",
                    withheldTerms.Count,
                    projectId,
                    string.Join(",", withheldTerms));
            }

            return sb.ToString();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Glossary retrieval failed for project {ProjectId}; injecting no glossary block ({Failure}).", projectId, FailureCodes.Describe(ex));
            return "";
        }
    }

    /// <summary>
    /// Builds the "## Verified query examples (authoritative)" block injected into the ask context ABOVE the
    /// mined learned-pattern block (§ Part A): the top-K human-verified golden eval cases nearest to the masked
    /// question, each rendered as its question + gold SQL. Because they are human-verified they outrank the
    /// machine-mined patterns. Returns "" — injecting nothing, behaviour-preserving — when golden exemplars are
    /// disabled, the embedder is unavailable, semantic retrieval is off, or no active golden case is near. Fails
    /// CLOSED (log + "") on transient retrieval errors, mirroring <see cref="BuildGlossaryBlockAsync"/>. Golden
    /// cases key their EMBEDDINGS on dataSourceId, but <see cref="McpEvalCase"/> carries a ProjectId and the
    /// caller's AUTHORIZED <paramref name="projectId"/> is a hard filter on the loaded cases — a data source
    /// shared by several projects must never leak one project's gold SQL into another's ask (codex PR-11 R4
    /// fix). Gold SQL is rendered without its comments, read in the data source's <paramref name="dialect"/>
    /// (<see cref="GroundingSqlText"/>), so cases stored before Beacon checked grounding SQL show none either; the question
    /// is rendered on one line (<see cref="GroundingText.Question"/>), so it cannot leave its <c>-- Q:</c> comment line. A
    /// case whose SQL or question cannot be shown that way is left out, and logged by id once per interval. Internal for
    /// unit tests.
    /// </summary>
    internal Task<string> BuildGoldenExemplarBlockAsync(int dataSourceId, int projectId, string question, McpSettingsData settings, string? dialect, CancellationToken ct)
    {
        return BuildGoldenExemplarBlockAsync(dataSourceId, projectId, question, settings, dialect, new MaskedQuestionVector(embeddingService, question), ct);
    }

    private async Task<string> BuildGoldenExemplarBlockAsync(
        int dataSourceId, int projectId, string question, McpSettingsData settings, string? dialect, MaskedQuestionVector questionVector, CancellationToken ct)
    {
        if (!settings.EnableGoldenExemplars || !embeddingService.IsAvailable || !settings.EnableSemanticRetrieval)
        {
            return "";
        }

        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(ct);

            var limit = Math.Max(1, settings.GoldenExemplarTopK);

            // Mask the question exactly as golden cases were embedded at index time so similarity keys on
            // structure, not literals.
            var maskedVector = await questionVector.GetAsync(ct);

            // Data-source-scoped (projectId omitted) — same NN helpers, GoldenCase owner type.
            var hits = context.Database.ProviderName == NpgsqlProviderName
                ? await GetNearestPostgresAsync(context, maskedVector, [dataSourceId], GoldenCaseOwnerTypes, limit, ct)
                : await GetNearestInMemoryAsync(context, maskedVector, [dataSourceId], GoldenCaseOwnerTypes, limit, ct);

            if (hits.Count == 0)
            {
                return "";
            }

            // GoldenCase OwnerId == McpEvalCase.Id. Load the hit cases (ACTIVE only — a stale vector for a
            // deactivated case maps to no active row and is skipped; and ONLY the caller's project — a hit
            // from another project sharing this data source maps to no row and is skipped), then re-project
            // in similarity order.
            var ownerIds = hits
                .Select(x => x.OwnerId)
                .ToList();

            var cases = await context.McpEvalCases
                .Where(x => x.DataSourceId == dataSourceId)
                .Where(x => x.ProjectId == projectId)
                .Where(x => x.IsActive)
                .Where(x => ownerIds.Contains(x.Id))
                .Select(x =>
                    new GoldenCaseHit
                    {
                        Id = x.Id,
                        Question = x.Question,
                        GoldSql = x.GoldSql
                    })
                .ToListAsync(ct);

            var casesById = cases.ToDictionary(x => x.Id);
            var budget = Math.Max(0, settings.GoldenExemplarBudgetChars);

            var sb = new StringBuilder();
            var leftOut = new List<int>();
            foreach (var hit in hits)
            {
                if (!casesById.TryGetValue(hit.OwnerId, out var golden))
                {
                    continue;
                }

                var goldSql = GroundingSqlText.WithoutComments(golden.GoldSql, dialect);
                var goldQuestion = GroundingText.Question(golden.Question);
                if (string.IsNullOrEmpty(goldSql) || goldQuestion == null)
                {
                    leftOut.Add(golden.Id);
                    continue;
                }

                var entry = $"-- Q: {goldQuestion}\n{goldSql}\n\n";

                // Budget guard: stop before exceeding the char budget so a large gold SQL can't blow the
                // context. The first fitting entry is always emitted (better one verified example than none),
                // so the guard only trims later entries.
                if (budget > 0 && sb.Length > 0 && sb.Length + entry.Length > budget)
                {
                    break;
                }

                if (sb.Length == 0)
                {
                    sb.AppendLine("\n## Verified query examples (authoritative)\n");
                }

                sb.Append(entry);
            }

            var withheldCases = DueToLog("golden-case", leftOut);
            if (withheldCases.Count > 0)
            {
                logger.LogWarning(
                    "{Count} golden case(s) of data source {DataSourceId} were left out of the prompt: their gold SQL holds a comment that cannot be removed, or their SQL or question holds characters that do not show (cases {CaseIds})",
                    withheldCases.Count,
                    dataSourceId,
                    string.Join(",", withheldCases));
            }

            return sb.ToString();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Golden-exemplar retrieval failed for data source {DataSourceId}; injecting no verified-examples block ({Failure}).", dataSourceId, FailureCodes.Describe(ex));
            return "";
        }
    }

    public async Task<string> GetTablesContextAsync(int dataSourceId, int projectId, IEnumerable<string> tableNames, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var dataSource = await LinkedDataSourceAsync(context, dataSourceId, projectId, ct);
        var mcpSettings = await sharedSourceSettings.GetEffectiveSettingsAsync(projectId, [dataSourceId], ct);

        var isApi = dataSource.DataSourceType == DataSourceType.Api;
        var nameList = tableNames
            .Select(x => x.ToLower())
            .Distinct()
            .ToList();

        // Match by table name or schema.table, case-insensitive, filtered in SQL so only the
        // requested tables (not the whole catalog) are materialized.
        var matched = await context.DatabaseMetadata
            .Where(m => m.DataSourceId == dataSourceId)
            .Where(m => nameList.Contains(m.TableName.ToLower())
                || nameList.Contains((m.SchemaName + "." + m.TableName).ToLower()))
            .Select(m => new
            {
                m.SchemaName,
                m.TableName,
                m.TableDescription,
                Columns = m.Columns.Select(c => new SchemaColumn(
                    c.ColumnName, c.DataType, c.IsPrimaryKey, c.IsNullable,
                    c.ForeignKeyTable, c.ForeignKeyColumn, c.Description,
                    c.MaxLength, c.SampleValues, c.ForeignKeySchema, c.SampleValuesComplete
                )).ToList()
            })
            .ToListAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine("# Table Schemas\n");
        foreach (var t in matched)
            SchemaContextFormatter.AppendTableWithFullColumns(sb, t.SchemaName, t.TableName, t.TableDescription, WithStoredSamples(t.Columns, mcpSettings), isApi);

        return sb.ToString();
    }

    // A caller without Query in the project gets the stored sample values only: live probes would let it read column
    // contents it may not query (no probe budget means none run).
    private static McpSettingsData ValueGroundingSettings(McpSettingsData settings, bool liveValueProbes)
    {
        if (liveValueProbes)
        {
            return settings;
        }

        var sampleOnly = settings.Clone();
        sampleOnly.ValueGroundingMaxProbes = 0;

        return sampleOnly;
    }

    // The row-derived data an ask context carries, for the egress log (LlmRowDataEgress).
    private static List<string> RowDataCategoriesOf(bool samplesShown, string valueMatches)
    {
        var categories = new List<string>();
        if (samplesShown)
        {
            categories.Add(LlmRowDataCategories.SampleValues);
        }

        if (!string.IsNullOrEmpty(valueMatches))
        {
            categories.Add(LlmRowDataCategories.ValueMatches);
        }

        return categories;
    }

    // Stored sample values are served only while sample-value collection is on for every live project that links the
    // data source, and only those that pass the PII screen of those projects as it is now (StoredSampleScreen): samples
    // stored before a stricter project linked the source, or before its patterns changed, are screened again here.
    private List<SchemaColumn> WithStoredSamples(IEnumerable<SchemaColumn> columns, McpSettingsData settings)
    {
        return columns
            .Select(x => WithStoredSamples(x, settings))
            .ToList();
    }

    private SchemaColumn WithStoredSamples(SchemaColumn column, McpSettingsData settings)
    {
        var stored = SchemaContextFormatter.DeserializeSampleValues(column.SampleValuesJson);
        var shown = StoredSampleScreen.Screen(guardrail, column.ColumnName, stored, settings);
        if (shown?.Count == stored?.Count)
        {
            return column;
        }

        return column with
        {
            SampleValuesJson = shown == null ? null : JsonSerializer.Serialize(shown),
            SampleValuesComplete = false
        };
    }

    private static string? TruncateContent(string? content, int maxLength)
    {
        if (content == null) return null;
        return content.Length <= maxLength ? content : content[..maxLength] + "...";
    }

    private static Dictionary<string, HashSet<string>> BuildSchemaCatalog(
        IEnumerable<(string SchemaName, string TableName, IEnumerable<string> ColumnNames)> tables)
    {
        var catalog = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (schema, table, columnNames) in tables)
        {
            var cols = columnNames.Select(x => x.ToLowerInvariant()).ToHashSet();
            catalog[table.ToLowerInvariant()] = cols;
            catalog[$"{schema}.{table}".ToLowerInvariant()] = cols;
        }
        return catalog;
    }

    // Feeds SqlSemanticLinter's FANOUT_AGGREGATE check: whether a join column is the FULL primary key
    // of the table it targets (a composite PK joined on one column is never "full", so it still fans out).
    private static Dictionary<string, IReadOnlySet<string>> BuildPrimaryKeyCatalog(
        IEnumerable<(string SchemaName, string TableName, IEnumerable<SchemaColumn> Columns)> tables)
    {
        var catalog = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (schema, table, columns) in tables)
        {
            var primaryKeyColumns = (IReadOnlySet<string>)columns
                .Where(c => c.IsPrimaryKey)
                .Select(c => c.ColumnName.ToLowerInvariant())
                .ToHashSet();
            catalog[table.ToLowerInvariant()] = primaryKeyColumns;
            catalog[$"{schema}.{table}".ToLowerInvariant()] = primaryKeyColumns;
        }
        return catalog;
    }

    private static string? ExtractTableDoc(string dataModelContent, string tableName)
    {
        // Try to find the section about this specific table in the DataModel content
        var marker = tableName;
        var idx = dataModelContent.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        // Extract a chunk of text around the table name (up to 500 chars after)
        var start = idx;
        var end = Math.Min(dataModelContent.Length, idx + 500);
        // Try to end at a paragraph boundary
        var nextBlank = dataModelContent.IndexOf("\n\n", start + marker.Length, StringComparison.Ordinal);
        if (nextBlank > 0 && nextBlank < end) end = nextBlank;

        return TruncateContent(dataModelContent[start..end], 400);
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "are", "was", "were", "how", "many", "much", "what", "which",
        "who", "whom", "with", "from", "into", "that", "this", "these", "those", "have",
        "has", "had", "did", "does", "all", "any", "each", "per", "show", "list", "give",
        "get", "find", "count", "last", "week", "month", "year", "day", "between", "over"
    };

    private static List<string> TokenizeQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        return System.Text.RegularExpressions.Regex
            .Split(query, "[^A-Za-z0-9]+", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1))
            .Where(x => x.Length > 2)
            .Select(x => x.ToLowerInvariant())
            .Where(x => !StopWords.Contains(x))
            .Distinct()
            .ToList();
    }

    private static int CountOverlap(List<string> terms, string name, string? description)
    {
        var nameLower = name.ToLowerInvariant();
        var descLower = description?.ToLowerInvariant();
        var count = 0;
        foreach (var term in terms)
        {
            if (nameLower.Contains(term) || (descLower != null && descLower.Contains(term)))
            {
                count++;
            }
        }

        return count;
    }

    // Example SQL is rendered in the data source's own dialect, read from dataSourceId when an example holds a comment marker.
    public Task<List<LearnedPatternInfo>> GetRelevantPatternsAsync(
        int dataSourceId, int projectId, List<string> tableNames, string? question = null,
        int maxPatterns = 10, int budgetChars = 1500, CancellationToken ct = default)
    {
        return GetRelevantPatternsAsync(dataSourceId, projectId, tableNames, question, maxPatterns, budgetChars, DialectOfAsync, questionVector: null, ct);
    }

    internal Task<List<LearnedPatternInfo>> GetRelevantPatternsAsync(
        int dataSourceId, int projectId, List<string> tableNames, string? question,
        int maxPatterns, int budgetChars, string? dialect, CancellationToken ct)
    {
        return GetRelevantPatternsAsync(dataSourceId, projectId, tableNames, question, maxPatterns, budgetChars, (_, _, _) => Task.FromResult(dialect), questionVector: null, ct);
    }

    // The learned-patterns block of an ask context, ranked by the ask's shared question vector.
    private async Task<string> LearnedPatternsBlockAsync(
        int dataSourceId, int projectId, List<string> tableNames, string question, McpSettingsData settings, string? dialect,
        MaskedQuestionVector questionVector, CancellationToken ct)
    {
        var patterns = await GetRelevantPatternsAsync(
            dataSourceId,
            projectId,
            tableNames,
            question,
            maxPatterns: 10,
            budgetChars: settings.LearningInjectionBudgetChars,
            (_, _, _) => Task.FromResult(dialect),
            questionVector,
            ct);

        return FormatLearnedPatternsForLlm(patterns);
    }

    // Example SQL comes back as a prompt shows it: without its comments, read in the data source's dialect
    // (GroundingSqlText), so patterns stored before Beacon checked grounding SQL show none either; example questions come
    // back on one line. An example SQL or question that cannot be shown that way is left out (the pattern stays), and
    // logged by pattern id once per interval.
    private async Task<List<LearnedPatternInfo>> GetRelevantPatternsAsync(
        int dataSourceId, int projectId, List<string> tableNames, string? question,
        int maxPatterns, int budgetChars, Func<BeaconContext, int, CancellationToken, Task<string?>> dialectOf, MaskedQuestionVector? questionVector,
        CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var lowerTables = tableNames
            .Select(x => x.ToLowerInvariant())
            .ToHashSet();

        // The approved/auto-approved pattern bank feeds BOTH paths: semantic selection picks the top-k
        // nearest lessons of ANY type out of it by embedding similarity, and the table-overlap ranking
        // orders the rest. Stale (superseded) lessons are filtered out here so they are NEVER injected
        // into a prompt on either path (§ Architecture ⑧ temporal decay) — history stays in the table.
        // The ProjectId filter is load-bearing (R5/C1): a data source shared by several projects mines
        // patterns for each of them, and another project's lessons (quoting its real questions/SQL)
        // must never reach this caller's prompt — same isolation as the golden-exemplar block.
        var patterns = await context.McpLearnedPatterns
            .Where(x => x.DataSourceId == dataSourceId)
            .Where(x => x.ProjectId == projectId)
            .Where(x => x.Status == McpPatternStatus.Approved || x.Status == McpPatternStatus.AutoApproved)
            .Where(x => x.SupersededAt == null)
            .OrderByDescending(x => x.Confidence)
            .ThenByDescending(x => x.SignalCount)
            .Take(100)
            .Select(x =>
                new PatternCandidate
                {
                    Id = x.Id,
                    PatternType = x.PatternType,
                    TableName = x.TableName,
                    SchemaName = x.SchemaName,
                    ColumnName = x.ColumnName,
                    PatternContent = x.PatternContent,
                    ExampleQuestion = x.ExampleQuestion,
                    ExampleSql = x.ExampleSql,
                    Confidence = x.Confidence
                })
            .ToListAsync(ct);
        patterns = await AsPromptShowsAsync(context, patterns, dataSourceId, dialectOf, ct);

        var settings = await settingsProvider.GetEffectiveSettingsAsync(projectId, ct);

        // DAIL-SQL semantic selection (§ Architecture ⑧): choose the top-k nearest lessons of ANY type by
        // masked-question embedding similarity instead of blanket-injecting the whole bank. Lessons that have
        // no embedding yet (e.g. not re-indexed) fall back to the table-overlap ranking and are merged in
        // after the semantic picks. Any failure falls back to the full overlap ranking over all types.
        var useSemantic = embeddingService.IsAvailable
            && settings.EnableSemanticRetrieval
            && !string.IsNullOrWhiteSpace(question);

        if (useSemantic)
        {
            try
            {
                var semanticRanked = await BuildSemanticRankingAsync(
                    context, patterns, lowerTables, questionVector ?? new MaskedQuestionVector(embeddingService, question!), dataSourceId, projectId, settings.ExemplarTopK, ct);
                if (semanticRanked != null)
                {
                    return ApplyBudget(semanticRanked, maxPatterns, budgetChars);
                }
                // No exemplar vectors indexed / none mapped back — fall through to overlap behaviour.
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Semantic exemplar selection failed for data source {DataSourceId}; falling back to table-overlap pattern ranking ({Failure}).", dataSourceId, FailureCodes.Describe(ex));
            }
        }

        // Behaviour-preserving fallback: the historical table-overlap ranking over ALL pattern types.
        return ApplyBudget(RankByTableOverlap(patterns, lowerTables), maxPatterns, budgetChars);
    }

    // Semantic path (§ Architecture ⑧): embed the masked question once, pull the nearest Exemplar embeddings
    // for this data source, and map their OwnerIds back to the loaded valid bank of ANY pattern type in
    // vector-similarity order — these top-k picks lead. Lessons that have NO exemplar embedding (e.g. added
    // since the last re-index, so they could not be ranked semantically) keep the table-overlap ranking and
    // follow, so they stay visible rather than silently disappearing. Lessons that DO have an embedding but
    // were not in the top-k are intentionally dropped — that is the point of top-k over blanket injection.
    // Returns null when no exemplar vector maps back to a loaded pattern, so the caller falls back to full
    // overlap ranking.
    // The kNN is scoped by data source AND the caller's authorized project (R5/C1) so another project's
    // exemplars on a shared data source can neither be selected nor occupy top-k slots. Exemplar rows
    // indexed before ProjectId stamping (project_id NULL) simply don't match — fail-closed, healed by the
    // next re-index; the project-filtered bank mapping below is the isolation backstop either way.
    private async Task<List<PatternCandidate>?> BuildSemanticRankingAsync(
        BeaconContext context, List<PatternCandidate> patterns, HashSet<string> lowerTables,
        MaskedQuestionVector questionVector, int dataSourceId, int projectId, int exemplarTopK, CancellationToken ct)
    {
        var topK = Math.Max(1, exemplarTopK);
        var maskedVector = await questionVector.GetAsync(ct);

        var hits = context.Database.ProviderName == NpgsqlProviderName
            ? await GetNearestPostgresAsync(context, maskedVector, [dataSourceId], ExemplarOwnerTypes, topK, ct, projectId: projectId)
            : await GetNearestInMemoryAsync(context, maskedVector, [dataSourceId], ExemplarOwnerTypes, topK, ct, projectId: projectId);

        if (hits.Count == 0)
        {
            return null;
        }

        // Exemplar OwnerId == McpLearnedPattern.Id. Map hits (already in similarity order) back to the loaded
        // bank of ALL types, preserving that order; ignore ids not in the bank (e.g. a hit whose pattern was
        // superseded and filtered out of the bank above, or one belonging to another project).
        var patternsById = patterns.ToDictionary(x => x.Id);

        var selected = new List<PatternCandidate>();
        var selectedIds = new HashSet<int>();
        foreach (var hit in hits)
        {
            if (patternsById.TryGetValue(hit.OwnerId, out var pattern) && selectedIds.Add(hit.OwnerId))
            {
                selected.Add(pattern);
            }
        }

        if (selected.Count == 0)
        {
            return null;
        }

        // A lesson with an exemplar embedding was a candidate for the top-k; if it was not selected it is
        // deliberately dropped. A lesson WITHOUT an embedding could not be ranked semantically, so it falls
        // back to the table-overlap ranking and is merged in after the semantic picks (one cheap id lookup —
        // no vectors loaded). This keeps freshly-added, not-yet-indexed lessons visible.
        var embeddedIds = await context.McpEmbeddings
            .Where(x => x.DataSourceId == dataSourceId)
            .Where(x => x.ProjectId == projectId)
            .Where(x => x.OwnerType == McpEmbeddingOwnerType.Exemplar)
            .Select(x => x.OwnerId)
            .ToListAsync(ct);
        var embeddedIdSet = embeddedIds.ToHashSet();

        var backfill = RankByTableOverlap(
            patterns.Where(x => !selectedIds.Contains(x.Id) && !embeddedIdSet.Contains(x.Id)),
            lowerTables);

        return selected
            .Concat(backfill)
            .ToList();
    }

    // Historical relevance ranking: table overlap (biggest lever) + pattern-type priority + confidence.
    private static List<PatternCandidate> RankByTableOverlap(
        IEnumerable<PatternCandidate> patterns, HashSet<string> lowerTables)
    {
        return patterns
            .Select(x =>
            {
                var tableFull = $"{x.SchemaName}.{x.TableName}".ToLowerInvariant();
                var tableOnly = x.TableName.ToLowerInvariant();
                var tableMatch = lowerTables.Contains(tableFull) || lowerTables.Contains(tableOnly);
                var typePriority = x.PatternType switch
                {
                    McpPatternType.SchemaCorrection => 4,
                    McpPatternType.ColumnClarification => 3,
                    McpPatternType.JoinPattern => 2,
                    McpPatternType.CommonQuery => 1,
                    _ => 0
                };
                var score = (tableMatch ? 10 : 0) + typePriority + x.Confidence;
                return (Pattern: x, Score: score);
            })
            .OrderByDescending(x => x.Score)
            .Select(x => x.Pattern)
            .ToList();
    }

    // Shared budget cap: take patterns in the supplied order until maxPatterns or budgetChars is hit.
    private static List<LearnedPatternInfo> ApplyBudget(
        List<PatternCandidate> ranked, int maxPatterns, int budgetChars)
    {
        var result = new List<LearnedPatternInfo>();
        var totalChars = 0;

        foreach (var x in ranked)
        {
            if (result.Count >= maxPatterns)
            {
                break;
            }

            var contentLen = x.PatternContent.Length + (x.ExampleSql?.Length ?? 0) + 20;
            if (totalChars + contentLen > budgetChars && result.Count > 0)
            {
                break;
            }

            totalChars += contentLen;
            result.Add(new LearnedPatternInfo(
                x.PatternType.ToString(),
                x.TableName,
                x.ColumnName,
                x.PatternContent,
                x.ExampleQuestion,
                x.ExampleSql,
                x.Confidence));
        }

        return result;
    }

    // The bank with each example as the prompt shows it: SQL without comments (the dialect read only when an example holds a
    // comment marker), the question on one line. An example that cannot be shown that way is left out (the lesson keeps its
    // content) and logged by pattern id only (§1.11), once per interval.
    private async Task<List<PatternCandidate>> AsPromptShowsAsync(
        BeaconContext context,
        List<PatternCandidate> patterns,
        int dataSourceId,
        Func<BeaconContext, int, CancellationToken, Task<string?>> dialectOf,
        CancellationToken ct)
    {
        var needsDialect = patterns.Any(x => GroundingDialects.NeedsDialect(x.ExampleSql));
        var dialect = needsDialect ? await dialectOf(context, dataSourceId, ct) : null;

        var leftOut = new List<int>();
        var rendered = new List<PatternCandidate>(patterns.Count);
        foreach (var pattern in patterns)
        {
            var exampleSql = string.IsNullOrWhiteSpace(pattern.ExampleSql) ? null : GroundingSqlText.WithoutComments(pattern.ExampleSql, dialect);
            var exampleQuestion = GroundingText.Question(pattern.ExampleQuestion);
            if ((!string.IsNullOrWhiteSpace(pattern.ExampleSql) && string.IsNullOrEmpty(exampleSql))
                || (!string.IsNullOrWhiteSpace(pattern.ExampleQuestion) && exampleQuestion == null))
            {
                leftOut.Add(pattern.Id);
            }

            rendered.Add(pattern with
            {
                ExampleSql = string.IsNullOrEmpty(exampleSql) ? null : exampleSql,
                ExampleQuestion = exampleQuestion
            });
        }

        var withheldPatterns = DueToLog("pattern-example", leftOut);
        if (withheldPatterns.Count > 0)
        {
            logger.LogWarning(
                "{Count} learned-pattern example(s) of data source {DataSourceId} were left out of the prompt: the SQL holds a comment that cannot be removed, or the SQL or question holds characters that do not show (patterns {PatternIds})",
                withheldPatterns.Count,
                dataSourceId,
                string.Join(",", withheldPatterns));
        }

        return rendered;
    }

    private static async Task<string?> DialectOfAsync(BeaconContext context, int dataSourceId, CancellationToken ct)
    {
        var source = await context.DataSources
            .Where(x => x.Id == dataSourceId)
            .Select(x =>
                new
                {
                    x.DataSourceType,
                    x.DatabaseEngineType
                })
            .FirstOrDefaultAsync(ct);

        return source == null ? null : DataSourceSqlDialect.Of(source.DataSourceType, source.DatabaseEngineType);
    }

    // The rows a prompt left out that are due to be logged — identifiers only (§1.11), each once per
    // WithheldGroundingLog.Interval.
    private IReadOnlyList<int> DueToLog(string kind, List<int> leftOut)
    {
        return leftOut.Count == 0 ? [] : _withheld.NotLoggedRecently(kind, leftOut);
    }

    // Materialized pattern-bank row shared by the semantic and overlap ranking paths.
    private sealed record PatternCandidate
    {
        public int Id { get; init; }
        public McpPatternType PatternType { get; init; }
        public string TableName { get; init; } = null!;
        public string SchemaName { get; init; } = null!;
        public string? ColumnName { get; init; }
        public string PatternContent { get; init; } = null!;
        public string? ExampleQuestion { get; init; }
        public string? ExampleSql { get; init; }
        public double Confidence { get; init; }
    }

    internal static string FormatLearnedPatternsForLlm(List<LearnedPatternInfo> patterns)
    {
        if (patterns.Count == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine("\n## Learned Patterns (from usage)\n");

        var corrections = patterns.Where(p => p.PatternType == "SchemaCorrection").ToList();
        if (corrections.Count > 0)
        {
            sb.AppendLine("### Known Corrections");
            foreach (var p in corrections)
                sb.AppendLine($"- {p.PatternContent}");
            sb.AppendLine();
        }

        var clarifications = patterns.Where(p => p.PatternType == "ColumnClarification").ToList();
        if (clarifications.Count > 0)
        {
            sb.AppendLine("### Column Clarifications");
            foreach (var p in clarifications)
            {
                var colPart = p.ColumnName != null ? $".{p.ColumnName}" : "";
                sb.AppendLine($"- {p.TableName}{colPart}: {p.PatternContent}");
            }
            sb.AppendLine();
        }

        var joins = patterns.Where(p => p.PatternType == "JoinPattern").ToList();
        if (joins.Count > 0)
        {
            sb.AppendLine("### Join Patterns");
            foreach (var p in joins)
                sb.AppendLine($"- {p.PatternContent}");
            sb.AppendLine();
        }

        var commonQueries = patterns.Where(p => p.PatternType == "CommonQuery").ToList();
        if (commonQueries.Count > 0)
        {
            sb.AppendLine("### Common Queries");
            foreach (var p in commonQueries)
            {
                if (!string.IsNullOrEmpty(p.ExampleQuestion))
                    sb.AppendLine($"- \"{p.ExampleQuestion}\"");
                else
                    sb.AppendLine($"- {p.PatternContent}");
                if (!string.IsNullOrEmpty(p.ExampleSql))
                    sb.AppendLine($"  → `{p.ExampleSql}`");
            }
            sb.AppendLine();
        }

        // NOTE (Tier-3 ⑪): BusinessTermMapping is the DEAD learned-pattern path — the admin-managed business
        // glossary (BuildGlossaryBlockAsync + the "## Business Glossary" block) is now the mechanism for
        // term→definition→target injection. The enum value and this rendering are left in place for any
        // historical rows, but no new BusinessTermMapping patterns are produced.
        var mappings = patterns.Where(p => p.PatternType == "BusinessTermMapping").ToList();
        if (mappings.Count > 0)
        {
            sb.AppendLine("### Business Term Mappings");
            foreach (var p in mappings)
                sb.AppendLine($"- {p.PatternContent}");
            sb.AppendLine();
        }

        var gaps = patterns.Where(p => p.PatternType == "DocumentationGap").ToList();
        if (gaps.Count > 0)
        {
            sb.AppendLine("### Documentation Gaps (caution)");
            foreach (var p in gaps)
                sb.AppendLine($"- {p.PatternContent}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    // Renders a glossary term's target: the qualified column path (schema.table.column, only the parts that
    // are set) and/or the metric expression as rendered (without comments). Null when the term carries neither.
    private static string? FormatGlossaryTarget(GlossaryHit term, string? metricExpression)
    {
        var parts = new List<string>();

        var column = new[] { term.TargetSchema, term.TargetTable, term.TargetColumn }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
        if (column.Count > 0)
        {
            parts.Add(string.Join(".", column));
        }

        if (!string.IsNullOrWhiteSpace(metricExpression))
        {
            parts.Add($"`{metricExpression}`");
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private sealed class GlossaryHit
    {
        public int Id { get; init; }
        public string Term { get; init; } = "";
        public string Definition { get; init; } = "";
        public string? TargetSchema { get; init; }
        public string? TargetTable { get; init; }
        public string? TargetColumn { get; init; }
        public string? MetricExpression { get; init; }
    }

    private sealed class GoldenCaseHit
    {
        public int Id { get; init; }
        public string Question { get; init; } = "";
        public string GoldSql { get; init; } = "";
    }
}
