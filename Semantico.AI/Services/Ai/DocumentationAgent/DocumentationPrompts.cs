using Semantico.AI.Services.Ai.DocumentationAgent.Models;
using Semantico.Core.Models.Ai;


namespace Semantico.AI.Services.Ai.DocumentationAgent;

/// <summary>
/// Centralized repository for all AI prompts used in database documentation generation.
/// This file makes prompts easier to review, refine, and version control.
///
/// <para><b>Improvements Made (2026-01-13):</b></para>
/// <list type="number">
/// <item>
/// <b>Explicit Enum Values:</b> Prompts now strictly enforce that LLM must provide definitive enum values
/// or explicitly state they're unknown. No more uncertain language like "likely", "might", "probably".
/// </item>
/// <item>
/// <b>Complete Domain Coverage:</b> Added safeguards against large "Other" category. Prompts now
/// require breaking down miscellaneous groups into logical subdomains (Infrastructure, Caching, etc.).
/// </item>
/// <item>
/// <b>Workflow Documentation:</b> Table documentation now must explain the table's role in end-to-end
/// workflows, showing how it interacts with other tables in sequences.
/// </item>
/// <item>
/// <b>Concrete Recommendations:</b> Replaced vague suggestions with specific requirements:
/// - Actual SQL for common queries
/// - Exact CREATE INDEX statements
/// - Specific constraint examples
/// </item>
/// <item>
/// <b>Architecture Focus:</b> Architecture section now identifies hub tables, explains patterns,
/// and provides scalability considerations.
/// </item>
/// </list>
///
/// <para><b>Usage:</b> All prompts are static strings/methods in this class. Update them here to
/// improve documentation quality across all future generations.</para>
/// </summary>
public static class DocumentationPrompts
{
    #region System Prompts

    public const string TableDocumentationSystemPrompt = """
        You are a database documentation expert. Generate clear, comprehensive documentation for database tables.

        Guidelines:
        - Be concise but thorough
        - Infer the purpose of columns from their names, types, and relationships
        - Note important constraints and relationships
        - Suggest common query patterns
        - Use markdown formatting
        - Do not include the raw schema data - transform it into readable documentation

        CRITICAL REQUIREMENTS FOR HIGH-QUALITY DOCUMENTATION:

        1. ENUM VALUES - NEVER GUESS OR USE UNCERTAIN LANGUAGE:
           - If you encounter integer fields that represent enums (e.g., status, type, notification_type), you MUST:
             * State definitively what each value means OR explicitly state "Enum values not available - requires code inspection"
             * NEVER use phrases like "likely values", "might include", "probably", "common values may be"
             * If uncertain, create a placeholder: "Enum values: Documentation needed - see application code"
           - Example GOOD documentation:
             ```
             notification_type (integer, NOT NULL)
               1 = Email
               2 = Teams
               3 = Jira
               4 = Slack
             ```
           - Example BAD documentation:
             ```
             notification_type (integer, NOT NULL)
               Likely values: 1=Email, 2=Teams, etc.
             ```

        2. DOMAIN COMPLETENESS - DOCUMENT ALL TABLE GROUPS:
           - Every table deserves full documentation, not just "core" tables
           - For infrastructure tables (job queues, locks, caching), explain:
             * Their role in the system architecture
             * How they support the main application
             * Common operational patterns
           - Group related tables into logical domains with clear boundaries

        3. WORKFLOW DOCUMENTATION - SHOW HOW TABLES INTERACT:
           - For each table, explain its role in end-to-end workflows
           - Example: "This table is step 3 in the subscription execution workflow:
             1) subscription triggers, 2) query executes, 3) THIS TABLE logs results, 4) notifications sent"
           - Cross-reference related tables in the workflow
           - Include sequence of operations where relevant

        4. DATA INTEGRITY - BE SPECIFIC ABOUT CONSTRAINTS:
           - List all NOT NULL, UNIQUE, CHECK constraints explicitly
           - Explain foreign key cascade behavior (CASCADE, RESTRICT, SET NULL)
           - Document any application-level constraints not visible in schema

        5. INDEXING - PROVIDE CONCRETE RECOMMENDATIONS:
           - Don't just say "consider indexing" - specify exact index definitions
           - Example: CREATE INDEX idx_users_email ON users(email) WHERE archived_at IS NULL;
           - Explain why each index is recommended based on query patterns

        6. USAGE PATTERNS - PROVIDE REAL QUERY EXAMPLES:
           - Include actual SQL queries, not just descriptions
           - Show both common CRUD operations and complex analytical queries
           - Include performance considerations (LIMIT clauses, WHERE filters)

        7. TROUBLESHOOTING - ADD COMMON ISSUES SECTION:
           - Document known pitfalls or edge cases
           - Explain how to diagnose common problems
           - Reference related error messages or logs

        8. AVOID REDUNDANCY:
           - Don't repeat the schema structure - you already showed columns above
           - Focus on WHY and HOW, not just WHAT
           - Transform technical details into actionable insights
        """;

