using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities;
using Semantico.Core.Data.Enums;
using Semantico.Core.Exceptions;
using Semantico.Core.Models;
using Semantico.AI.Services.Ai.DocumentationAgent.Models;
using Semantico.Core.Models.Ai;

using Semantico.AI.Services.LlmProviders;

namespace Semantico.AI.Services.Ai.DocumentationAgent;

/// <summary>
/// Implementation of the documentation agent service using Microsoft Agent Framework.
/// Orchestrates a multi-phase workflow for generating comprehensive database documentation.
/// </summary>
public class DocumentationAgentService : IDocumentationAgentService
{
    private readonly IDbContextFactory<SemanticoContext> _contextFactory;
    private readonly DocumentationAgentTools _tools;
    private readonly LlmProviderFactory _llmProviderFactory;
    private readonly ILogger<DocumentationAgentService> _logger;

    private const int MaxRetries = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    public DocumentationAgentService(
        IDbContextFactory<SemanticoContext> contextFactory,
        DocumentationAgentTools tools,
        LlmProviderFactory llmProviderFactory,
        ILogger<DocumentationAgentService> logger)
    {
        _contextFactory = contextFactory;
        _tools = tools;
        _llmProviderFactory = llmProviderFactory;
        _logger = logger;
    }

    public async Task<DocumentationAgentRun> StartDocumentationAsync(
        int dataSourceId,
        int userId,
        DocumentationAgentOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting documentation agent for DataSource {DataSourceId}", dataSourceId);

        options ??= new DocumentationAgentOptions();

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        // Validate data source exists
        var dataSource = await context.DataSources
            .FirstOrDefaultAsync(ds => ds.Id == dataSourceId, cancellationToken)
            ?? throw new SemanticoException($"DataSource with ID {dataSourceId} not found");

        // Create the documentation entity first
        var documentation = new DataSourceDocumentation
        {
            DataSourceId = dataSourceId,
            Title = options.Title ?? $"{dataSource.Name} Documentation",
            GeneratedByModel = "agent-workflow",
            GeneratedAt = DateTime.UtcNow,
            GeneratedByUserId = userId,
            Status = DocumentationStatus.Draft,
            CreatedBy = "agent",
            ModifiedBy = "agent"
        };

        context.DataSourceDocumentations.Add(documentation);
        await context.SaveChangesAsync(cancellationToken);

        // Create the agent run tracking entity
        var agentRun = new DocumentationAgentRun
        {
            DataSourceId = dataSourceId,
            DocumentationId = documentation.Id,
            StartedByUserId = userId,
            CurrentPhase = DocumentationAgentPhase.NotStarted,
            Status = DocumentationAgentStatus.Pending,
            StartedAt = DateTime.UtcNow,
            ProgressPercent = 0,
            ProgressMessage = "Initializing documentation agent..."
        };

        context.DocumentationAgentRuns.Add(agentRun);
        await context.SaveChangesAsync(cancellationToken);

        // Enqueue the workflow as a Hangfire background job for durable execution
        BackgroundJob.Enqueue<IDocumentationAgentService>(
            service => service.ExecuteWorkflowBackgroundAsync(agentRun.Id, options));

        _logger.LogInformation("Documentation agent workflow enqueued for AgentRun {AgentRunId}", agentRun.Id);

        return agentRun;
    }

    /// <summary>
    /// Public method for Hangfire to execute the workflow in the background.
    /// </summary>
    public async Task ExecuteWorkflowBackgroundAsync(int agentRunId, DocumentationAgentOptions options)
    {
        try
        {
            await ExecuteWorkflowAsync(agentRunId, options, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Documentation workflow failed for AgentRun {AgentRunId}", agentRunId);
            await MarkRunFailedAsync(agentRunId, ex.Message);
        }
    }

    /// <summary>
    /// Public method for Hangfire to execute retry workflow in the background.
    /// </summary>
    public async Task ExecuteRetryBackgroundAsync(int agentRunId, List<string> failedTables)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            var agentRun = await context.DocumentationAgentRuns.FirstOrDefaultAsync(r => r.Id == agentRunId);

            if (agentRun == null)
            {
                _logger.LogError("Agent run {AgentRunId} not found for retry", agentRunId);
                return;
            }

            var state = new DocumentationWorkflowState
            {
                DataSourceId = agentRun.DataSourceId,
                DocumentationId = agentRun.DocumentationId ?? 0,
                AgentRunId = agentRunId,
                CurrentPhase = DocumentationAgentPhase.TableDocumentation,
                DiscoveredTables = failedTables
            };

            await ExecuteTableDocumentationPhaseAsync(state, CancellationToken.None);
            await MarkRunCompletedAsync(agentRunId, state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retry workflow failed for AgentRun {AgentRunId}", agentRunId);
            await MarkRunFailedAsync(agentRunId, ex.Message);
        }
    }

