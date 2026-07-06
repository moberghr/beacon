# Quick Start Guide: AI Integration

**Feature**: 005-ai-integration
**Audience**: Developers implementing AI-powered documentation and alerts

---

## Overview

This guide walks through implementing the AI Integration feature in Beacon, from adding dependencies to testing the complete functionality.

**Estimated Implementation Time**: 2-3 weeks
- Week 1: Infrastructure + Documentation Generation
- Week 2: AI Alert Generation
- Week 3: UI + Testing + Optimization

---

## Prerequisites

✅ .NET 8 SDK installed
✅ Beacon development environment set up
✅ Access to LLM API keys (Anthropic Claude or OpenAI)
✅ Database migrations capability
✅ Basic understanding of MediatR pattern

---

## Step 1: Add NuGet Dependencies

```bash
cd Beacon.Core

# AI/LLM libraries
dotnet add package Microsoft.Extensions.AI --version 10.1.1
dotnet add package OpenAI --version 2.8.0
dotnet add package Anthropic --version 12.0.1
dotnet add package Azure.AI.OpenAI --version 2.0.0

# Documentation export libraries
dotnet add package Markdig --version 0.44.0
dotnet add package QuestPDF --version 2025.12.1

cd ../Beacon.UI
dotnet add package MudBlazor --version 7.21.0  # If not already present
```

**Build and verify**:
```bash
dotnet build --property WarningLevel=0
```

---

## Step 2: Create Entity Classes

**File**: `Beacon.Core/Data/Entities/DataSourceDocumentation.cs`

```csharp
namespace Beacon.Core.Data.Entities;

public class DataSourceDocumentation : BaseArchivableEntity, IChangeableEntity
{
    public int DataSourceId { get; set; }
    public string Title { get; set; } = null!;
    public string GeneratedByModel { get; set; } = null!;
    public DateTime GeneratedAt { get; set; }
    public int GeneratedByUserId { get; set; }
    public int? LastModifiedByUserId { get; set; }
    public DateTime? LastModifiedAt { get; set; }
    public DocumentationStatus Status { get; set; }
    public int TablesAnalyzed { get; set; }
    public int TokensUsed { get; set; }
    public decimal EstimatedCost { get; set; }
    public string? Metadata { get; set; }

    // Navigation properties
    public DataSource DataSource { get; set; } = null!;
    public ICollection<DocumentationSection> Sections { get; set; } = new List<DocumentationSection>();
    public ICollection<DocumentationVersion> Versions { get; set; } = new List<DocumentationVersion>();
}
```

**Repeat for**:
- `DocumentationSection.cs`
- `DocumentationVersion.cs`
- `AiAlertConfiguration.cs`
- `AiConversationHistory.cs`
- `AiUsageMetrics.cs`
- `AiPromptTemplate.cs`

**File**: `Beacon.Core/Data/Enums/DocumentationStatus.cs`

```csharp
namespace Beacon.Core.Data.Enums;

public enum DocumentationStatus
{
    Draft = 0,
    Published = 1,
    Archived = 2
}
```

**Repeat for**:
- `SectionType.cs`
- `ContentFormat.cs`
- `AlertStatus.cs`
- `ConversationRole.cs`
- `OperationType.cs`
- `DocumentationExportFormat.cs`

---

## Step 3: Update DbContext

**File**: `Beacon.Core/Data/BeaconContext.cs`