    public const string OverviewSystemPrompt = """
        You are a technical documentation expert. Generate clear, concise database documentation that provides a high-level understanding of the system.

        REQUIREMENTS:
        - Focus on the PURPOSE and DOMAIN of the database, not just listing tables
        - Identify KEY WORKFLOWS that explain how tables work together
        - Highlight ARCHITECTURAL PATTERNS (normalized, star schema, event sourcing, etc.)
        - Provide DEVELOPER GUIDANCE specific to this schema
        - Use clear, scannable markdown with headers
        - Keep it concise (aim for 300-500 words)

        CRITICAL: If you see a large "Other" or "Miscellaneous" domain group, DO NOT accept it as-is:
        - Break it down into logical subgroups (Infrastructure, Background Jobs, Caching, etc.)
        - Explain the role of each subgroup in the system
        - Provide context on how these "supporting" tables enable the core functionality
        """;

    public const string ArchitectureSystemPrompt = """
        You are a database architect. Generate clear architecture documentation with diagrams that explain the system's structure and relationships.

        REQUIREMENTS:
        - Create a Mermaid ERD showing the PRIMARY entities and their relationships
        - Focus on the "hub" tables with the most relationships (usually 6-10 core entities)
        - Explain the ARCHITECTURAL PATTERNS observed:
          * Hub-and-spoke (core entities with many dependents)
          * Temporal tracking (audit trails, history tables)
          * Polymorphic relationships (generic entity references)
          * Event-driven patterns (job queues, notifications)
        - Document SCALABILITY CONSIDERATIONS for high-volume tables
        - Include a "Data Flow" section showing how data moves through the system

        MERMAID ERD GUIDELINES:
        - Limit to 15-20 most important relationships to keep diagram readable
        - Use relationship labels that explain the cardinality and purpose
        - Group related entities visually if possible
        - Add a legend explaining diagram conventions

        Example structure:
        ```mermaid
        erDiagram
            subscriptions ||--o{ query_execution_history : "triggers"
            subscriptions ||--o{ notifications : "sends"
            subscriptions }o--|| queries : "executes"
        ```
        """;

    #endregion

    #region User Prompts

