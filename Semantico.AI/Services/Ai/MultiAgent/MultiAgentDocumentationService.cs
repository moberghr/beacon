using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Semantico.AI.Models.MultiAgent;
using Semantico.AI.Services.LlmProviders;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities;
using Semantico.Core.Data.Enums;
using Semantico.Core.Exceptions;
using Semantico.Core.Models;
using Semantico.Core.Models.Ai;
using Semantico.Core.Models.Metadata;
using DocumentationProgress = Semantico.Core.Models.Ai.MultiAgent.DocumentationProgress;

namespace Semantico.AI.Services.Ai.MultiAgent;

public class MultiAgentDocumentationService : IMultiAgentDocumentationService
{
    private readonly ILlmProvider _llmProvider;
    private readonly IDatabaseMetadataService _metadataService;
    private readonly IDbContextFactory<SemanticoContext> _contextFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<MultiAgentDocumentationService> _logger;

    private const string OrchestratorCacheKeyPrefix = "orchestrator_result_";

    public MultiAgentDocumentationService(
        ILlmProvider llmProvider,
        IDatabaseMetadataService metadataService,
        IDbContextFactory<SemanticoContext> contextFactory,
        IMemoryCache cache,
        ILogger<MultiAgentDocumentationService> logger)
    {
        _llmProvider = llmProvider;
        _metadataService = metadataService;
        _contextFactory = contextFactory;
        _cache = cache;
        _logger = logger;
    }

    public async Task<DataSourceDocumentation> GenerateDocumentationAsync(
        int dataSourceId,
        int userId,
        MultiAgentGenerationOptions options,
        IProgress<DocumentationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        _logger.LogInformation("Starting multi-agent documentation generation for DataSource {DataSourceId}", dataSourceId);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        // Fetch data source
        var dataSource = await context.DataSources
            .FirstOrDefaultAsync(ds => ds.Id == dataSourceId, cancellationToken)
            ?? throw new SemanticoException($"DataSource with ID {dataSourceId} not found");

        // Phase 1: Orchestrator - Domain Discovery
        progress?.Report(new DocumentationProgress
        {
            CurrentPhase = "Analyzing Schema",
            ElapsedTime = stopwatch.Elapsed
        });

        var metadata = await _metadataService.GetMetadataAsync(dataSourceId, cancellationToken);
        var filteredTables = FilterTables(metadata.Tables.ToList(), options);

        if (filteredTables.Count == 0)
            throw new AiServiceException("No tables found to document");

        var orchestratorResult = await RunOrchestratorAsync(
            dataSource.Name,
            dataSourceId,
            filteredTables,
            options,
            cancellationToken);

        _logger.LogInformation("Orchestrator identified {DomainCount} domains", orchestratorResult.DomainGroups.Count);

        // Phase 2: Parallel Domain Documentation
        progress?.Report(new DocumentationProgress
        {
            CurrentPhase = "Documenting Domains",
            TotalDomains = orchestratorResult.DomainGroups.Count,
            CompletedDomains = 0,
            ElapsedTime = stopwatch.Elapsed
        });

        var domainResults = await ProcessDomainsInParallelAsync(
            orchestratorResult.DomainGroups,
            filteredTables,
            options,
            progress,
            stopwatch,
            cancellationToken);

        _logger.LogInformation("Completed documentation for {DomainCount} domains", domainResults.Count);

        // Phase 3: Aggregation
        progress?.Report(new DocumentationProgress
        {
            CurrentPhase = "Aggregating Results",
            TotalDomains = orchestratorResult.DomainGroups.Count,
            CompletedDomains = domainResults.Count,
            ElapsedTime = stopwatch.Elapsed
        });

        var aggregatedDoc = await AggregateResultsAsync(
            dataSource.Name,
            orchestratorResult,
            domainResults,
            cancellationToken);

        stopwatch.Stop();

        // Save to database
        var documentation = await SaveDocumentationAsync(
            dataSourceId,
            userId,
            orchestratorResult,
            domainResults,
            aggregatedDoc,
            stopwatch.Elapsed,
            cancellationToken);

        _logger.LogInformation(
            "Multi-agent documentation generation completed in {Duration}ms. Total tokens: {Tokens}, Cost: ${Cost}",
            stopwatch.ElapsedMilliseconds,
            aggregatedDoc.TotalTokensUsed,
            aggregatedDoc.TotalEstimatedCost);

        return documentation;
    }