```csharp
public DbSet<DataSourceDocumentation> DataSourceDocumentations { get; set; } = null!;
public DbSet<DocumentationSection> DocumentationSections { get; set; } = null!;
public DbSet<DocumentationVersion> DocumentationVersions { get; set; } = null!;
public DbSet<AiAlertConfiguration> AiAlertConfigurations { get; set; } = null!;
public DbSet<AiConversationHistory> AiConversationHistories { get; set; } = null!;
public DbSet<AiUsageMetrics> AiUsageMetrics { get; set; } = null!;
public DbSet<AiPromptTemplate> AiPromptTemplates { get; set; } = null!;

protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    base.OnModelCreating(modelBuilder);

    // Indexes for DataSourceDocumentation
    modelBuilder.Entity<DataSourceDocumentation>()
        .HasIndex(d => d.DataSourceId)
        .HasDatabaseName("IX_DataSourceDocumentation_DataSourceId");

    modelBuilder.Entity<DataSourceDocumentation>()
        .HasIndex(d => d.Status)
        .HasDatabaseName("IX_DataSourceDocumentation_Status");

    // Indexes for DocumentationSection
    modelBuilder.Entity<DocumentationSection>()
        .HasIndex(s => s.DocumentationId)
        .HasDatabaseName("IX_DocumentationSection_DocumentationId");

    modelBuilder.Entity<DocumentationSection>()
        .HasIndex(s => s.TableName)
        .HasDatabaseName("IX_DocumentationSection_TableName");

    // ... Add remaining indexes from data-model.md
}
```

---

## Step 4: Create Configuration

**File**: `Beacon.Core/Models/Configuration/LlmConfiguration.cs`

```csharp
namespace Beacon.Core.Models.Configuration;

public class LlmConfiguration
{
    public LlmProvider Provider { get; set; }
    public string ApiKey { get; set; } = null!;
    public string? Endpoint { get; set; }  // For Azure OpenAI
    public string Model { get; set; } = null!;
    public string? FastModel { get; set; }
    public ProviderLimits Limits { get; set; } = new();
}

public enum LlmProvider
{
    OpenAI,
    Anthropic,
    AzureOpenAI
}

public class ProviderLimits
{
    public int MaxConcurrentRequests { get; set; } = 50;
    public int TokensPerMinute { get; set; }
    public int RequestsPerMinute { get; set; }
    public decimal MonthlyBudget { get; set; }
}
```

---

## Step 5: Implement LLM Provider Abstraction

**File**: `Beacon.Core/Services/LlmProviders/ILlmProvider.cs`

```csharp
namespace Beacon.Core.Services.LlmProviders;

public interface ILlmProvider
{
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default);
    Task<TokenCount> CountTokensAsync(string text, CancellationToken cancellationToken = default);
}

public record LlmRequest
{
    public List<ChatMessage> Messages { get; init; } = new();
    public string? SystemPrompt { get; init; }
    public decimal Temperature { get; init; } = 0.3m;
    public int MaxTokens { get; init; } = 4096;
}

public record ChatMessage(ConversationRole Role, string Content);

public record LlmResponse
{
    public string Content { get; init; } = null!;
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int TotalTokens => InputTokens + OutputTokens;
    public decimal EstimatedCost { get; init; }
    public string Model { get; init; } = null!;
}

public record TokenCount(int Tokens);
```

**File**: `Beacon.Core/Services/LlmProviders/ClaudeProvider.cs`

```csharp
namespace Beacon.Core.Services.LlmProviders;

public class ClaudeProvider : ILlmProvider
{
    private readonly AnthropicClient _client;
    private readonly string _model;

    public ClaudeProvider(string apiKey, string model = "claude-sonnet-4.5")
    {
        _client = new AnthropicClient(apiKey);
        _model = model;
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        var messages = request.Messages
            .Select(m => new Message
            {
                Role = m.Role == ConversationRole.User ? "user" : "assistant",
                Content = m.Content
            })
            .ToList();

        var response = await _client.Messages.CreateAsync(
            new MessageRequest
            {
                Model = _model,
                Messages = messages,
                SystemPrompt = request.SystemPrompt,
                Temperature = (float)request.Temperature,
                MaxTokens = request.MaxTokens
            },
            cancellationToken);

        // Calculate cost: $3/1M input, $15/1M output
        var inputCost = (response.Usage.InputTokens / 1_000_000m) * 3m;
        var outputCost = (response.Usage.OutputTokens / 1_000_000m) * 15m;

        return new LlmResponse
        {
            Content = response.Content[0].Text,
            InputTokens = response.Usage.InputTokens,
            OutputTokens = response.Usage.OutputTokens,
            EstimatedCost = inputCost + outputCost,
            Model = response.Model
        };
    }

    public async Task<TokenCount> CountTokensAsync(string text, CancellationToken cancellationToken = default)
    {
        // Use Anthropic's count API
        var result = await _client.Messages.CountTokensAsync(text, cancellationToken);
        return new TokenCount(result.TokenCount);
    }
}
```

