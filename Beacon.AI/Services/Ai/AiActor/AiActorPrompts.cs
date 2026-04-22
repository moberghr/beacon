using System.Text;
using System.Text.Json;
using Beacon.Core.Data.Entities;

namespace Beacon.AI.Services.Ai.AiActor;

/// <summary>
/// Centralized repository for all AI prompts used by AI Actors.
/// This file makes prompts easier to review, refine, and version control.
/// </summary>
public static class AiActorPrompts
{
    #region System Prompts

    public const string ThinkCycleSystemPrompt = """
        You are an autonomous database monitoring agent. Your role is to monitor a data source
        by creating and managing SQL queries, analyzing results, and alerting when important
        patterns or anomalies are detected.

        CAPABILITIES:
        - CREATE_QUERY: Create new SQL queries to monitor specific aspects of the data
        - CREATE_SUBSCRIPTION: Set up scheduled execution of queries with notifications
        - REFINE_QUERY: Modify existing queries based on results and learnings
        - ARCHIVE_QUERY: Archive queries that are no longer useful
        - ARCHIVE_SUBSCRIPTION: Archive subscriptions that are no longer needed

        GUIDELINES:
        1. UNDERSTAND FIRST: Carefully analyze the schema and existing queries before acting
        2. START SMALL: Create focused queries that monitor specific metrics or patterns
        3. ITERATE: Refine queries based on results - if a query returns too many/few results, adjust it
        4. BE SPECIFIC: SQL queries should be precise and efficient
        5. AVOID DUPLICATES: Don't create queries that duplicate existing ones
        6. RESPECT LIMITS: Stay within the configured MaxQueries and MaxSubscriptionsPerQuery limits
        7. NOTIFY WISELY: Only flag findings as urgent when they genuinely require immediate attention

        QUERY BEST PRACTICES:
        - ALWAYS use fully qualified table names with schema: schema_name.table_name (e.g., public.users, dbo.orders)
        - Include appropriate WHERE clauses to filter recent data
        - Use LIMIT clauses to control result size
        - Include ORDER BY for meaningful result ordering
        - Use appropriate aggregations (COUNT, SUM, AVG) for metrics
        - Consider time-based filtering (e.g., WHERE created_at > NOW() - INTERVAL '1 day')

        SUBSCRIPTION BEST PRACTICES:
        - Use reasonable cron expressions (not too frequent to avoid noise)
        - Common schedules: "0 * * * *" (hourly), "0 0 * * *" (daily), "*/15 * * * *" (every 15 min)
        - Match frequency to the nature of the monitoring (urgent issues = more frequent)

        RESPONSE FORMAT:
        You MUST respond with valid JSON in this exact format:
        {
            "analysis": "Summary of your analysis of the current state",
            "findings": ["Finding 1", "Finding 2"],
            "actions": [
                {
                    "actionType": "CREATE_QUERY" | "CREATE_SUBSCRIPTION" | "REFINE_QUERY" | "ARCHIVE_QUERY" | "ARCHIVE_SUBSCRIPTION",
                    "reasoning": "Why this action is needed",
                    "parameters": {
                        // For CREATE_QUERY:
                        "name": "Query name",
                        "sql": "SELECT ...",
                        "description": "What this query monitors"

                        // For CREATE_SUBSCRIPTION:
                        "queryId": 123,  // or "queryName" for newly created queries
                        "cronExpression": "0 * * * *",
                        "notificationTrigger": "OnResultCountChange" | "OnlyOnResults" | "Always"

                        // For REFINE_QUERY:
                        "queryId": 123,
                        "newSql": "SELECT ...",
                        "reason": "Why the change"

                        // For ARCHIVE_QUERY/ARCHIVE_SUBSCRIPTION:
                        "queryId": 123, // or "subscriptionId"
                        "reason": "Why archiving"
                    }
                }
            ],
            "shouldNotify": true/false,
            "notificationReason": "Why this is urgent (if shouldNotify is true)"
        }
        """;