    public async Task<DocumentationAgentRun> ResumeDocumentationAsync(
        int agentRunId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var agentRun = await context.DocumentationAgentRuns
            .FirstOrDefaultAsync(r => r.Id == agentRunId, cancellationToken)
            ?? throw new SemanticoException($"Agent run with ID {agentRunId} not found");

        if (agentRun.Status == DocumentationAgentStatus.Running)
            throw new SemanticoException("Agent run is already running");

        if (agentRun.Status == DocumentationAgentStatus.Completed)
            throw new SemanticoException("Agent run is already completed");

        // Load checkpoint state
        var state = agentRun.CheckpointStateJson != null
            ? JsonSerializer.Deserialize<DocumentationWorkflowState>(agentRun.CheckpointStateJson)
            : null;

        agentRun.Status = DocumentationAgentStatus.Running;
        agentRun.ProgressMessage = "Resuming from checkpoint...";
        await context.SaveChangesAsync(cancellationToken);

        // Enqueue resume workflow as Hangfire job
        var options = state?.Options ?? new DocumentationAgentOptions();
        BackgroundJob.Enqueue<IDocumentationAgentService>(
            service => service.ExecuteWorkflowBackgroundAsync(agentRunId, options));

        _logger.LogInformation("Documentation agent workflow resume enqueued for AgentRun {AgentRunId}", agentRunId);

        return agentRun;
    }

    public async Task CancelDocumentationAsync(
        int agentRunId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var agentRun = await context.DocumentationAgentRuns
            .FirstOrDefaultAsync(r => r.Id == agentRunId, cancellationToken)
            ?? throw new SemanticoException($"Agent run with ID {agentRunId} not found");

        agentRun.Status = DocumentationAgentStatus.Cancelled;
        agentRun.CompletedAt = DateTime.UtcNow;
        agentRun.ProgressMessage = "Cancelled by user";
        await context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Agent run {AgentRunId} cancelled", agentRunId);
    }

    public async Task<DocumentationAgentRun?> GetRunStatusAsync(
        int agentRunId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.DocumentationAgentRuns
            .Include(r => r.DataSource)
            .Include(r => r.Documentation)
            .FirstOrDefaultAsync(r => r.Id == agentRunId, cancellationToken);
    }

