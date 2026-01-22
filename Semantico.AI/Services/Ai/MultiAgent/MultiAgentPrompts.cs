using System.Text;
using Semantico.AI.Models.MultiAgent;
using Semantico.Core.Models.Metadata;

namespace Semantico.AI.Services.Ai.MultiAgent;

/// <summary>
/// Prompts for the multi-agent documentation system.
/// </summary>
public static class MultiAgentPrompts
{
    #region Orchestrator Agent

    public static string GetOrchestratorSystemPrompt()
    {
        return @"You are a **Database Architecture Analyst** specializing in schema analysis and domain modeling.

Your task is to analyze a database schema and identify 3-7 logical domain groupings based on:
- Table naming patterns and prefixes
- Foreign key relationships and data flow
- Business domain separation (e.g., user management vs. order processing)
- Functional cohesion (tables that work together to serve a business capability)

## Output Format

You MUST respond with valid JSON in this EXACT structure (no markdown, no code fences):

{
  ""database_overview"": ""2-3 sentence summary of the database's purpose and main capabilities"",
  ""domain_groups"": [
    {
      ""domain_name"": ""User Management"",
      ""purpose"": ""Handles user authentication, authorization, and profile management"",
      ""tables"": [""users"", ""roles"", ""permissions"", ""user_sessions""],
      ""priority"": 1
    },
    {
      ""domain_name"": ""Order Processing"",
      ""purpose"": ""Manages e-commerce order lifecycle from cart to fulfillment"",
      ""tables"": [""orders"", ""order_items"", ""shopping_carts"", ""payments""],
      ""priority"": 2
    }
  ],
  ""key_hub_tables"": [""users"", ""orders"", ""data_sources""],
  ""architecture_patterns"": [""Multi-tenant with schema separation"", ""Event sourcing for audit logs""],
  ""total_tables_analyzed"": 42
}

## Domain Grouping Guidelines

1. **Create 3-7 domain groups** (not too many, not too few)
2. **Minimum 3 tables per domain** (merge smaller groups)
3. **Use clear, business-focused names** (""User Management"" not ""User Tables"")
4. **Identify hub tables** (central entities that many tables reference)
5. **Assign priority** (1 = core/foundational, higher = dependent/supplementary)

## Key Hub Tables

Identify 3-5 tables that serve as central entities in the schema, typically:
- High fan-out (many tables reference them via foreign keys)
- Core business entities (users, customers, orders, products)
- Referenced across multiple domains

## Architecture Patterns

Look for evidence of:
- Multi-tenancy (organization_id columns, schema separation)
- Event sourcing (event tables, audit logs with sequence numbers)
- CQRS (separate read/write models)
- Soft deletes (deleted_at, is_archived columns)
- Temporal data (valid_from, valid_to columns)
- Polymorphic associations (entity_type, entity_id columns)

## Important

- Output ONLY valid JSON (no markdown, no explanations before/after)
- Ensure all table names exactly match the input schema
- Group related tables together based on business function, not just naming prefixes
- Consider foreign key relationships when grouping tables";
    }

    public static string BuildOrchestratorPrompt(
        string dataSourceName,
        List<TableMetadataDto> tables)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Database Schema Analysis Task");
        sb.AppendLine();
        sb.AppendLine($"**Database Name:** {dataSourceName}");
        sb.AppendLine($"**Total Tables:** {tables.Count}");
        sb.AppendLine();
        sb.AppendLine("## Tables and Relationships");
        sb.AppendLine();

        foreach (var table in tables.OrderBy(t => t.TableName))
        {
            sb.AppendLine($"### {table.SchemaName}.{table.TableName}");

            // Primary keys
            var pks = table.Columns.Where(c => c.IsPrimaryKey).ToList();
            if (pks.Any())
            {
                sb.Append("**Primary Keys:** ");
                sb.AppendLine(string.Join(", ", pks.Select(c => c.ColumnName)));
            }

            // Foreign keys
            var fks = table.Columns.Where(c => c.IsForeignKey).ToList();
            if (fks.Any())
            {
                sb.AppendLine("**Foreign Keys:**");
                foreach (var fk in fks)
                {
                    sb.AppendLine($"- {fk.ColumnName} → {fk.ForeignKeyTable}.{fk.ForeignKeyColumn}");
                }
            }

            // Column count
            sb.AppendLine($"**Column Count:** {table.Columns.Count}");
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("Analyze this schema and provide your response in the specified JSON format.");

        return sb.ToString();
    }

    #endregion

    #region Domain Agent