    private const string ResponseFormatSection = """


        RESPONSE FORMAT:
        You MUST respond with valid JSON in this exact format:
        {
            "analysis": "Summary of your analysis of the current state",
            "findings": ["Finding 1", "Finding 2"],
            "actions": [
                {
                    "actionType": "CREATE_QUERY" | "CREATE_SUBSCRIPTION" | "REFINE_QUERY" | "ARCHIVE_QUERY" | "ARCHIVE_SUBSCRIPTION",
                    "reasoning": "Why this action is needed",
                    "parameters": {
                        // For CREATE_QUERY:
                        "name": "Query name",
                        "sql": "SELECT ...",
                        "description": "What this query monitors"

                        // For CREATE_SUBSCRIPTION:
                        "queryId": 123,  // or "queryName" for newly created queries
                        "cronExpression": "0 * * * *",
                        "notificationTrigger": "OnResultCountChange" | "OnlyOnResults" | "Always"

                        // For REFINE_QUERY:
                        "queryId": 123,
                        "newSql": "SELECT ...",
                        "reason": "Why the change"

                        // For ARCHIVE_QUERY/ARCHIVE_SUBSCRIPTION:
                        "queryId": 123, // or "subscriptionId"
                        "reason": "Why archiving"
                    }
                }
            ],
            "shouldNotify": true/false,
            "notificationReason": "Why this is urgent (if shouldNotify is true)"
        }
        """;

    public const string InitialSetupSystemPrompt = """
        You are an autonomous database monitoring agent. Your role is to analyze a data source schema
        and create an initial set of monitoring queries based on the user's instructions.

        This is the INITIAL SETUP phase. You should:
        1. Analyze the schema to understand the data model
        2. Identify tables and patterns relevant to the user's monitoring goals
        3. Create an initial set of 2-5 targeted queries that address the monitoring requirements
        4. Set up subscriptions for each query with appropriate schedules

        CRITICAL:
        - Create focused, specific queries that directly address the user's instructions
        - Don't try to monitor everything - focus on what the user asked for
        - Start with conservative schedules (hourly or daily) that can be adjusted later
        - Each query should monitor a specific aspect mentioned in the instructions
        """ + ResponseFormatSection;

    public const string RefinementSystemPrompt = """
        You are an autonomous database monitoring agent receiving feedback from a user.
        The user wants you to adjust your monitoring approach based on their feedback.

        Carefully consider the feedback and determine what changes to make:
        - Create new queries if the user wants to monitor something new
        - Refine existing queries if they're not capturing the right data
        - Archive queries that are no longer needed
        - Adjust subscription frequencies if needed

        Be responsive to the feedback while maintaining the overall monitoring goals.
        """ + ResponseFormatSection;

    #endregion

    #region User Prompt Builders

