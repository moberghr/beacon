# Multi-Agent Database Documentation Architecture

## Overview

This document outlines the design for implementing a Microsoft-style multi-agent workflow for database documentation generation in Beacon. The system orchestrates multiple specialized LLM agents working in parallel on different parts of the database schema, then aggregates their results into comprehensive documentation.

## Current Architecture (Single-Agent)

The current `AiDocumentationService` uses a single LLM call:

```
User Request
    ↓
Fetch All Table Metadata
    ↓
Build Single Large Prompt
    ↓
Single LLM Call (4-8k tokens)
    ↓
Parse Response into Sections
    ↓
Save Documentation
```

**Limitations:**
- Token limit constraints for large databases (100+ tables)
- Long response times (30-60 seconds for complex schemas)
- Cannot process databases in parallel
- No progress visibility during generation
- Single point of failure (if LLM call fails, entire generation fails)

## Proposed Architecture (Multi-Agent)

```
User Request
    ↓
[Orchestrator Agent] - Big Picture Analysis
    ↓
Identify Logical Domains (3-5 groups)
    ↓
    ├─→ [Domain Agent 1] → User Management (10 tables)
    ├─→ [Domain Agent 2] → Order Processing (15 tables)
    ├─→ [Domain Agent 3] → Notification System (8 tables)
    ├─→ [Domain Agent 4] → Data Pipeline (12 tables)
    └─→ [Domain Agent 5] → Audit & Logging (5 tables)
    ↓ (parallel execution)
[Aggregator Agent] - Combine & Refine
    ↓
Save Documentation
```

## Workflow Phases

### Phase 1: Schema Analysis & Domain Discovery (Orchestrator)

**Input:** Complete database metadata (all tables)

**Task:** Analyze schema and identify 3-7 logical domain groupings

**Prompt Focus:**
- Table naming patterns
- Foreign key relationships
- Common prefixes/suffixes
- Functional clustering

**Output:**
```json
{
  "database_overview": "E-commerce platform with order management...",
  "domain_groups": [
    {
      "domain_name": "User Management",
      "purpose": "User authentication, authorization, and profile management",
      "tables": ["users", "roles", "permissions", "user_sessions"]
    },
    {
      "domain_name": "Order Processing",
      "purpose": "E-commerce order lifecycle from cart to fulfillment",
      "tables": ["orders", "order_items", "shopping_carts", "payments"]
    }
  ],
  "key_hub_tables": ["users", "orders", "data_sources"],
  "architecture_patterns": ["Multi-tenant", "Event sourcing for audit"]
}
```

**Token Budget:** 2000-3000 tokens (metadata only, no deep analysis)

### Phase 2: Parallel Domain Documentation (Specialized Agents)

**Input:** Domain group + relevant table metadata

**Tasks (per domain):**
1. Analyze tables within the domain
2. Explain business purpose and workflows
3. Document key columns and relationships
4. Provide example queries
5. Suggest optimizations

**Prompt Focus:**
- Deep dive into domain-specific logic
- Inter-table relationships within domain
- Business workflows and data flow
- Common usage patterns

**Output (per domain):**
```json
{
  "domain_name": "User Management",
  "purpose_overview": "...",
  "core_tables": [
    {
      "table_name": "users",
      "business_purpose": "...",
      "core_columns": [...],
      "usage_context": "..."
    }
  ],
  "relationships": "users (1) → user_sessions (many)...",
  "example_queries": ["SELECT...", "UPDATE..."],
  "recommendations": ["Add index on users.email", ...]
}
```

**Token Budget (per agent):** 1500-2500 tokens

**Parallelization:**
- Up to 5 concurrent agents (configurable)
- Rate limiting via existing `LlmRequestQueue`
- Progress tracking per domain

### Phase 3: Aggregation & Refinement (Aggregator)

**Input:**
- Orchestrator's overview
- All domain agent outputs

**Tasks:**
1. Combine domain documentation into cohesive structure
2. Identify cross-domain relationships
3. Create high-level ER diagram
4. Ensure consistent formatting and terminology
5. Add executive summary

**Prompt Focus:**
- Consistency across domains
- Cross-domain data flows
- System-wide patterns
- High-level architecture

**Output:** Final markdown documentation with all sections

**Token Budget:** 2000-4000 tokens