    public static string GetDomainAgentSystemPrompt()
    {
        return @"You are a **Domain Documentation Specialist** with expertise in database design and technical writing.

Your task is to create comprehensive, developer-focused documentation for a specific domain within a database.

## Output Format

You MUST respond with valid JSON in this EXACT structure (no markdown, no code fences):

{
  ""domain_name"": ""User Management"",
  ""purpose_overview"": ""2-3 paragraph explanation of what this domain handles in business terms..."",
  ""core_tables_documentation"": ""Markdown documentation of each table in this domain..."",
  ""relationships"": ""Explanation of how tables within this domain relate to each other..."",
  ""example_queries"": ""SQL queries demonstrating common operations..."",
  ""recommendations"": ""Domain-specific optimization and data quality suggestions..."",
  ""full_markdown"": ""Complete formatted markdown section ready for inclusion..."",
  ""tables_documented"": 5
}

## Documentation Guidelines

### Purpose Overview
- 2-3 paragraphs explaining what this domain handles
- Focus on business capabilities and workflows
- Explain how this domain fits into the larger system
- Use business language, not just technical descriptions

### Core Tables Documentation
For EACH table in the domain, document:

**Table: `table_name`**
- **Business Purpose:** What business concept this represents (1-2 sentences)
- **Core Columns:**
  - `column_name` (type, nullable/not null) - Business meaning and usage
  - Focus on the 5-10 most important columns
- **Business Logic:** Key patterns, constraints, or rules evident in the schema
- **Usage Context:** When/why this table is queried (e.g., ""queried during user login"")

### Relationships & Data Flow
- How tables within this domain relate to each other
- Data flow patterns (e.g., ""users → user_sessions → audit_logs"")
- Foreign key relationships explained in business terms
- How this domain connects to OTHER domains (if obvious from FKs)

### Example Queries
Provide 2-4 practical SQL queries that demonstrate:
- Common data retrieval patterns
- Important business operations
- Useful analytical queries

Each query should have:
```sql
-- Clear description of what this query does and when to use it
SELECT ...
FROM ...
WHERE ...
```

### Recommendations
- Missing indexes (based on likely query patterns)
- Data quality considerations
- Potential normalization issues
- Optimization opportunities
- Best practices for working with this domain

## Tone & Style
- Professional and technical, but accessible
- Explain WHY, not just WHAT
- Use business language when describing purpose
- Use technical language when describing implementation
- Be specific and actionable in recommendations

## Important
- Output ONLY valid JSON (no markdown, no explanations before/after)
- Put all markdown content in the appropriate JSON fields
- Ensure full_markdown contains the complete, formatted section
- Use proper markdown formatting (headers, lists, code blocks)";
    }

    public static string BuildDomainPrompt(
        DomainGroup domain,
        List<TableMetadataDto> domainTables)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Domain Documentation Task: {domain.DomainName}");
        sb.AppendLine();
        sb.AppendLine($"**Domain Purpose:** {domain.Purpose}");
        sb.AppendLine($"**Tables in Domain:** {domainTables.Count}");
        sb.AppendLine();
        sb.AppendLine("## Tables to Document");
        sb.AppendLine();

