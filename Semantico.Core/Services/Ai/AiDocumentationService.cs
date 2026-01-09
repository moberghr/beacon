using System.Text;
using System.Text.Json;
using Markdig;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities;
using Semantico.Core.Data.Enums;
using Semantico.Core.Exceptions;
using Semantico.Core.Models;
using Semantico.Core.Models.Ai;
using Semantico.Core.Models.Metadata;
using Semantico.Core.Services.LlmProviders;

namespace Semantico.Core.Services.Ai;

public class AiDocumentationService : IAiDocumentationService
{
    private readonly ILlmProvider _llmProvider;
    private readonly IDatabaseMetadataService _metadataService;
    private readonly IDbContextFactory<SemanticoContext> _contextFactory;
    private readonly ILogger<AiDocumentationService> _logger;

    public AiDocumentationService(
        ILlmProvider llmProvider,
        IDatabaseMetadataService metadataService,
        IDbContextFactory<SemanticoContext> contextFactory,
        ILogger<AiDocumentationService> logger)
    {
        _llmProvider = llmProvider;
        _metadataService = metadataService;
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public async Task<List<DataSourceDocumentation>> GetDocumentationsAsync(
        int dataSourceId,
        CancellationToken cancellationToken = default)
    {
        using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.DataSourceDocumentations
            .Where(d => d.DataSourceId == dataSourceId)
            .OrderByDescending(d => d.GeneratedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<DataSourceDocumentation> GenerateDocumentationAsync(
        int dataSourceId,
        int userId,
        GenerationOptions options,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting documentation generation for DataSource {DataSourceId}", dataSourceId);

        using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        // Fetch data source
        var dataSource = await context.DataSources
            .FirstOrDefaultAsync(ds => ds.Id == dataSourceId, cancellationToken)
            ?? throw new SemanticoException($"DataSource with ID {dataSourceId} not found");

        // Fetch schema metadata
        var metadata = await _metadataService.GetMetadataAsync(dataSourceId, cancellationToken);
        var filteredTables = FilterTables(metadata.Tables.ToList(), options);

        if (filteredTables.Count == 0)
            throw new AiServiceException("No tables found to document");

        // Build AI prompt
        var prompt = BuildSchemaAnalysisPrompt(dataSource.Name, filteredTables, options);

        // Call LLM
        var llmRequest = new LlmRequest
        {
            Messages = new List<ChatMessage>
            {
                new(ConversationRole.User, prompt)
            },
            SystemPrompt = GetDocumentationSystemPrompt(),
            Temperature = options.Temperature,
            MaxTokens = options.MaxTokens
        };

        var response = await _llmProvider.CompleteAsync(llmRequest, cancellationToken);

        // Create documentation entity
        var documentation = new DataSourceDocumentation
        {
            DataSourceId = dataSourceId,
            Title = options.Title ?? $"{dataSource.Name} Documentation",
            GeneratedByModel = response.Model,
            GeneratedAt = DateTime.UtcNow,
            GeneratedByUserId = userId,
            Status = DocumentationStatus.Draft,
            TablesAnalyzed = filteredTables.Count,
            TokensUsed = response.TotalTokens,
            EstimatedCost = response.EstimatedCost,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
            CreatedBy = userId.ToString(),
            ModifiedBy = userId.ToString()
        };

        // Parse AI response and create sections (pass 0 as ID, will be set by EF Core after save)
        var sections = ParseAiResponseToSections(response.Content, 0);
        documentation.Sections = sections;

        // Save to database (EF Core will set the DocumentationId on all sections)
        context.DataSourceDocumentations.Add(documentation);
        await context.SaveChangesAsync(cancellationToken);

        // Track usage
        await TrackUsageAsync(context, dataSourceId, userId, response, OperationType.SchemaAnalysis, cancellationToken);

        _logger.LogInformation("Documentation generated successfully: {DocumentationId}", documentation.Id);

        return documentation;
    }

    public async Task<DataSourceDocumentation> RegenerateDocumentationAsync(
        int documentationId,
        int userId,
        GenerationOptions options,
        CancellationToken cancellationToken = default)
    {
        using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var existing = await context.DataSourceDocumentations
            .Include(d => d.Sections)
            .FirstOrDefaultAsync(d => d.Id == documentationId, cancellationToken)
            ?? throw new SemanticoException($"Documentation with ID {documentationId} not found");

        // Archive old version
        var version = new DocumentationVersion
        {
            DocumentationId = documentationId,
            VersionNumber = await GetNextVersionNumberAsync(context, documentationId, cancellationToken),
            CreatedAt = DateTime.UtcNow,
            CreatedByUserId = userId,
            ChangeDescription = "Regenerated documentation",
            SnapshotJson = JsonSerializer.Serialize(existing.Sections),
            SectionsCount = existing.Sections.Count,
            TokensUsed = existing.TokensUsed
        };

        context.DocumentationVersions.Add(version);
        await context.SaveChangesAsync(cancellationToken);

        // Generate new documentation
        return await GenerateDocumentationAsync(existing.DataSourceId, userId, options, cancellationToken);
    }

    public async Task<DocumentationSection> RegenerateSectionAsync(
        int sectionId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var section = await context.DocumentationSections
            .Include(s => s.Documentation)
            .FirstOrDefaultAsync(s => s.Id == sectionId, cancellationToken)
            ?? throw new SemanticoException($"Section with ID {sectionId} not found");

        // Regenerate specific section logic would go here
        throw new NotImplementedException("Section regeneration not yet implemented");
    }

    public async Task<string> ExportToMarkdownAsync(
        int documentationId,
        CancellationToken cancellationToken = default)
    {
        using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var documentation = await context.DataSourceDocumentations
            .Include(d => d.Sections.OrderBy(s => s.SortOrder))
            .Include(d => d.DataSource)
            .FirstOrDefaultAsync(d => d.Id == documentationId, cancellationToken)
            ?? throw new SemanticoException($"Documentation with ID {documentationId} not found");

        var sb = new StringBuilder();
        sb.AppendLine($"# {documentation.Title}");
        sb.AppendLine();
        sb.AppendLine($"**Generated:** {documentation.GeneratedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"**Model:** {documentation.GeneratedByModel}");
        sb.AppendLine($"**Tables Analyzed:** {documentation.TablesAnalyzed}");
        sb.AppendLine();

        foreach (var section in documentation.Sections)
        {
            sb.AppendLine($"## {section.SectionType}: {section.TableName ?? "Overview"}");
            sb.AppendLine();
            sb.AppendLine(section.GetDisplayContent());
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public async Task<string> ExportToHtmlAsync(
        int documentationId,
        string? customCss = null,
        CancellationToken cancellationToken = default)
    {
        var markdown = await ExportToMarkdownAsync(documentationId, cancellationToken);

        var pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Build();

        var htmlBody = Markdown.ToHtml(markdown, pipeline);

        return WrapInHtmlDocument(htmlBody, customCss);
    }

    public async Task<byte[]> ExportToPdfAsync(
        int documentationId,
        CancellationToken cancellationToken = default)
    {
        using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var documentation = await context.DataSourceDocumentations
            .Include(d => d.Sections.OrderBy(s => s.SortOrder))
            .FirstOrDefaultAsync(d => d.Id == documentationId, cancellationToken)
            ?? throw new SemanticoException($"Documentation with ID {documentationId} not found");

        QuestPDF.Settings.License = LicenseType.Community;

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, Unit.Centimetre);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(x => x.FontSize(11));

                page.Header()
                    .Text(documentation.Title)
                    .SemiBold().FontSize(20).FontColor(Colors.Blue.Medium);

                page.Content()
                    .PaddingVertical(1, Unit.Centimetre)
                    .Column(column =>
                    {
                        column.Spacing(10);

                        foreach (var section in documentation.Sections)
                        {
                            column.Item().Text($"{section.SectionType}: {section.TableName ?? "Overview"}")
                                .FontSize(14).SemiBold();
                            column.Item().Text(section.GetDisplayContent())
                                .FontSize(11);
                        }
                    });

                page.Footer()
                    .AlignCenter()
                    .Text(x =>
                    {
                        x.Span("Page ");
                        x.CurrentPageNumber();
                    });
            });
        }).GeneratePdf();
    }

    // Private helper methods

    private List<TableMetadataDto> FilterTables(
        List<TableMetadataDto> tables,
        GenerationOptions options)
    {
        var filtered = tables.AsEnumerable();

        if (options.SpecificTables?.Any() == true)
        {
            filtered = filtered.Where(t => options.SpecificTables.Contains(t.TableName));
        }

        if (options.ExcludedTables?.Any() == true)
        {
            filtered = filtered.Where(t => !options.ExcludedTables.Contains(t.TableName));
        }

        return filtered.Take(options.MaxTables).ToList();
    }

    private string BuildSchemaAnalysisPrompt(
        string dataSourceName,
        List<TableMetadataDto> tables,
        GenerationOptions options)
    {
        var sb = new StringBuilder();

        // Context section
        sb.AppendLine("# Database Documentation Task");
        sb.AppendLine();
        sb.AppendLine("## Context");
        sb.AppendLine($"- **Database Name:** {dataSourceName}");
        sb.AppendLine($"- **Total Tables:** {tables.Count}");
        sb.AppendLine("- **Objective:** Generate comprehensive documentation for developers and data analysts");
        sb.AppendLine("- **Audience:** Software developers, data engineers, database administrators, and technical stakeholders");
        sb.AppendLine();

        // Database Schema section with detailed metadata
        sb.AppendLine("## Database Schema");
        sb.AppendLine();

        foreach (var table in tables)
        {
            sb.AppendLine($"### Table: `{table.SchemaName}.{table.TableName}`");
            sb.AppendLine();

            // Table description if available
            if (!string.IsNullOrEmpty(table.Description))
            {
                sb.AppendLine($"**Description:** {table.Description}");
                sb.AppendLine();
            }

            sb.AppendLine("**Columns:**");
            sb.AppendLine();

            // Primary keys first
            var primaryKeys = table.Columns.Where(c => c.IsPrimaryKey).ToList();
            if (primaryKeys.Any())
            {
                sb.AppendLine("*Primary Keys:*");
                foreach (var column in primaryKeys)
                {
                    sb.AppendLine($"- **{column.ColumnName}** ({column.DataType}) - PRIMARY KEY {(column.IsNullable ? "NULL" : "NOT NULL")}");
                    if (!string.IsNullOrEmpty(column.DefaultValue))
                    {
                        sb.AppendLine($"  - Default: `{column.DefaultValue}`");
                    }
                }
                sb.AppendLine();
            }

            // Foreign keys
            var foreignKeys = table.Columns.Where(c => c.IsForeignKey).ToList();
            if (foreignKeys.Any())
            {
                sb.AppendLine("*Foreign Keys:*");
                foreach (var column in foreignKeys)
                {
                    var refInfo = !string.IsNullOrEmpty(column.ForeignKeyTable)
                        ? $" → References `{column.ForeignKeyTable}.{column.ForeignKeyColumn}`"
                        : "";
                    sb.AppendLine($"- **{column.ColumnName}** ({column.DataType}){refInfo} {(column.IsNullable ? "NULL" : "NOT NULL")}");
                }
                sb.AppendLine();
            }

            // Regular columns
            var regularColumns = table.Columns.Where(c => !c.IsPrimaryKey && !c.IsForeignKey).ToList();
            if (regularColumns.Any())
            {
                sb.AppendLine("*Columns:*");
                foreach (var column in regularColumns)
                {
                    sb.AppendLine($"- **{column.ColumnName}** ({column.DataType}) {(column.IsNullable ? "NULL" : "NOT NULL")}");
                    if (!string.IsNullOrEmpty(column.DefaultValue))
                    {
                        sb.AppendLine($"  - Default: `{column.DefaultValue}`");
                    }
                }
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private string GetDocumentationSystemPrompt()
    {
        return @"You are an expert Database Administrator and Technical Writer with deep expertise in database design, data modeling, and semantic analysis.

Your task is to analyze the provided database schema and generate thorough, easy-to-understand documentation organized by functional domains.

## Documentation Structure:

### 1. Overview
Write a brief 2-3 sentence summary of:
- The database's overall purpose
- Main business domain (e.g., e-commerce, healthcare, data pipeline)
- Key capabilities

### 2. System Architecture
Create a Mermaid ER diagram showing main entities and relationships:
```mermaid
erDiagram
    CUSTOMER ||--o{ ORDER : places
    ORDER ||--|{ LINE_ITEM : contains
```

### 3. Domain Groups
**IMPORTANT:** Group related tables by their functional domain (e.g., ""User Management"", ""Order Processing"", ""Audit & Logging"").

For EACH domain group, create a section titled: ""### Domain: [Name]"" and structure it as follows:

#### Purpose & Overview
- 2-3 sentences explaining what this domain handles in business terms
- Key capabilities and responsibilities of this domain
- How it fits into the overall system

#### Core Tables & Entities
For each major table in this domain:
- **table_name**
  - **Purpose:** What business concept this represents (1 sentence)
  - **Core Columns:** List 3-5 most important columns with their business meaning
    - `column_name` (type) - What it stores and why it matters
  - **Business Logic:** Key patterns, constraints, or rules evident in the schema
  - **Usage Context:** When/why this table is queried (e.g., ""queried during user login"", ""updated when orders are placed"")

#### Relationships & Data Flow
- How tables within this domain relate to each other
- How this domain connects to other domains
- Data flow patterns (e.g., ""migrationjobs → migration_executions → migration_history"")

#### Example Queries
Provide 2-3 practical SQL queries that demonstrate:
- Common data retrieval patterns for this domain
- Important business operations
- Useful analytical queries

```sql
-- [Clear description of what this query does and when to use it]
SELECT ...
FROM ...
WHERE ...
```

#### Recommendations & Best Practices
- Domain-specific optimization suggestions
- Data quality considerations
- Missing indexes or constraints
- Potential improvements

## Important Guidelines:

1. **Identify 3-5 logical domain groups** based on table naming patterns and relationships
2. **Each domain group gets ONE comprehensive section** (not separate sections for tables/queries/recommendations)
3. **Use this exact header format:** ""### Domain: [Name]"" (e.g., ""### Domain: User Management"")
4. **Follow the structure exactly** - include all subsections (Purpose & Overview, Core Tables & Entities, Relationships & Data Flow, Example Queries, Recommendations)
5. **For each table, include all fields**: Purpose, Core Columns, Business Logic, Usage Context
6. **Keep the main Overview brief** - save details for domain sections
7. **Focus on business meaning** - explain the ""why"" not just the ""what""

## Example Domain Groupings:
- ""User Management"" - users, roles, permissions, sessions
- ""Data Pipeline"" - migration jobs, executions, transformations
- ""Notification System"" - recipients, notifications, subscriptions
- ""Audit & Monitoring"" - logs, metrics, history tables

## Example Domain Section Format:

### Domain: Data Migration System

#### Purpose & Overview
Orchestrates ETL processes for moving data between databases with transformation capabilities, retry logic, and comprehensive execution tracking. Enables data warehouse loading, cross-system synchronization, and database migration projects with full audit trails.

#### Core Tables & Entities
- **migration_jobs**
  - **Purpose:** Defines reusable migration templates with source/destination configuration and transformation logic
  - **Core Columns:**
    - `name` (text) - Human-readable job identifier for management and reporting
    - `source_data_source_id` (int) - Origin database connection for data extraction
    - `destination_data_source_id` (int) - Target database for data loading
    - `transformation_script` (text) - SQL/JavaScript logic for data manipulation during migration
    - `schedule_cron` (text) - Cron expression for automated execution timing
  - **Business Logic:** Supports full refresh, incremental, and upsert modes; includes retry mechanism with max_retries and retry_attempt fields
  - **Usage Context:** Queried when scheduling ETL pipelines, referenced during manual migration execution, analyzed for job performance metrics

- **migration_executions**
  - **Purpose:** Tracks individual execution attempts with detailed metrics and status
  - **Core Columns:**
    - `migration_job_id` (int) - Links to parent job definition
    - `status` (enum) - Execution state: pending/running/completed/failed
    - `source_rows_read` (int) - Input data volume for validation and auditing
    - `destination_rows_written` (int) - Output data volume to verify completeness
    - `parent_execution_id` (int) - Self-referencing for retry tracking
  - **Business Logic:** Self-referencing parent_execution_id enables retry chain tracking; row-level metrics enable data quality validation
  - **Usage Context:** Monitored during active migrations, queried for success rate analysis, used in alerting for failed jobs

#### Relationships & Data Flow
- migration_jobs (1) → migration_executions (many) - One job template spawns multiple execution attempts
- migration_executions self-reference via parent_execution_id - Tracks retry chains for resilience analysis
- Both tables link to data_sources for source/destination connection details
- Data flow: Job definition → Execution attempt → Row-level metrics → Completion status

#### Example Queries

```sql
-- Find failing migrations that need attention
SELECT mj.name, COUNT(*) as failure_count, MAX(me.completed_at) as last_failure
FROM migration_jobs mj
JOIN migration_executions me ON mj.id = me.migration_job_id
WHERE me.status = 'failed'
  AND me.completed_at > NOW() - INTERVAL '7 days'
GROUP BY mj.id, mj.name
HAVING COUNT(*) > 3
ORDER BY failure_count DESC;

-- Calculate success rate and average duration per job
SELECT mj.name,
       COUNT(CASE WHEN me.status = 'completed' THEN 1 END) * 100.0 / COUNT(*) as success_rate,
       AVG(EXTRACT(EPOCH FROM (me.completed_at - me.started_at))) as avg_duration_seconds
FROM migration_jobs mj
JOIN migration_executions me ON mj.id = me.migration_job_id
WHERE me.parent_execution_id IS NULL  -- Only count original attempts
GROUP BY mj.id, mj.name;
```

#### Recommendations & Best Practices
- Add index on migration_executions(status, completed_at) for monitoring queries
- Consider partitioning migration_executions by date for long-term history management
- Implement alerting on consecutive failures using parent_execution_id chain
- Store transformation_script in version control system, reference by hash in migration_jobs
- Add data quality checks: compare source_rows_read vs destination_rows_written for completeness validation

## Tone & Style:
- Professional and technical, but accessible
- Explain complex concepts clearly
- Use business language when describing purpose
- Use technical language when describing implementation
- Be specific and actionable in recommendations

Focus on revealing the 'why' behind the schema, not just describing the 'what'.";
    }

    private List<DocumentationSection> ParseAiResponseToSections(string aiContent, int documentationId)
    {
        if (string.IsNullOrWhiteSpace(aiContent))
            return new List<DocumentationSection>();

        var sections = new List<DocumentationSection>();
        var sortOrder = 1;

        // Split by top-level headers (### or ##)
        var lines = aiContent.Split('\n');
        var currentSection = new StringBuilder();
        SectionType currentType = SectionType.Overview;
        string? currentTableName = null;
        bool hasSeenHeader = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // Detect section headers (### Section Name or ## Section Name)
            if (line.StartsWith("### ") || (line.StartsWith("## ") && !line.StartsWith("###")))
            {
                // Save previous section if it has content
                if (currentSection.Length > 0)
                {
                    var content = currentSection.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        sections.Add(CreateSection(currentType, currentTableName, content, sortOrder++));
                    }
                    currentSection.Clear();
                }

                // Determine section type from header
                var headerText = line.StartsWith("### ") ? line.Substring(4).Trim() : line.Substring(3).Trim();
                // Remove numbered prefixes like "1. " or "2. "
                headerText = System.Text.RegularExpressions.Regex.Replace(headerText, @"^\d+\.\s*", "");
                (currentType, currentTableName) = DetermineSectionType(headerText);
                hasSeenHeader = true;
            }
            else
            {
                currentSection.AppendLine(line);
            }
        }

        // Add final section
        if (currentSection.Length > 0)
        {
            var content = currentSection.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(content))
            {
                sections.Add(CreateSection(currentType, currentTableName, content, sortOrder++));
            }
        }

        // If no sections were parsed (no headers found), create a single overview section
        if (sections.Count == 0 && !string.IsNullOrWhiteSpace(aiContent))
        {
            sections.Add(CreateSection(SectionType.Overview, null, aiContent.Trim(), 1));
        }

        return sections;
    }

    private DocumentationSection CreateSection(
        SectionType sectionType,
        string? tableName,
        string content,
        int sortOrder)
    {
        // Generate a title based on section type and table name
        string? title = GenerateSectionTitle(sectionType, tableName);

        return new DocumentationSection
        {
            // Don't set DocumentationId - EF Core will set it via navigation property
            Title = title,
            SectionType = sectionType,
            TableName = tableName,
            SortOrder = sortOrder,
            AiGeneratedContent = content ?? string.Empty,
            IsUserEdited = false,
            ContentFormat = ContentFormat.Markdown,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
            CreatedBy = "system",
            ModifiedBy = "system"
        };
    }

    private string? GenerateSectionTitle(SectionType sectionType, string? tableName)
    {
        return sectionType switch
        {
            SectionType.Overview => "Overview",
            SectionType.Architecture => "System Architecture",
            SectionType.TableDetail when !string.IsNullOrEmpty(tableName) => tableName, // Domain names or table names as-is
            SectionType.TableDetail => "Key Entities & Tables",
            SectionType.Relationships => "Relationships",
            SectionType.BestPractices => "Data Patterns & Recommendations",
            SectionType.UsageExamples => "Example SQL Queries",
            _ => sectionType.ToString()
        };
    }

    private (SectionType type, string? tableName) DetermineSectionType(string headerText)
    {
        var lowerHeader = headerText.ToLowerInvariant();

        // Check for domain sections (new format)
        if (lowerHeader.StartsWith("domain:"))
        {
            var domainName = headerText.Substring(7).Trim();
            return (SectionType.TableDetail, domainName); // Use TableDetail for domain sections
        }

        // Check for table-specific sections
        if (lowerHeader.StartsWith("table:"))
        {
            var tableName = headerText.Substring(6).Trim();
            // Remove backticks if present
            tableName = tableName.Trim('`');
            return (SectionType.TableDetail, tableName);
        }

        // Map section headers to types
        if (lowerHeader.Contains("overview") || lowerHeader.Contains("summary"))
            return (SectionType.Overview, null);

        if (lowerHeader.Contains("architecture") || lowerHeader.Contains("diagram") || lowerHeader.Contains("er diagram"))
            return (SectionType.Architecture, null);

        if (lowerHeader.Contains("key entities") || lowerHeader.Contains("main tables") || lowerHeader.Contains("entities"))
            return (SectionType.TableDetail, null);

        if (lowerHeader.Contains("relationship") || lowerHeader.Contains("associations") || lowerHeader.Contains("foreign key"))
            return (SectionType.Relationships, null);

        if (lowerHeader.Contains("pattern") || lowerHeader.Contains("convention") || lowerHeader.Contains("naming"))
            return (SectionType.BestPractices, null);

        if (lowerHeader.Contains("issue") || lowerHeader.Contains("recommendation") || lowerHeader.Contains("improvement"))
            return (SectionType.BestPractices, null);

        if (lowerHeader.Contains("query") || lowerHeader.Contains("sql") || lowerHeader.Contains("example"))
            return (SectionType.UsageExamples, null);

        // Default to overview
        return (SectionType.Overview, null);
    }

    private async Task TrackUsageAsync(
        SemanticoContext context,
        int dataSourceId,
        int userId,
        LlmResponse response,
        OperationType operationType,
        CancellationToken cancellationToken = default)
    {
        var metrics = new AiUsageMetrics
        {
            UserId = userId,
            DataSourceId = dataSourceId,
            Provider = "Unknown", // Would be determined from config
            Model = response.Model,
            InputTokens = response.InputTokens,
            OutputTokens = response.OutputTokens,
            TotalTokens = response.TotalTokens,
            EstimatedCost = response.EstimatedCost,
            OperationType = operationType,
            Timestamp = DateTime.UtcNow,
            PromptCacheHit = response.PromptCacheHit
        };

        context.AiUsageMetrics.Add(metrics);
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task<int> GetNextVersionNumberAsync(
        SemanticoContext context,
        int documentationId,
        CancellationToken cancellationToken = default)
    {
        var maxVersion = await context.DocumentationVersions
            .Where(v => v.DocumentationId == documentationId)
            .MaxAsync(v => (int?)v.VersionNumber, cancellationToken) ?? 0;

        return maxVersion + 1;
    }

    private string WrapInHtmlDocument(string htmlBody, string? customCss)
    {
        var defaultCss = @"
            body { font-family: Arial, sans-serif; max-width: 900px; margin: 0 auto; padding: 20px; }
            h1 { color: #333; border-bottom: 2px solid #007bff; padding-bottom: 10px; }
            h2 { color: #555; margin-top: 30px; }
            table { border-collapse: collapse; width: 100%; margin: 20px 0; }
            th, td { border: 1px solid #ddd; padding: 8px; text-align: left; }
            th { background-color: #007bff; color: white; }
        ";

        return $@"<!DOCTYPE html>
<html>
<head>
    <meta charset=""UTF-8"">
    <title>Database Documentation</title>
    <style>{customCss ?? defaultCss}</style>
</head>
<body>
{htmlBody}
</body>
</html>";
    }
}