    public async Task<OrchestratorResult?> GetCachedOrchestratorResultAsync(
        int dataSourceId,
        CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask; // Satisfy async signature
        var cacheKey = $"{OrchestratorCacheKeyPrefix}{dataSourceId}";
        return _cache.Get<OrchestratorResult>(cacheKey);
    }

    public async Task ClearOrchestratorCacheAsync(
        int dataSourceId,
        CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask; // Satisfy async signature
        var cacheKey = $"{OrchestratorCacheKeyPrefix}{dataSourceId}";
        _cache.Remove(cacheKey);
        _logger.LogInformation("Cleared orchestrator cache for DataSource {DataSourceId}", dataSourceId);
    }

    #region Phase 1: Orchestrator

    private async Task<OrchestratorResult> RunOrchestratorAsync(
        string dataSourceName,
        int dataSourceId,
        List<TableMetadataDto> tables,
        MultiAgentGenerationOptions options,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Running orchestrator agent for {TableCount} tables", tables.Count);

        // Check cache first
        if (options.EnableOrchestratorCache)
        {
            var cached = await GetCachedOrchestratorResultAsync(dataSourceId, cancellationToken);
            if (cached != null)
            {
                _logger.LogInformation("Using cached orchestrator result");
                return cached;
            }
        }

        // Build prompt
        var prompt = MultiAgentPrompts.BuildOrchestratorPrompt(dataSourceName, tables);

        // Call LLM
        var request = new LlmRequest
        {
            Messages = new List<ChatMessage>
            {
                new(ConversationRole.User, prompt)
            },
            SystemPrompt = MultiAgentPrompts.GetOrchestratorSystemPrompt(),
            Temperature = options.Temperature,
            MaxTokens = options.MaxTokens
        };

        var response = await _llmProvider.CompleteAsync(request, cancellationToken);

        // Parse JSON response
        var result = ParseOrchestratorResponse(response.Content, tables.Count);

        // Validate and adjust domain groups
        result = ValidateAndAdjustDomainGroups(result, tables, options);

        // Cache result
        if (options.EnableOrchestratorCache)
        {
            var cacheKey = $"{OrchestratorCacheKeyPrefix}{dataSourceId}";
            var cacheOptions = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(options.OrchestratorCacheDurationMinutes)
            };
            _cache.Set(cacheKey, result, cacheOptions);
        }