    /// <summary>
    /// Builds the comprehensive prompt for documenting a single database table.
    /// </summary>
    public static string BuildTableDocumentationPrompt(
        string tableName,
        string columnsSection,
        string relationshipsSection,
        string sampleDataSection)
    {
        return $"""
            Document the following database table: **{tableName}**

            {columnsSection}

            {relationshipsSection}

            {sampleDataSection}

            Generate comprehensive markdown documentation for this table including:

            ## Required Sections:

            ### Overview
            - Brief description of the table's purpose (2-3 sentences)
            - How this table fits into the overall system architecture

            ### Column Descriptions
            - For EACH column, explain:
              * Its purpose and meaning
              * Valid values or ranges (especially for enums - be explicit!)
              * Business rules or validation requirements
              * When it's populated and by whom/what
            - Group columns logically (Primary Keys, Foreign Keys, Data Fields, Audit Fields)

            ### Relationships
            - Explain parent-child relationships and their business meaning
            - Document cascade behavior for foreign keys
            - Note any polymorphic or many-to-many relationships

            ### Usage Notes
            - Common query patterns with ACTUAL SQL examples
            - How this table is typically accessed (read-heavy, write-heavy, mixed)
            - Any performance considerations or optimization tips

            ### Data Integrity Considerations
            - Constraints (NOT NULL, UNIQUE, CHECK, foreign key cascade rules)
            - Recommended indexes with CREATE INDEX statements
            - Data validation rules (application-level if not in schema)
            - Transaction requirements

            ### Common Issues & Troubleshooting (if applicable)
            - Known edge cases or pitfalls
            - How to diagnose common problems
            - Monitoring recommendations

            REMEMBER:
            - Be DEFINITIVE about enum values or explicitly state they're unknown
            - Cross-reference related tables by name
            - Explain WHY things are designed this way, not just WHAT exists
            - Transform technical details into actionable developer guidance
            """;
    }

    /// <summary>
    /// Builds the prompt for generating the database overview section.
    /// </summary>
    public static string BuildOverviewPrompt(
        int dataSourceId,
        int totalTables,
        string domainGroupsSummary,
        int totalDocumented)
    {
        return $"""
            Generate a comprehensive overview of this database schema documentation.

            Database: DataSource ID {dataSourceId}
            Total tables documented: {totalDocumented} of {totalTables}
            Domain groups: {domainGroupsSummary}

            The overview should include:

            1. **Purpose Statement** (2-3 sentences)
               - What is this database's primary function?
               - What kind of application or system does it support?

            2. **Main Domain Areas** (organized by business capability)
               - List the major functional areas (e.g., "Core Data Management", "Query Operations", "Monitoring & Alerting")
               - For EACH domain, explain its purpose and key tables (2-3 tables per domain)
               - If you see an "Other" or large miscellaneous group, break it down into subdomains

            3. **Key Workflows** (end-to-end processes)
               - Describe 2-3 important workflows that span multiple tables
               - Show the sequence: "User creates subscription → Query executes → Results stored → Notification sent"
               - Explain how different domains interact

            4. **Architectural Patterns & Conventions**
               - Identify design patterns (hub-and-spoke, event-driven, temporal tracking, etc.)
               - Note naming conventions (table prefixes, column naming patterns)
               - Call out any unique or interesting architectural decisions

            5. **Developer Guidance**
               - Getting started: Which tables should new developers learn first?
               - Best practices for querying this schema
               - Common pitfalls to avoid
               - Performance considerations

            Format as markdown with clear headers and bullet points.
            Keep it scannable - developers should be able to understand the system in 2-3 minutes of reading.

            CRITICAL: If you see more than 30% of tables in "Other" category, REFUSE to accept that grouping.
            Instead, analyze table names and relationships to create meaningful subgroups like:
            - Infrastructure (background jobs, queues, locks)
            - Caching & Performance (hash, counters, state)
            - Security & Access Control
            - Audit & Compliance
            """;
    }

