using System.Diagnostics;
using System.Globalization;
using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Helpers;
using Beacon.Core.RowSecurity;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.Core.SecurityAudit;

namespace Beacon.AI.Services.Documentation;

internal sealed class ProjectDocumentationService(
    IDbContextFactory<BeaconContext> contextFactory,
    ILlmProvider llmProvider,
    IDataSourceProviderFactory dataSourceProviderFactory,
    ISqlExecutionGate gate,
    IConfiguredDatabaseResolver configuredDatabases,
    IRowSecurityResolver rowSecurityResolver,
    ISharedSourceSettings sharedSourceSettings,
    IQueryGuardrailService guardrail,
    ILlmRowDataEgress rowDataEgress,
    ISecurityAuditRecorder securityAudit,
    IOptions<McpDeploymentOptions> deploymentOptions,
    ILogger<ProjectDocumentationService> logger,
    ISqlExecutionAudit? executionAudit = null) : IProjectDocumentationService
{
    /// <summary>
    /// What a section whose generation failed holds: a fixed text, never the failure's own message (a provider's error
    /// text would otherwise be served to agents as documentation). The failure is logged by its code.
    /// </summary>
    internal const string FailedSectionContent = "*Generation failed for this section. Please regenerate.*";

    public async Task<ProjectDocumentation> GenerateDocumentationAsync(
        int projectId, int userId, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        logger.LogInformation("Starting project documentation generation for Project {ProjectId}", projectId);

        var docContext = await GatherContextAsync(projectId, ct);

        // The prompts that list discovered column values carry row data to the LLM provider: each such request is logged.
        var columnValuesProvider = rowDataEgress.Track(llmProvider, ColumnValuesScope(projectId, userId, docContext));

        var sections = new List<ProjectDocumentationSection>();
        var totalInput = 0;
        var totalOutput = 0;
        var totalCost = 0m;
        var model = "unknown";

        // Run passes 1-7 in parallel (independent context), then pass 8 (glossary) sequentially
        var parallelPasses = new (ProjectDocSectionType Type, string Title, int SortOrder, Func<DocumentationContext, string> BuildPrompt, bool ListsColumnValues)[]
        {
            (ProjectDocSectionType.ProjectOverview, "Project Overview", 0, ProjectDocumentationPrompts.BuildProjectOverviewPrompt, false),
            (ProjectDocSectionType.BusinessDomains, "Business Domains", 1, ProjectDocumentationPrompts.BuildBusinessDomainsPrompt, true),
            (ProjectDocSectionType.DataModel, "Data Model", 2, ProjectDocumentationPrompts.BuildDataModelPrompt, true),
            (ProjectDocSectionType.DataFlows, "Data Flows", 3, ProjectDocumentationPrompts.BuildDataFlowsPrompt, false),
            (ProjectDocSectionType.CodeLineage, "Code Lineage", 4, ProjectDocumentationPrompts.BuildCodeLineagePrompt, false),
            (ProjectDocSectionType.DataQuality, "Data Quality", 5, ProjectDocumentationPrompts.BuildDataQualityPrompt, false),
        };

        // Add API documentation pass only if there are API data sources
        var hasApiSources = docContext.ApiDataSources.Count > 0;

        var tasks = parallelPasses.Select(async pass =>
        {
            try
            {
                var userPrompt = pass.BuildPrompt(docContext);
                var response = await CallLlmAsync(pass.ListsColumnValues ? columnValuesProvider : llmProvider, ProjectDocumentationPrompts.GetSystemPrompt(pass.Type), userPrompt, ct);
                return (pass.Type, pass.Title, pass.SortOrder, Content: response.Content,
                    response.InputTokens, response.OutputTokens, response.Cost, response.Model, Error: (string?)null);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Documentation generation failed for section {SectionType} ({Failure})", pass.Type, FailureCodes.Describe(ex));
                return (pass.Type, pass.Title, pass.SortOrder,
                    Content: FailedSectionContent,
                    InputTokens: 0, OutputTokens: 0, Cost: 0m, Model: "unknown", Error: ex.Message);
            }
        }).ToList();

        if (hasApiSources)
        {
            tasks.Add(GenerateApiDocumentationAsync(docContext, ct));
        }

        var results = await Task.WhenAll(tasks);

        foreach (var result in results)
        {
            sections.Add(new ProjectDocumentationSection
            {
                SectionType = result.Type,
                Title = result.Title,
                Content = DocumentationContentParser.SanitizeMermaidDiagrams(result.Content),
                SortOrder = result.SortOrder
            });
            totalInput += result.InputTokens;
            totalOutput += result.OutputTokens;
            totalCost += result.Cost;
            if (result.Model != "unknown") model = result.Model;
        }

        var failedSections = results.Count(x => x.Error != null);

        // Pass 8: Glossary — runs after others so it can reference their content
        try
        {
            var glossaryPrompt = ProjectDocumentationPrompts.BuildGlossaryPrompt(docContext, sections);
            var glossaryResponse = await CallLlmAsync(columnValuesProvider, ProjectDocumentationPrompts.GetSystemPrompt(ProjectDocSectionType.Glossary), glossaryPrompt, ct);
            sections.Add(new ProjectDocumentationSection
            {
                SectionType = ProjectDocSectionType.Glossary,
                Title = "Glossary",
                Content = DocumentationContentParser.SanitizeMermaidDiagrams(glossaryResponse.Content),
                SortOrder = 7
            });
            totalInput += glossaryResponse.InputTokens;
            totalOutput += glossaryResponse.OutputTokens;
            totalCost += glossaryResponse.Cost;
            if (glossaryResponse.Model != "unknown") model = glossaryResponse.Model;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Documentation generation failed for Glossary ({Failure})", FailureCodes.Describe(ex));
            failedSections++;
            sections.Add(new ProjectDocumentationSection
            {
                SectionType = ProjectDocSectionType.Glossary,
                Title = "Glossary",
                Content = FailedSectionContent,
                SortOrder = 7
            });
        }

        sw.Stop();

        // Persist. The sections a person edited in the latest documentation are read inside the save's transaction, right
        // before the documentation is written, and replace what this run generated for them: regeneration never silently
        // supersedes curated text, and an edit committed before the save is in the read.
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var forbidRowData = deploymentOptions.Value.ForbidRowDataToLlm;
        var saveResult = await securityAudit.SaveWithEventsAsync(
            context,
            async () =>
            {
                var generatedSections = sections.ToList();
                var curated = await CuratedSectionsOfLatestAsync(context, projectId, ct);
                var (carried, dropped) = CarryCuratedSections(generatedSections, curated, forbidRowData);
                var saved = new ProjectDocumentation
                {
                    ProjectId = projectId,
                    GeneratedByModel = model,
                    GeneratedAt = DateTime.UtcNow,
                    GeneratedByUserId = userId,
                    InputTokens = totalInput,
                    OutputTokens = totalOutput,
                    EstimatedCost = totalCost,
                    DataSourcesAnalyzed = docContext.DataSourceCount,
                    TablesAnalyzed = docContext.TableCount,
                    CodeReferencesAnalyzed = docContext.CodeReferenceCount,
                    GenerationDuration = sw.Elapsed,
                    Sections = generatedSections
                };

                context.ProjectDocumentations.Add(saved);
                await context.SaveChangesAsync(ct);

                return (Documentation: saved, Carried: carried, Dropped: dropped);
            },
            x =>
            [
                new SecurityAuditEntry(SecurityAuditActions.DocumentationGenerated)
                    .On(SecurityAuditResources.Project, projectId)
                    .InProject(projectId)
                    .By(userId > 0 ? SecurityAuditActor.OfUser(userId) : SecurityAuditActor.System)
                    .With(new SecurityAuditDetails()
                        .Add("documentationId", x.Documentation.Id)
                        .Add("sectionCount", x.Documentation.Sections.Count)
                        .Add("curatedSectionsKept", x.Carried)
                        .Add("curatedSectionsDropped", x.Dropped)
                        .Add("failedSections", failedSections))
            ],
            ct);
        var documentation = saveResult.Documentation;

        logger.LogInformation(
            "Project documentation {DocId} generated for Project {ProjectId} in {Duration:F1}s ({Sections} sections, {Tokens} tokens)",
            documentation.Id, projectId, sw.Elapsed.TotalSeconds, documentation.Sections.Count, totalInput + totalOutput);

        return documentation;
    }

    public Task<string> ExportToMarkdownAsync(int documentationId, CancellationToken ct = default)
    {
        return ExportToMarkdownAsync(documentationId, [], ct);
    }

    private async Task<string> ExportToMarkdownAsync(int documentationId, IReadOnlyCollection<ProjectDocSectionType> leaveOut, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var doc = await context.ProjectDocumentations
            .Include(d => d.Sections.OrderBy(s => s.SortOrder))
            .Include(d => d.Project)
            .Where(d => d.Id == documentationId)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"Documentation {documentationId} not found");

        var sb = new StringBuilder();
        sb.AppendLine($"# {doc.Project.Name} — Project Documentation");
        sb.AppendLine();
        sb.AppendLine($"*Generated: {doc.GeneratedAt:yyyy-MM-dd HH:mm} UTC | Model: {doc.GeneratedByModel}*");
        sb.AppendLine();

        foreach (var section in doc.Sections.Where(x => !leaveOut.Contains(x.SectionType)))
        {
            sb.AppendLine($"## {section.Title}");
            sb.AppendLine();
            sb.AppendLine(section.Content);
            sb.AppendLine();
        }

        // Append learned patterns from usage
        var dsIds = await context.ProjectDataSources
            .WhereReadable()
            .Where(pds => pds.ProjectId == doc.ProjectId)
            .Select(pds => pds.DataSourceId)
            .ToListAsync(ct);

        var learnedPatterns = await context.McpLearnedPatterns
            .Where(x => x.ProjectId == doc.ProjectId)
            .Where(x => dsIds.Contains(x.DataSourceId))
            .Where(x => x.Status == McpPatternStatus.Approved || x.Status == McpPatternStatus.AutoApproved)
            .OrderByDescending(x => x.Confidence)
            .Take(30)
            .ToListAsync(ct);

        if (learnedPatterns.Count > 0)
        {
            sb.AppendLine("## Usage-Learned Insights");
            sb.AppendLine();
            sb.AppendLine("*The following insights were automatically learned from MCP query patterns.*");
            sb.AppendLine();

            // Example SQL as a prompt shows it — the export feeds knowledge answers, get_documentation and the doc-chunk
            // index: without its comments, in the pattern's data source's dialect; left out when it cannot be shown so.
            var dialects = await GroundingDialects.OfAsync(
                context,
                learnedPatterns
                    .Where(x => GroundingDialects.NeedsDialect(x.ExampleSql))
                    .Select(x => x.DataSourceId)
                    .Distinct()
                    .ToList(),
                ct);
            var leftOut = new List<int>();
            var byType = learnedPatterns.GroupBy(p => p.PatternType);
            foreach (var group in byType)
            {
                sb.AppendLine($"### {group.Key}");
                foreach (var p in group)
                {
                    sb.AppendLine($"- **{p.SchemaName}.{p.TableName}**{(p.ColumnName != null ? $".{p.ColumnName}" : "")}: {p.PatternContent}");
                    if (string.IsNullOrWhiteSpace(p.ExampleSql))
                    {
                        continue;
                    }

                    var exampleSql = GroundingSqlText.WithoutComments(p.ExampleSql, dialects.GetValueOrDefault(p.DataSourceId));
                    if (string.IsNullOrEmpty(exampleSql))
                    {
                        leftOut.Add(p.Id);
                        continue;
                    }

                    sb.AppendLine($"  - Example: `{exampleSql}`");
                }
                sb.AppendLine();
            }

            if (leftOut.Count > 0)
            {
                logger.LogWarning(
                    "{Count} learned-pattern example SQL(s) of project {ProjectId} were left out of the documentation export: they hold a comment that cannot be removed or characters that do not show (patterns {PatternIds})",
                    leftOut.Count,
                    doc.ProjectId,
                    string.Join(",", leftOut));
            }
        }

        return sb.ToString();
    }

    public async Task<string> ExportToHtmlAsync(int documentationId, CancellationToken ct = default)
    {
        var markdown = await ExportToMarkdownAsync(documentationId, ct);
        return ConvertToHtml(markdown);
    }

    public Task<string?> ExportLatestToMarkdownAsync(int projectId, CancellationToken ct = default)
    {
        return ExportLatestToMarkdownAsync(projectId, [], ct);
    }

    public async Task<string?> ExportLatestToMarkdownAsync(int projectId, IReadOnlyCollection<ProjectDocSectionType> leaveOut, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var latestId = await context.ProjectDocumentations
            .Where(d => d.ProjectId == projectId)
            .OrderByDescending(d => d.GeneratedAt)
            .Select(d => (int?)d.Id)
            .FirstOrDefaultAsync(ct);

        if (latestId == null) return null;
        return await ExportToMarkdownAsync(latestId.Value, leaveOut, ct);
    }

    /// <summary>
    /// Puts each curated section (<paramref name="curated"/>, edited by a person) in place of the section of its type in
    /// <paramref name="sections"/>, or adds it when this run generated none of its type: a copy with its title, content
    /// and who edited it when, so it stays curated in the new documentation. While row data may not reach the LLM
    /// (<paramref name="forbidRowData"/>) a section whose text can list discovered values is not carried: it is regenerated
    /// under the lock, never copied forward unscreened. Returns how many were carried and how many dropped.
    /// </summary>
    internal static (int Carried, int Dropped) CarryCuratedSections(
        List<ProjectDocumentationSection> sections,
        IReadOnlyList<ProjectDocumentationSection> curated,
        bool forbidRowData = false)
    {
        var carried = 0;
        var dropped = 0;
        foreach (var edited in curated)
        {
            if (forbidRowData && edited.SectionType.ListsValues())
            {
                dropped++;
                continue;
            }

            var generated = sections.FindIndex(x => x.SectionType == edited.SectionType);
            var kept = new ProjectDocumentationSection
            {
                SectionType = edited.SectionType,
                Title = edited.Title,
                Content = edited.Content,
                SortOrder = generated >= 0 ? sections[generated].SortOrder : edited.SortOrder,
                EditedByUserId = edited.EditedByUserId,
                EditedTime = edited.EditedTime
            };

            if (generated >= 0)
            {
                sections[generated] = kept;
            }
            else
            {
                sections.Add(kept);
            }

            carried++;
        }

        return (carried, dropped);
    }

    // The sections of the project's latest documentation that a person edited, one per type (the latest edit).
    private static async Task<List<ProjectDocumentationSection>> CuratedSectionsOfLatestAsync(BeaconContext context, int projectId, CancellationToken ct)
    {
        var latestId = await context.ProjectDocumentations
            .Where(x => x.ProjectId == projectId)
            .OrderByDescending(x => x.GeneratedAt)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(ct);
        if (latestId == null)
        {
            return [];
        }

        var edited = await context.ProjectDocumentationSections
            .Where(x => x.ProjectDocumentationId == latestId)
            .Where(x => x.EditedTime != null)
            .OrderBy(x => x.SortOrder)
            .ToListAsync(ct);

        return edited
            .GroupBy(x => x.SectionType)
            .Select(x => x
                .OrderByDescending(y => y.EditedTime)
                .First())
            .ToList();
    }

    // --- Section helpers ---

    // Generates the API-documentation section. Kept as a named method so the caller
    // can `tasks.Add(GenerateApiDocumentationAsync(...))` without wrapping in Task.Run,
    // which would otherwise queue an extra thread-pool item for an I/O-bound call.
    private async Task<(ProjectDocSectionType Type, string Title, int SortOrder, string Content, int InputTokens, int OutputTokens, decimal Cost, string Model, string? Error)>
        GenerateApiDocumentationAsync(DocumentationContext docContext, CancellationToken ct)
    {
        try
        {
            var userPrompt = ProjectDocumentationPrompts.BuildApiDocumentationPrompt(docContext);
            var response = await CallLlmAsync(llmProvider, ProjectDocumentationPrompts.GetSystemPrompt(ProjectDocSectionType.ApiDocumentation), userPrompt, ct);
            return (ProjectDocSectionType.ApiDocumentation, "API Documentation", 6,
                Content: response.Content,
                response.InputTokens, response.OutputTokens, response.Cost, response.Model, Error: null);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Documentation generation failed for API Documentation ({Failure})", FailureCodes.Describe(ex));
            return (ProjectDocSectionType.ApiDocumentation, "API Documentation", 6,
                Content: FailedSectionContent,
                InputTokens: 0, OutputTokens: 0, Cost: 0m, Model: "unknown", Error: ex.Message);
        }
    }

    // --- Context Gathering ---

    private async Task<DocumentationContext> GatherContextAsync(int projectId, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var project = await context.Projects
            .Where(p => p.Id == projectId)
            .Select(p => new { p.Id, p.Name, p.Description })
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"Project {projectId} not found");

        // Get data source IDs and info
        var dataSources = await context.ProjectDataSources
            .WhereReadable()
            .Where(pds => pds.ProjectId == projectId)
            .Select(pds => new DataSourceInfo(
                pds.DataSourceId,
                pds.DataSource.Name,
                pds.DataSource.DataSourceType,
                pds.DataSource.DatabaseEngineType))
            .ToListAsync(ct);

        var dsIds = dataSources.Select(ds => ds.Id).ToList();

        // Run independent queries in parallel (each uses the shared DbContext factory)
        var tablesTask = GetTablesAsync(dsIds, ct);
        var codeRefsTask = GetCodeReferencesAsync(projectId, ct);
        var qualityTask = GetQualityScoresAsync(dsIds, ct);
        var contractsTask = GetContractsAsync(dsIds, ct);
        var reposTask = GetRepositoriesAsync(projectId, ct);
        var patternsTask = GetLearnedPatternsForDocAsync(projectId, dsIds, ct);

        await Task.WhenAll(tablesTask, codeRefsTask, qualityTask, contractsTask, reposTask, patternsTask);

        var tables = tablesTask.Result;
        var codeReferences = codeRefsTask.Result;
        var qualityScores = qualityTask.Result;
        var contracts = contractsTask.Result;
        var repositories = reposTask.Result;
        var learnedPatterns = patternsTask.Result;

        // Phase 2: Discover enum/status values from external data sources
        var enumValues = await DiscoverEnumValuesAsync(projectId, dsIds, tables, ct);

        return new DocumentationContext
        {
            ProjectName = project.Name,
            ProjectDescription = project.Description,
            DataSources = dataSources,
            Tables = tables,
            CodeReferences = codeReferences,
            QualityScores = qualityScores,
            Contracts = contracts,
            Repositories = repositories,
            LearnedPatterns = learnedPatterns,
            EnumValues = enumValues
        };
    }

    // --- LLM Calling ---

    // The user prompt is the project's metadata — names, descriptions, values, code snippets, earlier sections — so it is sent
    // as one data block under the data rule.
    private static async Task<LlmResponse> CallLlmAsync(ILlmProvider provider, string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var request = new LlmRequest
        {
            SystemPrompt = $"{systemPrompt.TrimEnd()}\n\n{PromptData.Rule}",
            Messages = [new ChatMessage(ConversationRole.User, PromptData.Fence("project metadata", userPrompt))],
            Temperature = 0.3m,
            MaxTokens = 8192,
            Priority = LlmRequestPriority.Background,
            Operation = LlmOperations.Documentation
        };
        return await provider.CompleteAsync(request, ct);
    }

    // The column values discovery found, for the egress log: their data sources, on behalf of the user who asked.
    private static LlmRowDataScope ColumnValuesScope(int projectId, int userId, DocumentationContext docContext)
    {
        var dataSourceIds = docContext.EnumValues
            .Select(x => x.DataSourceId)
            .Distinct()
            .Order()
            .ToList();

        return new LlmRowDataScope(projectId, dataSourceIds, dataSourceIds.Count > 0 ? [LlmRowDataCategories.ColumnValues] : [])
        {
            CallerId = userId.ToString(CultureInfo.InvariantCulture)
        };
    }

    // Test-facing forwarders; the builders live in ProjectDocumentationPrompts.
    internal static string BuildProjectOverviewPrompt(DocumentationContext ctx) => ProjectDocumentationPrompts.BuildProjectOverviewPrompt(ctx);
    internal static string BuildBusinessDomainsPrompt(DocumentationContext ctx) => ProjectDocumentationPrompts.BuildBusinessDomainsPrompt(ctx);
    internal static string BuildDataModelPrompt(DocumentationContext ctx) => ProjectDocumentationPrompts.BuildDataModelPrompt(ctx);
    internal static string BuildDataFlowsPrompt(DocumentationContext ctx) => ProjectDocumentationPrompts.BuildDataFlowsPrompt(ctx);
    internal static string BuildCodeLineagePrompt(DocumentationContext ctx) => ProjectDocumentationPrompts.BuildCodeLineagePrompt(ctx);
    internal static string BuildDataQualityPrompt(DocumentationContext ctx) => ProjectDocumentationPrompts.BuildDataQualityPrompt(ctx);
    internal static string BuildApiDocumentationPrompt(DocumentationContext ctx) => ProjectDocumentationPrompts.BuildApiDocumentationPrompt(ctx);
    internal static string BuildGlossaryPrompt(DocumentationContext ctx, List<ProjectDocumentationSection> existingSections) => ProjectDocumentationPrompts.BuildGlossaryPrompt(ctx, existingSections);

    // --- Parallel Context Queries ---

    private async Task<List<TableInfo>> GetTablesAsync(List<int> dsIds, CancellationToken ct)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct);
        return await ctx.DatabaseMetadata
            .Where(m => dsIds.Contains(m.DataSourceId))
            .Select(m => new TableInfo
            {
                DataSourceId = m.DataSourceId,
                DataSourceName = m.DataSource.Name,
                SchemaName = m.SchemaName,
                TableName = m.TableName,
                Description = m.TableDescription,
                Columns = m.Columns
                    .OrderBy(c => c.OrdinalPosition)
                    .Select(c => new ColumnDetail(
                        c.ColumnName, c.DataType, c.IsNullable, c.IsPrimaryKey,
                        c.ForeignKeyTable, c.ForeignKeyColumn, c.Description, c.MaxLength))
                    .ToList(),
                Indexes = m.Indexes
                    .Select(i => new IndexDetail(i.IndexName, string.Join(", ", i.Columns), i.IsUnique))
                    .ToList()
            })
            .OrderBy(m => m.SchemaName)
            .ThenBy(m => m.TableName)
            .ToListAsync(ct);
    }

    private async Task<List<CodeRefInfo>> GetCodeReferencesAsync(int projectId, CancellationToken ct)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct);
        return await ctx.CodeReferences
            .Where(r => r.GitHubRepository.ProjectId == projectId)
            .Select(r => new CodeRefInfo(
                r.FilePath, r.LineNumber, r.ReferenceType, r.SchemaName,
                r.TableName, r.ColumnName, r.CodeSnippet, r.ClassName, r.MethodName))
            .ToListAsync(ct);
    }

    private async Task<List<QualityScoreInfo>> GetQualityScoresAsync(List<int> dsIds, CancellationToken ct)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct);
        return await ctx.DataQualityScores
            .Where(q => dsIds.Contains(q.DataSourceId))
            .Select(q => new QualityScoreInfo(
                q.DataSourceId, q.SchemaName, q.TableName, q.Score, q.TrendDirection))
            .ToListAsync(ct);
    }

    private async Task<List<ContractInfo>> GetContractsAsync(List<int> dsIds, CancellationToken ct)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct);
        return await ctx.DataContracts
            .Where(c => dsIds.Contains(c.DataSourceId))
            .Select(c => new ContractInfo(
                c.DataSourceId, c.SchemaName, c.TableName,
                c.Rules.Select(r => new ContractRuleInfo(r.RuleType, r.ColumnName, r.Configuration)).ToList()))
            .ToListAsync(ct);
    }

    private async Task<List<RepoInfo>> GetRepositoriesAsync(int projectId, CancellationToken ct)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct);
        return await ctx.GitHubRepositories
            .Where(r => r.ProjectId == projectId)
            .Select(r => new RepoInfo(r.RepositoryUrl, r.Branch, r.TotalFilesScanned, r.TotalReferencesFound))
            .ToListAsync(ct);
    }

    // The project's approved patterns as the documentation prompt is given them: example SQL without its comments in the
    // pattern's data source's dialect, example questions on one line; an example that cannot be shown so is left out (the
    // lesson stays), logged by pattern id.
    internal async Task<List<LearnedPatternForDoc>> GetLearnedPatternsForDocAsync(int projectId, List<int> dataSourceIds, CancellationToken ct)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct);
        var patterns = await ctx.McpLearnedPatterns
            .Where(x => x.ProjectId == projectId)
            .Where(x => dataSourceIds.Contains(x.DataSourceId))
            .Where(x => x.Status == McpPatternStatus.Approved || x.Status == McpPatternStatus.AutoApproved)
            .OrderByDescending(x => x.Confidence)
            .Take(30)
            .Select(x =>
                new
                {
                    x.Id,
                    x.DataSourceId,
                    x.PatternType,
                    x.TableName,
                    x.ColumnName,
                    x.PatternContent,
                    x.ExampleQuestion,
                    x.ExampleSql
                })
            .ToListAsync(ct);

        var dialects = await GroundingDialects.OfAsync(
            ctx,
            patterns
                .Where(x => GroundingDialects.NeedsDialect(x.ExampleSql))
                .Select(x => x.DataSourceId)
                .Distinct()
                .ToList(),
            ct);
        var leftOut = new List<int>();
        var forDoc = new List<LearnedPatternForDoc>(patterns.Count);
        foreach (var pattern in patterns)
        {
            var exampleSql = string.IsNullOrWhiteSpace(pattern.ExampleSql)
                ? null
                : GroundingSqlText.WithoutComments(pattern.ExampleSql, dialects.GetValueOrDefault(pattern.DataSourceId));
            var exampleQuestion = GroundingText.Question(pattern.ExampleQuestion);
            if ((!string.IsNullOrWhiteSpace(pattern.ExampleSql) && string.IsNullOrEmpty(exampleSql))
                || (!string.IsNullOrWhiteSpace(pattern.ExampleQuestion) && exampleQuestion == null))
            {
                leftOut.Add(pattern.Id);
            }

            forDoc.Add(new LearnedPatternForDoc(
                pattern.PatternType.ToString(), pattern.TableName, pattern.ColumnName,
                pattern.PatternContent, exampleQuestion, string.IsNullOrEmpty(exampleSql) ? null : exampleSql));
        }

        if (leftOut.Count > 0)
        {
            logger.LogWarning(
                "{Count} learned-pattern example(s) of project {ProjectId} were left out of the documentation prompt: they hold a comment that cannot be removed or characters that do not show (patterns {PatternIds})",
                leftOut.Count,
                projectId,
                string.Join(",", leftOut));
        }

        return forDoc;
    }

    // --- Enum/Status Discovery ---

    /// <summary>The most frequent values discovery reads per column; a statement reads at most this many per column.</summary>
    internal const int EnumValuesPerColumn = 50;

    private static readonly HashSet<string> EnumNameHints = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "type", "kind", "category", "level", "state", "role",
        "priority", "flag", "code", "tier", "grade", "phase", "mode", "stage", "class"
    };

    private static readonly HashSet<string> IntegerTypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "int", "integer", "smallint", "tinyint", "bigint",
        "int2", "int4", "int8", "number", "numeric"
    };

    private static readonly HashSet<string> StringTypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "varchar", "nvarchar", "char", "nchar", "text",
        "character varying", "character", "bpchar"
    };

    private static bool IsEnumCandidate(ColumnDetail col)
    {
        if (col.IsPrimaryKey || col.ForeignKeyTable != null)
        {
            return false;
        }

        var baseType = col.DataType.Split('(')[0].Trim().ToLowerInvariant();

        if (IntegerTypeNames.Contains(baseType))
        {
            return true;
        }

        if (StringTypeNames.Contains(baseType))
        {
            return col.MaxLength is null or <= 50;
        }

        return false;
    }

    private static bool HasEnumNameHint(string columnName)
    {
        return EnumNameHints.Any(hint =>
            columnName.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<List<EnumColumnValues>> DiscoverEnumValuesAsync(
        int projectId, List<int> dataSourceIds, List<TableInfo> tables, CancellationToken ct)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        var dataSources = await ctx.DataSources
            .Where(x => dataSourceIds.Contains(x.Id))
            .Where(x => x.DataSourceType == DataSourceType.Database)
            .Where(x => x.DatabaseEngineType != null)
            .ToListAsync(ct);

        if (dataSources.Count == 0)
        {
            return [];
        }

        var tasks = dataSources.Select(async ds =>
        {
            try
            {
                // Discovered values are row data that goes into the documentation prompts: read only while sample-value
                // collection is on for the project and every live project that links the source (the deployment lock
                // Beacon:Mcp:ForbidRowDataToLlm pins it off), screened with their custom PII patterns.
                var settings = await sharedSourceSettings.GetEffectiveSettingsAsync(projectId, [ds.Id], ct);
                if (!settings.EnableSampleValueCollection)
                {
                    return (Values: new List<EnumColumnValues>(), SamplingOff: true);
                }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

                return (Values: await DiscoverEnumValuesForDataSourceAsync(ds, tables, settings.CustomPiiPatterns, timeoutCts.Token, ct), SamplingOff: false);
            }
            catch (Exception ex)
            {
                // The type and error code only (§1.11): a message can quote row values.
                logger.LogWarning(
                    "Enum value discovery failed for data source {DataSourceId}: {Failure}",
                    ds.Id,
                    FailureCodes.Describe(ex));

                return (Values: new List<EnumColumnValues>(), SamplingOff: false);
            }
        });

        var results = await Task.WhenAll(tasks);

        // Once per run, so the documentation that lists no values for these sources is explained.
        var samplingOff = dataSources
            .Zip(results)
            .Where(x => x.Second.SamplingOff)
            .Select(x => x.First.Id)
            .Order()
            .ToList();
        if (samplingOff.Count > 0)
        {
            logger.LogInformation(
                "Enum value discovery for project {ProjectId} lists no values of data sources {DataSourceIds}: sample-value collection is off for them",
                projectId,
                samplingOff);
        }

        return results
            .SelectMany(x => x.Values)
            .ToList();
    }

    /// <summary>
    /// Samples the distinct values of each table's likely enum columns. The SQL is built from the stored catalog, whose names
    /// come from the connected database: a table or column whose name carries quote, bracket, semicolon or control
    /// characters is skipped, every name is quoted in the engine's own way, and a column is identified in the result by its
    /// position, never by a literal of its name. Each statement passes the read-only execution gate — capped at
    /// <see cref="EnumValuesPerColumn"/> rows per column — and runs through the provider's read-only path (§1.5/§1.10), in
    /// the execution log before it is sent (<see cref="ISqlExecutionAudit"/>). A
    /// table that is skipped is logged by name with the reason, and every run ends with how many tables were discovered.
    /// A column whose name <see cref="ColumnValueExclusions"/> leaves out (PII, secret-like, a financial amount) is not
    /// read, and a column one of whose values looks like PII or a credential is dropped.
    /// </summary>
    private async Task<List<EnumColumnValues>> DiscoverEnumValuesForDataSourceAsync(
        DataSource dataSource, List<TableInfo> tables, IReadOnlyList<string> customPiiPatterns, CancellationToken ct, CancellationToken callerToken)
    {
        var provider = dataSourceProviderFactory.GetProvider(dataSource.DataSourceType);
        var engineType = dataSource.DatabaseEngineType!.Value;
        var dialect = DataSourceSqlDialect.Of(dataSource);
        var configuredDatabase = configuredDatabases.Resolve(dataSource);
        var rowSecurity = await rowSecurityResolver.ResolveAsync(dataSource.Id, ct);
        var sourceTables = tables
            .Where(x => x.DataSourceId == dataSource.Id)
            .ToList();
        var result = new List<EnumColumnValues>();
        var attempted = 0;
        var discovered = 0;

        try
        {
            foreach (var table in sourceTables)
            {
                if (SqlIdentifierGuard.HasUnsafeCharacters(table.SchemaName) || SqlIdentifierGuard.HasUnsafeCharacters(table.TableName))
                {
                    LogEnumDiscoverySkip(dataSource, table, "its name carries a quote, bracket, semicolon or control character");
                    continue;
                }

                var candidates = table.Columns
                    .Where(IsEnumCandidate)
                    .Where(x => !SqlIdentifierGuard.HasUnsafeCharacters(x.Name))
                    .Where(x => !ColumnValueExclusions.IsExcludedColumn(guardrail, x.Name, customPiiPatterns))
                    .OrderByDescending(x => HasEnumNameHint(x.Name))
                    .Take(5)
                    .Select(x => x.Name)
                    .ToList();

                if (candidates.Count == 0)
                {
                    continue;
                }

                attempted++;
                var sql = BuildEnumDiscoveryQuery(engineType, table.SchemaName, table.TableName, candidates);
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
                    MaxRows: candidates.Count * EnumValuesPerColumn,
                    rowSecurity)
                {
                    HostManagedKey = dataSource.HostManagedKey,
                    ConfiguredDatabase = configuredDatabase
                });

                if (report.Blocked)
                {
                    LogEnumDiscoverySkip(dataSource, table, "the execution gate refused its statement");
                    continue;
                }

                // Each statement is in the execution log before it runs: it reads real column values.
                var finalSql = report.FinalSql;
                Task<ProviderQueryResult> Read(CancellationToken token) =>
                    provider.ExecuteReadOnlyQueryAsync(dataSource, finalSql, new Dictionary<string, object?>(), token);
                var queryResult = executionAudit == null
                    ? await Read(ct)
                    : await executionAudit.RunAsync(
                        new SqlExecutionStart(dataSource.Id, finalSql, SqlExecutionContexts.DocumentationEnumDiscovery),
                        Read,
                        ct,
                        callerToken);

                if (!queryResult.Success)
                {
                    // The server's error text can quote values (§1.11): the table and the reason class only.
                    LogEnumDiscoverySkip(dataSource, table, "its statement failed on the data source");
                    continue;
                }

                discovered++;
                var grouped = queryResult.Rows
                    .Where(x => x.GetValueOrDefault("val") != null)
                    .GroupBy(x => ColumnAt(candidates, x.GetValueOrDefault("col")))
                    .Where(x => x.Key != null)
                    .Where(x => x.Count() <= 25);

                foreach (var group in grouped)
                {
                    var values = group
                        .Select(x =>
                        {
                            var rawVal = x.GetValueOrDefault("val")?.ToString() ?? "NULL";
                            var val = rawVal.Length > 100 ? rawVal[..100] + "..." : rawVal;
                            var cnt = long.TryParse(x.GetValueOrDefault("cnt")?.ToString(), out var c) ? c : 0;

                            return new EnumValueCount(val, cnt);
                        })
                        .OrderByDescending(x => x.Count)
                        .ToList();

                    // A column one of whose values looks like PII or a credential lists none of them.
                    var screened = values
                        .Select(x => x.Value)
                        .ToList();
                    if (ColumnValueExclusions.ContainsExcludedValue(screened, customPiiPatterns))
                    {
                        continue;
                    }

                    result.Add(new EnumColumnValues(
                        dataSource.Id, table.SchemaName, table.TableName, group.Key!, values));
                }
            }
        }
        finally
        {
            logger.LogInformation(
                "Enum discovery of data source {DataSourceId}: {DiscoveredCount} of {TableCount} tables discovered",
                dataSource.Id,
                discovered,
                attempted);
        }

        return result;
    }

    private void LogEnumDiscoverySkip(DataSource dataSource, TableInfo table, string reason)
    {
        logger.LogWarning(
            "Enum discovery skipped table {Table} of data source {DataSourceId}: {Reason}",
            SqlIdentifierGuard.ForLog($"{table.SchemaName}.{table.TableName}"),
            dataSource.Id,
            reason);
    }

    /// <summary>
    /// The discovery statement for <paramref name="columnNames"/> of one table: per column, its
    /// <see cref="EnumValuesPerColumn"/> most frequent values
    /// (<c>col</c> is the column's position in <paramref name="columnNames"/>), each branch a derived table so the
    /// branches' own ORDER BY and LIMIT/TOP are valid inside UNION ALL. Names are quoted for the engine
    /// (<see cref="SqlIdentifierGuard.Quote"/>); callers pass only names without quote, bracket, semicolon or control
    /// characters. Internal for unit tests.
    /// </summary>
    internal static string BuildEnumDiscoveryQuery(
        DatabaseEngineType engineType, string schemaName, string tableName, IReadOnlyList<string> columnNames)
    {
        var dialect = engineType.ToString();
        var qualifiedTable = $"{SqlIdentifierGuard.Quote(schemaName, dialect)}.{SqlIdentifierGuard.Quote(tableName, dialect)}";
        var usesTop = engineType is DatabaseEngineType.MSSQL or DatabaseEngineType.AzureSynapse;
        var castType = engineType switch
        {
            DatabaseEngineType.MSSQL or DatabaseEngineType.AzureSynapse => "VARCHAR(MAX)",
            DatabaseEngineType.MySQL => "CHAR",
            _ => "TEXT"
        };

        var parts = columnNames.Select((name, index) =>
        {
            var column = SqlIdentifierGuard.Quote(name, dialect);
            var selectClause = usesTop ? $"SELECT TOP {EnumValuesPerColumn}" : "SELECT";
            var branch = $"{selectClause} {index} AS col, CAST({column} AS {castType}) AS val, COUNT(*) AS cnt " +
                         $"FROM {qualifiedTable} " +
                         $"GROUP BY {column} " +
                         $"ORDER BY cnt DESC" +
                         (usesTop ? "" : $" LIMIT {EnumValuesPerColumn}");

            return $"SELECT * FROM ({branch}) AS enum_{index}";
        });

        return string.Join(" UNION ALL ", parts);
    }

    // The candidate a result row's position marker names, or null for a marker that is not one of them.
    private static string? ColumnAt(IReadOnlyList<string> candidates, object? marker)
    {
        return int.TryParse(marker?.ToString(), out var index) && index >= 0 && index < candidates.Count
            ? candidates[index]
            : null;
    }

    // --- HTML Conversion ---

    private static readonly MarkdownPipeline MarkdownPipelineInstance = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseGridTables()
        .UseEmphasisExtras()
        .UseTaskLists()
        .UseAutoLinks()
        .UseListExtras()
        .UseAutoIdentifiers()
        .UseFootnotes()
        .UseDefinitionLists()
        .DisableHtml()
        .Build();

    // Only the extensions the docs use: no generic attributes (they would put attacker-chosen attributes on
    // elements) and no media links (they would embed iframes).
    private static string? SafeUrl(string? url)
    {
        var trimmed = new string((url ?? "").Where(x => x > ' ' && x != '\u007f').ToArray());
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
        {
            return url;
        }

        if (trimmed.StartsWith("//") || trimmed.Contains('\\'))
        {
            return null;
        }

        var colon = trimmed.IndexOf(':');
        var firstSeparator = trimmed.IndexOfAny(['/', '?', '#']);
        if (colon < 0 || (firstSeparator >= 0 && firstSeparator < colon))
        {
            return url;
        }

        var scheme = trimmed[..colon];
        return scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
               || scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
               || scheme.Equals("mailto", StringComparison.OrdinalIgnoreCase)
            ? url
            : null;
    }

    internal static string ConvertToHtml(string markdown)
    {
        var document = Markdig.Markdown.Parse(markdown, MarkdownPipelineInstance);
        foreach (var link in document.Descendants<LinkInline>().ToList())
        {
            var safe = SafeUrl(link.GetDynamicUrl?.Invoke() ?? link.Url);
            if (safe == null)
            {
                link.GetDynamicUrl = null;
                link.Url = "#";
            }
        }

        foreach (var autolink in document.Descendants<AutolinkInline>().ToList())
        {
            if (SafeUrl(autolink.Url) == null)
            {
                autolink.ReplaceBy(new LiteralInline(autolink.Url));
            }
        }

        var body = Markdig.Markdown.ToHtml(document, MarkdownPipelineInstance);

        return "<!DOCTYPE html>\n" +
               "<html lang=\"en\">\n" +
               "<head>\n" +
               "    <meta charset=\"UTF-8\" />\n" +
               "    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\" />\n" +
               "    <meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; img-src https:\" />\n" +
               "    <title>Project Documentation</title>\n" +
               "    <style>\n" +
               "        body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; max-width: 1200px; margin: 0 auto; padding: 2rem; color: #1a1a1a; }\n" +
               "        h1 { border-bottom: 2px solid #0066cc; padding-bottom: 0.5rem; }\n" +
               "        h2 { border-bottom: 1px solid #ddd; padding-bottom: 0.3rem; margin-top: 2rem; }\n" +
               "        h3 { margin-top: 1.5rem; }\n" +
               "        table { border-collapse: collapse; width: 100%; margin: 1rem 0; }\n" +
               "        th, td { border: 1px solid #ddd; padding: 0.5rem 0.75rem; text-align: left; }\n" +
               "        th { background-color: #f5f5f5; font-weight: 600; }\n" +
               "        tr:nth-child(even) { background-color: #fafafa; }\n" +
               "        code { background-color: #f0f0f0; padding: 0.1rem 0.3rem; border-radius: 3px; font-family: monospace; }\n" +
               "        pre { background-color: #f0f0f0; padding: 1rem; border-radius: 4px; overflow-x: auto; }\n" +
               "        pre code { background: none; padding: 0; }\n" +
               "        blockquote { border-left: 3px solid #0066cc; margin: 0; padding: 0.5rem 1rem; color: #555; }\n" +
               "    </style>\n" +
               "</head>\n" +
               "<body>\n" +
               body +
               "\n</body>\n</html>";
    }

    // --- Context Models ---

    internal sealed class DocumentationContext
    {
        public string ProjectName { get; init; } = null!;
        public string? ProjectDescription { get; init; }
        public List<DataSourceInfo> DataSources { get; init; } = new();
        public List<DataSourceInfo> ApiDataSources => DataSources.Where(ds => ds.Type == DataSourceType.Api).ToList();
        public List<TableInfo> Tables { get; init; } = new();
        public List<CodeRefInfo> CodeReferences { get; init; } = new();
        public List<QualityScoreInfo> QualityScores { get; init; } = new();
        public List<ContractInfo> Contracts { get; init; } = new();
        public List<RepoInfo> Repositories { get; init; } = new();

        public List<LearnedPatternForDoc> LearnedPatterns { get; init; } = new();
        public List<EnumColumnValues> EnumValues { get; init; } = new();

        public int DataSourceCount => DataSources.Count;
        public int TableCount => Tables.Count;
        public int CodeReferenceCount => CodeReferences.Count;

        public List<EnumColumnValues> GetEnumValuesForTable(string schemaName, string tableName) =>
            EnumValues
                .Where(x => x.SchemaName == schemaName)
                .Where(x => x.TableName == tableName)
                .ToList();
    }

    internal sealed record LearnedPatternForDoc(
        string PatternType, string TableName, string? ColumnName,
        string Content, string? ExampleQuestion, string? ExampleSql);

    internal sealed record DataSourceInfo(int Id, string Name, DataSourceType Type, DatabaseEngineType? EngineType);
    internal sealed record RepoInfo(string Url, string Branch, int FilesScanned, int ReferencesFound);

    internal sealed class TableInfo
    {
        public int DataSourceId { get; init; }
        public string DataSourceName { get; init; } = null!;
        public string SchemaName { get; init; } = null!;
        public string TableName { get; init; } = null!;
        public string? Description { get; init; }
        public List<ColumnDetail> Columns { get; init; } = new();
        public List<IndexDetail> Indexes { get; init; } = new();
    }

    internal sealed record ColumnDetail(
        string Name, string DataType, bool IsNullable, bool IsPrimaryKey,
        string? ForeignKeyTable, string? ForeignKeyColumn, string? Description,
        int? MaxLength);

    internal sealed record IndexDetail(string Name, string Columns, bool IsUnique);

    internal sealed record CodeRefInfo(
        string FilePath, int? LineNumber, CodeReferenceType ReferenceType,
        string? SchemaName, string? TableName, string? ColumnName,
        string? CodeSnippet, string? ClassName, string? MethodName);

    internal sealed record QualityScoreInfo(
        int DataSourceId, string SchemaName, string TableName, double Score, DataQualityTrendDirection Trend);

    internal sealed record ContractInfo(
        int DataSourceId, string SchemaName, string TableName, List<ContractRuleInfo> Rules);

    internal sealed record ContractRuleInfo(
        DataContractRuleType RuleType, string? ColumnName, string? Configuration);

    internal sealed record EnumColumnValues(
        int DataSourceId, string SchemaName, string TableName,
        string ColumnName, List<EnumValueCount> Values);

    internal sealed record EnumValueCount(string Value, long Count);

}