        return result;
    }

    private OrchestratorResult ParseOrchestratorResponse(string content, int totalTablesAnalyzed)
    {
        try
        {
            // Extract JSON from potential markdown code blocks
            var jsonContent = ExtractJsonFromResponse(content);

            var jsonDoc = JsonDocument.Parse(jsonContent);
            var root = jsonDoc.RootElement;

            var domainGroups = new List<DomainGroup>();
            if (root.TryGetProperty("domain_groups", out var domainGroupsElement))
            {
                foreach (var domainElement in domainGroupsElement.EnumerateArray())
                {
                    domainGroups.Add(new DomainGroup
                    {
                        DomainName = domainElement.GetProperty("domain_name").GetString() ?? "Unknown Domain",
                        Purpose = domainElement.GetProperty("purpose").GetString() ?? "",
                        Tables = domainElement.GetProperty("tables").EnumerateArray()
                            .Select(t => t.GetString() ?? "")
                            .Where(t => !string.IsNullOrEmpty(t))
                            .ToList(),
                        Priority = domainElement.TryGetProperty("priority", out var priority)
                            ? priority.GetInt32()
                            : 100
                    });
                }
            }

            return new OrchestratorResult
            {
                DatabaseOverview = root.GetProperty("database_overview").GetString() ?? "",
                DomainGroups = domainGroups,
                KeyHubTables = root.TryGetProperty("key_hub_tables", out var hubTables)
                    ? hubTables.EnumerateArray().Select(t => t.GetString() ?? "").Where(t => !string.IsNullOrEmpty(t)).ToList()
                    : new List<string>(),
                ArchitecturePatterns = root.TryGetProperty("architecture_patterns", out var patterns)
                    ? patterns.EnumerateArray().Select(p => p.GetString() ?? "").Where(p => !string.IsNullOrEmpty(p)).ToList()
                    : new List<string>(),
                TotalTablesAnalyzed = totalTablesAnalyzed
            };
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse orchestrator JSON response");
            throw new AiServiceException("Failed to parse orchestrator response. The LLM did not return valid JSON.", ex);
        }
    }

    private OrchestratorResult ValidateAndAdjustDomainGroups(
        OrchestratorResult result,
        List<TableMetadataDto> allTables,
        MultiAgentGenerationOptions options)
    {
        // Ensure all tables are assigned to a domain
        // The LLM may return table names as schema.table or just table, so we need to match both formats
        var assignedTables = result.DomainGroups.SelectMany(d => d.Tables).Distinct().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unassignedTables = allTables
            .Where(t => !assignedTables.Contains(t.TableName) && !assignedTables.Contains($"{t.SchemaName}.{t.TableName}"))
            .Select(t => $"{t.SchemaName}.{t.TableName}")
            .ToList();

        if (unassignedTables.Any())
        {
            _logger.LogWarning("Found {Count} unassigned tables, creating 'Other' domain", unassignedTables.Count);
            result = result with
            {
                DomainGroups = result.DomainGroups.Append(new DomainGroup
                {
                    DomainName = "Other",
                    Purpose = "Miscellaneous tables not fitting into other domains",
                    Tables = unassignedTables,
                    Priority = 999
                }).ToList()
            };
        }

        // Merge small domains if needed
        var smallDomains = result.DomainGroups
            .Where(d => d.Tables.Count < options.MinTablesPerDomain)
            .ToList();

        if (smallDomains.Count > 1)
        {
            _logger.LogInformation("Merging {Count} small domains", smallDomains.Count);
            var mergedTables = smallDomains.SelectMany(d => d.Tables).ToList();
            var remainingDomains = result.DomainGroups.Except(smallDomains).ToList();

            result = result with
            {
                DomainGroups = remainingDomains.Append(new DomainGroup
                {
                    DomainName = "Supporting Tables",
                    Purpose = "Supporting tables from multiple domains",
                    Tables = mergedTables,
                    Priority = 500
                }).ToList()
            };
        }

        return result;
    }

    #endregion

    #region Phase 2: Domain Agents

    private async Task<List<DomainResult>> ProcessDomainsInParallelAsync(
        List<DomainGroup> domainGroups,
        List<TableMetadataDto> allTables,
        MultiAgentGenerationOptions options,
        IProgress<DocumentationProgress>? progress,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var results = new ConcurrentBag<DomainResult>();
        var semaphore = new SemaphoreSlim(options.MaxConcurrentAgents);
        var completedCount = 0;

        var orderedDomains = domainGroups.OrderBy(d => d.Priority).ToList();

        var tasks = orderedDomains.Select(async domain =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                _logger.LogInformation("Starting domain agent for {DomainName} ({TableCount} tables)",
                    domain.DomainName, domain.Tables.Count);

                var result = await ProcessDomainAsync(domain, allTables, options, cancellationToken);
                results.Add(result);

                var completed = Interlocked.Increment(ref completedCount);

                progress?.Report(new DocumentationProgress
                {
                    CurrentPhase = "Documenting Domains",
                    CurrentDomain = domain.DomainName,
                    CompletedDomains = completed,
                    TotalDomains = domainGroups.Count,
                    ElapsedTime = stopwatch.Elapsed
                });

                _logger.LogInformation("Completed domain agent for {DomainName}", domain.DomainName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to document domain {DomainName}", domain.DomainName);
                // Add a failed result so we can continue with other domains
                results.Add(new DomainResult
                {
                    DomainName = domain.DomainName,
                    PurposeOverview = $"**ERROR:** Failed to generate documentation for this domain. {ex.Message}",
                    CoreTablesDocumentation = "",
                    Relationships = "",
                    ExampleQueries = "",
                    Recommendations = "",
                    FullMarkdown = $"## Domain: {domain.DomainName}\n\n**ERROR:** Failed to generate documentation.\n\n{ex.Message}",
                    TablesDocumented = domain.Tables.Count
                });
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        return results.OrderBy(r => domainGroups.FindIndex(d => d.DomainName == r.DomainName)).ToList();
    }

    private async Task<DomainResult> ProcessDomainAsync(
        DomainGroup domain,
        List<TableMetadataDto> allTables,
        MultiAgentGenerationOptions options,
        CancellationToken cancellationToken)
    {
        // Filter tables for this domain
        // The LLM may return table names as schema.table or just table, so match both formats
        var domainTableSet = domain.Tables.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var domainTables = allTables
            .Where(t => domainTableSet.Contains(t.TableName) || domainTableSet.Contains($"{t.SchemaName}.{t.TableName}"))
            .ToList();

        if (domainTables.Count == 0)
        {
            _logger.LogWarning("No tables found for domain {DomainName}", domain.DomainName);
            return new DomainResult
            {
                DomainName = domain.DomainName,
                PurposeOverview = domain.Purpose,
                CoreTablesDocumentation = "No tables found for this domain.",
                Relationships = "",
                ExampleQueries = "",
                Recommendations = "",
                FullMarkdown = $"## Domain: {domain.DomainName}\n\n{domain.Purpose}\n\nNo tables found.",
                TablesDocumented = 0
            };
        }

        // Build prompt
        var prompt = MultiAgentPrompts.BuildDomainPrompt(domain, domainTables);

        // Call LLM
        var request = new LlmRequest
        {
            Messages = new List<ChatMessage>
            {
                new(ConversationRole.User, prompt)
            },
            SystemPrompt = MultiAgentPrompts.GetDomainAgentSystemPrompt(),
            Temperature = options.Temperature,
            MaxTokens = options.MaxTokens
        };

        var response = await _llmProvider.CompleteAsync(request, cancellationToken);

        // Parse response
        return ParseDomainResponse(domain.DomainName, response, domainTables.Count);
    }

    private DomainResult ParseDomainResponse(string domainName, LlmResponse response, int tablesDocumented)
    {
        try
        {
            // Extract JSON from potential markdown code blocks
            var jsonContent = ExtractJsonFromResponse(response.Content);

            var jsonDoc = JsonDocument.Parse(jsonContent);
            var root = jsonDoc.RootElement;

            return new DomainResult
            {
                DomainName = domainName,
                PurposeOverview = root.GetProperty("purpose_overview").GetString() ?? "",
                CoreTablesDocumentation = root.GetProperty("core_tables_documentation").GetString() ?? "",
                Relationships = root.GetProperty("relationships").GetString() ?? "",
                ExampleQueries = root.GetProperty("example_queries").GetString() ?? "",
                Recommendations = root.GetProperty("recommendations").GetString() ?? "",
                FullMarkdown = root.GetProperty("full_markdown").GetString() ?? "",
                TablesDocumented = tablesDocumented,
                TokensUsed = response.TotalTokens,
                EstimatedCost = response.Cost
            };
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse domain JSON response for {DomainName}", domainName);
            // Return fallback with raw content
            return new DomainResult
            {
                DomainName = domainName,
                PurposeOverview = "Failed to parse structured response",
                CoreTablesDocumentation = "",
                Relationships = "",
                ExampleQueries = "",
                Recommendations = "",
                FullMarkdown = $"## Domain: {domainName}\n\n{response.Content}",
                TablesDocumented = tablesDocumented,
                TokensUsed = response.TotalTokens,
                EstimatedCost = response.Cost
            };
        }
    }

    #endregion

    #region Phase 3: Aggregator

    private async Task<AggregatedDocumentation> AggregateResultsAsync(
        string dataSourceName,
        OrchestratorResult orchestratorResult,
        List<DomainResult> domainResults,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Running aggregator agent to combine {DomainCount} domain results", domainResults.Count);

        // Build prompt
        var prompt = MultiAgentPrompts.BuildAggregatorPrompt(dataSourceName, orchestratorResult, domainResults);

        // Call LLM
        var request = new LlmRequest
        {
            Messages = new List<ChatMessage>
            {
                new(ConversationRole.User, prompt)
            },
            SystemPrompt = MultiAgentPrompts.GetAggregatorSystemPrompt(),
            Temperature = 0.3m,
            MaxTokens = 4096
        };

        var response = await _llmProvider.CompleteAsync(request, cancellationToken);

        // Parse response
        return ParseAggregatorResponse(response, domainResults);
    }

    private AggregatedDocumentation ParseAggregatorResponse(LlmResponse response, List<DomainResult> domainResults)
    {
        try
        {
            // Extract JSON from potential markdown code blocks
            var jsonContent = ExtractJsonFromResponse(response.Content);

            var jsonDoc = JsonDocument.Parse(jsonContent);
            var root = jsonDoc.RootElement;

            var domainSections = new List<DomainSection>();
            if (root.TryGetProperty("domain_sections", out var sectionsElement))
            {
                foreach (var sectionElement in sectionsElement.EnumerateArray())
                {
                    domainSections.Add(new DomainSection
                    {
                        DomainName = sectionElement.GetProperty("domain_name").GetString() ?? "",
                        Content = sectionElement.GetProperty("content").GetString() ?? "",
                        SortOrder = sectionElement.GetProperty("sort_order").GetInt32()
                    });
                }
            }

            var totalTokens = response.TotalTokens + domainResults.Sum(d => d.TokensUsed);
            var totalCost = response.Cost + domainResults.Sum(d => d.EstimatedCost);

            return new AggregatedDocumentation
            {
                ExecutiveSummary = root.GetProperty("executive_summary").GetString() ?? "",
                ArchitectureDiagram = root.GetProperty("architecture_diagram").GetString() ?? "",
                DomainSections = domainSections,
                CrossDomainRelationships = root.GetProperty("cross_domain_relationships").GetString() ?? "",
                CompleteMarkdown = root.GetProperty("complete_markdown").GetString() ?? "",
                TotalTokensUsed = totalTokens,
                TotalEstimatedCost = totalCost,
                TotalDuration = TimeSpan.Zero // Will be set by caller
            };
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse aggregator JSON response");
            // Fallback: manually combine domain results
            return CreateFallbackAggregation(domainResults, response);
        }
    }

    private AggregatedDocumentation CreateFallbackAggregation(List<DomainResult> domainResults, LlmResponse response)
    {
        var markdown = new System.Text.StringBuilder();
        markdown.AppendLine("# Database Documentation");
        markdown.AppendLine();
        markdown.AppendLine("## Overview");
        markdown.AppendLine("Documentation generated using multi-agent workflow.");
        markdown.AppendLine();

        var sortOrder = 1;
        var domainSections = new List<DomainSection>();

        foreach (var domainResult in domainResults)
        {
            markdown.AppendLine(domainResult.FullMarkdown);
            markdown.AppendLine();

            domainSections.Add(new DomainSection
            {
                DomainName = domainResult.DomainName,
                Content = domainResult.FullMarkdown,
                SortOrder = sortOrder++
            });
        }

        var totalTokens = response.TotalTokens + domainResults.Sum(d => d.TokensUsed);
        var totalCost = response.Cost + domainResults.Sum(d => d.EstimatedCost);

        return new AggregatedDocumentation
        {
            ExecutiveSummary = "Documentation generated using multi-agent workflow.",
            ArchitectureDiagram = "",
            DomainSections = domainSections,
            CrossDomainRelationships = "",
            CompleteMarkdown = markdown.ToString(),
            TotalTokensUsed = totalTokens,
            TotalEstimatedCost = totalCost,
            TotalDuration = TimeSpan.Zero
        };
    }

    #endregion

    #region Save to Database

    private async Task<DataSourceDocumentation> SaveDocumentationAsync(
        int dataSourceId,
        int userId,
        OrchestratorResult orchestratorResult,
        List<DomainResult> domainResults,
        AggregatedDocumentation aggregatedDoc,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var dataSource = await context.DataSources.FindAsync(new object[] { dataSourceId }, cancellationToken)
            ?? throw new SemanticoException($"DataSource {dataSourceId} not found");

        var documentation = new DataSourceDocumentation
        {
            DataSourceId = dataSourceId,
            Title = $"{dataSource.Name} Documentation (Multi-Agent)",
            GeneratedByModel = "Multi-Agent System",
            GeneratedAt = DateTime.UtcNow,
            GeneratedByUserId = userId,
            Status = DocumentationStatus.Draft,
            TablesAnalyzed = orchestratorResult.TotalTablesAnalyzed,
            TokensUsed = aggregatedDoc.TotalTokensUsed,
            EstimatedCost = aggregatedDoc.TotalEstimatedCost,
            CreatedBy = userId.ToString(),
            ModifiedBy = userId.ToString()
        };

        // Create sections from aggregated documentation
        var sections = new List<DocumentationSection>();
        var sortOrder = 1;

        // Executive Summary section
        sections.Add(new DocumentationSection
        {
            Title = "Executive Summary",
            SectionType = SectionType.Overview,
            TableName = null,
            SortOrder = sortOrder++,
            AiGeneratedContent = aggregatedDoc.ExecutiveSummary,
            IsUserEdited = false,
            ContentFormat = ContentFormat.Markdown,
            CreatedBy = userId.ToString(),
            ModifiedBy = userId.ToString()
        });

        // Architecture section
        if (!string.IsNullOrEmpty(aggregatedDoc.ArchitectureDiagram))
        {
            sections.Add(new DocumentationSection
            {
                Title = "System Architecture",
                SectionType = SectionType.Architecture,
                TableName = null,
                SortOrder = sortOrder++,
                AiGeneratedContent = aggregatedDoc.ArchitectureDiagram,
                IsUserEdited = false,
                ContentFormat = ContentFormat.Markdown,
                CreatedBy = userId.ToString(),
                ModifiedBy = userId.ToString()
            });
        }

        // Domain sections
        foreach (var domainSection in aggregatedDoc.DomainSections.OrderBy(d => d.SortOrder))
        {
            sections.Add(new DocumentationSection
            {
                Title = $"Domain: {domainSection.DomainName}",
                SectionType = SectionType.TableDetail,
                TableName = domainSection.DomainName,
                SortOrder = sortOrder++,
                AiGeneratedContent = domainSection.Content,
                IsUserEdited = false,
                ContentFormat = ContentFormat.Markdown,
                CreatedBy = userId.ToString(),
                ModifiedBy = userId.ToString()
            });
        }

        // Cross-domain relationships section
        if (!string.IsNullOrEmpty(aggregatedDoc.CrossDomainRelationships))
        {
            sections.Add(new DocumentationSection
            {
                Title = "Cross-Domain Relationships",
                SectionType = SectionType.Relationships,
                TableName = null,
                SortOrder = sortOrder++,
                AiGeneratedContent = aggregatedDoc.CrossDomainRelationships,
                IsUserEdited = false,
                ContentFormat = ContentFormat.Markdown,
                CreatedBy = userId.ToString(),
                ModifiedBy = userId.ToString()
            });
        }

        documentation.Sections = sections;

        context.DataSourceDocumentations.Add(documentation);
        await context.SaveChangesAsync(cancellationToken);

        return documentation;
    }

    #endregion

    #region Helper Methods

    private List<TableMetadataDto> FilterTables(
        List<TableMetadataDto> tables,
        MultiAgentGenerationOptions options)
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

    private string ExtractJsonFromResponse(string content)
    {
        // Remove markdown code fences if present
        var trimmed = content.Trim();

        if (trimmed.StartsWith("```json"))
        {
            trimmed = trimmed.Substring("```json".Length);
        }
        else if (trimmed.StartsWith("```"))
        {
            trimmed = trimmed.Substring("```".Length);
        }

        if (trimmed.EndsWith("```"))
        {
            trimmed = trimmed.Substring(0, trimmed.Length - 3);
        }

        return trimmed.Trim();
    }

    #endregion
}
