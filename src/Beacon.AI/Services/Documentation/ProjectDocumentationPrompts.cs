using System.Globalization;
using System.Text;
using Beacon.AI.Services.Knowledge;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Services.Providers;
using static Beacon.AI.Services.Documentation.ProjectDocumentationService;

namespace Beacon.AI.Services.Documentation;

internal static class ProjectDocumentationPrompts
{
    // --- System Prompts ---

    internal static string GetSystemPrompt(ProjectDocSectionType sectionType) => sectionType switch
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
    // Each builder bounds its variable part with a DocumentationPromptBudget: items are added whole, in their order, and the
    // ones past the budget are counted in one closing line.

    internal static string BuildProjectOverviewPrompt(DocumentationContext ctx)
    {
        var sb = new StringBuilder();
        var budget = new DocumentationPromptBudget(sb);
        sb.AppendLine($"# Project: {ctx.ProjectName}");
        if (ctx.ProjectDescription != null) sb.AppendLine($"Description: {ctx.ProjectDescription}");
        sb.AppendLine();

        sb.AppendLine("## Data Sources:");
        foreach (var ds in ctx.DataSources)
        {
            var engine = ds.Type == DataSourceType.Api ? ConnectorRegistry.GetDisplayName(DataSourceType.Api) : ds.EngineType?.ToString() ?? "Unknown";
            var tableCount = ctx.Tables.Count(t => t.DataSourceId == ds.Id);
            budget.TryAdd($"- {ds.Name} ({engine}) — {tableCount} tables{Environment.NewLine}");
        }

        sb.AppendLine();
        sb.AppendLine("## Repositories:");
        foreach (var repo in ctx.Repositories)
        {
            budget.TryAdd($"- {repo.Url} (branch: {repo.Branch}, files scanned: {repo.FilesScanned}, references found: {repo.ReferencesFound}){Environment.NewLine}");
        }

        sb.AppendLine();
        sb.AppendLine($"Total tables: {ctx.TableCount}");
        sb.AppendLine($"Total code references: {ctx.CodeReferenceCount}");

        var avgQuality = ctx.QualityScores.Count > 0 ? ctx.QualityScores.Average(q => q.Score) : (double?)null;
        if (avgQuality.HasValue) sb.AppendLine($"Average quality score: {avgQuality:F0}%");

        sb.AppendLine($"Active data contracts: {ctx.Contracts.Count}");
        budget.AppendNote();

        return sb.ToString();
    }

    internal static string BuildBusinessDomainsPrompt(DocumentationContext ctx)
    {
        var sb = new StringBuilder();
        var budget = new DocumentationPromptBudget(sb);
        sb.AppendLine("# Tables and Relationships");
        sb.AppendLine();

        foreach (var group in ctx.Tables.GroupBy(t => t.DataSourceName))
        {
            budget.Heading($"## Data Source: {group.Key}{Environment.NewLine}");
            foreach (var table in group)
            {
                var block = new StringBuilder();
                SchemaContextFormatter.AppendTable(block, $"{table.SchemaName}.{table.TableName}", table.Description, table.Columns, SchemaTableLayout.DocumentationDomains);

                var tableEnums = ctx.GetEnumValuesForTable(table.SchemaName, table.TableName);
                if (tableEnums.Count > 0)
                {
                    foreach (var enumCol in tableEnums)
                    {
                        var values = string.Join(", ", enumCol.Values.Select(x => x.Value));
                        block.AppendLine($"  - {enumCol.ColumnName} values: [{values}]");
                    }
                }

                budget.TryAdd(block.ToString());
            }

            budget.EndGroup(Environment.NewLine);
        }

        // Code references grouped by class
        sb.AppendLine("# Code References by Class");
        foreach (var classGroup in ctx.CodeReferences.Where(r => r.ClassName != null).GroupBy(r => r.ClassName))
        {
            var block = new StringBuilder();
            block.AppendLine($"## {classGroup.Key}");
            foreach (var refGroup in classGroup.GroupBy(r => r.TableName))
            {
                block.AppendLine($"  - {refGroup.Key}: {string.Join(", ", refGroup.Select(r => $"{r.ReferenceType}({r.MethodName})"))}");
            }

            budget.TryAdd(block.ToString());
        }

        budget.AppendNote();

        return sb.ToString();
    }

