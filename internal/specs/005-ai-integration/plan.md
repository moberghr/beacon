# Implementation Plan: AI Integration

**Branch**: `005-ai-integration` | **Date**: 2026-01-03 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/005-ai-integration/spec.md`

**Note**: This template is filled in by the `/speckit.plan` command. See `.specify/templates/commands/plan.md` for the execution workflow.

## Summary

This feature integrates Large Language Model (LLM) capabilities into Beacon to provide two primary capabilities:

1. **AI-Powered Data Source Documentation**: Automatically analyze data source schemas and sample data to generate comprehensive documentation that users can edit and export in multiple formats (Markdown, HTML, PDF, JSON).

2. **Natural Language Alert Configuration**: Enable users to describe complex alert conditions in natural language, with AI translating these descriptions into SQL queries that integrate with Beacon's existing subscription and notification infrastructure.

This extends Beacon beyond simple query execution and data monitoring into intelligent data analysis and democratized alert creation for non-technical users.

## Technical Context

**Language/Version**: C# 12 / .NET 8.0
**Primary Dependencies**:
- Existing: EF Core 8.0, MediatR, Blazor Server, MudBlazor
- New: Microsoft.Extensions.AI v10.1.1 (IChatClient abstraction)
- New: OpenAI v2.8.0, Anthropic v12.0.1, Azure.AI.OpenAI v2.0.0 (direct provider SDKs)
- New: QuestPDF v2025.12.1 (PDF generation - free for <$1M revenue)
- New: Markdig v0.44.0 (Markdown/HTML rendering)

**Storage**: PostgreSQL (primary) and SQL Server (secondary) via provider-specific projects
**Testing**: xUnit with existing test infrastructure
**Target Platform**: Web (Blazor Server), cross-platform via .NET 8
**Project Type**: Web application with existing Clean Architecture structure
**Performance Goals**:
- AI documentation generation: <2 minutes for 20 tables (excluding LLM API latency)
- AI query generation: <30 seconds for simple alerts (excluding LLM API latency)
- Documentation export: <10 seconds for typical documentation (50 pages PDF)

**Constraints**:
- LLM API rate limits: 80K TPM / 1K RPM (Anthropic Tier 1), scalable to 400K+ TPM enterprise
- LLM API costs: ~$7.40/month for 100 active users ($3/$15 per 1M tokens for Claude Sonnet 4.5)
- Context window: 200K tokens (Claude Sonnet 4.5) - supports ~497 tables max, recommend 50 with filtering
- Data privacy: AI should never send sensitive data without explicit user consent
- Existing notification infrastructure must be reused (no changes to Email/Teams/Slack/Jira adapters)

**Scale/Scope**:
- Expected: 100-1000 users per instance
- Data sources per user: 5-50
- Tables per data source: 10-500 (requires batching for large schemas)
- Documentation versions per data source: 10-50 (requires cleanup strategy)
- AI conversations per user: 100-1000 (requires storage and retrieval optimization)

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

### I. Clean Architecture ✅

**Compliance**: PASS

- AI services (IAiDocumentationService, IAiAlertGenerationService) will reside in Beacon.Core with interfaces
- LLM provider implementations will be in Beacon.Core with factory pattern for provider selection
- UI components for documentation editing and AI alert configuration will be in Beacon.UI
- Dependencies flow inward: UI → AI Services → LLM Provider Abstractions
- New entities (DataSourceDocumentation, AiAlertConfiguration) will implement IChangeableEntity
- AI usage metrics will inherit from BaseArchivableEntity

**Rationale**: Follows existing architecture pattern, no violations.

### II. Schema-Agnostic Database Design ✅

**Compliance**: PASS

- New tables (DataSourceDocumentation, DocumentationSection, AiAlertConfiguration, AiConversationHistory, AiUsageMetrics) will be added via migrations
- Migrations will use default "beacon" schema during generation
- Runtime schema specified via existing AddPostgreSqlBeacon/AddSqlServerBeacon configuration
- No hardcoded schema references in entities or migrations

**Rationale**: New entities follow existing schema-agnostic pattern, no violations.

### III. Multi-Provider Database Support ✅

**Compliance**: PASS

- New migrations will be created for both Beacon.Core.PostgreSql and Beacon.Core.SqlServer
- JSON storage for AI conversation history and documentation content may require provider-specific column types (jsonb for PostgreSQL, nvarchar(max) for SQL Server) but this is handled through EF Core configuration
- No MySQL-specific migrations needed (MySQL is only used for query execution by consumers, not for Beacon's internal storage)

**Rationale**: Follows existing multi-provider pattern, no violations.

### IV. Handler-Based Command/Query Pattern (CQRS) ✅

**Compliance**: PASS

- All AI operations will use MediatR handlers:
  - `GenerateDocumentationCommand` / `GenerateDocumentationHandler`
  - `UpdateDocumentationCommand` / `UpdateDocumentationHandler`
  - `ExportDocumentationQuery` / `ExportDocumentationHandler`
  - `GenerateAlertQueryCommand` / `GenerateAlertQueryHandler`
  - `RefineAlertQueryCommand` / `RefineAlertQueryHandler`
- Handlers will be `internal sealed class` with primary constructor injection
- Request/response defined as records at file end

**Rationale**: Follows existing CQRS pattern, no violations.

### V. Strong Typing and Explicit Contracts ✅

**Compliance**: PASS

- All AI requests/responses will use explicit models (not anonymous types or dictionaries)
- LLM API responses will be mapped to strongly-typed DTOs
- Documentation export will use explicit format enums (DocumentationExportFormat.Markdown/Html/Pdf/Json)
- AI conversation history will use proper value objects for messages
- Token usage tracking will use explicit numeric types (int for token counts, decimal for costs)

**Rationale**: Follows existing strong typing principles, no violations.

### VI. Code Style Consistency ✅

**Compliance**: PASS

- All new code will follow PascalCase for classes/methods/properties, camelCase for parameters/locals
- Files organized by domain: src/Beacon.Core/Services/Ai/, src/Beacon.Core/Models/Ai/
- Custom exception: AiServiceException inheriting from BeaconException
- Imports ordered: System → third-party (LLM SDKs) → project namespaces

**Rationale**: Follows existing code style conventions, no violations.

---

**GATE STATUS**: ✅ **PASSED** - All constitutional principles are satisfied. No complexity violations. Proceed to Phase 0 research.

---

## Post-Design Constitution Check (Phase 1 Complete)

**Re-evaluation Date**: 2026-01-03
**Status**: ✅ **PASSED** - Design maintains constitutional compliance

### I. Clean Architecture ✅

**Status**: COMPLIANT

**Verification**:
- ✅ All entities (DataSourceDocumentation, AiAlertConfiguration, etc.) implement IChangeableEntity
- ✅ AI services (IAiDocumentationService, IAiAlertGenerationService) defined in Beacon.Core
- ✅ LLM provider abstractions (ILlmProvider) isolate external dependencies
- ✅ UI components in Beacon.UI (GenerateDocumentation.razor, CreateAiAlert.razor)
- ✅ Dependencies flow inward: UI → Handlers → Services → LLM Providers
- ✅ No infrastructure concerns in domain entities

**Data Model Compliance**:
- 7 new entities following BaseArchivableEntity pattern
- All entities have proper navigation properties
- No direct LLM SDK references in entities

### II. Schema-Agnostic Database Design ✅

**Status**: COMPLIANT

**Verification**:
- ✅ All migrations use default "beacon" schema
- ✅ No hardcoded schema references in entity configurations
- ✅ JSON storage uses provider-agnostic string types with EF Core configuration
- ✅ Indexes created with schema-agnostic syntax

**Notes**:
- JSON columns (Metadata, ValidationErrors, Conversation) handled via EF Core's JSON support
- PostgreSQL: jsonb type automatically configured
- SQL Server: nvarchar(max) with JSON handling

### III. Multi-Provider Database Support ✅

**Status**: COMPLIANT

**Verification**:
- ✅ Migrations will be generated for both Beacon.Core.PostgreSql and Beacon.Core.SqlServer
- ✅ All data types are provider-neutral (string, int, decimal, DateTime)
- ✅ Indexes use standard SQL syntax
- ✅ JSON storage abstracted through EF Core

**Impact**:
- ~7 new tables per provider
- ~19 new indexes per provider
- JSON columns tested across both PostgreSQL and SQL Server

### IV. Handler-Based Command/Query Pattern (CQRS) ✅

**Status**: COMPLIANT

**Verification**:
- ✅ All operations implemented as MediatR handlers (13 total):
  - GenerateDocumentationHandler
  - UpdateDocumentationHandler
  - ExportDocumentationHandler
  - GenerateAlertQueryHandler
  - RefineAlertQueryHandler
  - ActivateAiAlertHandler
  - PauseAiAlertHandler
  - ResumeAiAlertHandler
  - GetAiUsageMetricsHandler
  - ConfigureAiProviderHandler
  - UpdatePromptTemplateHandler
  - And more...
- ✅ All handlers are internal sealed classes
- ✅ Request/Response records defined as separate types
- ✅ Primary constructor injection pattern followed

**Contract Structure**:
- 3 contract files (documentation, alert, admin)
- Clear separation of commands vs queries
- Strongly-typed request/response models

### V. Strong Typing and Explicit Contracts ✅

**Status**: COMPLIANT

**Verification**:
- ✅ All entities use explicit types (no anonymous types)
- ✅ Enums defined for all status/type fields (DocumentationStatus, AlertStatus, SectionType, etc.)
- ✅ LLM responses mapped to strongly-typed DTOs (LlmResponse, TokenCount)
- ✅ Export formats use enum (DocumentationExportFormat)
- ✅ Required string properties use null! pattern
- ✅ Proper indexing on queried properties (DataSourceId, Status, Timestamp)

**Type Safety**:
- 6 new enums for domain modeling
- No primitive obsession
- Value objects for LLM interactions

### VI. Code Style Consistency ✅

**Status**: COMPLIANT

**Verification**:
- ✅ All entities follow PascalCase for classes/properties
- ✅ Services organized by domain: Services/Ai/, Services/LlmProviders/
- ✅ Custom exception: AiServiceException (to be created, inherits from BeaconException)
- ✅ Imports will be ordered: System → Microsoft.Extensions → Third-party → Beacon
- ✅ Files organized in folders: Handlers/Ai/, Models/Ai/, Services/Ai/

**Organization**:
- Clear separation of concerns
- Folder structure mirrors architectural layers
- Consistent naming conventions

---

**FINAL GATE STATUS**: ✅ **PASSED**

All constitutional principles remain satisfied after Phase 1 design. The implementation plan follows established patterns and introduces no architectural violations.

**Complexity Assessment**: No violations recorded. The feature integrates cleanly into existing architecture without introducing unnecessary complexity.

---

## Project Structure

### Documentation (this feature)

```text
specs/[###-feature]/
├── plan.md              # This file (/speckit.plan command output)
├── research.md          # Phase 0 output (/speckit.plan command)
├── data-model.md        # Phase 1 output (/speckit.plan command)
├── quickstart.md        # Phase 1 output (/speckit.plan command)
├── contracts/           # Phase 1 output (/speckit.plan command)
└── tasks.md             # Phase 2 output (/speckit.tasks command - NOT created by /speckit.plan)
```

### Source Code (repository root)

```text
src/Beacon.Core/                          # Core domain logic (Clean Architecture)
├── Data/
│   ├── Entities/
│   │   ├── DataSourceDocumentation.cs   # NEW: Stores AI-generated documentation
│   │   ├── DocumentationSection.cs      # NEW: Individual doc sections with AI/user flags
│   │   ├── DocumentationExport.cs       # NEW: Cached HTML exports with version tracking
│   │   ├── DiagramGroup.cs              # NEW: User-customizable ERD table groupings
│   │   ├── SchemaSnapshot.cs            # NEW: Schema state at documentation generation
│   │   ├── SchemaChange.cs              # NEW: Detected schema changes with history
│   │   ├── FieldAnalysis.cs             # NEW: Per-column analysis with patterns/recommendations
│   │   ├── AiAlertConfiguration.cs      # NEW: Natural language + generated SQL storage (+ IsAiGenerated flag)
│   │   ├── AiConversationHistory.cs     # NEW: Tracks AI conversation context
│   │   ├── AiUsageMetrics.cs            # NEW: Token usage, costs tracking
│   │   ├── AiMonitoringConfiguration.cs # NEW: Per-data-source AI monitoring settings
│   │   ├── AiMonitoringBaseline.cs      # NEW: Learned "normal" patterns for metrics
│   │   ├── AiInsight.cs                 # NEW: AI-discovered anomalies and findings
│   │   └── PromptTemplateVersion.cs     # NEW: Version history for documentation prompt
│   └── Enums/
│       ├── DocumentationExportFormat.cs # NEW: Markdown, HTML, PDF, JSON
│       ├── ExportFormat.cs              # NEW: Html, Pdf, Markdown, Json (for DocumentationExport)
│       ├── DiagramGroupingCriteria.cs   # NEW: ForeignKey, Naming, Semantic, Manual
│       ├── SchemaChangeType.cs          # NEW: TableAdded, ColumnRemoved, etc.
│       ├── SchemaObjectType.cs          # NEW: Table, Column, Relationship
│       ├── RenameStatus.cs              # NEW: Pending, Confirmed, Rejected
│       ├── FieldUsageStatus.cs          # NEW: Used, PotentiallyUnused, Unused
│       ├── DetectedDataPattern.cs       # NEW: Email, Phone, Date, Json, Url, Numeric, Uuid
│       ├── AiProvider.cs                # NEW: OpenAI, Claude, AzureOpenAI
│       ├── MonitoringMode.cs            # NEW: TaskMode, NotificationMode
│       ├── MonitoringScheduleFrequency.cs # NEW: Hourly, Daily, Weekly, Custom
│       ├── VerbosityLevel.cs            # NEW: Minimal, Standard, Detailed, Full
│       ├── AnomalyType.cs               # NEW: Statistical, TrendChange, MissingData, etc.
│       ├── InsightSeverity.cs           # NEW: Low, Medium, High, Critical
│       ├── InsightStatus.cs             # NEW: New, Reviewed, Dismissed, ConvertedToAlert
│       ├── BaselineType.cs              # NEW: Statistical, Trend, Threshold, Volume
│       └── TrendDirection.cs            # NEW: Stable, Increasing, Decreasing, Volatile
├── Models/
│   └── Ai/                              # NEW: AI-specific DTOs and models
│       ├── DocumentationGenerationRequest.cs
│       ├── DocumentationExportRequest.cs
│       ├── FieldAnalysisRequest.cs      # NEW: Field analysis options
│       ├── FieldAnalysisSummary.cs      # NEW: Analysis results summary
│       ├── SamplingConfig.cs            # NEW: Sampling thresholds and calculation
│       ├── AlertGenerationRequest.cs
│       ├── AlertRefinementRequest.cs
│       ├── MonitoringLimits.cs          # NEW: Query/token/cost limits config
│       ├── MonitoringUsage.cs           # NEW: Current usage tracking
│       ├── InsightFilter.cs             # NEW: Filter options for insights query
│       ├── AlertAdjustment.cs           # NEW: AI-proposed alert changes
│       ├── LlmResponse.cs
│       └── TokenUsageInfo.cs
├── Services/
│   ├── Ai/                              # NEW: AI service layer
│   │   ├── IAiDocumentationService.cs
│   │   ├── AiDocumentationService.cs
│   │   ├── IFieldAnalysisService.cs     # NEW: Field quality analysis interface
│   │   ├── FieldAnalysisService.cs      # NEW: Pattern detection, sampling, recommendations
│   │   ├── IAiAlertGenerationService.cs
│   │   ├── AiAlertGenerationService.cs
│   │   ├── IDocumentationExportService.cs
│   │   ├── DocumentationExportService.cs
│   │   ├── IHtmlExportService.cs        # NEW: Interactive HTML generation
│   │   ├── HtmlExportService.cs         # NEW: Mermaid diagrams, collapsible sections, TOC
│   │   ├── IMermaidDiagramService.cs    # NEW: ERD diagram generation
│   │   ├── MermaidDiagramService.cs     # NEW: Converts schema to Mermaid ERD syntax
│   │   ├── IDiagramGroupingService.cs   # NEW: AI-powered table grouping
│   │   ├── DiagramGroupingService.cs    # NEW: FK, naming, semantic analysis for groups
│   │   ├── ISchemaChangeDetectionService.cs  # NEW: Schema comparison and change detection
│   │   ├── SchemaChangeDetectionService.cs   # NEW: Compares snapshots, detects changes
│   │   ├── IRenameDetectionService.cs   # NEW: AI rename suggestion interface
│   │   ├── RenameDetectionService.cs    # NEW: Uses AI to detect renames vs add/delete
│   │   ├── IAiMonitoringService.cs      # NEW: Unsupervised monitoring interface
│   │   ├── AiMonitoringService.cs       # NEW: Anomaly detection, baseline learning
│   │   ├── IBaselineLearningService.cs  # NEW: Baseline calculation interface
│   │   ├── BaselineLearningService.cs   # NEW: Statistical baseline learning
│   │   ├── IAnomalyDetectionService.cs  # NEW: Anomaly detection interface
│   │   └── AnomalyDetectionService.cs   # NEW: Detects all anomaly types
│   └── LlmProviders/                    # NEW: LLM provider abstractions
│       ├── ILlmProvider.cs              # Interface for all providers
│       ├── OpenAiProvider.cs            # OpenAI implementation
│       ├── ClaudeProvider.cs            # Anthropic Claude implementation
│       ├── AzureOpenAiProvider.cs       # Azure OpenAI implementation
│       └── LlmProviderFactory.cs        # Provider selection logic
└── Handlers/
    └── Ai/                              # NEW: MediatR handlers for AI operations
        ├── GenerateDocumentation/
        │   ├── GenerateDocumentationCommand.cs
        │   └── GenerateDocumentationHandler.cs
        ├── UpdateDocumentation/
        ├── ExportDocumentation/
        ├── HtmlExport/                  # NEW: Interactive HTML export handlers
        │   ├── ExportHtmlCommand.cs
        │   ├── ExportHtmlHandler.cs
        │   ├── GenerateDiagramGroupsQuery.cs
        │   ├── GenerateDiagramGroupsHandler.cs
        │   ├── SaveDiagramGroupsCommand.cs
        │   ├── SaveDiagramGroupsHandler.cs
        │   ├── GetDiagramGroupsQuery.cs
        │   └── GetDiagramGroupsHandler.cs
        ├── SchemaChange/                # NEW: Schema change detection handlers
        │   ├── CheckSchemaChangesQuery.cs
        │   ├── CheckSchemaChangesHandler.cs
        │   ├── ConfirmRenameCommand.cs
        │   ├── ConfirmRenameHandler.cs
        │   ├── GetSchemaChangeHistoryQuery.cs
        │   ├── GetSchemaChangeHistoryHandler.cs
        │   ├── AcknowledgeSchemaChangesCommand.cs
        │   └── AcknowledgeSchemaChangesHandler.cs
        ├── FieldAnalysis/               # NEW: Field analysis handlers
        │   ├── RunFieldAnalysisCommand.cs
        │   ├── RunFieldAnalysisHandler.cs
        │   ├── ApproveFullScanCommand.cs
        │   ├── ApproveFullScanHandler.cs
        │   ├── GetFieldAnalysisQuery.cs
        │   └── GetFieldAnalysisHandler.cs
        ├── GenerateAlertQuery/
        ├── RefineAlertQuery/
        ├── Monitoring/                  # NEW: AI monitoring handlers
        │   ├── EnableAiMonitoringCommand.cs
        │   ├── EnableAiMonitoringHandler.cs
        │   ├── DisableAiMonitoringCommand.cs
        │   ├── DisableAiMonitoringHandler.cs
        │   ├── GetMonitoringConfigurationQuery.cs
        │   ├── GetMonitoringConfigurationHandler.cs
        │   ├── ListAiInsightsQuery.cs
        │   ├── ListAiInsightsHandler.cs
        │   ├── GetAiInsightDetailQuery.cs
        │   ├── GetAiInsightDetailHandler.cs
        │   ├── ReviewAiInsightCommand.cs
        │   ├── ReviewAiInsightHandler.cs
        │   ├── ConvertInsightToAlertCommand.cs
        │   ├── ConvertInsightToAlertHandler.cs
        │   ├── TriggerManualAnalysisCommand.cs
        │   └── TriggerManualAnalysisHandler.cs
        └── PromptVersioning/            # NEW: Prompt template versioning handlers
            ├── CreatePromptVersionCommand.cs
            ├── CreatePromptVersionHandler.cs
            ├── ListPromptVersionsQuery.cs
            ├── ListPromptVersionsHandler.cs
            ├── GetPromptVersionQuery.cs
            ├── GetPromptVersionHandler.cs
            ├── RestorePromptVersionCommand.cs
            └── RestorePromptVersionHandler.cs

src/Beacon.Core.PostgreSql/              # PostgreSQL provider
└── Data/
    └── Migrations/
        └── [Timestamp]_AddAiIntegration.cs  # NEW: AI entities migration

src/Beacon.Core.SqlServer/               # SQL Server provider
└── Data/
    └── Migrations/
        └── [Timestamp]_AddAiIntegration.cs  # NEW: AI entities migration

src/Beacon.UI/                            # Blazor UI
└── Components/
    └── Pages/
        ├── DataSources/
        │   └── DataSourceDocumentation.razor        # NEW: Documentation viewer/editor
        ├── Ai/                                      # NEW: AI-specific pages
        │   ├── GenerateDocumentation.razor
        │   ├── EditDocumentation.razor
        │   ├── ExportDocumentation.razor
        │   ├── FieldAnalysisResults.razor           # NEW: Field analysis results viewer
        │   ├── FieldAnalysisApproval.razor          # NEW: Full scan approval dialog
        │   ├── ExportHtmlPreview.razor              # NEW: Preview interactive HTML export
        │   ├── DiagramGroupEditor.razor             # NEW: Customize ERD diagram groups
        │   ├── MermaidDiagramViewer.razor           # NEW: Render Mermaid ERD diagrams
        │   ├── SchemaChangeBanner.razor             # NEW: Notification banner for schema changes
        │   ├── SchemaChangeDiff.razor               # NEW: Diff view for schema changes
        │   ├── SchemaChangeHistory.razor            # NEW: Full change history with filters
        │   ├── RenameConfirmationDialog.razor       # NEW: Confirm/reject rename suggestions
        │   ├── CreateAiAlert.razor                  # Natural language alert input
        │   ├── AiAlertConversation.razor            # Chat interface for refinement
        │   ├── AiInsights.razor                     # NEW: AI Insights page (monitoring findings)
        │   ├── AiInsightDetail.razor                # NEW: Single insight detail view
        │   ├── MonitoringConfiguration.razor        # NEW: Enable/configure AI monitoring
        │   └── MonitoringBaselines.razor            # NEW: View learned baselines
        └── Admin/
            ├── AiConfiguration.razor                # NEW: AI provider config, usage monitoring
            └── PromptVersionManager.razor           # NEW: View/create/restore prompt versions

src/Beacon.Tests/                         # Test project
└── Ai/                                  # NEW: AI tests
    ├── AiDocumentationServiceTests.cs
    ├── AiAlertGenerationServiceTests.cs
    ├── DocumentationExportServiceTests.cs
    ├── FieldAnalysis/                   # NEW: Field analysis tests
    │   ├── FieldAnalysisServiceTests.cs
    │   ├── PatternDetectionTests.cs
    │   ├── SamplingConfigTests.cs
    │   └── MigrationFeasibilityTests.cs
    ├── HtmlExport/                      # NEW: HTML export tests
    │   ├── HtmlExportServiceTests.cs
    │   ├── MermaidDiagramServiceTests.cs
    │   ├── DiagramGroupingServiceTests.cs
    │   ├── CacheInvalidationTests.cs
    │   └── CollapsibleSectionsTests.cs
    ├── SchemaChange/                    # NEW: Schema change detection tests
    │   ├── SchemaChangeDetectionServiceTests.cs
    │   ├── RenameDetectionServiceTests.cs
    │   ├── SchemaSnapshotTests.cs
    │   ├── SchemaDiffTests.cs
    │   └── ChangeHistoryTests.cs
    ├── Monitoring/                      # NEW: AI monitoring tests
    │   ├── AiMonitoringServiceTests.cs
    │   ├── BaselineLearningServiceTests.cs
    │   ├── AnomalyDetectionServiceTests.cs
    │   ├── StatisticalAnomalyTests.cs
    │   ├── TrendChangeDetectionTests.cs
    │   ├── MissingDataDetectionTests.cs
    │   ├── LimitEnforcementTests.cs
    │   └── InsightConversionTests.cs
    ├── PromptVersioning/                # NEW: Prompt versioning tests
    │   ├── CreatePromptVersionTests.cs
    │   ├── ListPromptVersionsTests.cs
    │   ├── RestorePromptVersionTests.cs
    │   └── VersionNumberSequenceTests.cs
    └── LlmProviders/
        ├── OpenAiProviderTests.cs
        ├── ClaudeProviderTests.cs
        └── AzureOpenAiProviderTests.cs
```

**Structure Decision**:

This feature extends the existing Clean Architecture structure with new AI capabilities:

1. **Core Domain**: All AI logic lives in `Beacon.Core` following existing patterns (services, handlers, entities)
2. **Provider Abstraction**: `ILlmProvider` interface enables multiple LLM providers (OpenAI, Claude, Azure) without coupling to specific SDKs
3. **Database Agnostic**: New entities and migrations follow existing schema-agnostic pattern across PostgreSQL and SQL Server
4. **UI Separation**: All UI components for AI features are isolated in `src/Beacon.UI/Components/Pages/Ai/`
5. **Testing**: New test classes follow existing xUnit structure in `src/Beacon.Tests/`

No new top-level projects are needed - the feature integrates cleanly into the existing architecture.

## Complexity Tracking

> **Fill ONLY if Constitution Check has violations that must be justified**

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| [e.g., 4th project] | [current need] | [why 3 projects insufficient] |
| [e.g., Repository pattern] | [specific problem] | [why direct DB access insufficient] |