**Repeat for**:
- `OpenAiProvider.cs`
- `AzureOpenAiProvider.cs`

**File**: `Beacon.Core/Services/LlmProviders/LlmProviderFactory.cs`

```csharp
namespace Beacon.Core.Services.LlmProviders;

public class LlmProviderFactory
{
    private readonly LlmConfiguration _config;

    public LlmProviderFactory(LlmConfiguration config)
    {
        _config = config;
    }

    public ILlmProvider CreateProvider()
    {
        return _config.Provider switch
        {
            LlmProvider.OpenAI => new OpenAiProvider(_config.ApiKey, _config.Model),
            LlmProvider.Anthropic => new ClaudeProvider(_config.ApiKey, _config.Model),
            LlmProvider.AzureOpenAI => new AzureOpenAiProvider(
                _config.Endpoint!,
                _config.ApiKey,
                _config.Model),
            _ => throw new NotSupportedException($"Provider {_config.Provider} not supported")
        };
    }
}
```

---

## Step 6: Implement Core Services

**File**: `Beacon.Core/Services/Ai/IAiDocumentationService.cs`

```csharp
namespace Beacon.Core.Services.Ai;

public interface IAiDocumentationService
{
    Task<DataSourceDocumentation> GenerateDocumentationAsync(
        int dataSourceId,
        GenerationOptions options,
        CancellationToken cancellationToken = default);

    Task<string> ExportToMarkdownAsync(
        int documentationId,
        CancellationToken cancellationToken = default);

    Task<string> ExportToHtmlAsync(
        int documentationId,
        string? customCss = null,
        CancellationToken cancellationToken = default);

    Task<byte[]> ExportToPdfAsync(
        int documentationId,
        PdfOptions options,
        CancellationToken cancellationToken = default);
}
```

**File**: `Beacon.Core/Services/Ai/AiDocumentationService.cs`

```csharp
namespace Beacon.Core.Services.Ai;

public class AiDocumentationService : IAiDocumentationService
{
    private readonly ILlmProvider _llmProvider;
    private readonly IDatabaseMetadataService _metadataService;
    private readonly BeaconContext _context;
    private readonly ILogger<AiDocumentationService> _logger;

    public AiDocumentationService(
        ILlmProvider llmProvider,
        IDatabaseMetadataService metadataService,
        BeaconContext context,
        ILogger<AiDocumentationService> logger)
    {
        _llmProvider = llmProvider;
        _metadataService = metadataService;
        _context = context;
        _logger = logger;
    }

    public async Task<DataSourceDocumentation> GenerateDocumentationAsync(
        int dataSourceId,
        GenerationOptions options,
        CancellationToken cancellationToken = default)
    {
        // 1. Fetch schema metadata
        var metadata = await _metadataService.GetTablesAsync(dataSourceId);

        // 2. Filter tables
        var tables = FilterTables(metadata, options);

        // 3. Build AI prompt
        var prompt = BuildSchemaAnalysisPrompt(tables, options.SampleRowsPerTable);

        // 4. Call LLM
        var llmRequest = new LlmRequest
        {
            Messages = new List<ChatMessage>
            {
                new(ConversationRole.User, prompt)
            },
            SystemPrompt = "You are an expert database analyst...",
            Temperature = 0.3m,
            MaxTokens = 4096
        };

        var response = await _llmProvider.CompleteAsync(llmRequest, cancellationToken);

        // 5. Parse AI response and create documentation entities
        var documentation = new DataSourceDocumentation
        {
            DataSourceId = dataSourceId,
            Title = options.Title ?? $"{metadata.DataSourceName} Documentation",
            GeneratedByModel = response.Model,
            GeneratedAt = DateTime.UtcNow,
            GeneratedByUserId = GetCurrentUserId(),
            Status = DocumentationStatus.Draft,
            TablesAnalyzed = tables.Count,
            TokensUsed = response.TotalTokens,
            EstimatedCost = response.EstimatedCost
        };

        // 6. Save to database
        _context.DataSourceDocumentations.Add(documentation);
        await _context.SaveChangesAsync(cancellationToken);

        // 7. Track usage
        await TrackUsageAsync(dataSourceId, response, OperationType.SchemaAnalysis);

        return documentation;
    }

    // ... Implement other methods
}
```