    internal static string BuildDataModelPrompt(DocumentationContext ctx)
    {
        var sb = new StringBuilder();
        var budget = new DocumentationPromptBudget(sb);
        sb.AppendLine("# Full Schema Metadata");
        sb.AppendLine();

        foreach (var group in ctx.Tables.GroupBy(t => t.DataSourceName))
        {
            budget.Heading($"## Data Source: {group.Key}{Environment.NewLine}");
            foreach (var table in group)
            {
                var block = new StringBuilder();
                SchemaContextFormatter.AppendTable(block, $"{table.SchemaName}.{table.TableName}", table.Description, table.Columns, SchemaTableLayout.DocumentationDataModel);

                if (table.Indexes.Count > 0)
                {
                    block.AppendLine("Indexes:");
                    foreach (var idx in table.Indexes)
                        block.AppendLine($"  - {idx.Name} ({idx.Columns}){(idx.IsUnique ? " UNIQUE" : "")}");
                }

                var tableEnums = ctx.GetEnumValuesForTable(table.SchemaName, table.TableName);
                if (tableEnums.Count > 0)
                {
                    block.AppendLine("Enum/Status Values:");
                    foreach (var enumCol in tableEnums)
                    {
                        var values = string.Join(", ",
                            enumCol.Values.Select(x => $"{x.Value} ({x.Count:N0} rows)"));
                        block.AppendLine($"  - {enumCol.ColumnName}: [{values}]");
                    }
                }

                block.AppendLine();
                budget.TryAdd(block.ToString());
            }

            budget.EndGroup("");
        }

        budget.AppendNote();

        return sb.ToString();
    }

    internal static string BuildDataFlowsPrompt(DocumentationContext ctx)
    {
        var sb = new StringBuilder();
        var budget = new DocumentationPromptBudget(sb);
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
            budget.TryAdd(DataFlowTableBlock(group.Key, group));
        }

        sb.AppendLine();
        sb.AppendLine("## Read Operations (data serving)");
        foreach (var group in reads.GroupBy(r => r.TableName ?? "unknown"))
        {
            budget.TryAdd(DataFlowTableBlock(group.Key, group));
        }

        sb.AppendLine();
        sb.AppendLine("## FK Relationships (data dependencies)");
        foreach (var table in ctx.Tables)
        {
            var fks = table.Columns.Where(c => c.ForeignKeyTable != null).ToList();
            if (fks.Count > 0)
            {
                budget.TryAdd($"  - {table.SchemaName}.{table.TableName} → {string.Join(", ", fks.Select(c => $"{c.ForeignKeyTable} (via {c.ColumnName})"))}{Environment.NewLine}");
            }
        }

        budget.AppendNote();