    public async Task<List<DocumentationAgentRun>> GetRunsForDataSourceAsync(
        int dataSourceId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.DocumentationAgentRuns
            .Where(r => r.DataSourceId == dataSourceId)
            .OrderByDescending(r => r.StartedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<DocumentationAgentRun> RetryFailedTablesAsync(
        int agentRunId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var originalRun = await context.DocumentationAgentRuns
            .FirstOrDefaultAsync(r => r.Id == agentRunId, cancellationToken)
            ?? throw new SemanticoException($"Agent run with ID {agentRunId} not found");

        if (string.IsNullOrEmpty(originalRun.FailedTablesJson))
            throw new SemanticoException("No failed tables to retry");

        var failedTableObjects = JsonSerializer.Deserialize<List<TableFailure>>(originalRun.FailedTablesJson)
            ?? throw new SemanticoException("Failed to parse failed tables");

        var failedTables = failedTableObjects.Select(f => f.TableName).ToList();

        // Create a new agent run specifically for retrying
        var retryRun = new DocumentationAgentRun
        {
            DataSourceId = originalRun.DataSourceId,
            DocumentationId = originalRun.DocumentationId,
            StartedByUserId = originalRun.StartedByUserId,
            CurrentPhase = DocumentationAgentPhase.TableDocumentation,
            Status = DocumentationAgentStatus.Pending,
            StartedAt = DateTime.UtcNow,
            ProgressPercent = 0,
            ProgressMessage = $"Retrying {failedTables.Count} failed tables...",
            // Pre-populate with the tables to retry
            DiscoveredTablesJson = JsonSerializer.Serialize(failedTables),
            TotalTablesDiscovered = failedTables.Count
        };

        context.DocumentationAgentRuns.Add(retryRun);
        await context.SaveChangesAsync(cancellationToken);

        // Enqueue retry workflow as Hangfire job
        BackgroundJob.Enqueue<IDocumentationAgentService>(
            service => service.ExecuteRetryBackgroundAsync(retryRun.Id, failedTables));

        _logger.LogInformation("Retry workflow enqueued for AgentRun {AgentRunId} with {TableCount} tables",
            retryRun.Id, failedTables.Count);

        return retryRun;
    }

    #region Private Workflow Methods

    private async Task ExecuteWorkflowAsync(
        int agentRunId,
        DocumentationAgentOptions options,
        CancellationToken cancellationToken,
        DocumentationWorkflowState? existingState = null)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var agentRun = await context.DocumentationAgentRuns
            .FirstOrDefaultAsync(r => r.Id == agentRunId, cancellationToken)
            ?? throw new SemanticoException($"Agent run with ID {agentRunId} not found");

        // Initialize or restore state
        var state = existingState ?? new DocumentationWorkflowState
        {
            DataSourceId = agentRun.DataSourceId,
            DocumentationId = agentRun.DocumentationId ?? 0,
            AgentRunId = agentRunId,
            Options = options
        };

        // Update status to running
        agentRun.Status = DocumentationAgentStatus.Running;
        await context.SaveChangesAsync(cancellationToken);

        try
        {
            // Phase 1: Discovery (if not already done)
            if (state.CurrentPhase < DocumentationAgentPhase.TableDocumentation)
            {
                await ExecuteDiscoveryPhaseAsync(state, cancellationToken);
            }

            // Phase 2: Table Documentation (parallel with batching)
            if (state.CurrentPhase < DocumentationAgentPhase.Synthesis)
            {
                await ExecuteTableDocumentationPhaseAsync(state, cancellationToken);
            }

            // Phase 3: Synthesis
            await ExecuteSynthesisPhaseAsync(state, cancellationToken);

            // Mark as completed
            await MarkRunCompletedAsync(agentRunId, state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Workflow execution failed at phase {Phase}", state.CurrentPhase);
            await SaveCheckpointAsync(state);
            throw;
        }
    }

    private async Task ExecuteDiscoveryPhaseAsync(
        DocumentationWorkflowState state,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Executing Discovery phase for DataSource {DataSourceId}", state.DataSourceId);

        // Get table list
        var tableListResult = await _tools.GetTableList(state.DataSourceId, cancellationToken);

        // Filter tables by selected schemas if specified
        var allTables = tableListResult.Tables;
        if (state.Options.SelectedSchemas?.Any() == true)
        {
            _logger.LogInformation("Filtering tables by selected schemas: {Schemas}", string.Join(", ", state.Options.SelectedSchemas));
            allTables = allTables.Where(t => state.Options.SelectedSchemas.Contains(t.SchemaName)).ToList();
        }

        state.DiscoveredTables = allTables.Select(t => t.TableName).ToList();

        await _tools.UpdateProgress(state.AgentRunId, "Discovery", 15, $"Found {state.DiscoveredTables.Count} tables. Analyzing relationships...", cancellationToken);

        // Group tables by domain using LLM
        state.DomainGroups = await GroupTablesByDomainAsync(state.DataSourceId, allTables, cancellationToken);

        state.CurrentPhase = DocumentationAgentPhase.Discovery;
        await SaveCheckpointAsync(state);

        await _tools.UpdateProgress(state.AgentRunId, "Discovery", 20, $"Discovery complete. Identified {state.DomainGroups.Count} domain groups.", cancellationToken);
    }

    private async Task ExecuteTableDocumentationPhaseAsync(
        DocumentationWorkflowState state,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Executing Table Documentation phase for {TableCount} tables", state.DiscoveredTables.Count);

        var tablesToProcess = state.DiscoveredTables
            .Except(state.CompletedTables)
            .ToList();

        var totalTables = state.DiscoveredTables.Count;
        var batchSize = state.Options.MaxParallelTables;

        for (int i = state.CurrentBatchIndex; i < tablesToProcess.Count; i += batchSize)
        {
            var batch = tablesToProcess.Skip(i).Take(batchSize).ToList();
            state.CurrentBatchIndex = i;

            var progressPercent = 20 + (int)((i / (float)totalTables) * 60);
            await _tools.UpdateProgress(
                state.AgentRunId,
                "TableDocumentation",
                progressPercent,
                $"Documenting tables {i + 1}-{Math.Min(i + batchSize, totalTables)} of {totalTables}...",
                cancellationToken);

            // Process batch in parallel
            var tasks = batch.Select(tableName => DocumentTableWithRetryAsync(state, tableName, cancellationToken));
            var results = await Task.WhenAll(tasks);

            // Update state with results
            foreach (var result in results)
            {
                if (result.Success)
                    state.CompletedTables.Add(result.TableName);
                else
                    state.FailedTables.Add(new TableFailure
                    {
                        TableName = result.TableName,
                        ErrorMessage = result.ErrorMessage ?? "Unknown error",
                        RetryCount = result.RetryCount,
                        LastAttempt = DateTime.UtcNow
                    });
            }

            // Save checkpoint after each batch
            await SaveCheckpointAsync(state);
        }

        state.CurrentPhase = DocumentationAgentPhase.TableDocumentation;
        await _tools.UpdateProgress(
            state.AgentRunId,
            "TableDocumentation",
            80,
            $"Table documentation complete. {state.CompletedTables.Count} succeeded, {state.FailedTables.Count} failed.",
            cancellationToken);
    }

    private async Task<TableDocumentationResult> DocumentTableWithRetryAsync(
        DocumentationWorkflowState state,
        string tableName,
        CancellationToken cancellationToken)
    {
        var retryCount = 0;
        Exception? lastException = null;

        while (retryCount < state.Options.MaxRetries)
        {
            try
            {
                await DocumentSingleTableAsync(state, tableName, cancellationToken);
                return new TableDocumentationResult { TableName = tableName, Success = true };
            }
            catch (Exception ex)
            {
                lastException = ex;
                retryCount++;
                _logger.LogError(ex, "Failed to document table {TableName}, attempt {Attempt}", tableName, retryCount);

                if (retryCount < state.Options.MaxRetries)
                {
                    await Task.Delay(RetryDelay * retryCount, cancellationToken);
                }
            }
        }

        return new TableDocumentationResult
        {
            TableName = tableName,
            Success = false,
            ErrorMessage = GetFullExceptionMessage(lastException),
            RetryCount = retryCount
        };
    }

    /// <summary>
    /// Extracts the full exception message including all inner exceptions.
    /// Useful for EF Core exceptions where the real error is in the inner exception.
    /// </summary>
    private static string GetFullExceptionMessage(Exception? exception)
    {
        if (exception == null)
            return "Unknown error";

        var messages = new List<string>();
        var currentException = exception;

        while (currentException != null)
        {
            messages.Add($"{currentException.GetType().Name}: {currentException.Message}");
            currentException = currentException.InnerException;
        }

        return string.Join(" --> ", messages);
    }

    private async Task DocumentSingleTableAsync(
        DocumentationWorkflowState state,
        string tableName,
        CancellationToken cancellationToken)
    {
        // Get table metadata
        var metadata = await _tools.GetTableMetadata(state.DataSourceId, tableName, cancellationToken: cancellationToken);
        if (!metadata.Success)
            throw new SemanticoException($"Failed to get metadata for table {tableName}: {metadata.ErrorMessage}");

        // Get relationships
        var relationships = await _tools.GetRelationships(state.DataSourceId, tableName, cancellationToken: cancellationToken);

        // Get sample data if enabled
        SampleDataResult? sampleData = null;
        if (state.Options.IncludeSampleData)
        {
            sampleData = await _tools.QuerySampleData(state.DataSourceId, tableName, state.Options.MaxSampleRows, metadata.SchemaName, cancellationToken);
        }

        // Generate documentation using LLM
        var documentation = await GenerateTableDocumentationAsync(state, tableName, metadata, relationships, sampleData, cancellationToken);

        // Save the section
        var saveResult = await _tools.SaveDocumentationSection(
            state.DocumentationId,
            tableName,
            "TableDetail",
            documentation,
            tableName,
            100 + state.CompletedTables.Count, // Sort order after overview sections
            cancellationToken);

        if (!saveResult.Success)
        {
            throw new SemanticoException($"Failed to save documentation section for table {tableName}: {saveResult.ErrorMessage}");
        }
    }

    private async Task ExecuteSynthesisPhaseAsync(
        DocumentationWorkflowState state,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Executing Synthesis phase");

        await _tools.UpdateProgress(state.AgentRunId, "Synthesis", 85, "Generating overview and cross-table insights...", cancellationToken);

        // Get all existing sections for context
        var existingSections = await _tools.GetExistingSections(state.DocumentationId, cancellationToken: cancellationToken);

        // Generate overview section
        var overview = await GenerateOverviewAsync(state, existingSections.Sections, cancellationToken);
        var overviewResult = await _tools.SaveDocumentationSection(
            state.DocumentationId,
            "Overview",
            "Overview",
            overview,
            sortOrder: 1,
            cancellationToken: cancellationToken);

        if (!overviewResult.Success)
        {
            throw new SemanticoException($"Failed to save overview section: {overviewResult.ErrorMessage}");
        }

        // Generate architecture/ERD section
        var architecture = await GenerateArchitectureAsync(state, cancellationToken);
        var architectureResult = await _tools.SaveDocumentationSection(
            state.DocumentationId,
            "Architecture & Relationships",
            "Architecture",
            architecture,
            sortOrder: 2,
            cancellationToken: cancellationToken);

        if (!architectureResult.Success)
        {
            throw new SemanticoException($"Failed to save architecture section: {architectureResult.ErrorMessage}");
        }

        state.CurrentPhase = DocumentationAgentPhase.Synthesis;
        await _tools.UpdateProgress(state.AgentRunId, "Synthesis", 100, "Documentation generation complete!", cancellationToken);
    }

    private async Task<List<TableGroup>> GroupTablesByDomainAsync(
        int dataSourceId,
        List<TableSummary> tables,
        CancellationToken cancellationToken)
    {
        // Simple heuristic grouping for now - can be enhanced with LLM
        var groups = new List<TableGroup>();

        // Group by common prefixes
        var prefixGroups = tables
            .GroupBy(t => GetTablePrefix(t.TableName))
            .Where(g => !string.IsNullOrEmpty(g.Key));

        foreach (var group in prefixGroups)
        {
            groups.Add(new TableGroup
            {
                GroupName = group.Key,
                Tables = group.Select(t => t.TableName).ToList()
            });
        }

        // Add remaining tables to "Other" group
        var groupedTables = groups.SelectMany(g => g.Tables).ToHashSet();
        var ungroupedTables = tables
            .Where(t => !groupedTables.Contains(t.TableName))
            .Select(t => t.TableName)
            .ToList();

        if (ungroupedTables.Count > 0)
        {
            groups.Add(new TableGroup
            {
                GroupName = "Other",
                Tables = ungroupedTables
            });
        }

        return groups;
    }

    private static string GetTablePrefix(string tableName)
    {
        var underscoreIndex = tableName.IndexOf('_');
        if (underscoreIndex > 2 && underscoreIndex < 20)
            return tableName[..underscoreIndex];

        return string.Empty;
    }

    private async Task<string> GenerateTableDocumentationAsync(
        DocumentationWorkflowState state,
        string tableName,
        TableMetadataResult metadata,
        RelationshipsResult relationships,
        SampleDataResult? sampleData,
        CancellationToken cancellationToken)
    {
        var columnsSection = DocumentationPrompts.FormatColumnsSection(metadata.Columns);
        var relationshipsSection = DocumentationPrompts.FormatRelationshipsSection(relationships);
        var sampleDataSection = DocumentationPrompts.FormatSampleDataSection(sampleData);

        var prompt = DocumentationPrompts.BuildTableDocumentationPrompt(
            tableName,
            columnsSection,
            relationshipsSection,
            sampleDataSection);

        var request = new LlmRequest
        {
            Messages = [new ChatMessage(ConversationRole.User, prompt)],
            SystemPrompt = DocumentationPrompts.TableDocumentationSystemPrompt,
            Temperature = 0.3m,
            MaxTokens = 2000
        };

        return await CallLlmAndTrackUsageAsync(state, request, cancellationToken);
    }

    private async Task<string> GenerateOverviewAsync(
        DocumentationWorkflowState state,
        List<SectionSummary> sections,
        CancellationToken cancellationToken)
    {
        var totalDocumented = sections.Count(s => s.SectionType == "TableDetail");
        var domainGroupsSummary = string.Join(", ", state.DomainGroups.Select(g => $"{g.GroupName} ({g.Tables.Count} tables)"));

        var prompt = DocumentationPrompts.BuildOverviewPrompt(
            state.DataSourceId,
            state.DiscoveredTables.Count,
            domainGroupsSummary,
            totalDocumented);

        var request = new LlmRequest
        {
            Messages = [new ChatMessage(ConversationRole.User, prompt)],
            SystemPrompt = DocumentationPrompts.OverviewSystemPrompt,
            Temperature = 0.4m,
            MaxTokens = 1500
        };

        return await CallLlmAndTrackUsageAsync(state, request, cancellationToken);
    }

    private async Task<string> GenerateArchitectureAsync(
        DocumentationWorkflowState state,
        CancellationToken cancellationToken)
    {
        // Build a relationship summary and identify hub tables
        var relationshipsSb = new System.Text.StringBuilder();
        var tableRelationshipCounts = new Dictionary<string, int>();

        foreach (var table in state.DiscoveredTables.Take(20)) // Limit for token efficiency
        {
            var rels = await _tools.GetRelationships(state.DataSourceId, table, cancellationToken: cancellationToken);
            if (rels.Success && (rels.OutgoingReferences.Count > 0 || rels.IncomingReferences.Count > 0))
            {
                var totalRels = rels.OutgoingReferences.Count + rels.IncomingReferences.Count;
                tableRelationshipCounts[table] = totalRels;
                relationshipsSb.AppendLine($"- {table}: {rels.OutgoingReferences.Count} outgoing, {rels.IncomingReferences.Count} incoming relationships");
            }
        }

        // Identify hub tables (top 6-8 by relationship count)
        var hubTables = tableRelationshipCounts
            .OrderByDescending(kvp => kvp.Value)
            .Take(8)
            .Select(kvp => kvp.Key)
            .ToList();

        var prompt = DocumentationPrompts.BuildArchitecturePrompt(
            relationshipsSb.ToString(),
            hubTables);

        var request = new LlmRequest
        {
            Messages = [new ChatMessage(ConversationRole.User, prompt)],
            SystemPrompt = DocumentationPrompts.ArchitectureSystemPrompt,
            Temperature = 0.3m,
            MaxTokens = 2000
        };

        return await CallLlmAndTrackUsageAsync(state, request, cancellationToken);
    }

    /// <summary>
    /// Helper method that wraps LLM calls and automatically tracks token usage and cost in the workflow state.
    /// </summary>
    private async Task<string> CallLlmAndTrackUsageAsync(
        DocumentationWorkflowState state,
        LlmRequest request,
        CancellationToken cancellationToken)
    {
        var llmProvider = _llmProviderFactory.CreateProvider();
        var response = await llmProvider.CompleteAsync(request, cancellationToken);

        // Accumulate token usage and cost
        state.TotalTokensUsed += response.TotalTokens;
        state.TotalCost += response.Cost;

        // Track the model used (use the first one encountered)
        if (string.IsNullOrEmpty(state.ModelUsed))
        {
            state.ModelUsed = response.Model;
        }

        return response.Content;
    }

    private async Task SaveCheckpointAsync(DocumentationWorkflowState state)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var agentRun = await context.DocumentationAgentRuns
            .FirstOrDefaultAsync(r => r.Id == state.AgentRunId);

        if (agentRun != null)
        {
            agentRun.CurrentPhase = state.CurrentPhase;
            agentRun.TotalTablesDiscovered = state.DiscoveredTables.Count;
            agentRun.TablesCompleted = state.CompletedTables.Count;
            agentRun.TablesFailed = state.FailedTables.Count;
            agentRun.CurrentBatchIndex = state.CurrentBatchIndex;
            agentRun.DiscoveredTablesJson = JsonSerializer.Serialize(state.DiscoveredTables);
            agentRun.CompletedTablesJson = JsonSerializer.Serialize(state.CompletedTables);
            agentRun.FailedTablesJson = JsonSerializer.Serialize(state.FailedTables);
            agentRun.DomainGroupsJson = JsonSerializer.Serialize(state.DomainGroups);
            agentRun.CheckpointStateJson = JsonSerializer.Serialize(state);
            agentRun.LastCheckpointAt = DateTime.UtcNow;
            state.LastCheckpoint = DateTime.UtcNow;

            await context.SaveChangesAsync();
        }
    }

    private async Task MarkRunCompletedAsync(int agentRunId, DocumentationWorkflowState state)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var agentRun = await context.DocumentationAgentRuns
            .FirstOrDefaultAsync(r => r.Id == agentRunId);

        if (agentRun != null)
        {
            agentRun.Status = DocumentationAgentStatus.Completed;
            agentRun.CurrentPhase = DocumentationAgentPhase.Completed;
            agentRun.CompletedAt = DateTime.UtcNow;
            agentRun.ProgressPercent = 100;
            agentRun.ProgressMessage = "Documentation generation completed successfully";
            agentRun.TablesCompleted = state.CompletedTables.Count;
            agentRun.TablesFailed = state.FailedTables.Count;

            // Update the documentation entity
            if (agentRun.DocumentationId.HasValue)
            {
                var documentation = await context.DataSourceDocumentations
                    .FirstOrDefaultAsync(d => d.Id == agentRun.DocumentationId);

                if (documentation != null)
                {
                    documentation.Status = DocumentationStatus.Draft;
                    documentation.TablesAnalyzed = state.CompletedTables.Count;
                    documentation.TokensUsed = state.TotalTokensUsed;
                    documentation.EstimatedCost = state.TotalCost;
                    if (!string.IsNullOrEmpty(state.ModelUsed))
                    {
                        documentation.GeneratedByModel = state.ModelUsed;
                    }
                }
            }

            await context.SaveChangesAsync();
        }

        _logger.LogInformation("Agent run {AgentRunId} completed. {Completed} tables documented, {Failed} failed",
            agentRunId, state.CompletedTables.Count, state.FailedTables.Count);
    }

    private async Task MarkRunFailedAsync(int agentRunId, string errorMessage)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var agentRun = await context.DocumentationAgentRuns
            .FirstOrDefaultAsync(r => r.Id == agentRunId);

        if (agentRun != null)
        {
            agentRun.Status = DocumentationAgentStatus.Failed;
            agentRun.CurrentPhase = DocumentationAgentPhase.Failed;
            agentRun.CompletedAt = DateTime.UtcNow;
            agentRun.LastError = errorMessage;
            agentRun.ProgressMessage = $"Failed: {errorMessage}";

            await context.SaveChangesAsync();
        }

        _logger.LogError("Agent run {AgentRunId} failed: {Error}", agentRunId, errorMessage);
    }

    #endregion

    private class TableDocumentationResult
    {
        public string TableName { get; set; } = null!;
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public int RetryCount { get; set; }
    }
}
