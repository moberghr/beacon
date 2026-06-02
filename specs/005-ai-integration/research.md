# Research Findings: AI Integration

**Date**: 2026-01-03
**Feature**: 005-ai-integration
**Purpose**: Resolve technical unknowns from Technical Context section

---

## 1. LLM SDK Choice for .NET

### Decision: Microsoft.Extensions.AI + Direct Provider SDKs

**Selected Stack:**
- `Microsoft.Extensions.AI` (IChatClient abstraction) - Foundation layer
- `OpenAI` (Official .NET SDK) - For OpenAI models
- `Anthropic` (Official SDK) - For Claude models
- `Azure.AI.OpenAI` - For Azure OpenAI models

**Rationale:**
1. **Lightweight abstraction**: Microsoft.Extensions.AI provides minimal overhead while enabling provider portability via `IChatClient` interface
2. **Future-proof**: Foundation for Microsoft Agent Framework (GA Q1 2026)
3. **Direct SDK access**: Maintain full control over LLM API features (token counting, streaming, function calling)
4. **Native DI integration**: Seamlessly fits Beacon's existing `ServiceConfiguration.cs` pattern
5. **Multi-provider support**: Users can choose their LLM provider via configuration (aligns with DataSource pattern)

**Alternatives Considered:**

| Option | Pros | Cons | Why Not Chosen |
|--------|------|------|----------------|
| **Microsoft Semantic Kernel** | Rich features (memory, agents, templates), enterprise-ready | Heavier abstraction, moderate overhead, steep learning curve | Unnecessary complexity for Beacon's focused SQL generation use case |
| **LangChain .NET** | Familiar to Python users, maximum provider choice | Community-driven, early-stage maturity, smaller contributor base | Maturity concerns, not officially supported |
| **Direct SDKs only** | Zero overhead, full control | No provider abstraction, harder to switch providers | Lacks portability for users who want to choose their LLM |

**Implementation Approach:**
```csharp
// Provider-agnostic service interface
public interface ILlmService
{
    Task<string> GenerateQueryAsync(string naturalLanguage, int dataSourceId);
    Task<string> AnalyzeSchemaAsync(DatabaseMetadata metadata);
    Task<string> RefineQueryAsync(string sql, string feedback, int dataSourceId);
}

// Factory pattern for provider selection
services.AddSingleton<IChatClient>(sp =>
{
    var config = sp.GetRequiredService<LlmConfiguration>();
    return config.Provider switch
    {
        LlmProvider.OpenAI => new OpenAIClient(config.ApiKey)
            .GetChatClient(config.Model).AsIChatClient(),
        LlmProvider.Anthropic => new AnthropicClient(config.ApiKey)
            .GetChatClient(config.Model).AsIChatClient(),
        LlmProvider.AzureOpenAI => new AzureOpenAIClient(
            new Uri(config.Endpoint!), new ApiKeyCredential(config.ApiKey))
            .GetChatClient(config.Model).AsIChatClient(),
        _ => throw new NotSupportedException()
    };
});
```

**NuGet Packages:**
```xml
<PackageReference Include="Microsoft.Extensions.AI" Version="10.1.1" />
<PackageReference Include="OpenAI" Version="2.8.0" />
<PackageReference Include="Anthropic" Version="12.0.1" />
<PackageReference Include="Azure.AI.OpenAI" Version="2.0.0" />
```

---

## 2. LLM Provider Analysis

### Primary Provider: Anthropic Claude Sonnet 4.5

**Decision:** Use Claude Sonnet 4.5 as the default/recommended model for schema analysis and SQL generation.

**Pricing (Per Million Tokens):**
- Input: $3.00
- Output: $15.00
- Context Window: 200K tokens (1M beta available)

**Rationale:**
1. **Cost-effective**: 40% cheaper input tokens than GPT-4o ($3 vs $5)
2. **Large context window**: 200K tokens handles 50+ tables with schema + sample data (497 tables theoretical max)
3. **Superior SQL generation**: Excellent at understanding complex schemas and relationships
4. **Prompt caching**: 90% cost savings on repeated schema analysis (cache hits: 0.1× input cost)
5. **Batch API**: 50% discount on batch processing

**Secondary Provider: OpenAI GPT-4o mini**

**Use Cases:**
- Query validation
- Simple refinements
- Natural language intent parsing