## Implementation Components

### 1. New Service: `MultiAgentDocumentationService`

```csharp
public interface IMultiAgentDocumentationService
{
    Task<DataSourceDocumentation> GenerateDocumentationAsync(
        int dataSourceId,
        int userId,
        MultiAgentGenerationOptions options,
        IProgress<DocumentationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public record MultiAgentGenerationOptions
{
    public int MaxConcurrentAgents { get; init; } = 5;
    public int MinTablesPerDomain { get; init; } = 3;
    public int MaxDomainsToIdentify { get; init; } = 7;
    public decimal Temperature { get; init; } = 0.3m;
    public bool EnableOrchestratorCache { get; init; } = true; // Cache domain groupings
}

public record DocumentationProgress
{
    public string CurrentPhase { get; init; } = null!; // "Analyzing", "Documenting Domains", "Aggregating"
    public int TotalDomains { get; init; }
    public int CompletedDomains { get; init; }
    public string? CurrentDomain { get; init; }
    public TimeSpan ElapsedTime { get; init; }
}
```

### 2. Agent Prompts

**Location:** `Beacon.Core/Services/Ai/MultiAgent/MultiAgentPrompts.cs`

```csharp
public static class MultiAgentPrompts
{
    public static string GetOrchestratorSystemPrompt() { ... }
    public static string GetDomainAgentSystemPrompt() { ... }
    public static string GetAggregatorSystemPrompt() { ... }

    public static string BuildOrchestratorPrompt(List<TableMetadataDto> tables) { ... }
    public static string BuildDomainPrompt(DomainGroup domain, List<TableMetadataDto> tables) { ... }
    public static string BuildAggregatorPrompt(OrchestratorResult overview, List<DomainResult> domainResults) { ... }
}
```

### 3. Models

**Location:** `Beacon.Core/Models/Ai/MultiAgent/`

```csharp
public record OrchestratorResult
{
    public string DatabaseOverview { get; init; } = null!;
    public List<DomainGroup> DomainGroups { get; init; } = new();
    public List<string> KeyHubTables { get; init; } = new();
    public List<string> ArchitecturePatterns { get; init; } = new();
}

public record DomainGroup
{
    public string DomainName { get; init; } = null!;
    public string Purpose { get; init; } = null!;
    public List<string> Tables { get; init; } = new();
}

public record DomainResult
{
    public string DomainName { get; init; } = null!;
    public string PurposeOverview { get; init; } = null!;
    public string Relationships { get; init; } = null!;
    public string ExampleQueries { get; init; } = null!;
    public string Recommendations { get; init; } = null!;
    public string FullMarkdown { get; init; } = null!; // Complete domain section
}

public record AggregatedDocumentation
{
    public string ExecutiveSummary { get; init; } = null!;
    public string ArchitectureDiagram { get; init; } = null!; // Mermaid ER diagram
    public List<DomainSection> DomainSections { get; init; } = new();
    public string CrossDomainRelationships { get; init; } = null!;
}
```

### 4. Service Implementation Structure