---

## Step 7: Implement MediatR Handlers

**File**: `Beacon.Core/Handlers/Ai/GenerateDocumentation/GenerateDocumentationHandler.cs`

```csharp
namespace Beacon.Core.Handlers.Ai.GenerateDocumentation;

internal sealed class GenerateDocumentationHandler
    : IRequestHandler<GenerateDocumentationCommand, GenerateDocumentationResult>
{
    private readonly IAiDocumentationService _documentationService;
    private readonly ILogger<GenerateDocumentationHandler> _logger;

    public GenerateDocumentationHandler(
        IAiDocumentationService documentationService,
        ILogger<GenerateDocumentationHandler> logger)
    {
        _documentationService = documentationService;
        _logger = logger;
    }

    public async Task<GenerateDocumentationResult> Handle(
        GenerateDocumentationCommand request,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;

        var documentation = await _documentationService.GenerateDocumentationAsync(
            request.DataSourceId,
            request.Options,
            cancellationToken);

        var generationTime = DateTime.UtcNow - startTime;

        return new GenerateDocumentationResult
        {
            DocumentationId = documentation.Id,
            Title = documentation.Title,
            TablesAnalyzed = documentation.TablesAnalyzed,
            SectionsGenerated = documentation.Sections.Count,
            TokensUsed = documentation.TokensUsed,
            EstimatedCost = documentation.EstimatedCost,
            GenerationTime = generationTime,
            GeneratedByModel = documentation.GeneratedByModel,
            Warnings = new List<string>()
        };
    }
}

// Request and Response records at end of file
public record GenerateDocumentationCommand : IRequest<GenerateDocumentationResult>
{
    public int DataSourceId { get; init; }
    public string? Title { get; init; }
    public GenerationOptions Options { get; init; } = new();
}

public record GenerateDocumentationResult
{
    public int DocumentationId { get; init; }
    public string Title { get; init; } = null!;
    public int TablesAnalyzed { get; init; }
    public int SectionsGenerated { get; init; }
    public int TokensUsed { get; init; }
    public decimal EstimatedCost { get; init; }
    public TimeSpan GenerationTime { get; init; }
    public string GeneratedByModel { get; init; } = null!;
    public List<string> Warnings { get; init; } = new();
}
```

---

## Step 8: Register Services in DI

**File**: `Beacon.Core/ServiceConfiguration.cs`

```csharp
public static class ServiceConfiguration
{
    public static IServiceCollection AddBeacon(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Existing registrations...

        // AI Configuration
        var llmConfig = configuration.GetSection("Beacon:LLM").Get<LlmConfiguration>()
            ?? throw new InvalidOperationException("LLM configuration missing");

        services.AddSingleton(llmConfig);

        // LLM Provider
        services.AddSingleton<ILlmProvider>(sp =>
        {
            var config = sp.GetRequiredService<LlmConfiguration>();
            var factory = new LlmProviderFactory(config);
            return factory.CreateProvider();
        });

        // AI Services
        services.AddScoped<IAiDocumentationService, AiDocumentationService>();
        services.AddScoped<IAiAlertGenerationService, AiAlertGenerationService>();
        services.AddScoped<IDocumentationExportService, DocumentationExportService>();

        // Request queue for rate limiting
        services.AddSingleton<LlmRequestQueue>();

        return services;
    }
}
```

---

## Step 9: Add Configuration

**File**: `Beacon.SampleProject/appsettings.json`