**Pricing (Per Million Tokens):**
- Input: $0.15
- Output: $0.60

**Rationale:** 97% cost savings vs Claude Sonnet for simple tasks

**Enterprise Option: Azure OpenAI**

**Use Cases:**
- Customers requiring compliance (HIPAA, SOC 2, ISO 27001)
- On-premise deployments with Azure Private Link
- Predictable workloads with PTU reservations

**Pricing:** Same as OpenAI direct ($5/$15 for GPT-4o)

**Rate Limits:**

| Provider | Model | Paid Tier (Entry) | Enterprise |
|----------|-------|-------------------|------------|
| Anthropic | Sonnet 4.5 | 80K TPM / 1K RPM | 400K+ TPM / 4K+ RPM |
| OpenAI | GPT-4o | 500K TPM / 1K RPM | 10M+ TPM / 5K+ RPM |
| OpenAI | GPT-4o mini | 2M TPM / 5K RPM | 20M+ TPM / 10K+ RPM |
| Azure | GPT-4o | 30K TPM / 600 RPM | Custom PTUs |

*TPM = Tokens Per Minute, RPM = Requests Per Minute*

**Cost Estimates for Beacon:**

**Per-Operation Costs:**
- 50-table schema analysis: $0.065 (Claude Sonnet 4.5)
- Single query generation: $0.020 (Claude Sonnet 4.5)
- Query validation: $0.0004 (GPT-4o mini)
- Query refinement: $0.008 (Claude Sonnet 4.5)

**Monthly Cost Projection (100 Active Users):**
- Schema analysis (10 data sources): $0.65
- Query generation (200 queries): $4.00
- Query refinement (40 refinements): $0.02
- Documentation generation: $1.50
- Buffer (20%): $1.23
- **Total: $7.40/month** (~$89/year)

**Context Window Capacity:**

Based on 400 tokens per table (schema + 10 sample rows):

| Model | Context Window | Max Tables |
|-------|----------------|------------|
| Claude Sonnet 4.5 | 200K | 497 tables |
| GPT-4o | 128K | 317 tables |
| GPT-4o mini | 128K | 317 tables |

**Recommendation:** For databases with 200+ tables, implement schema filtering (select 8-15 relevant tables) rather than passing entire schema. Research shows 84% token reduction with minimal accuracy impact.

---

## 3. PDF Generation Library

### Decision: QuestPDF