```csharp
public class MultiAgentDocumentationService : IMultiAgentDocumentationService
{
    private readonly ILlmProvider _llmProvider;
    private readonly IDatabaseMetadataService _metadataService;
    private readonly IDbContextFactory<BeaconContext> _contextFactory;
    private readonly ILogger<MultiAgentDocumentationService> _logger;

    public async Task<DataSourceDocumentation> GenerateDocumentationAsync(...)
    {
        // Phase 1: Orchestrator - Domain Discovery
        var metadata = await _metadataService.GetMetadataAsync(dataSourceId, cancellationToken);
        var orchestratorResult = await RunOrchestratorAsync(metadata, options, cancellationToken);

        progress?.Report(new DocumentationProgress
        {
            CurrentPhase = "Documenting Domains",
            TotalDomains = orchestratorResult.DomainGroups.Count
        });

        // Phase 2: Parallel Domain Documentation
        var domainResults = await ProcessDomainsInParallelAsync(
            orchestratorResult.DomainGroups,
            metadata.Tables,
            options,
            progress,
            cancellationToken);

        progress?.Report(new DocumentationProgress
        {
            CurrentPhase = "Aggregating Results",
            CompletedDomains = domainResults.Count
        });

        // Phase 3: Aggregation
        var finalDoc = await AggregateResultsAsync(
            orchestratorResult,
            domainResults,
            cancellationToken);

        // Save to database
        return await SaveDocumentationAsync(dataSourceId, userId, finalDoc, cancellationToken);
    }

    private async Task<OrchestratorResult> RunOrchestratorAsync(...)
    {
        // Build orchestrator prompt with all table names + basic metadata
        var prompt = MultiAgentPrompts.BuildOrchestratorPrompt(metadata.Tables.ToList());

        var request = new LlmRequest
        {
            Messages = new List<ChatMessage> { new(ConversationRole.User, prompt) },
            SystemPrompt = MultiAgentPrompts.GetOrchestratorSystemPrompt(),
            Temperature = options.Temperature,
            MaxTokens = 3000
        };

        var response = await _llmProvider.CompleteAsync(request, cancellationToken);

        // Parse JSON response into OrchestratorResult
        return ParseOrchestratorResponse(response.Content);
    }

    private async Task<List<DomainResult>> ProcessDomainsInParallelAsync(...)
    {
        var results = new ConcurrentBag<DomainResult>();
        var semaphore = new SemaphoreSlim(options.MaxConcurrentAgents);

        var tasks = domainGroups.Select(async domain =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var result = await ProcessDomainAsync(domain, allTables, options, cancellationToken);
                results.Add(result);

                progress?.Report(new DocumentationProgress
                {
                    CurrentPhase = "Documenting Domains",
                    CurrentDomain = domain.DomainName,
                    CompletedDomains = results.Count,
                    TotalDomains = domainGroups.Count
                });
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results.OrderBy(r => r.DomainName).ToList();
    }

    private async Task<DomainResult> ProcessDomainAsync(...)
    {
        // Filter tables for this domain
        var domainTables = allTables
            .Where(t => domain.Tables.Contains(t.TableName))
            .ToList();

        // Build domain-specific prompt
        var prompt = MultiAgentPrompts.BuildDomainPrompt(domain, domainTables);

        var request = new LlmRequest
        {
            Messages = new List<ChatMessage> { new(ConversationRole.User, prompt) },
            SystemPrompt = MultiAgentPrompts.GetDomainAgentSystemPrompt(),
            Temperature = options.Temperature,
            MaxTokens = 2500
        };

        var response = await _llmProvider.CompleteAsync(request, cancellationToken);

        // Parse response into DomainResult
        return ParseDomainResponse(domain.DomainName, response.Content);
    }

    private async Task<AggregatedDocumentation> AggregateResultsAsync(...)
    {
        // Build aggregator prompt with orchestrator overview + all domain results
        var prompt = MultiAgentPrompts.BuildAggregatorPrompt(orchestratorResult, domainResults);

        var request = new LlmRequest
        {
            Messages = new List<ChatMessage> { new(ConversationRole.User, prompt) },
            SystemPrompt = MultiAgentPrompts.GetAggregatorSystemPrompt(),
            Temperature = 0.3m,
            MaxTokens = 4000
        };

        var response = await _llmProvider.CompleteAsync(request, cancellationToken);

        // Parse final aggregated documentation
        return ParseAggregatedResponse(response.Content);
    }
}
```

## Database Schema Changes

**No schema changes required!** The existing entities support this workflow:

- `DataSourceDocumentation` - Same as before
- `DocumentationSection` - Each domain becomes a section
- `AiUsageMetrics` - Track each agent call separately

## Benefits

### Performance
- **Parallel processing**: 5 domains processed simultaneously = ~5x faster for large databases
- **Reduced token usage per call**: Smaller, focused prompts = better LLM performance
- **Incremental progress**: Users see domains completing in real-time

### Quality
- **Deeper domain analysis**: Each agent focuses on specific business area
- **Better context**: Domain agents work with manageable token budgets
- **Consistency**: Aggregator ensures terminology and formatting alignment

### Scalability
- **Handle large databases**: 200+ tables can be split into 10 domains of 20 tables each
- **Graceful degradation**: If one domain fails, others continue
- **Rate limiting**: Existing `LlmRequestQueue` handles concurrent requests

### User Experience
- **Progress visibility**: Users see "Documenting User Management (2/5)..."
- **Faster time-to-first-result**: Orchestrator completes in 5-10 seconds
- **Resume capability**: Can re-run specific domains if needed