```json
{
  "Beacon": {
    "LLM": {
      "Provider": "Anthropic",
      "ApiKey": "sk-ant-api03-...",
      "Model": "claude-sonnet-4.5",
      "FastModel": "gpt-4o-mini",
      "Limits": {
        "MaxConcurrentRequests": 50,
        "TokensPerMinute": 80000,
        "RequestsPerMinute": 1000,
        "MonthlyBudget": 100.00
      }
    }
  }
}
```

**Note**: Store API keys in User Secrets or Azure Key Vault for production:
```bash
dotnet user-secrets set "Beacon:LLM:ApiKey" "sk-ant-api03-..."
```

---

## Step 10: Create Database Migration

```bash
cd Beacon.Core.PostgreSql

dotnet ef migrations add AddAiIntegration \
  --startup-project ../Beacon.SampleProject

# Review generated migration
code Data/Migrations/*_AddAiIntegration.cs

# Apply migration
dotnet ef database update --startup-project ../Beacon.SampleProject
```

**IMPORTANT**: User will create migrations manually as per Beacon conventions.

---

## Step 11: Implement UI Components

**File**: `Beacon.UI/Components/Pages/Ai/GenerateDocumentation.razor`

```razor
@page "/ai/documentation/generate/{DataSourceId:int}"
@inject IMediator Mediator

<MudContainer MaxWidth="MaxWidth.Large">
    <MudText Typo="Typo.h4" Class="mb-4">Generate AI Documentation</MudText>

    <MudCard>
        <MudCardContent>
            <MudTextField @bind-Value="_title" Label="Documentation Title" />

            <MudNumericField @bind-Value="_maxTables" Label="Max Tables" Min="1" Max="500" />

            <MudNumericField @bind-Value="_sampleRows" Label="Sample Rows Per Table" Min="1" Max="100" />

            <MudSwitch @bind-Checked="_includeSampleData" Label="Include Sample Data" Color="Color.Primary" />

            <MudSwitch @bind-Checked="_includeRelationships" Label="Include Relationships" Color="Color.Primary" />
        </MudCardContent>

        <MudCardActions>
            <MudButton Variant="Variant.Filled" Color="Color.Primary" OnClick="GenerateAsync" Disabled="_isGenerating">
                @if (_isGenerating)
                {
                    <MudProgressCircular Class="ms-n1" Size="Size.Small" Indeterminate="true" />
                    <MudText Class="ms-2">Generating...</MudText>
                }
                else
                {
                    <MudText>Generate Documentation</MudText>
                }
            </MudButton>
        </MudCardActions>
    </MudCard>

    @if (_result != null)
    {
        <MudAlert Severity="Severity.Success" Class="mt-4">
            Documentation generated successfully!
            <MudText>Tables Analyzed: @_result.TablesAnalyzed</MudText>
            <MudText>Sections Generated: @_result.SectionsGenerated</MudText>
            <MudText>Estimated Cost: $@_result.EstimatedCost.ToString("F4")</MudText>
            <MudButton Href="@($"/ai/documentation/view/{_result.DocumentationId}")" Color="Color.Primary" Class="mt-2">
                View Documentation
            </MudButton>
        </MudAlert>
    }
</MudContainer>

@code {
    [Parameter] public int DataSourceId { get; set; }

    private string? _title;
    private int _maxTables = 50;
    private int _sampleRows = 10;
    private bool _includeSampleData = true;
    private bool _includeRelationships = true;
    private bool _isGenerating;
    private GenerateDocumentationResult? _result;

    private async Task GenerateAsync()
    {
        _isGenerating = true;
        try
        {
            var command = new GenerateDocumentationCommand
            {
                DataSourceId = DataSourceId,
                Title = _title,
                Options = new GenerationOptions
                {
                    MaxTables = _maxTables,
                    SampleRowsPerTable = _sampleRows,
                    IncludeSampleData = _includeSampleData,
                    IncludeRelationships = _includeRelationships
                }
            };

            _result = await Mediator.Send(command);
        }
        finally
        {
            _isGenerating = false;
        }
    }
}
```

---

## Step 12: Testing

**File**: `Beacon.Tests/Ai/AiDocumentationServiceTests.cs`

