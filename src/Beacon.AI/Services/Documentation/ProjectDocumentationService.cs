using System.Diagnostics;
using System.Globalization;
using System.Text;
using Markdig;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Helpers;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;

namespace Beacon.AI.Services.Documentation;

internal sealed class ProjectDocumentationService(
    IDbContextFactory<BeaconContext> contextFactory,
    ILlmProvider llmProvider,
    IDataSourceProviderFactory dataSourceProviderFactory,
    ISqlExecutionGate gate,
    IConfiguredDatabaseResolver configuredDatabases,
    ISharedSourceSettings sharedSourceSettings,
    IQueryGuardrailService guardrail,
    ILlmRowDataEgress rowDataEgress,
    ILogger<ProjectDocumentationService> logger) : IProjectDocumentationService
{
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
            (ProjectDocSectionType.ProjectOverview, "Project Overview", 0, BuildProjectOverviewPrompt, false),
            (ProjectDocSectionType.BusinessDomains, "Business Domains", 1, BuildBusinessDomainsPrompt, true),
            (ProjectDocSectionType.DataModel, "Data Model", 2, BuildDataModelPrompt, true),
            (ProjectDocSectionType.DataFlows, "Data Flows", 3, BuildDataFlowsPrompt, false),
            (ProjectDocSectionType.CodeLineage, "Code Lineage", 4, BuildCodeLineagePrompt, false),
            (ProjectDocSectionType.DataQuality, "Data Quality", 5, BuildDataQualityPrompt, false),
        };

        // Add API documentation pass only if there are API data sources
        var hasApiSources = docContext.ApiDataSources.Count > 0;

        var tasks = parallelPasses.Select(async pass =>
        {
            try
            {
                var userPrompt = pass.BuildPrompt(docContext);
                var response = await CallLlmAsync(pass.ListsColumnValues ? columnValuesProvider : llmProvider, GetSystemPrompt(pass.Type), userPrompt, ct);
                return (pass.Type, pass.Title, pass.SortOrder, Content: response.Content,
                    response.InputTokens, response.OutputTokens, response.Cost, response.Model, Error: (string?)null);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Documentation generation failed for section {SectionType} ({Failure})", pass.Type, FailureCodes.Describe(ex));
                return (pass.Type, pass.Title, pass.SortOrder,
                    Content: $"*Generation failed for this section: {ex.Message}. Please regenerate.*",
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

        // Pass 8: Glossary — runs after others so it can reference their content
        try
        {
            var glossaryPrompt = BuildGlossaryPrompt(docContext, sections);
            var glossaryResponse = await CallLlmAsync(columnValuesProvider, GetSystemPrompt(ProjectDocSectionType.Glossary), glossaryPrompt, ct);
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
            sections.Add(new ProjectDocumentationSection
            {
                SectionType = ProjectDocSectionType.Glossary,
                Title = "Glossary",
                Content = $"*Generation failed for this section: {ex.Message}. Please regenerate.*",
                SortOrder = 7
            });
        }

        sw.Stop();

        // Persist
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var documentation = new ProjectDocumentation
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
            Sections = sections
        };

        context.ProjectDocumentations.Add(documentation);
        await context.SaveChangesAsync(ct);

        logger.LogInformation(
            "Project documentation {DocId} generated for Project {ProjectId} in {Duration:F1}s ({Sections} sections, {Tokens} tokens)",
            documentation.Id, projectId, sw.Elapsed.TotalSeconds, sections.Count, totalInput + totalOutput);

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

    // --- Section helpers ---

    // Generates the API-documentation section. Kept as a named method so the caller
    // can `tasks.Add(GenerateApiDocumentationAsync(...))` without wrapping in Task.Run,
    // which would otherwise queue an extra thread-pool item for an I/O-bound call.
    private async Task<(ProjectDocSectionType Type, string Title, int SortOrder, string Content, int InputTokens, int OutputTokens, decimal Cost, string Model, string? Error)>
        GenerateApiDocumentationAsync(DocumentationContext docContext, CancellationToken ct)
    {
        try
        {
            var userPrompt = BuildApiDocumentationPrompt(docContext);
            var response = await CallLlmAsync(llmProvider, GetSystemPrompt(ProjectDocSectionType.ApiDocumentation), userPrompt, ct);
            return (ProjectDocSectionType.ApiDocumentation, "API Documentation", 6,
                Content: response.Content,
                response.InputTokens, response.OutputTokens, response.Cost, response.Model, Error: null);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Documentation generation failed for API Documentation ({Failure})", FailureCodes.Describe(ex));
            return (ProjectDocSectionType.ApiDocumentation, "API Documentation", 6,
                Content: $"*Generation failed for this section: {ex.Message}. Please regenerate.*",
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

    private static async Task<LlmResponse> CallLlmAsync(ILlmProvider provider, string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var request = new LlmRequest
        {
            SystemPrompt = systemPrompt,
            Messages = [new ChatMessage(ConversationRole.User, userPrompt)],
            Temperature = 0.3m,
            MaxTokens = 8192
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

    // --- System Prompts ---

    private static string GetSystemPrompt(ProjectDocSectionType sectionType) => sectionType switch
    {
        ProjectDocSectionType.ProjectOverview => """
            You are a senior technical writer creating project documentation.
            Generate a comprehensive project overview section in Markdown.

            Include:
            - A 2-4 paragraph executive summary of what this project does
            - The tech stack and data infrastructure
            - Key data sources and their roles
            - GitHub repositories and their purpose
            - Overall health and quality posture

            Be specific and reference actual table names, repository names, and data source names from the provided context.
            Do NOT use generic filler text. Every sentence should be grounded in the actual project data.
            Output only the Markdown content for this section, no section header (it will be added automatically).
            """,

        ProjectDocSectionType.BusinessDomains => """
            You are a senior technical writer and business analyst creating project documentation.
            Analyze the database schema, code references, and relationships to identify logical business domains.

            For each business domain you identify:
            1. **Domain Name** — a clear, descriptive name (e.g., "User Management", "Order Processing", "Payment System")
            2. **Description** — 2-3 paragraphs explaining what this domain handles, the business processes it supports, and how it fits into the overall system
            3. **Tables** — which database tables belong to this domain and their roles
            4. **Services** — which code classes/services implement this domain (from code references)
            5. **Business Process Flow** — a Mermaid flowchart diagram showing the key business process for this domain

            Format the Mermaid diagrams like:
            ```mermaid
            flowchart TD
                A[Step 1] --> B[Step 2]
                B --> C{Decision}
                C -->|Yes| D[Step 3a]
                C -->|No| E[Step 3b]
            ```

            CRITICAL Mermaid syntax rules:
            - Node IDs must use only letters, numbers, and underscores — no spaces or special characters.
            - Text inside brackets `[]`, braces `{}`, or parentheses `()` must not contain unescaped special mermaid characters. Use `#quot;` for quotes if needed.
            - Keep diagrams to 15 nodes or fewer. If a process is complex, show the high-level flow only.

            Group ALL tables into domains. If a table doesn't clearly fit, create an "Infrastructure / Shared" domain.
            Be thorough — analyze FK relationships to understand how domains connect.
            Reference actual table names, column names, and class names from the context.
            Output only the Markdown content for this section, no section header.
            """,

        ProjectDocSectionType.DataModel => """
            You are a senior data architect creating comprehensive data model documentation.

            For each logical group of related tables (using FK relationships to group them):
            1. **Group overview** — what this group of tables represents and its purpose
            2. **Entity-Relationship Diagram** — a Mermaid ER diagram showing the tables in this group with their key columns and relationships
            3. **Per-table documentation**:
               - Purpose and business meaning
               - Key columns with their business significance (not just technical type — explain WHAT the data means)
               - Relationships explained in business terms
               - Data patterns and constraints
               - Example use cases

            Format ER diagrams like:
            ```mermaid
            erDiagram
                ORDERS ||--o{ ORDER_ITEMS : contains
                ORDERS {
                    int id PK
                    int customer_id FK
                    datetime created_at
                    string status
                }
            ```

            CRITICAL Mermaid syntax rules for erDiagram:
            - Keep diagrams concise: show ONLY PK, FK columns and 2-3 key business columns per table. Document remaining columns in the per-table section below, NOT in the diagram.
            - If a group has more than 8 tables, split into multiple smaller diagrams or show only relationships without column blocks.
            - Entity names must use only letters, numbers, and underscores — no spaces, dots, or special characters.
            - Relationship labels must be simple quoted strings — no parentheses, colons, or special characters inside quotes.
            - Always close every opening brace `{` with a closing brace `}`.
            - Column type must be a single word (use `string` not `varchar(255)`, use `int` not `integer`).

            Be thorough and specific. Explain columns in business terms, not just their data types.
            If a column name is ambiguous, infer its purpose from context (FK targets, naming patterns, associated code).
            For columns with known enum/status values, document what each value means in business terms.
            Use the provided distinct values and their row counts to infer the meaning of each value.
            Output only the Markdown content for this section, no section header.
            """,

        ProjectDocSectionType.DataFlows => """
            You are a senior data engineer creating data flow documentation.

            Analyze the code references, table relationships, and schema structure to describe how data flows through the system.

            Include:
            1. **Data Ingestion** — how data enters the system (API endpoints, imports, user input). Reference specific code classes that write data.
            2. **Data Processing** — transformations, business logic, calculations. Reference services that process data.
            3. **Data Serving** — how data is read and presented (API endpoints, queries, reports). Reference code that reads data.
            4. **Flow Diagrams** — Mermaid sequence or flowchart diagrams showing key data flows

            Format flow diagrams like:
            ```mermaid
            flowchart LR
                A[API Request] --> B[Service Layer]
                B --> C[(Database)]
                C --> D[Query Handler]
                D --> E[API Response]
            ```

            Or sequence diagrams:
            ```mermaid
            sequenceDiagram
                participant Client
                participant API
                participant Service
                participant Database
                Client->>API: POST /orders
                API->>Service: CreateOrder()
                Service->>Database: INSERT INTO orders
            ```

            CRITICAL Mermaid syntax rules:
            - Participant names and aliases must use only letters, numbers, and underscores.
            - Arrow labels must not contain unescaped special characters like `[`, `]`, `{`, `}`.
            - Keep sequence diagrams to 6 participants and 15 interactions or fewer.
            - For flowcharts, keep to 15 nodes or fewer. Node IDs must be alphanumeric.

            Base everything on actual code references and table names from the context.
            Identify write paths (DapperQuery, RawSql, Migration) vs read paths (EntityModel, ApiEndpoint).
            Output only the Markdown content for this section, no section header.
            """,

        ProjectDocSectionType.CodeLineage => """
            You are a senior software architect documenting code-to-data lineage.

            Create a comprehensive mapping of which code components interact with which data entities.

            Structure the documentation by service/class:
            1. For each major service or class that has code references:
               - **Service name** and its responsibility
               - **Tables it reads** — list with method names and purpose
               - **Tables it writes** — list with method names and purpose
               - **Data dependencies** — what data must exist for this service to function

            Then provide a summary view:
            2. For each table with code references:
               - **Writers** — services/methods that insert or update
               - **Readers** — services/methods that query
               - **Impact analysis** — what would break if this table's schema changed

            Include a Mermaid diagram showing the high-level service-to-table dependency graph.
            Keep the diagram concise (max 20 nodes). Use `graph LR` or `flowchart LR`.
            Node IDs must use only letters, numbers, and underscores. Use subgraphs to group related items.

            Be specific — use actual file paths, class names, method names, and table names from the context.
            Output only the Markdown content for this section, no section header.
            """,

        ProjectDocSectionType.DataQuality => """
            You are a data quality analyst creating a data quality assessment.

            Document the current data quality posture:
            1. **Quality Summary** — overall scores, trends, areas of concern
            2. **Per-table quality** — scores, active contracts, rule types
            3. **Failing or low-score areas** — specific tables or rules with issues, with recommendations
            4. **Quality Coverage** — which tables have data contracts and which don't (coverage gaps)
            5. **Recommendations** — prioritized list of improvements

            Use actual table names, scores, and rule types from the context.
            If quality data is sparse, note the coverage gaps and recommend adding contracts.
            Output only the Markdown content for this section, no section header.
            """,

        ProjectDocSectionType.ApiDocumentation => """
            You are a senior API technical writer creating API documentation.

            For each API data source, document:
            1. **Overview** — what the API does, its purpose
            2. **Endpoints** — grouped by domain/tag:
               - Endpoint name and purpose
               - Response fields with their meanings
               - Business context for when/why to use this endpoint
            3. **Integration patterns** — how the API connects to other data sources in the project

            Use actual endpoint names, field names, and descriptions from the context.
            Output only the Markdown content for this section, no section header.
            """,

        ProjectDocSectionType.Glossary => """
            You are creating a glossary for project documentation.

            Create a comprehensive glossary that maps business terms to technical entities.
            Format as a Markdown table with columns: Term, Definition, Related Tables/Columns, Related Code.

            Include:
            - Business domain terms identified in the documentation
            - Key entity names and what they represent
            - Abbreviations or codes used in column names
            - Status values and enum columns — use the actual distinct values provided to define what each value means in business terms

            Sort alphabetically. Be concise but precise in definitions.
            Output only the Markdown table, no section header.
            """,

        _ => "You are a technical writer. Generate Markdown documentation based on the provided context."
    };

    // --- User Prompt Builders ---

    private static string BuildProjectOverviewPrompt(DocumentationContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Project: {ctx.ProjectName}");
        if (ctx.ProjectDescription != null) sb.AppendLine($"Description: {ctx.ProjectDescription}");
        sb.AppendLine();

        sb.AppendLine("## Data Sources:");
        foreach (var ds in ctx.DataSources)
        {
            var engine = ds.Type == DataSourceType.Api ? ConnectorRegistry.GetDisplayName(DataSourceType.Api) : ds.EngineType?.ToString() ?? "Unknown";
            var tableCount = ctx.Tables.Count(t => t.DataSourceId == ds.Id);
            sb.AppendLine($"- {ds.Name} ({engine}) — {tableCount} tables");
        }

        sb.AppendLine();
        sb.AppendLine("## Repositories:");
        foreach (var repo in ctx.Repositories)
            sb.AppendLine($"- {repo.Url} (branch: {repo.Branch}, files scanned: {repo.FilesScanned}, references found: {repo.ReferencesFound})");

        sb.AppendLine();
        sb.AppendLine($"Total tables: {ctx.TableCount}");
        sb.AppendLine($"Total code references: {ctx.CodeReferenceCount}");

        var avgQuality = ctx.QualityScores.Count > 0 ? ctx.QualityScores.Average(q => q.Score) : (double?)null;
        if (avgQuality.HasValue) sb.AppendLine($"Average quality score: {avgQuality:F0}%");

        sb.AppendLine($"Active data contracts: {ctx.Contracts.Count}");

        return sb.ToString();
    }

    private static string BuildBusinessDomainsPrompt(DocumentationContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Tables and Relationships");
        sb.AppendLine();

        foreach (var group in ctx.Tables.GroupBy(t => t.DataSourceName))
        {
            sb.AppendLine($"## Data Source: {group.Key}");
            foreach (var table in group)
            {
                sb.AppendLine($"### {table.SchemaName}.{table.TableName}");
                if (table.Description != null) sb.AppendLine($"Description: {table.Description}");

                // Key columns with FK relationships
                foreach (var col in table.Columns.Where(c => c.IsPrimaryKey || c.ForeignKeyTable != null))
                {
                    sb.Append($"  - {col.Name} ({col.DataType})");
                    if (col.IsPrimaryKey) sb.Append(" [PK]");
                    if (col.ForeignKeyTable != null) sb.Append($" FK→{col.ForeignKeyTable}.{col.ForeignKeyColumn}");
                    sb.AppendLine();
                }

                // All columns (brief)
                sb.AppendLine($"  Columns: {string.Join(", ", table.Columns.Select(c => c.Name))}");

                var tableEnums = ctx.GetEnumValuesForTable(table.SchemaName, table.TableName);
                if (tableEnums.Count > 0)
                {
                    foreach (var enumCol in tableEnums)
                    {
                        var values = string.Join(", ", enumCol.Values.Select(x => x.Value));
                        sb.AppendLine($"  - {enumCol.ColumnName} values: [{values}]");
                    }
                }
            }
            sb.AppendLine();
        }

        // Code references grouped by class
        sb.AppendLine("# Code References by Class");
        foreach (var classGroup in ctx.CodeReferences.Where(r => r.ClassName != null).GroupBy(r => r.ClassName))
        {
            sb.AppendLine($"## {classGroup.Key}");
            foreach (var refGroup in classGroup.GroupBy(r => r.TableName))
            {
                sb.AppendLine($"  - {refGroup.Key}: {string.Join(", ", refGroup.Select(r => $"{r.ReferenceType}({r.MethodName})"))}");
            }
        }

        return sb.ToString();
    }

    private static string BuildDataModelPrompt(DocumentationContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Full Schema Metadata");
        sb.AppendLine();

        foreach (var group in ctx.Tables.GroupBy(t => t.DataSourceName))
        {
            sb.AppendLine($"## Data Source: {group.Key}");
            foreach (var table in group)
            {
                sb.AppendLine($"### {table.SchemaName}.{table.TableName}");
                if (table.Description != null) sb.AppendLine($"Description: {table.Description}");

                sb.AppendLine("Columns:");
                foreach (var col in table.Columns)
                {
                    sb.Append($"  - {col.Name} {col.DataType}");
                    if (col.IsPrimaryKey) sb.Append(" PK");
                    if (!col.IsNullable) sb.Append(" NOT NULL");
                    if (col.ForeignKeyTable != null) sb.Append($" FK→{col.ForeignKeyTable}.{col.ForeignKeyColumn}");
                    if (col.Description != null) sb.Append($" — {col.Description}");
                    sb.AppendLine();
                }

                if (table.Indexes.Count > 0)
                {
                    sb.AppendLine("Indexes:");
                    foreach (var idx in table.Indexes)
                        sb.AppendLine($"  - {idx.Name} ({idx.Columns}){(idx.IsUnique ? " UNIQUE" : "")}");
                }

                var tableEnums = ctx.GetEnumValuesForTable(table.SchemaName, table.TableName);
                if (tableEnums.Count > 0)
                {
                    sb.AppendLine("Enum/Status Values:");
                    foreach (var enumCol in tableEnums)
                    {
                        var values = string.Join(", ",
                            enumCol.Values.Select(x => $"{x.Value} ({x.Count:N0} rows)"));
                        sb.AppendLine($"  - {enumCol.ColumnName}: [{values}]");
                    }
                }

                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static string BuildDataFlowsPrompt(DocumentationContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Code References (showing how data flows through code)");
        sb.AppendLine();

        // Group by write vs read
        var writes = ctx.CodeReferences
            .Where(r => r.ReferenceType is CodeReferenceType.DapperQuery or CodeReferenceType.RawSql or CodeReferenceType.Migration or CodeReferenceType.StoredProcedureCall)
            .ToList();
        var reads = ctx.CodeReferences
            .Where(r => r.ReferenceType is CodeReferenceType.EntityModel or CodeReferenceType.ApiEndpoint or CodeReferenceType.DbContextConfiguration)
            .ToList();

        sb.AppendLine("## Write Operations (data ingestion/processing)");
        foreach (var group in writes.GroupBy(r => r.TableName ?? "unknown"))
        {
            sb.AppendLine($"### Table: {group.Key}");
            foreach (var r in group.Take(10))
            {
                sb.AppendLine($"  - {r.ClassName}.{r.MethodName} ({r.ReferenceType}) — {r.FilePath}:{r.LineNumber}");
                if (r.CodeSnippet != null) sb.AppendLine($"    ```{r.CodeSnippet}```");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Read Operations (data serving)");
        foreach (var group in reads.GroupBy(r => r.TableName ?? "unknown"))
        {
            sb.AppendLine($"### Table: {group.Key}");
            foreach (var r in group.Take(10))
            {
                sb.AppendLine($"  - {r.ClassName}.{r.MethodName} ({r.ReferenceType}) — {r.FilePath}:{r.LineNumber}");
                if (r.CodeSnippet != null) sb.AppendLine($"    ```{r.CodeSnippet}```");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## FK Relationships (data dependencies)");
        foreach (var table in ctx.Tables)
        {
            var fks = table.Columns.Where(c => c.ForeignKeyTable != null).ToList();
            if (fks.Count > 0)
            {
                sb.AppendLine($"  - {table.SchemaName}.{table.TableName} → {string.Join(", ", fks.Select(c => $"{c.ForeignKeyTable} (via {c.Name})"))}");
            }
        }

        return sb.ToString();
    }

    private static string BuildCodeLineagePrompt(DocumentationContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Code-to-Data References");
        sb.AppendLine();

        // By class/service
        sb.AppendLine("## By Service/Class:");
        foreach (var classGroup in ctx.CodeReferences
            .Where(r => r.ClassName != null)
            .GroupBy(r => r.ClassName!)
            .OrderByDescending(g => g.Count()))
        {
            sb.AppendLine($"### {classGroup.Key}");
            var byTable = classGroup.GroupBy(r => r.TableName ?? "unknown");
            foreach (var tableGroup in byTable)
            {
                var types = string.Join(", ", tableGroup.Select(r => $"{r.ReferenceType}").Distinct());
                var methods = string.Join(", ", tableGroup.Select(r => r.MethodName).Where(m => m != null).Distinct().Take(5));
                sb.AppendLine($"  - {tableGroup.Key}: [{types}] methods: {methods}");
            }
        }

        // By table
        sb.AppendLine();
        sb.AppendLine("## By Table:");
        foreach (var tableGroup in ctx.CodeReferences
            .Where(r => r.TableName != null)
            .GroupBy(r => r.TableName!)
            .OrderByDescending(g => g.Count()))
        {
            sb.AppendLine($"### {tableGroup.Key}");
            var writers = tableGroup.Where(r => r.ReferenceType is CodeReferenceType.DapperQuery or CodeReferenceType.RawSql or CodeReferenceType.Migration or CodeReferenceType.StoredProcedureCall)
                .Select(r => $"{r.ClassName}.{r.MethodName}").Distinct().ToList();
            var readers = tableGroup.Where(r => r.ReferenceType is CodeReferenceType.EntityModel or CodeReferenceType.ApiEndpoint or CodeReferenceType.DbContextConfiguration)
                .Select(r => $"{r.ClassName}.{r.MethodName}").Distinct().ToList();
            if (writers.Count > 0) sb.AppendLine($"  Writers: {string.Join(", ", writers)}");
            if (readers.Count > 0) sb.AppendLine($"  Readers: {string.Join(", ", readers)}");
        }

        return sb.ToString();
    }

    private static string BuildDataQualityPrompt(DocumentationContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Data Quality Information");
        sb.AppendLine();

        if (ctx.QualityScores.Count == 0 && ctx.Contracts.Count == 0)
        {
            sb.AppendLine("No data quality scores or contracts have been configured yet.");
            sb.AppendLine($"Total tables in the project: {ctx.TableCount}");
            return sb.ToString();
        }

        sb.AppendLine("## Quality Scores:");
        foreach (var score in ctx.QualityScores.OrderBy(q => q.Score))
        {
            sb.AppendLine($"  - {score.SchemaName}.{score.TableName}: {score.Score:F0}% (trend: {score.Trend})");
        }

        sb.AppendLine();
        sb.AppendLine("## Data Contracts:");
        foreach (var contract in ctx.Contracts)
        {
            sb.AppendLine($"### {contract.SchemaName}.{contract.TableName}");
            foreach (var rule in contract.Rules)
            {
                sb.Append($"  - {rule.RuleType}");
                if (rule.ColumnName != null) sb.Append($" on {rule.ColumnName}");
                if (rule.Configuration != null) sb.Append($" (config: {rule.Configuration})");
                sb.AppendLine();
            }
        }

        // Tables without quality contracts
        var tablesWithContracts = ctx.Contracts.Select(c => $"{c.SchemaName}.{c.TableName}").ToHashSet();
        var tablesWithout = ctx.Tables
            .Where(t => !tablesWithContracts.Contains($"{t.SchemaName}.{t.TableName}"))
            .Select(t => $"{t.SchemaName}.{t.TableName}")
            .ToList();

        if (tablesWithout.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"## Tables Without Quality Contracts ({tablesWithout.Count}):");
            foreach (var t in tablesWithout.Take(50))
                sb.AppendLine($"  - {t}");
        }

        return sb.ToString();
    }

    private static string BuildApiDocumentationPrompt(DocumentationContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# API Data Sources");
        sb.AppendLine();

        foreach (var apiDs in ctx.ApiDataSources)
        {
            var tables = ctx.Tables.Where(t => t.DataSourceId == apiDs.Id).ToList();
            sb.AppendLine($"## {apiDs.Name}");
            sb.AppendLine();

            foreach (var endpoint in tables)
            {
                sb.AppendLine($"### {endpoint.TableName}");
                if (endpoint.Description != null) sb.AppendLine($"Description: {endpoint.Description}");
                sb.AppendLine("Response fields:");
                foreach (var col in endpoint.Columns)
                {
                    sb.Append($"  - {col.Name} ({col.DataType})");
                    if (col.Description != null) sb.Append($" — {col.Description}");
                    sb.AppendLine();
                }
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static string BuildGlossaryPrompt(DocumentationContext ctx, List<ProjectDocumentationSection> existingSections)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Context for Glossary Generation");
        sb.AppendLine();
        sb.AppendLine("## Table and Column Names:");
        foreach (var table in ctx.Tables.Take(100))
        {
            sb.AppendLine($"- {table.SchemaName}.{table.TableName}: {string.Join(", ", table.Columns.Select(c => c.Name))}");
        }

        if (ctx.EnumValues.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Enum/Status Column Values:");
            foreach (var enumCol in ctx.EnumValues)
            {
                var values = string.Join(", ", enumCol.Values.Select(x => x.Value));
                sb.AppendLine($"- {enumCol.SchemaName}.{enumCol.TableName}.{enumCol.ColumnName}: [{values}]");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Key Terms from Generated Documentation:");
        // Include excerpts from previously generated sections for the glossary to reference
        foreach (var section in existingSections.Where(s => s.Content.Length > 50).Take(4))
        {
            var excerpt = section.Content.Length > 500 ? section.Content[..500] : section.Content;
            sb.AppendLine($"### {section.Title}:");
            sb.AppendLine(excerpt);
            sb.AppendLine();
        }

        return sb.ToString();
    }

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

                return (Values: await DiscoverEnumValuesForDataSourceAsync(ds, tables, settings.CustomPiiPatterns, timeoutCts.Token), SamplingOff: false);
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
    /// <see cref="EnumValuesPerColumn"/> rows per column — and runs through the provider's read-only path (§1.5/§1.10). A
    /// table that is skipped is logged by name with the reason, and every run ends with how many tables were discovered.
    /// A column whose name <see cref="ColumnValueExclusions"/> leaves out (PII, secret-like, a financial amount) is not
    /// read, and a column one of whose values looks like PII or a credential is dropped.
    /// </summary>
    private async Task<List<EnumColumnValues>> DiscoverEnumValuesForDataSourceAsync(
        DataSource dataSource, List<TableInfo> tables, IReadOnlyList<string> customPiiPatterns, CancellationToken ct)
    {
        var provider = dataSourceProviderFactory.GetProvider(dataSource.DataSourceType);
        var engineType = dataSource.DatabaseEngineType!.Value;
        var dialect = DataSourceSqlDialect.Of(dataSource);
        var configuredDatabase = configuredDatabases.Resolve(dataSource);
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
                    MaxRows: candidates.Count * EnumValuesPerColumn)
                {
                    HostManagedKey = dataSource.HostManagedKey,
                    ConfiguredDatabase = configuredDatabase
                });

                if (report.Blocked)
                {
                    LogEnumDiscoverySkip(dataSource, table, "the execution gate refused its statement");
                    continue;
                }

                var queryResult = await provider.ExecuteReadOnlyQueryAsync(
                    dataSource, report.FinalSql, new Dictionary<string, object?>(), ct);

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
        .UseAdvancedExtensions()
        .Build();

    private static string ConvertToHtml(string markdown)
    {

        var body = Markdig.Markdown.ToHtml(markdown, MarkdownPipelineInstance);

        return "<!DOCTYPE html>\n" +
               "<html lang=\"en\">\n" +
               "<head>\n" +
               "    <meta charset=\"UTF-8\" />\n" +
               "    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\" />\n" +
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

    private sealed class DocumentationContext
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

    private sealed record DataSourceInfo(int Id, string Name, DataSourceType Type, DatabaseEngineType? EngineType);
    private sealed record RepoInfo(string Url, string Branch, int FilesScanned, int ReferencesFound);

    private sealed class TableInfo
    {
        public int DataSourceId { get; init; }
        public string DataSourceName { get; init; } = null!;
        public string SchemaName { get; init; } = null!;
        public string TableName { get; init; } = null!;
        public string? Description { get; init; }
        public List<ColumnDetail> Columns { get; init; } = new();
        public List<IndexDetail> Indexes { get; init; } = new();
    }

    private sealed record ColumnDetail(
        string Name, string DataType, bool IsNullable, bool IsPrimaryKey,
        string? ForeignKeyTable, string? ForeignKeyColumn, string? Description,
        int? MaxLength);

    private sealed record IndexDetail(string Name, string Columns, bool IsUnique);

    private sealed record CodeRefInfo(
        string FilePath, int? LineNumber, CodeReferenceType ReferenceType,
        string? SchemaName, string? TableName, string? ColumnName,
        string? CodeSnippet, string? ClassName, string? MethodName);

    private sealed record QualityScoreInfo(
        int DataSourceId, string SchemaName, string TableName, double Score, DataQualityTrendDirection Trend);

    private sealed record ContractInfo(
        int DataSourceId, string SchemaName, string TableName, List<ContractRuleInfo> Rules);

    private sealed record ContractRuleInfo(
        DataContractRuleType RuleType, string? ColumnName, string? Configuration);

    private sealed record EnumColumnValues(
        int DataSourceId, string SchemaName, string TableName,
        string ColumnName, List<EnumValueCount> Values);

    private sealed record EnumValueCount(string Value, long Count);

}