    /// <summary>
    /// Builds the prompt for generating architecture and ERD documentation.
    /// </summary>
    public static string BuildArchitecturePrompt(string relationshipsSummary, List<string> hubTables)
    {
        var hubTablesList = string.Join(", ", hubTables);

        return $"""
            Generate an architecture section for this database documentation.

            Key Hub Tables (most connected): {hubTablesList}

            Relationships Summary:
            {relationshipsSummary}

            Generate a comprehensive architecture document including:

            ## High-Level Overview
            - Describe the overall data model architecture
            - Identify the architectural style (normalized relational, star schema, hybrid, etc.)
            - Explain the hub-and-spoke pattern (if applicable)

            ## Key Entity Relationships
            - Focus on the CORE entities (hub tables) and their primary relationships
            - Explain cardinality (one-to-one, one-to-many, many-to-many)
            - Document relationship patterns:
              * Hierarchical (parent-child trees)
              * Many-to-many (junction tables)
              * Polymorphic (generic entity references)
              * Temporal (versioning, history tracking)

            ## Entity Relationship Diagram
            - Create a Mermaid ERD showing the PRIMARY entities and relationships
            - LIMIT to 15-20 most important relationships to keep it readable
            - Focus on hub tables and their immediate dependents
            - Use clear relationship labels (e.g., "creates", "belongs to", "triggers")
            - Use Mermaid erDiagram syntax with proper cardinality notation

            ## Architectural Patterns
            Document patterns you observe:
            - **Hub-and-Spoke**: Core entities with many dependents (name the hubs)
            - **Normalized Relational**: Third normal form, minimal redundancy
            - **Temporal Tracking**: History tables, audit trails
            - **Event-Driven**: Job queues, notification systems
            - **Soft Deletes**: archived_at patterns for data retention
            - **Optimistic Locking**: update_count or version columns

            ## Data Flow
            - Describe how data moves through the system for key workflows
            - Show the lifecycle of major entities (create → process → archive)
            - Identify entry points (where data comes in) and exit points (where it goes out)

            ## Scalability Considerations
            - Identify high-volume tables that may need partitioning
            - Note tables with heavy read or write patterns
            - Suggest indexing strategies for performance
            - Call out tables that may benefit from caching

            Format as markdown with the Mermaid diagram in a ```mermaid code block.
            """;
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Formats column metadata into a markdown section for the table documentation prompt.
    /// </summary>
    public static string FormatColumnsSection(List<ColumnInfo> columns)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Columns");
        sb.AppendLine();

        foreach (var col in columns)
        {
            var flags = new List<string>();
            if (col.IsPrimaryKey) flags.Add("PK");
            if (col.IsForeignKey) flags.Add("FK");
            if (!col.IsNullable) flags.Add("NOT NULL");

            sb.AppendLine($"- **{col.Name}** ({col.DataType}){(flags.Count > 0 ? $" [{string.Join(", ", flags)}]" : "")}");

            if (col.IsForeignKey && col.ForeignKeyTable != null)
                sb.AppendLine($"  - References: {col.ForeignKeyTable}.{col.ForeignKeyColumn}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Formats relationship metadata into a markdown section for the table documentation prompt.
    /// </summary>
    public static string FormatRelationshipsSection(Models.RelationshipsResult relationships)
    {
        if (!relationships.Success || (relationships.OutgoingReferences.Count == 0 && relationships.IncomingReferences.Count == 0))
            return string.Empty;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine("## Relationships");

        if (relationships.OutgoingReferences.Count > 0)
        {
            sb.AppendLine("### References (this table → other tables)");
            foreach (var rel in relationships.OutgoingReferences)
                sb.AppendLine($"- {rel.SourceColumn} → {rel.ReferencedTable}.{rel.ReferencedColumn}");
        }

        if (relationships.IncomingReferences.Count > 0)
        {
            sb.AppendLine("### Referenced By (other tables → this table)");
            foreach (var rel in relationships.IncomingReferences)
                sb.AppendLine($"- {rel.SourceTable}.{rel.SourceColumn}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Formats sample data into a markdown table for the table documentation prompt.
    /// </summary>
    public static string FormatSampleDataSection(Models.SampleDataResult? sampleData)
    {
        if (sampleData?.Success != true || sampleData.SampleRows.Count == 0)
            return string.Empty;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine("## Sample Data");
        sb.AppendLine($"Showing {sampleData.RowCount} sample rows:");
        sb.AppendLine();

        // Format as markdown table
        sb.AppendLine($"| {string.Join(" | ", sampleData.ColumnNames)} |");
        sb.AppendLine($"| {string.Join(" | ", sampleData.ColumnNames.Select(_ => "---"))} |");

        foreach (var row in sampleData.SampleRows.Take(5))
        {
            var values = sampleData.ColumnNames.Select(c => row.GetValueOrDefault(c) ?? "NULL");
            sb.AppendLine($"| {string.Join(" | ", values)} |");
        }

        return sb.ToString();
    }

    #endregion
}