## Configuration

Add to `appsettings.json`:

```json
{
  "Beacon": {
    "AI": {
      "Documentation": {
        "UseMultiAgent": true,
        "MaxConcurrentAgents": 5,
        "MinTablesPerDomain": 3,
        "MaxDomainsToIdentify": 7,
        "EnableOrchestratorCache": true,
        "OrchestratorCacheDurationMinutes": 60
      }
    }
  }
}
```

## Migration Strategy

### Phase 1: Add Multi-Agent Service (This PR)
- Create `MultiAgentDocumentationService`
- Add agent prompts
- Add models
- Add configuration

### Phase 2: UI Integration
- Add "Use Multi-Agent" toggle in documentation generation UI
- Show progress bar with domain completion
- Allow switching between single-agent and multi-agent modes

### Phase 3: Make Multi-Agent Default
- Test thoroughly on various database sizes
- Compare quality of single-agent vs multi-agent outputs
- Deprecate single-agent mode (keep for fallback)

## Testing Strategy

### Unit Tests
- `OrchestratorAgentTests` - Test domain grouping logic
- `DomainAgentTests` - Test domain-specific documentation
- `AggregatorAgentTests` - Test result combination

### Integration Tests
- Test with small database (10 tables, 2 domains)
- Test with medium database (50 tables, 5 domains)
- Test with large database (100+ tables, 7 domains)
- Test error handling (agent failure, timeout, rate limits)

### Quality Tests
- Compare multi-agent output to single-agent output
- Ensure no duplicate content
- Verify cross-domain relationships are captured
- Check that all tables are documented

## Cost Analysis

**Current (Single-Agent):**
- 1 large call: 8000 input tokens + 6000 output tokens = 14000 tokens
- Cost: ~$0.02 per documentation generation

**Multi-Agent:**
- Orchestrator: 3000 input + 1000 output = 4000 tokens
- 5 Domain Agents: 5 × (2000 input + 1500 output) = 17500 tokens
- Aggregator: 3000 input + 2000 output = 5000 tokens
- **Total: 26500 tokens**
- Cost: ~$0.04 per documentation generation

**Trade-off:** 2x cost for 5x performance + better quality

## Timeline

- **Week 1:** Implement core multi-agent service and orchestrator
- **Week 2:** Implement domain agents and aggregator
- **Week 3:** Add progress tracking and UI integration
- **Week 4:** Testing and quality comparison

## Open Questions

1. **Domain grouping algorithm**: Should we use AI (orchestrator) or heuristic-based (prefix matching)?
   - **Recommendation**: Start with AI for flexibility, add heuristics as fallback

2. **Failure handling**: What if a domain agent fails?
   - **Recommendation**: Mark domain as "failed", continue with others, show partial results

3. **Caching strategy**: Cache orchestrator results per data source?
   - **Recommendation**: Yes, cache for 1 hour to avoid re-analyzing schema

4. **Re-documentation**: If user regenerates, re-run all agents or reuse cached results?
   - **Recommendation**: Re-run all for freshness, offer "use previous groupings" option

## File Structure

```
Beacon.Core/
├── Services/
│   └── Ai/
│       ├── MultiAgent/
│       │   ├── IMultiAgentDocumentationService.cs
│       │   ├── MultiAgentDocumentationService.cs
│       │   ├── MultiAgentPrompts.cs
│       │   └── AgentOrchestrator.cs
│       └── AiDocumentationService.cs (existing)
├── Models/
│   └── Ai/
│       └── MultiAgent/
│           ├── OrchestratorResult.cs
│           ├── DomainGroup.cs
│           ├── DomainResult.cs
│           ├── AggregatedDocumentation.cs
│           └── MultiAgentGenerationOptions.cs
└── Configuration/
    └── MultiAgentDocumentationOptions.cs
```

## Summary

This multi-agent architecture transforms database documentation from a monolithic single-call process to an orchestrated workflow of specialized agents. The approach provides:

✅ **Better performance** through parallelization
✅ **Higher quality** through focused analysis
✅ **Better UX** through progress visibility
✅ **Scalability** for large databases
✅ **Maintainability** through clear separation of concerns

The implementation follows Microsoft's agent design patterns while leveraging Beacon's existing infrastructure (LLM providers, metadata service, entity framework).