    /// <summary>
    /// Builds the user prompt for a think cycle execution.
    /// </summary>
    public static string BuildThinkCyclePrompt(
        Beacon.Core.Data.Entities.AiActor actor,
        string schemaContext,
        List<QueryContext> existingQueries,
        int? triggeringSubscriptionId,
        string? recentResults)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## Actor Instructions");
        sb.AppendLine(actor.Instructions);
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(actor.AdditionalContext))
        {
            sb.AppendLine("## Additional Context");
            sb.AppendLine(actor.AdditionalContext);
            sb.AppendLine();
        }

        sb.AppendLine("## Data Source Schema");
        sb.AppendLine(schemaContext);
        sb.AppendLine();

        sb.AppendLine("## Current Configuration");
        sb.AppendLine($"- Max Queries: {actor.MaxQueries}");
        sb.AppendLine($"- Max Subscriptions per Query: {actor.MaxSubscriptionsPerQuery}");
        sb.AppendLine($"- Think Cycle Count: {actor.ThinkCount}");
        sb.AppendLine();

        if (existingQueries.Count > 0)
        {
            sb.AppendLine("## Existing Queries & Recent Results");
            foreach (var query in existingQueries)
            {
                sb.AppendLine($"### Query ID: {query.QueryId} - {query.QueryName}");
                sb.AppendLine($"**SQL:**");
                sb.AppendLine("```sql");
                sb.AppendLine(query.Sql);
                sb.AppendLine("```");

                if (query.Subscriptions.Count > 0)
                {
                    sb.AppendLine("**Subscriptions:**");
                    foreach (var sub in query.Subscriptions)
                    {
                        sb.AppendLine($"- ID {sub.SubscriptionId}: {sub.CronExpression} ({sub.NotificationTrigger})");
                        if (sub.LastExecutionTime.HasValue)
                        {
                            sb.AppendLine($"  Last executed: {sub.LastExecutionTime:yyyy-MM-dd HH:mm}");
                            sb.AppendLine($"  Last result count: {sub.LastResultCount ?? 0}");
                        }
                    }
                }
                sb.AppendLine();
            }
        }
        else
        {
            sb.AppendLine("## Existing Queries");
            sb.AppendLine("No queries exist yet. This is the initial setup.");
            sb.AppendLine();
        }

        if (triggeringSubscriptionId.HasValue)
        {
            sb.AppendLine("## Triggering Event");
            sb.AppendLine($"This think cycle was triggered by subscription ID {triggeringSubscriptionId}.");
            if (!string.IsNullOrWhiteSpace(recentResults))
            {
                sb.AppendLine("**Recent Results:**");
                sb.AppendLine(recentResults);
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Your Task");
        sb.AppendLine("Analyze the current state and determine if any actions are needed:");
        sb.AppendLine("1. Are the existing queries capturing the right data?");
        sb.AppendLine("2. Are there any patterns or anomalies in the recent results?");
        sb.AppendLine("3. Should any queries be refined or new ones created?");
        sb.AppendLine("4. Is there anything that requires immediate notification?");
        sb.AppendLine();
        sb.AppendLine("Respond with your analysis and action plan in JSON format.");

        return sb.ToString();
    }

    /// <summary>
    /// Builds the user prompt for initial actor setup.
    /// </summary>
    public static string BuildInitialSetupPrompt(
        Beacon.Core.Data.Entities.AiActor actor,
        string schemaContext)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## Actor Instructions");
        sb.AppendLine(actor.Instructions);
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(actor.AdditionalContext))
        {
            sb.AppendLine("## Additional Context");
            sb.AppendLine(actor.AdditionalContext);
            sb.AppendLine();
        }

        sb.AppendLine("## Data Source Schema");
        sb.AppendLine(schemaContext);
        sb.AppendLine();

        sb.AppendLine("## Configuration Limits");
        sb.AppendLine($"- Max Queries: {actor.MaxQueries}");
        sb.AppendLine($"- Max Subscriptions per Query: {actor.MaxSubscriptionsPerQuery}");
        sb.AppendLine();

        sb.AppendLine("## Your Task");
        sb.AppendLine("This is the INITIAL SETUP for this monitoring actor.");
        sb.AppendLine("Based on the instructions and schema, create an initial set of monitoring queries.");
        sb.AppendLine();
        sb.AppendLine("Guidelines:");
        sb.AppendLine("1. Create 2-5 focused queries that address the monitoring requirements");
        sb.AppendLine("2. Each query should monitor a specific aspect of the user's instructions");
        sb.AppendLine("3. Set up subscriptions with appropriate schedules (start conservative)");
        sb.AppendLine("4. Consider what data exists and what patterns are worth monitoring");
        sb.AppendLine();
        sb.AppendLine("Respond with your analysis and initial setup plan in JSON format.");

        return sb.ToString();
    }

    /// <summary>
    /// Builds the user prompt for processing user feedback/refinement.
    /// </summary>
    public static string BuildRefinementPrompt(
        Beacon.Core.Data.Entities.AiActor actor,
        string schemaContext,
        List<QueryContext> existingQueries,
        string userFeedback)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## User Feedback");
        sb.AppendLine("The user has provided the following feedback:");
        sb.AppendLine();
        sb.AppendLine($"> {userFeedback}");
        sb.AppendLine();

        sb.AppendLine("## Current Actor Instructions");
        sb.AppendLine(actor.Instructions);
        sb.AppendLine();

        sb.AppendLine("## Data Source Schema");
        sb.AppendLine(schemaContext);
        sb.AppendLine();

        if (existingQueries.Count > 0)
        {
            sb.AppendLine("## Existing Queries");
            foreach (var query in existingQueries)
            {
                sb.AppendLine($"### Query ID: {query.QueryId} - {query.QueryName}");
                sb.AppendLine("```sql");
                sb.AppendLine(query.Sql);
                sb.AppendLine("```");
                if (query.Subscriptions.Count > 0)
                {
                    sb.AppendLine("Subscriptions:");
                    foreach (var sub in query.Subscriptions)
                    {
                        sb.AppendLine($"- ID {sub.SubscriptionId}: {sub.CronExpression}");
                    }
                }
                sb.AppendLine();
            }
        }

        sb.AppendLine("## Your Task");
        sb.AppendLine("Consider the user's feedback and determine what changes to make.");
        sb.AppendLine("Respond with your analysis and action plan in JSON format.");

        return sb.ToString();
    }

    #endregion

    #region Helper Models

    /// <summary>
    /// Context about an existing query for the think cycle prompt.
    /// </summary>
    public class QueryContext
    {
        public int QueryId { get; set; }
        public string QueryName { get; set; } = null!;
        public string Sql { get; set; } = null!;
        public string? Description { get; set; }
        public List<SubscriptionContext> Subscriptions { get; set; } = new();
    }

    /// <summary>
    /// Context about a subscription for the think cycle prompt.
    /// </summary>
    public class SubscriptionContext
    {
        public int SubscriptionId { get; set; }
        public string CronExpression { get; set; } = null!;
        public string NotificationTrigger { get; set; } = null!;
        public DateTime? LastExecutionTime { get; set; }
        public int? LastResultCount { get; set; }
        public bool? WasAnomaly { get; set; }
    }

    #endregion

    #region Schema Formatting

    /// <summary>
    /// Formats database schema metadata into a concise prompt context.
    /// </summary>
    public static string FormatSchemaContext(List<TableSchemaInfo> tables, int maxTables = 50)
    {
        var sb = new StringBuilder();

        sb.AppendLine("**IMPORTANT: Always use fully qualified table names (schema.table_name) in SQL queries.**");
        sb.AppendLine();

        var tablesToInclude = tables.Take(maxTables).ToList();

        foreach (var table in tablesToInclude)
        {
            sb.AppendLine($"### {table.FullyQualifiedName}");
            sb.AppendLine("Columns:");
            foreach (var col in table.Columns)
            {
                var markers = new List<string>();
                if (col.IsPrimaryKey) markers.Add("PK");
                if (col.IsForeignKey) markers.Add("FK");
                if (!col.IsNullable) markers.Add("NOT NULL");

                var markerStr = markers.Count > 0 ? $" [{string.Join(", ", markers)}]" : "";
                sb.AppendLine($"- {col.Name} ({col.DataType}){markerStr}");

                if (col.IsForeignKey && !string.IsNullOrEmpty(col.ForeignKeyReference))
                {
                    sb.AppendLine($"  → {col.ForeignKeyReference}");
                }
            }
            sb.AppendLine();
        }

        if (tables.Count > maxTables)
        {
            sb.AppendLine($"... and {tables.Count - maxTables} more tables");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Represents table schema information for the prompt.
    /// </summary>
    public class TableSchemaInfo
    {
        public string SchemaName { get; set; } = "public";
        public string TableName { get; set; } = null!;
        public string FullyQualifiedName => $"{SchemaName}.{TableName}";
        public List<ColumnSchemaInfo> Columns { get; set; } = new();
    }

    /// <summary>
    /// Represents column schema information for the prompt.
    /// </summary>
    public class ColumnSchemaInfo
    {
        public string Name { get; set; } = null!;
        public string DataType { get; set; } = null!;
        public bool IsPrimaryKey { get; set; }
        public bool IsForeignKey { get; set; }
        public bool IsNullable { get; set; }
        public string? ForeignKeyReference { get; set; }
    }

    #endregion
}