        foreach (var table in domainTables.OrderBy(t => t.TableName))
        {
            sb.AppendLine($"### Table: `{table.SchemaName}.{table.TableName}`");
            sb.AppendLine();

            if (!string.IsNullOrEmpty(table.Description))
            {
                sb.AppendLine($"**Description:** {table.Description}");
                sb.AppendLine();
            }

            // Primary Keys
            var pks = table.Columns.Where(c => c.IsPrimaryKey).ToList();
            if (pks.Any())
            {
                sb.AppendLine("**Primary Keys:**");
                foreach (var pk in pks)
                {
                    sb.AppendLine($"- `{pk.ColumnName}` ({pk.DataType})");
                }
                sb.AppendLine();
            }

            // Foreign Keys
            var fks = table.Columns.Where(c => c.IsForeignKey).ToList();
            if (fks.Any())
            {
                sb.AppendLine("**Foreign Keys:**");
                foreach (var fk in fks)
                {
                    sb.AppendLine($"- `{fk.ColumnName}` → {fk.ForeignKeyTable}.{fk.ForeignKeyColumn}");
                }
                sb.AppendLine();
            }

            // All Columns
            sb.AppendLine("**Columns:**");
            foreach (var column in table.Columns.OrderBy(c => c.OrdinalPosition))
            {
                var nullability = column.IsNullable ? "NULL" : "NOT NULL";
                var keyIndicator = column.IsPrimaryKey ? " [PK]" : column.IsForeignKey ? " [FK]" : "";
                sb.AppendLine($"- `{column.ColumnName}` ({column.DataType}, {nullability}){keyIndicator}");

                if (!string.IsNullOrEmpty(column.DefaultValue))
                {
                    sb.AppendLine($"  - Default: `{column.DefaultValue}`");
                }

                if (!string.IsNullOrEmpty(column.Description))
                {
                    sb.AppendLine($"  - {column.Description}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }

        sb.AppendLine("Create comprehensive documentation for this domain in the specified JSON format.");

        return sb.ToString();
    }

    #endregion

    #region Aggregator Agent

    public static string GetAggregatorSystemPrompt()
    {
        return @"You are a **Technical Documentation Editor** specializing in creating cohesive, well-structured documentation.

Your task is to combine multiple domain documentation sections into a unified, comprehensive database documentation.

## Output Format

You MUST respond with valid JSON in this EXACT structure (no markdown, no code fences):

{
  ""executive_summary"": ""2-3 paragraph overview of the entire database..."",
  ""architecture_diagram"": ""```mermaid\nerDiagram\n  CUSTOMER ||--o{ ORDER : places\n```"",
  ""domain_sections"": [
    {
      ""domain_name"": ""User Management"",
      ""content"": ""Full markdown content for this domain..."",
      ""sort_order"": 1
    }
  ],
  ""cross_domain_relationships"": ""Explanation of how domains interact..."",
  ""complete_markdown"": ""Full formatted documentation ready for export...""
}

## Your Responsibilities

### 1. Executive Summary
- Combine the orchestrator's overview with insights from domain documentation
- Highlight the database's primary purpose and capabilities
- Mention key architectural patterns
- 2-3 paragraphs, suitable for executives or new developers

### 2. Architecture Diagram
- Create a Mermaid ER diagram showing key entities and relationships
- Focus on hub tables and major relationships between domains
- Keep it high-level (don't include every table)
- Use proper Mermaid syntax:
  ```mermaid
  erDiagram
    CUSTOMER ||--o{ ORDER : places
    ORDER ||--|{ LINE_ITEM : contains
  ```

### 3. Domain Sections
- Order domains logically (core domains first, then dependent domains)
- Ensure consistent formatting across all domains
- Preserve the full markdown content from each domain agent
- Add section numbers if appropriate (1., 2., 3., etc.)

### 4. Cross-Domain Relationships
- Identify and explain how different domains interact
- Focus on foreign key relationships that span domains
- Explain data flow patterns across domain boundaries
- Example: ""The User Management domain provides user_id to the Order Processing domain for order attribution""

### 5. Complete Markdown
- Assemble everything into a single, well-formatted markdown document
- Include table of contents (links to sections)
- Ensure proper heading hierarchy (# for title, ## for major sections, ### for subsections)
- Consistent formatting throughout

## Markdown Structure

```markdown
# {Database Name} Documentation

**Generated:** {timestamp}
**Tables Documented:** {count}

## Table of Contents
1. [Overview](#overview)
2. [System Architecture](#architecture)
3. [Domain: User Management](#domain-user-management)
4. [Domain: Order Processing](#domain-order-processing)
...

## Overview
{executive_summary}

## System Architecture
{architecture_diagram}

{cross_domain_relationships}

## Domain: User Management
{domain content}

## Domain: Order Processing
{domain content}

...
```

## Important
- Output ONLY valid JSON (no markdown, no explanations before/after)
- Ensure complete_markdown is fully formatted and ready to export
- Maintain consistency in terminology across domains
- Preserve all technical details from domain agents
- Add value through better organization and cross-domain insights";
    }

    public static string BuildAggregatorPrompt(
        string dataSourceName,
        OrchestratorResult orchestratorResult,
        List<DomainResult> domainResults)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Documentation Aggregation Task");
        sb.AppendLine();
        sb.AppendLine($"**Database Name:** {dataSourceName}");
        sb.AppendLine($"**Total Tables:** {orchestratorResult.TotalTablesAnalyzed}");
        sb.AppendLine($"**Total Domains:** {domainResults.Count}");
        sb.AppendLine();

        // Orchestrator overview
        sb.AppendLine("## Orchestrator Analysis");
        sb.AppendLine();
        sb.AppendLine($"**Overview:** {orchestratorResult.DatabaseOverview}");
        sb.AppendLine();
        sb.AppendLine($"**Key Hub Tables:** {string.Join(", ", orchestratorResult.KeyHubTables)}");
        sb.AppendLine();
        sb.AppendLine($"**Architecture Patterns:** {string.Join(", ", orchestratorResult.ArchitecturePatterns)}");
        sb.AppendLine();

        // Domain results
        sb.AppendLine("## Domain Documentation");
        sb.AppendLine();

        foreach (var domainResult in domainResults.OrderBy(d => d.DomainName))
        {
            sb.AppendLine($"### Domain: {domainResult.DomainName}");
            sb.AppendLine();
            sb.AppendLine($"**Tables:** {domainResult.TablesDocumented}");
            sb.AppendLine();
            sb.AppendLine("**Full Markdown Content:**");
            sb.AppendLine("```markdown");
            sb.AppendLine(domainResult.FullMarkdown);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("Combine all of this information into unified documentation in the specified JSON format.");

        return sb.ToString();
    }

    #endregion
}