        return sb.ToString();
    }

    private static string DataFlowTableBlock(string table, IEnumerable<CodeRefInfo> references)
    {
        var block = new StringBuilder();
        block.AppendLine($"### Table: {table}");
        foreach (var r in references.Take(10))
        {
            block.AppendLine($"  - {r.ClassName}.{r.MethodName} ({r.ReferenceType}) — {r.FilePath}:{r.LineNumber}");
            if (r.CodeSnippet != null) block.AppendLine($"    ```{r.CodeSnippet}```");
        }

        return block.ToString();
    }

    internal static string BuildCodeLineagePrompt(DocumentationContext ctx)
    {
        var sb = new StringBuilder();
        var budget = new DocumentationPromptBudget(sb);
        sb.AppendLine("# Code-to-Data References");
        sb.AppendLine();

        // By class/service
        sb.AppendLine("## By Service/Class:");
        foreach (var classGroup in ctx.CodeReferences
            .Where(r => r.ClassName != null)
            .GroupBy(r => r.ClassName!)
            .OrderByDescending(g => g.Count()))
        {
            var block = new StringBuilder();
            block.AppendLine($"### {classGroup.Key}");
            var byTable = classGroup.GroupBy(r => r.TableName ?? "unknown");
            foreach (var tableGroup in byTable)
            {
                var types = string.Join(", ", tableGroup.Select(r => $"{r.ReferenceType}").Distinct());
                var methods = string.Join(", ", tableGroup.Select(r => r.MethodName).Where(m => m != null).Distinct().Take(5));
                block.AppendLine($"  - {tableGroup.Key}: [{types}] methods: {methods}");
            }

            budget.TryAdd(block.ToString());
        }

        // By table
        sb.AppendLine();
        sb.AppendLine("## By Table:");
        foreach (var tableGroup in ctx.CodeReferences
            .Where(r => r.TableName != null)
            .GroupBy(r => r.TableName!)
            .OrderByDescending(g => g.Count()))
        {
            var block = new StringBuilder();
            block.AppendLine($"### {tableGroup.Key}");
            var writers = tableGroup.Where(r => r.ReferenceType is CodeReferenceType.DapperQuery or CodeReferenceType.RawSql or CodeReferenceType.Migration or CodeReferenceType.StoredProcedureCall)
                .Select(r => $"{r.ClassName}.{r.MethodName}").Distinct().ToList();
            var readers = tableGroup.Where(r => r.ReferenceType is CodeReferenceType.EntityModel or CodeReferenceType.ApiEndpoint or CodeReferenceType.DbContextConfiguration)
                .Select(r => $"{r.ClassName}.{r.MethodName}").Distinct().ToList();
            if (writers.Count > 0) block.AppendLine($"  Writers: {string.Join(", ", writers)}");
            if (readers.Count > 0) block.AppendLine($"  Readers: {string.Join(", ", readers)}");

            budget.TryAdd(block.ToString());
        }

        budget.AppendNote();

        return sb.ToString();
    }

    internal static string BuildDataQualityPrompt(DocumentationContext ctx)
    {
        var sb = new StringBuilder();
        var budget = new DocumentationPromptBudget(sb);
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
            budget.TryAdd($"  - {score.SchemaName}.{score.TableName}: {score.Score:F0}% (trend: {score.Trend}){Environment.NewLine}");
        }

        sb.AppendLine();
        sb.AppendLine("## Data Contracts:");
        foreach (var contract in ctx.Contracts)
        {
            var block = new StringBuilder();
            block.AppendLine($"### {contract.SchemaName}.{contract.TableName}");
            foreach (var rule in contract.Rules)
            {
                block.Append($"  - {rule.RuleType}");
                if (rule.ColumnName != null) block.Append($" on {rule.ColumnName}");
                if (rule.Configuration != null) block.Append($" (config: {rule.Configuration})");
                block.AppendLine();
            }

            budget.TryAdd(block.ToString());
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
            {
                budget.TryAdd($"  - {t}{Environment.NewLine}");
            }
        }

        budget.AppendNote();

        return sb.ToString();
    }

    internal static string BuildApiDocumentationPrompt(DocumentationContext ctx)
    {
        var sb = new StringBuilder();
        var budget = new DocumentationPromptBudget(sb);
        sb.AppendLine("# API Data Sources");
        sb.AppendLine();

        foreach (var apiDs in ctx.ApiDataSources)
        {
            var tables = ctx.Tables.Where(t => t.DataSourceId == apiDs.Id).ToList();
            budget.Heading($"## {apiDs.Name}{Environment.NewLine}{Environment.NewLine}");

            foreach (var endpoint in tables)
            {
                var block = new StringBuilder();
                SchemaContextFormatter.AppendTable(block, endpoint.TableName, endpoint.Description, endpoint.Columns, SchemaTableLayout.DocumentationApi);
                budget.TryAdd(block.ToString());
            }

            budget.EndGroup("");
        }

        budget.AppendNote();

        return sb.ToString();
    }

    internal static string BuildGlossaryPrompt(DocumentationContext ctx, List<ProjectDocumentationSection> existingSections)
    {
        var sb = new StringBuilder();
        var budget = new DocumentationPromptBudget(sb);
        sb.AppendLine("# Context for Glossary Generation");
        sb.AppendLine();
        sb.AppendLine("## Table and Column Names:");
        foreach (var table in ctx.Tables.Take(100))
        {
            budget.TryAdd($"- {table.SchemaName}.{table.TableName}: {string.Join(", ", table.Columns.Select(c => c.ColumnName))}{Environment.NewLine}");
        }

        if (ctx.EnumValues.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Enum/Status Column Values:");
            foreach (var enumCol in ctx.EnumValues)
            {
                var values = string.Join(", ", enumCol.Values.Select(x => x.Value));
                budget.TryAdd($"- {enumCol.SchemaName}.{enumCol.TableName}.{enumCol.ColumnName}: [{values}]{Environment.NewLine}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Key Terms from Generated Documentation:");
        // Include excerpts from previously generated sections for the glossary to reference
        foreach (var section in existingSections.Where(s => s.Content.Length > 50).Take(4))
        {
            var excerpt = section.Content.Length > 500 ? section.Content[..500] : section.Content;
            budget.TryAdd($"### {section.Title}:{Environment.NewLine}{excerpt}{Environment.NewLine}{Environment.NewLine}");
        }

        budget.AppendNote();

        return sb.ToString();
    }
}
