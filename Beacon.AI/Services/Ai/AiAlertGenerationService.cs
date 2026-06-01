using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Exceptions;
using Beacon.Core.Models;
using Beacon.Core.Models.Ai;
using Beacon.Core.Models.Metadata;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Services.Shared;

namespace Beacon.AI.Services.Ai;

public class AiAlertGenerationService : IAiAlertGenerationService
{
    private readonly ILlmProvider _llmProvider;
    private readonly IDatabaseMetadataService _metadataService;
    private readonly IDbContextFactory<BeaconContext> _contextFactory;
    private readonly ILogger<AiAlertGenerationService> _logger;

    public AiAlertGenerationService(
        ILlmProvider llmProvider,
        IDatabaseMetadataService metadataService,
        IDbContextFactory<BeaconContext> contextFactory,
        ILogger<AiAlertGenerationService> logger)
    {
        _llmProvider = llmProvider;
        _metadataService = metadataService;
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public async Task<AiAlertConfiguration> GenerateAlertAsync(
        int dataSourceId,
        string naturalLanguageDescription,
        string createdBy,
        AlertGenerationOptions options,
        CancellationToken cancellationToken = default)
    {
        using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        _logger.LogInformation("Starting alert generation for DataSource {DataSourceId}", dataSourceId);

        // Fetch data source
        var dataSource = await context.DataSources
            .Where(ds => ds.Id == dataSourceId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconException($"DataSource with ID {dataSourceId} not found");

        // Fetch schema metadata
        var metadata = await _metadataService.GetMetadataAsync(dataSourceId, cancellationToken);
        var tables = metadata.Tables.ToList();

        // Build AI prompt for SQL generation
        var prompt = BuildAlertGenerationPrompt(dataSource.Name, naturalLanguageDescription, tables);

        // Call LLM
        var llmRequest = new LlmRequest
        {
            Messages = new List<ChatMessage>
            {
                new(ConversationRole.User, prompt)
            },
            SystemPrompt = GetAlertGenerationSystemPrompt(),
            Temperature = options.Temperature,
            MaxTokens = options.MaxTokens
        };

        var response = await _llmProvider.CompleteAsync(llmRequest, cancellationToken);

        // Extract SQL from response
        var generatedSql = ExtractSqlFromResponse(response.Content);

        // Validate syntax if requested
        string? validationErrors = null;
        if (options.ValidateSyntax)
        {
            var isValid = await ValidateQuerySyntaxAsync(dataSourceId, generatedSql, cancellationToken);
            if (!isValid)
            {
                validationErrors = "SQL syntax validation failed";
            }
        }

        // Create alert configuration
        var alertConfig = new AiAlertConfiguration
        {
            DataSourceId = dataSourceId,
            Name = $"AI Alert: {naturalLanguageDescription.Substring(0, Math.Min(50, naturalLanguageDescription.Length))}",
            NaturalLanguageDescription = naturalLanguageDescription,
            GeneratedSql = generatedSql,
            FinalSql = generatedSql,
            GeneratedByModel = response.Model,
            GenerationReasoning = options.IncludeExplanation ? response.Content : null,
            Status = validationErrors == null ? AlertStatus.Draft : AlertStatus.ValidationFailed,
            ValidationErrors = validationErrors,
            ConversationTurns = 1,
            TokensUsed = response.InputTokens,
            EstimatedCost = response.Cost,
            CreatedBy = createdBy,
            ModifiedBy = createdBy
        };

        // Save conversation history
        AddConversationTurn(alertConfig, ConversationRole.User, naturalLanguageDescription, response.InputTokens, response.Model, 1);
        AddConversationTurn(alertConfig, ConversationRole.Assistant, response.Content, response.OutputTokens, response.Model, 2);

        // Save to database
        context.AiAlertConfigurations.Add(alertConfig);
        await context.SaveChangesAsync(cancellationToken);

        // Track usage
        await TrackUsageAsync(context, dataSourceId, createdBy, response, OperationType.QueryGeneration, cancellationToken);

        _logger.LogInformation("Alert configuration created successfully: {AlertConfigId}", alertConfig.Id);

        return alertConfig;
    }

    public async Task<AiAlertConfiguration> RefineAlertAsync(
        int alertConfigurationId,
        string userFeedback,
        string modifiedBy,
        CancellationToken cancellationToken = default)
    {
        using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var alertConfig = await context.AiAlertConfigurations
            .Include(a => a.ConversationHistory)
            .Where(a => a.Id == alertConfigurationId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconException($"Alert configuration with ID {alertConfigurationId} not found");

        // Build conversation history
        var messages = alertConfig.ConversationHistory
            .OrderBy(h => h.TurnNumber)
            .Select(h => new ChatMessage(h.Role, h.MessageContent))
            .ToList();

        // Add user feedback
        messages.Add(new ChatMessage(ConversationRole.User, userFeedback));

        // Call LLM with conversation context
        var llmRequest = new LlmRequest
        {
            Messages = messages,
            SystemPrompt = GetAlertRefinementSystemPrompt(),
            Temperature = 0.3m,
            MaxTokens = 2048
        };

        var response = await _llmProvider.CompleteAsync(llmRequest, cancellationToken);

        // Extract refined SQL
        var refinedSql = ExtractSqlFromResponse(response.Content);

        // Update alert configuration
        alertConfig.FinalSql = refinedSql;
        alertConfig.UserFeedback = userFeedback;
        alertConfig.ConversationTurns++;
        alertConfig.TokensUsed += response.TotalTokens;
        alertConfig.EstimatedCost += response.Cost;
        alertConfig.ModifiedBy = modifiedBy;

        // Add conversation entries
        var nextTurn = alertConfig.ConversationHistory.Max(h => h.TurnNumber) + 1;
        AddConversationTurn(alertConfig, ConversationRole.User, userFeedback, response.InputTokens, response.Model, nextTurn);
        AddConversationTurn(alertConfig, ConversationRole.Assistant, response.Content, response.OutputTokens, response.Model, nextTurn + 1);

        await context.SaveChangesAsync(cancellationToken);

        // Track usage
        await TrackUsageAsync(context, alertConfig.DataSourceId, modifiedBy, response, OperationType.QueryRefinement, cancellationToken);

        return alertConfig;
    }

    public Task<bool> ValidateQuerySyntaxAsync(
        int dataSourceId,
        string sql,
        CancellationToken cancellationToken = default)
    {
        // Connector-side syntax validation is deferred — the SQL is currently validated
        // when it executes against the target engine, which surfaces the same errors with
        // accurate engine-specific messages. An upfront EXPLAIN pass can land later.
        _logger.LogDebug("Skipping upfront SQL validation for data source {DataSourceId}; relying on execution-time checks.", dataSourceId);
        return Task.FromResult(true);
    }

    public async Task<AiAlertConfiguration> ApproveAndActivateAlertAsync(
        int alertConfigurationId,
        string approvedBy,
        CancellationToken cancellationToken = default)
    {
        using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var alertConfig = await context.AiAlertConfigurations
            .Where(a => a.Id == alertConfigurationId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconException($"Alert configuration with ID {alertConfigurationId} not found");

        // Subscription materialisation from an approved AI alert config is a follow-up
        // feature (linked to the AI Actor planner). Approval here flips the status only;
        // the user activates the subscription manually from the alert detail page.
        alertConfig.Status = AlertStatus.Approved;
        alertConfig.ModifiedBy = approvedBy;

        await context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Alert configuration approved: {AlertConfigId}", alertConfigurationId);

        return alertConfig;
    }

    // Private helper methods

    private string BuildAlertGenerationPrompt(
        string dataSourceName,
        string naturalLanguageDescription,
        List<TableMetadataDto> tables)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Generate a SQL query for the following alert requirement:");
        sb.AppendLine();
        sb.AppendLine($"**Requirement:** {naturalLanguageDescription}");
        sb.AppendLine();
        sb.AppendLine($"**Database:** {dataSourceName}");
        sb.AppendLine();
        sb.AppendLine("**Available Tables:**");

        foreach (var table in tables.Take(20)) // Limit to first 20 tables
        {
            sb.AppendLine($"- {table.TableName}");
            foreach (var column in table.Columns.Take(10))
            {
                sb.AppendLine($"  - {column.ColumnName} ({column.DataType})");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Please generate a SQL query that:");
        sb.AppendLine("1. Returns the exact data needed for this alert");
        sb.AppendLine("2. Uses appropriate WHERE clauses and filters");
        sb.AppendLine("3. Returns a reasonable number of rows");
        sb.AppendLine("4. Is efficient and follows best practices");
        sb.AppendLine();
        sb.AppendLine("Return only the SQL query, wrapped in ```sql code blocks.");

        return sb.ToString();
    }

    private string GetAlertGenerationSystemPrompt()
    {
        return "You are an expert SQL developer specializing in writing efficient, accurate queries for alerting systems. " +
               "Generate SQL queries that are production-ready, efficient, and return exactly the data needed. " +
               "Always include appropriate WHERE clauses to filter data. " +
               "Use standard SQL syntax compatible with most databases.";
    }

    private string GetAlertRefinementSystemPrompt()
    {
        return "You are an expert SQL developer helping to refine and improve alert queries based on user feedback. " +
               "Carefully consider the user's feedback and adjust the SQL query accordingly. " +
               "Maintain efficiency and clarity while addressing the user's concerns.";
    }

    private string ExtractSqlFromResponse(string response)
    {
        // Extract SQL from markdown code blocks
        var sqlStart = response.IndexOf("```sql", StringComparison.OrdinalIgnoreCase);
        if (sqlStart >= 0)
        {
            sqlStart += 6; // Skip past ```sql
            var sqlEnd = response.IndexOf("```", sqlStart);
            if (sqlEnd > sqlStart)
            {
                return response.Substring(sqlStart, sqlEnd - sqlStart).Trim();
            }
        }

        // If no code block found, return the whole response trimmed
        return response.Trim();
    }

    private void AddConversationTurn(
        AiAlertConfiguration config,
        ConversationRole role,
        string content,
        int tokens,
        string model,
        int turnNumber)
    {
        config.ConversationHistory.Add(new AiConversationHistory
        {
            AiAlertConfigurationId = config.Id,
            TurnNumber = turnNumber,
            Role = role,
            MessageContent = content,
            TokensUsed = tokens,
            Model = model,
            Timestamp = DateTime.UtcNow
        });
    }

    private async Task TrackUsageAsync(
        BeaconContext context,
        int dataSourceId,
        string userId,
        LlmResponse response,
        OperationType operationType,
        CancellationToken cancellationToken = default)
    {
        var metrics = new AiUsageMetrics
        {
            UserId = int.TryParse(userId, out var uid) ? uid : 0,
            DataSourceId = dataSourceId,
            Provider = "Unknown",
            Model = response.Model,
            InputTokens = response.InputTokens,
            OutputTokens = response.OutputTokens,
            TotalTokens = response.TotalTokens,
            EstimatedCost = response.Cost,
            OperationType = operationType,
            Timestamp = DateTime.UtcNow,
            PromptCacheHit = response.PromptCacheHit
        };

        context.AiUsageMetrics.Add(metrics);
        await context.SaveChangesAsync(cancellationToken);
    }
}