```csharp
public class AiDocumentationServiceTests
{
    private readonly Mock<ILlmProvider> _mockLlmProvider;
    private readonly Mock<IDatabaseMetadataService> _mockMetadataService;
    private readonly BeaconContext _context;
    private readonly AiDocumentationService _service;

    public AiDocumentationServiceTests()
    {
        _mockLlmProvider = new Mock<ILlmProvider>();
        _mockMetadataService = new Mock<IDatabaseMetadataService>();
        _context = CreateInMemoryContext();
        _service = new AiDocumentationService(
            _mockLlmProvider.Object,
            _mockMetadataService.Object,
            _context,
            Mock.Of<ILogger<AiDocumentationService>>());
    }

    [Fact]
    public async Task GenerateDocumentationAsync_ValidDataSource_ReturnsDocumentation()
    {
        // Arrange
        var dataSourceId = 1;
        var options = new GenerationOptions();

        _mockMetadataService.Setup(m => m.GetTablesAsync(dataSourceId))
            .ReturnsAsync(new DatabaseMetadata { Tables = CreateSampleTables() });

        _mockLlmProvider.Setup(p => p.CompleteAsync(It.IsAny<LlmRequest>(), default))
            .ReturnsAsync(new LlmResponse
            {
                Content = "# Documentation\n\n## Tables...",
                InputTokens = 1000,
                OutputTokens = 500,
                EstimatedCost = 0.05m,
                Model = "claude-sonnet-4.5"
            });

        // Act
        var result = await _service.GenerateDocumentationAsync(dataSourceId, options);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(dataSourceId, result.DataSourceId);
        Assert.Equal(1500, result.TokensUsed);
        Assert.Equal(0.05m, result.EstimatedCost);
    }
}
```

**Run tests**:
```bash
dotnet test
```

---

## Step 13: Manual Testing

1. **Start application**:
```bash
dotnet run --project Beacon.SampleProject
```

2. **Configure LLM**:
   - Navigate to Admin > AI Configuration
   - Enter API key
   - Select provider and model
   - Test connection

3. **Generate Documentation**:
   - Navigate to Data Sources
   - Select a data source
   - Click "Generate AI Documentation"
   - Review generated documentation
   - Edit sections if needed
   - Export to PDF/HTML/Markdown

4. **Create AI Alert**:
   - Navigate to Queries
   - Click "Create AI Alert"
   - Enter natural language description
   - Review generated SQL
   - Refine if needed
   - Activate alert

---

## Common Issues & Troubleshooting

### Issue: LLM API returns 401 Unauthorized

**Solution**: Check API key in configuration:
```bash
dotnet user-secrets list
```

### Issue: Migration fails with "table already exists"

**Solution**: Drop and recreate:
```bash
dotnet ef database drop --startup-project Beacon.SampleProject
dotnet ef database update --startup-project Beacon.SampleProject
```

### Issue: AI generates invalid SQL

**Solution**: Enable validation and refinement:
```csharp
Options = new AlertGenerationOptions
{
    ValidateSyntax = true,
    RequestClarification = true
}
```

### Issue: Rate limit exceeded (429)

**Solution**: Check and adjust rate limits in config:
```json
"Limits": {
  "MaxConcurrentRequests": 20,
  "RequestsPerMinute": 500
}
```

---

## Next Steps

After completing this quickstart:

1. **Optimize costs**: Implement schema filtering and prompt caching
2. **Add monitoring**: Set up budget alerts and usage dashboards
3. **Customize prompts**: Update AI prompt templates for your domain
4. **Integrate with CI/CD**: Automate documentation generation on schema changes
5. **Collect feedback**: Gather user feedback to improve AI accuracy

---

## Resources

- [Technical Specification](./spec.md)
- [Data Model](./data-model.md)
- [API Contracts](./contracts/)
- [Research Findings](./research.md)

---

## Support

For issues or questions:
- GitHub Issues: https://github.com/yourorg/beacon/issues
- Documentation: https://docs.beacon.io
- Community Discord: https://discord.gg/beacon