**Selected Library:** [QuestPDF](https://www.questpdf.com/) v2025.12.1

**Rationale:**
1. **Modern fluent API**: Intuitive C# API with low learning curve
2. **Excellent performance**: Generates thousands of pages per second with minimal memory footprint
3. **Rich table support**: Headers/footers that repeat on each page, perfect for documentation
4. **Styling capabilities**: 50+ layout elements, full control over fonts, colors, spacing
5. **Active maintenance**: Regular 2025 updates, explicit .NET 8/9/10 support
6. **PDF/A compliance**: PDF/A-2/3 variants and PDF/UA accessibility support
7. **Cost-effective licensing**: Free for businesses under $1M annual revenue

**License:**
- **Free for**: Individuals, non-profits, businesses < $1M revenue, FOSS projects
- **Commercial**: Professional/Enterprise licenses required for businesses > $1M revenue
- **Type**: Hybrid (MIT for small businesses, commercial for larger organizations)

**Code Example:**
```csharp
Document.Create(container => {
    container.Page(page => {
        page.Size(PageSizes.A4);
        page.Margin(2, Unit.Centimetre);

        page.Header().Text("Data Source Documentation").SemiBold().FontSize(24);

        page.Content().Column(column => {
            column.Item().Text("Schema Analysis").FontSize(18);
            column.Item().Table(table => {
                table.ColumnsDefinition(columns => {
                    columns.ConstantColumn(150);
                    columns.RelativeColumn();
                });
                table.Header(header => {
                    header.Cell().Text("Table").Bold();
                    header.Cell().Text("Description").Bold();
                });
                // Add table rows from AI-generated content
            });
        });

        page.Footer().AlignCenter().Text(x => {
            x.CurrentPageNumber();
            x.Span(" / ");
            x.TotalPages();
        });
    });
}).GeneratePdf("documentation.pdf");
```

**Alternatives Considered:**

| Option | License | Pros | Cons | Why Not Chosen |
|--------|---------|------|------|----------------|
| **iText 7** | AGPL/Commercial ($45k-$210k) | Comprehensive features, mature | Extremely expensive, steep learning curve | Cost prohibitive for most users |
| **PdfSharp/MigraDoc** | MIT (free) | Free, open-source | Basic features, limited HTML support | Lacks advanced layout capabilities |
| **SelectPdf** | Proprietary ($499-$1,599) | Simple HTML-to-PDF | Windows-only, 5-page limit for free edition | Platform limitation |
| **DinkToPdf** | LGPL | Free | Based on abandoned wkhtmltopdf (archived 2023) | Obsolete technology |
| **Puppeteer Sharp** | MIT (free) | Modern CSS support | Requires Chromium (~200MB), high memory | Better for HTML-heavy content |

**HTML/Markdown Conversion Note:**
QuestPDF is code-first and doesn't natively support HTML/Markdown. For Beacon's use case:
1. Generate structured documentation data from AI
2. Use QuestPDF's fluent API to build PDF directly
3. Alternatively: Use Markdig to generate HTML, then Puppeteer Sharp for HTML→PDF if needed

**Recommendation for Beacon:**
- **Primary**: QuestPDF for structured, programmatic PDF generation
- **Alternative**: Puppeteer Sharp if AI generates rich HTML documentation requiring pixel-perfect rendering

---

## 4. Markdown/HTML Rendering Library

### Decision: Markdig

**Selected Library:** [Markdig](https://github.com/xoofx/markdig) v0.44.0

**Rationale:**
1. **Standards compliance**: Full CommonMark + GitHub Flavored Markdown (GFM) support
2. **Performance**: ~100x faster than MarkdownSharp, ~20% faster than C reference implementation
3. **Extensibility**: Rich extension system for custom elements and renderers
4. **Table support**: Native GFM table syntax with column alignment
5. **Active maintenance**: Regular updates (0.44.0 released December 2025)
6. **Production-proven**: Used by Microsoft documentation tooling and numerous enterprise applications
7. **.NET 8 compatibility**: Targets .NET Standard 2.0/2.1, fully compatible with .NET 8+

**Features:**
- Fenced code blocks with syntax highlighting hooks
- Task lists
- Generic attributes (custom CSS classes)
- Emoji support
- Math equations
- Custom inline/block parsers
- Custom renderers for HTML output

**Code Example:**
```csharp
var pipeline = new MarkdownPipelineBuilder()
    .UseAdvancedExtensions()  // Activates GFM tables, task lists, etc.
    .UseGenericAttributes()   // Enable custom CSS classes
    .Build();

var html = Markdown.ToHtml(markdown, pipeline);

// With custom CSS classes in Markdown:
// ## Heading {.custom-header}
// Renders as: <h2 class="custom-header">Heading</h2>
```

**Integration with Beacon:**

Markdig aligns perfectly with Beacon's existing patterns in `Adapters/Helpers.cs`:

```csharp
// Generate Markdown from AI documentation
public static string GenerateDocumentationMarkdown(DocumentationData data)
{
    var sb = new StringBuilder();
    sb.AppendLine($"# {data.DataSourceName} Documentation");
    sb.AppendLine();

    foreach (var table in data.Tables)
    {
        sb.AppendLine($"## Table: {table.Name}");
        sb.AppendLine();
        sb.AppendLine(table.AiDescription);
        sb.AppendLine();

        // Generate GFM table for columns
        sb.AppendLine("| Column | Type | Description |");
        sb.AppendLine("|--------|------|-------------|");
        foreach (var col in table.Columns)
        {
            sb.AppendLine($"| {col.Name} | {col.Type} | {col.AiDescription} |");
        }
        sb.AppendLine();
    }

    return sb.ToString();
}

// Convert Markdown to HTML
public static string ConvertMarkdownToHtml(string markdown, string? customCss = null)
{
    var pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    var htmlBody = Markdown.ToHtml(markdown, pipeline);

    return WrapInHtmlDocument(htmlBody, customCss);
}
```

**Alternatives Considered:**

| Option | Pros | Cons | Why Not Chosen |
|--------|------|------|----------------|
| **CommonMark.NET** | CommonMark compliant, good performance | Deprecated, no GFM support, no extensibility | Maintainer recommends Markdig |
| **MarkdownSharp** | Used by Stack Overflow | ~100x slower, unmaintained since 2017, regex-based | Obsolete performance and maintenance |
| **Custom HTML** | Zero dependencies, maximum control | No Markdown intermediate format, more maintenance | Loses standardization benefits |

**Export Workflow:**

```
AI Documentation Generation
    ↓
Structured Data Models (C# objects)
    ↓
Markdown Generation (using StringBuilder) ← Export as .md
    ↓
HTML Conversion (using Markdig) ← Export as .html
    ↓
PDF Generation (using QuestPDF or Puppeteer Sharp) ← Export as .pdf
    ↓
JSON Serialization (using System.Text.Json) ← Export as .json
```

**NuGet Package:**
```xml
<PackageReference Include="Markdig" Version="0.44.0" />
```

---

## 5. Cost Optimization Strategies

### Schema Filtering

**Problem:** Passing 50+ table schemas consumes 20,000+ tokens per request.

**Solution:** Filter to relevant tables (8-15) based on natural language query.

**Implementation:**
```csharp
public async Task<string> GenerateQueryAsync(string naturalLanguage, int dataSourceId)
{
    // Step 1: Identify relevant tables using lightweight model
    var relevantTables = await IdentifyRelevantTablesAsync(
        naturalLanguage,
        dataSourceId,
        model: "gpt-4o-mini"  // Fast and cheap
    );

    // Step 2: Fetch only relevant table metadata
    var filteredMetadata = await GetFilteredMetadataAsync(
        dataSourceId,
        relevantTables
    );

    // Step 3: Generate SQL with focused context
    return await GenerateSqlAsync(
        naturalLanguage,
        filteredMetadata,
        model: "claude-sonnet-4.5"
    );
}
```

**Impact:**
- Original: 50 tables × 400 tokens = 20,000 tokens
- Filtered: 8 tables × 400 = 3,200 tokens
- **Savings: 84% reduction** ($0.065 → $0.010 per query)

### Prompt Caching (Claude)

**Problem:** Repeated schema analysis for same data source wastes tokens.

**Solution:** Use Claude's prompt caching (5-minute TTL).

**Impact:**
- First call: 20,600 tokens × $3 × 1.25 / 1M = $0.077 (cache write)
- Subsequent calls: 20,600 tokens × $3 × 0.1 / 1M = $0.006 (cache hit)
- **Savings: 90% on repeated operations**

### Model Selection Matrix

Use the right model for each task:

| Task | Model | Cost per Request | Rationale |
|------|-------|------------------|-----------|
| Schema analysis (>30 tables) | Claude Sonnet 4.5 | $0.065 | Large context, best SQL |
| Query generation | Claude Sonnet 4.5 | $0.020 | Superior SQL generation |
| Query validation | GPT-4o mini | $0.0004 | Fast, cheap, sufficient |
| Query refinement | GPT-4o mini | $0.001 | Simple task |
| Error fixing | Claude Sonnet 4.5 | $0.020 | Better debugging |
| Documentation | Claude Sonnet 4.5 | $0.065 | Long context needed |

### Rate Limiting

**Problem:** 100 concurrent users can hit rate limits (500 RPM for OpenAI Tier 1).

**Solution:** Implement request queuing with semaphore-based throttling:

```csharp
public class LlmRequestQueue
{
    private readonly SemaphoreSlim _rateLimiter;

    public LlmRequestQueue(int maxConcurrent = 50)
    {
        _rateLimiter = new SemaphoreSlim(maxConcurrent);
    }

    public async Task<string> EnqueueRequestAsync(LlmRequest request)
    {
        await _rateLimiter.WaitAsync();
        try
        {
            return await ExecuteWithRetryAsync(request);
        }
        finally
        {
            _rateLimiter.Release();
        }
    }
}
```

### Cost Monitoring

Track usage in database for billing and optimization:

```csharp
public class LlmUsageMetrics : BaseArchivableEntity
{
    public int? QueryId { get; set; }
    public int? DataSourceId { get; set; }
    public string Provider { get; set; } = null!;  // OpenAI, Anthropic, Azure
    public string Model { get; set; } = null!;      // gpt-4o, claude-sonnet-4.5
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int TotalTokens { get; set; }
    public decimal EstimatedCost { get; set; }
    public string OperationType { get; set; } = null!;  // SchemaAnalysis, QueryGeneration
    public DateTime Timestamp { get; set; }
}
```

---

## 6. Updated Technical Context

With research complete, here are the resolved decisions:

**Primary Dependencies (NEW):**
- ✅ **LLM SDK**: Microsoft.Extensions.AI + OpenAI SDK + Anthropic SDK + Azure.AI.OpenAI
- ✅ **PDF Generation**: QuestPDF v2025.12.1 (free for <$1M revenue, commercial licenses available)
- ✅ **Markdown/HTML**: Markdig v0.44.0

**LLM Provider Configuration:**
- ✅ **Default Model**: Claude Sonnet 4.5 ($3/$15 per 1M tokens)
- ✅ **Fast Model**: GPT-4o mini ($0.15/$0.60 per 1M tokens)
- ✅ **Enterprise Option**: Azure OpenAI (same pricing as OpenAI, additional compliance features)
- ✅ **Rate Limits**: 80K TPM / 1K RPM (Anthropic Tier 1), scalable to 400K+ TPM enterprise
- ✅ **Context Window**: 200K tokens (497 tables max, recommend 50 tables max with filtering)

**Performance Goals (UPDATED):**
- AI documentation generation: <2 minutes for 20 tables (excluding ~5-15s LLM API latency)
- AI query generation: <30 seconds for simple alerts (excluding ~2-5s LLM API latency)
- Documentation export: <10 seconds for 50-page PDF

**Constraints (UPDATED):**
- LLM API rate limits: 80K TPM / 1K RPM (entry tier), implement request queuing for peak usage
- LLM API costs: ~$7.40/month for 100 active users with optimization strategies
- Context window: 200K tokens (use schema filtering for databases with >50 tables)
- QuestPDF license: Free for businesses <$1M revenue, commercial license required for larger organizations

**Cost Budget:**
- Monthly (100 users): ~$7.40
- Yearly (100 users): ~$89
- Enterprise (500 users): ~$44/month, ~$528/year

---

## 7. Implementation Recommendations

### Phase 1: Core Infrastructure
1. Add NuGet packages: Microsoft.Extensions.AI, OpenAI, Anthropic, Markdig, QuestPDF
2. Implement `ILlmProvider` abstraction with factory pattern
3. Create `LlmConfiguration` class for user-selected provider
4. Register LLM services in `ServiceConfiguration.cs`
5. Implement basic prompt construction for schema introspection
6. Create token tracking service with database storage

### Phase 2: Documentation Generation
1. Implement `IAiDocumentationService` for schema analysis
2. Create Markdown generation using StringBuilder patterns
3. Implement HTML conversion using Markdig
4. Create PDF generation using QuestPDF fluent API
5. Add JSON export for programmatic access
6. Implement versioning for documentation history

### Phase 3: Alert Generation
1. Implement `IAiAlertGenerationService` for natural language → SQL
2. Create conversation history management
3. Implement query validation and refinement
4. Integrate with existing Subscription/Notification infrastructure
5. Add feedback collection for continuous improvement

### Phase 4: Optimization
1. Implement schema filtering (relevant table selection)
2. Enable prompt caching for Claude
3. Add request queuing with rate limiting
4. Implement cost tracking and budget alerts
5. Add batch processing for bulk operations

---

## Summary

All technical unknowns have been resolved with clear decisions:

| Unknown | Decision | Cost/License | Status |
|---------|----------|--------------|--------|
| **LLM SDK** | Microsoft.Extensions.AI + Direct SDKs | Free (MIT) | ✅ Resolved |
| **LLM Provider** | Claude Sonnet 4.5 (default), GPT-4o mini (fast) | $3/$15 per 1M tokens | ✅ Resolved |
| **PDF Library** | QuestPDF | Free <$1M revenue, commercial for larger | ✅ Resolved |
| **Markdown Library** | Markdig | Free (BSD-2-Clause) | ✅ Resolved |
| **Rate Limits** | 80K TPM / 1K RPM (entry), 400K+ TPM (enterprise) | N/A | ✅ Resolved |
| **Context Window** | 200K tokens (Claude), recommend 50 tables max | N/A | ✅ Resolved |

**Total Monthly Cost Estimate**: $7.40 for 100 active users (~$0.074 per user)

**Implementation Confidence**: High - All chosen technologies are mature, actively maintained, and production-proven in enterprise .NET applications.

**Next Steps**: Proceed to Phase 1 (Design & Contracts) to generate data-model.md and API contracts.
