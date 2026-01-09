# Tasks: AI Integration

**Input**: Design documents from `/specs/005-ai-integration/`
**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/

**Tests**: Tests are included per user story where specified.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US1b, US2, US3...)
- Include exact file paths in descriptions

## User Story Summary

| Story | Title | Priority | Dependencies |
|-------|-------|----------|--------------|
| US1 | AI-Generated Data Source Documentation | P1 | Foundational |
| US1b | AI-Powered Field Quality Analysis | P1 | US1 |
| US3 | AI-Powered Smart Alerts | P1 | Foundational |
| US2 | Editable and Exportable Documentation | P2 | US1 |
| US2b | Interactive HTML Export with ERD | P2 | US2 |
| US2c | Schema Change Detection | P2 | US1 |
| US4 | AI Alert Query Refinement | P2 | US3 |
| US6 | Unsupervised AI Monitoring | P2 | US3 |
| US6b | AI Monitoring Configuration | P2 | US6 |
| US2d | Prompt Template Versioning | P3 | US1 |
| US5 | AI-Assisted Alert Template Library | P3 | US3 |

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Add NuGet packages and create base project structure for AI features

- [ ] T001 Add NuGet packages to Semantico.Core: Microsoft.Extensions.AI, OpenAI, Anthropic, Azure.AI.OpenAI, Markdig, QuestPDF in Semantico.Core/Semantico.Core.csproj
- [ ] T002 [P] Create AI folder structure: Semantico.Core/Services/Ai/, Semantico.Core/Services/LlmProviders/, Semantico.Core/Models/Ai/, Semantico.Core/Handlers/Ai/
- [ ] T003 [P] Create AI folder structure in UI: Semantico.UI/Components/Pages/Ai/
- [ ] T004 [P] Create AI folder structure in Tests: Semantico.Tests/Ai/

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core LLM infrastructure that MUST be complete before ANY user story can be implemented

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

### Enums (Parallelizable)

- [ ] T005 [P] Create AiProvider enum (OpenAI, Claude, AzureOpenAI) in Semantico.Core/Data/Enums/AiProvider.cs
- [ ] T006 [P] Create DocumentationStatus enum (Draft, Published, Archived) in Semantico.Core/Data/Enums/DocumentationStatus.cs
- [ ] T007 [P] Create SectionType enum in Semantico.Core/Data/Enums/SectionType.cs
- [ ] T008 [P] Create ContentFormat enum in Semantico.Core/Data/Enums/ContentFormat.cs
- [ ] T009 [P] Create AlertStatus enum in Semantico.Core/Data/Enums/AlertStatus.cs
- [ ] T010 [P] Create ConversationRole enum in Semantico.Core/Data/Enums/ConversationRole.cs
- [ ] T011 [P] Create OperationType enum in Semantico.Core/Data/Enums/OperationType.cs
- [ ] T012 [P] Create DocumentationExportFormat enum in Semantico.Core/Data/Enums/DocumentationExportFormat.cs

### LLM Provider Infrastructure

- [ ] T013 Create LlmConfiguration model in Semantico.Core/Models/Configuration/LlmConfiguration.cs
- [ ] T014 Create ILlmProvider interface with LlmRequest, LlmResponse, TokenCount records in Semantico.Core/Services/LlmProviders/ILlmProvider.cs
- [ ] T015 [P] Implement OpenAiProvider in Semantico.Core/Services/LlmProviders/OpenAiProvider.cs
- [ ] T016 [P] Implement ClaudeProvider in Semantico.Core/Services/LlmProviders/ClaudeProvider.cs
- [ ] T017 [P] Implement AzureOpenAiProvider in Semantico.Core/Services/LlmProviders/AzureOpenAiProvider.cs
- [ ] T018 Create LlmProviderFactory in Semantico.Core/Services/LlmProviders/LlmProviderFactory.cs
- [ ] T019 Create LlmRequestQueue for rate limiting in Semantico.Core/Services/LlmProviders/LlmRequestQueue.cs

### Core Models

- [ ] T020 [P] Create TokenUsageInfo model in Semantico.Core/Models/Ai/TokenUsageInfo.cs
- [ ] T021 [P] Create LlmResponse model in Semantico.Core/Models/Ai/LlmResponse.cs

### Core Exception

- [ ] T022 Create AiServiceException class in Semantico.Core/Exceptions/AiServiceException.cs

### Service Registration

- [ ] T023 Add LLM services registration to ServiceConfiguration.cs in Semantico.Core/ServiceConfiguration.cs
- [ ] T024 Add LLM configuration section to appsettings.json in Semantico.SampleProject/appsettings.json

### Usage Tracking Entity

- [ ] T025 Create AiUsageMetrics entity in Semantico.Core/Data/Entities/AiUsageMetrics.cs
- [ ] T026 Add AiUsageMetrics DbSet and indexes to SemanticoContext in Semantico.Core/Data/SemanticoContext.cs (partial - add DbSet)

**Checkpoint**: LLM provider infrastructure ready - user story implementation can now begin

---

## Phase 3: User Story 1 - AI-Generated Data Source Documentation (Priority: P1) 🎯 MVP

**Goal**: Enable AI to analyze data sources and generate comprehensive documentation from schema and sample data

**Independent Test**: Connect to a data source with multiple tables, trigger AI analysis, verify documentation is generated with schema info, column descriptions, and relationships

### Entities for US1

- [ ] T027 [P] [US1] Create DataSourceDocumentation entity in Semantico.Core/Data/Entities/DataSourceDocumentation.cs
- [ ] T028 [P] [US1] Create DocumentationSection entity in Semantico.Core/Data/Entities/DocumentationSection.cs
- [ ] T029 [P] [US1] Create DocumentationVersion entity in Semantico.Core/Data/Entities/DocumentationVersion.cs
- [ ] T030 [US1] Add DataSourceDocumentation, DocumentationSection, DocumentationVersion DbSets and indexes to SemanticoContext in Semantico.Core/Data/SemanticoContext.cs

### Models for US1

- [ ] T031 [P] [US1] Create DocumentationGenerationRequest model in Semantico.Core/Models/Ai/DocumentationGenerationRequest.cs
- [ ] T032 [P] [US1] Create GenerationOptions model in Semantico.Core/Models/Ai/GenerationOptions.cs

### Prompt Template Entity

- [ ] T033 [US1] Create AiPromptTemplate entity in Semantico.Core/Data/Entities/AiPromptTemplate.cs
- [ ] T034 [US1] Add AiPromptTemplate DbSet and indexes to SemanticoContext

### Services for US1

- [ ] T035 [US1] Create IAiDocumentationService interface in Semantico.Core/Services/Ai/IAiDocumentationService.cs
- [ ] T036 [US1] Implement AiDocumentationService in Semantico.Core/Services/Ai/AiDocumentationService.cs
- [ ] T037 [US1] Register IAiDocumentationService in ServiceConfiguration.cs

### Handlers for US1

- [ ] T038 [US1] Create GenerateDocumentationCommand and GenerateDocumentationHandler in Semantico.Core/Handlers/Ai/GenerateDocumentation/GenerateDocumentationHandler.cs
- [ ] T039 [US1] Create GetDocumentationQuery and GetDocumentationHandler in Semantico.Core/Handlers/Ai/GetDocumentation/GetDocumentationHandler.cs
- [ ] T040 [US1] Create ListDocumentationsQuery and ListDocumentationsHandler in Semantico.Core/Handlers/Ai/ListDocumentations/ListDocumentationsHandler.cs

### UI for US1

- [ ] T041 [US1] Create GenerateDocumentation.razor page in Semantico.UI/Components/Pages/Ai/GenerateDocumentation.razor
- [ ] T042 [US1] Create ViewDocumentation.razor page in Semantico.UI/Components/Pages/Ai/ViewDocumentation.razor
- [ ] T043 [US1] Add AI Documentation link to DataSource detail page navigation

### Exclusion Configuration for US1

- [ ] T043a [US1] Create ExclusionConfiguration model with TablePatterns and ColumnPatterns in Semantico.Core/Models/Ai/ExclusionConfiguration.cs
- [ ] T043b [US1] Add ExclusionConfiguration property to DataSourceDocumentation entity
- [ ] T043c [US1] Create TableExclusionSelector.razor component with pattern matching UI in Semantico.UI/Components/Pages/Ai/TableExclusionSelector.razor
- [ ] T043d [US1] Integrate TableExclusionSelector into GenerateDocumentation.razor before generation starts
- [ ] T043e [US1] Update AiDocumentationService to respect exclusion patterns during schema analysis

### Tests for US1

- [ ] T044 [P] [US1] Create AiDocumentationServiceTests in Semantico.Tests/Ai/AiDocumentationServiceTests.cs
- [ ] T045 [P] [US1] Create GenerateDocumentationHandlerTests in Semantico.Tests/Ai/GenerateDocumentationHandlerTests.cs

**Checkpoint**: User Story 1 complete - can generate AI documentation for data sources

---

## Phase 4: User Story 1b - AI-Powered Field Quality Analysis (Priority: P1)

**Goal**: Run queries against data to validate field usage, detect patterns, identify unused columns and data type mismatches

**Independent Test**: Run field analysis on a table with known issues (empty columns, dates as strings), verify AI detects and reports issues with metrics

**Depends on**: US1

### Enums for US1b

- [ ] T046 [P] [US1b] Create FieldUsageStatus enum (Unused, PotentiallyUnused, Used) in Semantico.Core/Data/Enums/FieldUsageStatus.cs
- [ ] T047 [P] [US1b] Create DetectedDataPattern enum (Email, Phone, Date, Json, Url, Numeric, Uuid, Mixed) in Semantico.Core/Data/Enums/DetectedDataPattern.cs

### Entities for US1b

- [ ] T048 [US1b] Create FieldAnalysis entity in Semantico.Core/Data/Entities/FieldAnalysis.cs
- [ ] T049 [US1b] Add FieldAnalysis DbSet and indexes to SemanticoContext

### Models for US1b

- [ ] T050 [P] [US1b] Create FieldAnalysisRequest model in Semantico.Core/Models/Ai/FieldAnalysisRequest.cs
- [ ] T051 [P] [US1b] Create FieldAnalysisSummary model in Semantico.Core/Models/Ai/FieldAnalysisSummary.cs
- [ ] T052 [P] [US1b] Create SamplingConfig model with calculation logic in Semantico.Core/Models/Ai/SamplingConfig.cs

### Services for US1b

- [ ] T053 [US1b] Create IFieldAnalysisService interface in Semantico.Core/Services/Ai/IFieldAnalysisService.cs
- [ ] T054 [US1b] Implement FieldAnalysisService with pattern detection and sampling in Semantico.Core/Services/Ai/FieldAnalysisService.cs
- [ ] T055 [US1b] Register IFieldAnalysisService in ServiceConfiguration.cs

### Handlers for US1b

- [ ] T056 [US1b] Create RunFieldAnalysisCommand and RunFieldAnalysisHandler in Semantico.Core/Handlers/Ai/FieldAnalysis/RunFieldAnalysisHandler.cs
- [ ] T057 [US1b] Create ApproveFullScanCommand and ApproveFullScanHandler in Semantico.Core/Handlers/Ai/FieldAnalysis/ApproveFullScanHandler.cs
- [ ] T058 [US1b] Create GetFieldAnalysisQuery and GetFieldAnalysisHandler in Semantico.Core/Handlers/Ai/FieldAnalysis/GetFieldAnalysisHandler.cs

### UI for US1b

- [ ] T059 [US1b] Create FieldAnalysisResults.razor page in Semantico.UI/Components/Pages/Ai/FieldAnalysisResults.razor
- [ ] T060 [US1b] Create FieldAnalysisApproval.razor dialog for full scan approval in Semantico.UI/Components/Pages/Ai/FieldAnalysisApproval.razor
- [ ] T061 [US1b] Integrate field analysis trigger into GenerateDocumentation.razor

### Tests for US1b

- [ ] T062 [P] [US1b] Create FieldAnalysisServiceTests in Semantico.Tests/Ai/FieldAnalysis/FieldAnalysisServiceTests.cs
- [ ] T063 [P] [US1b] Create PatternDetectionTests in Semantico.Tests/Ai/FieldAnalysis/PatternDetectionTests.cs
- [ ] T064 [P] [US1b] Create SamplingConfigTests in Semantico.Tests/Ai/FieldAnalysis/SamplingConfigTests.cs

**Checkpoint**: User Story 1b complete - can analyze field quality and detect patterns

---

## Phase 5: User Story 3 - AI-Powered Smart Alerts (Priority: P1) 🎯 MVP

**Goal**: Allow users to describe alert conditions in natural language, AI generates SQL queries that integrate with existing subscription/notification system

**Independent Test**: Provide natural language alert description, verify AI generates appropriate SQL, confirm alert triggers work with existing notification channels

### Entities for US3

- [ ] T065 [P] [US3] Create AiAlertConfiguration entity in Semantico.Core/Data/Entities/AiAlertConfiguration.cs
- [ ] T066 [P] [US3] Create AiConversationHistory entity in Semantico.Core/Data/Entities/AiConversationHistory.cs
- [ ] T067 [US3] Add AiAlertConfiguration and AiConversationHistory DbSets and indexes to SemanticoContext

### Models for US3

- [ ] T068 [P] [US3] Create AlertGenerationRequest model in Semantico.Core/Models/Ai/AlertGenerationRequest.cs
- [ ] T069 [P] [US3] Create AlertRefinementRequest model in Semantico.Core/Models/Ai/AlertRefinementRequest.cs

### Services for US3

- [ ] T070 [US3] Create IAiAlertGenerationService interface in Semantico.Core/Services/Ai/IAiAlertGenerationService.cs
- [ ] T071 [US3] Implement AiAlertGenerationService with natural language to SQL in Semantico.Core/Services/Ai/AiAlertGenerationService.cs
- [ ] T072 [US3] Register IAiAlertGenerationService in ServiceConfiguration.cs

### Handlers for US3

- [ ] T073 [US3] Create GenerateAlertQueryCommand and GenerateAlertQueryHandler in Semantico.Core/Handlers/Ai/GenerateAlertQuery/GenerateAlertQueryHandler.cs
- [ ] T074 [US3] Create ActivateAiAlertCommand and ActivateAiAlertHandler in Semantico.Core/Handlers/Ai/ActivateAiAlert/ActivateAiAlertHandler.cs
- [ ] T075 [US3] Create PauseAiAlertCommand and ResumeAiAlertCommand handlers in Semantico.Core/Handlers/Ai/PauseResumeAiAlert/
- [ ] T076 [US3] Create GetAiAlertQuery and ListAiAlertsQuery handlers in Semantico.Core/Handlers/Ai/GetAiAlert/

### UI for US3

- [ ] T077 [US3] Create CreateAiAlert.razor page with natural language input in Semantico.UI/Components/Pages/Ai/CreateAiAlert.razor
- [ ] T078 [US3] Create AiAlertReview.razor page showing generated SQL and explanation in Semantico.UI/Components/Pages/Ai/AiAlertReview.razor
- [ ] T079 [US3] Create AiAlertList.razor page in Semantico.UI/Components/Pages/Ai/AiAlertList.razor
- [ ] T080 [US3] Add AI Alert creation link to main navigation

### Tests for US3

- [ ] T081 [P] [US3] Create AiAlertGenerationServiceTests in Semantico.Tests/Ai/AiAlertGenerationServiceTests.cs
- [ ] T082 [P] [US3] Create GenerateAlertQueryHandlerTests in Semantico.Tests/Ai/GenerateAlertQueryHandlerTests.cs

**Checkpoint**: User Story 3 complete - can create AI-powered alerts from natural language

---

## Phase 6: User Story 2 - Editable and Exportable Documentation (Priority: P2)

**Goal**: Edit AI-generated documentation inline and export to Markdown, HTML, PDF, JSON formats

**Independent Test**: Generate documentation, make edits, export to each format, verify formatting and user edit preservation

**Depends on**: US1

### Services for US2

- [ ] T083 [US2] Create IDocumentationExportService interface in Semantico.Core/Services/Ai/IDocumentationExportService.cs
- [ ] T084 [US2] Implement DocumentationExportService with Markdig and QuestPDF in Semantico.Core/Services/Ai/DocumentationExportService.cs
- [ ] T085 [US2] Register IDocumentationExportService in ServiceConfiguration.cs

### Handlers for US2

- [ ] T086 [US2] Create UpdateDocumentationCommand and UpdateDocumentationHandler in Semantico.Core/Handlers/Ai/UpdateDocumentation/UpdateDocumentationHandler.cs
- [ ] T087 [US2] Create ExportDocumentationQuery and ExportDocumentationHandler in Semantico.Core/Handlers/Ai/ExportDocumentation/ExportDocumentationHandler.cs
- [ ] T088 [US2] Create RegenerateDocumentationCommand for merge/new version options in Semantico.Core/Handlers/Ai/RegenerateDocumentation/RegenerateDocumentationHandler.cs

### UI for US2

- [ ] T089 [US2] Create EditDocumentation.razor page with inline editor in Semantico.UI/Components/Pages/Ai/EditDocumentation.razor
- [ ] T090 [US2] Create ExportDocumentation.razor dialog with format selection in Semantico.UI/Components/Pages/Ai/ExportDocumentation.razor
- [ ] T091 [US2] Add AI/user edit visual indicators to ViewDocumentation.razor
- [ ] T092 [US2] Create RegenerateDocumentation.razor dialog with merge options

### Tests for US2

- [ ] T093 [P] [US2] Create DocumentationExportServiceTests in Semantico.Tests/Ai/DocumentationExportServiceTests.cs
- [ ] T094 [P] [US2] Create UpdateDocumentationHandlerTests in Semantico.Tests/Ai/UpdateDocumentationHandlerTests.cs

**Checkpoint**: User Story 2 complete - can edit and export documentation in multiple formats

---

## Phase 7: User Story 2b - Interactive HTML Export with ERD Diagrams (Priority: P2)

**Goal**: Export documentation as interactive HTML with collapsible sections, TOC, and Mermaid ERD diagrams

**Independent Test**: Generate HTML export for data source with related tables, verify collapsible sections, TOC navigation, and Mermaid diagrams render correctly

**Depends on**: US2

### Enums for US2b

- [ ] T095 [P] [US2b] Create ExportFormat enum in Semantico.Core/Data/Enums/ExportFormat.cs
- [ ] T096 [P] [US2b] Create DiagramGroupingCriteria enum in Semantico.Core/Data/Enums/DiagramGroupingCriteria.cs

### Entities for US2b

- [ ] T097 [P] [US2b] Create DocumentationExport entity in Semantico.Core/Data/Entities/DocumentationExport.cs
- [ ] T098 [P] [US2b] Create DiagramGroup entity in Semantico.Core/Data/Entities/DiagramGroup.cs
- [ ] T099 [US2b] Add DocumentationExport and DiagramGroup DbSets and indexes to SemanticoContext

### Services for US2b

- [ ] T100 [US2b] Create IHtmlExportService interface in Semantico.Core/Services/Ai/IHtmlExportService.cs
- [ ] T101 [US2b] Implement HtmlExportService with collapsible sections and TOC in Semantico.Core/Services/Ai/HtmlExportService.cs
- [ ] T102 [US2b] Create IMermaidDiagramService interface in Semantico.Core/Services/Ai/IMermaidDiagramService.cs
- [ ] T103 [US2b] Implement MermaidDiagramService for ERD generation in Semantico.Core/Services/Ai/MermaidDiagramService.cs
- [ ] T104 [US2b] Create IDiagramGroupingService interface in Semantico.Core/Services/Ai/IDiagramGroupingService.cs
- [ ] T105 [US2b] Implement DiagramGroupingService with AI grouping logic in Semantico.Core/Services/Ai/DiagramGroupingService.cs
- [ ] T106 [US2b] Register HTML export and diagram services in ServiceConfiguration.cs

### Handlers for US2b

- [ ] T107 [US2b] Create ExportHtmlCommand and ExportHtmlHandler in Semantico.Core/Handlers/Ai/HtmlExport/ExportHtmlHandler.cs
- [ ] T108 [US2b] Create GenerateDiagramGroupsQuery and handler in Semantico.Core/Handlers/Ai/HtmlExport/GenerateDiagramGroupsHandler.cs
- [ ] T109 [US2b] Create SaveDiagramGroupsCommand and handler in Semantico.Core/Handlers/Ai/HtmlExport/SaveDiagramGroupsHandler.cs
- [ ] T110 [US2b] Create GetDiagramGroupsQuery and handler in Semantico.Core/Handlers/Ai/HtmlExport/GetDiagramGroupsHandler.cs

### UI for US2b

- [ ] T111 [US2b] Create ExportHtmlPreview.razor page in Semantico.UI/Components/Pages/Ai/ExportHtmlPreview.razor
- [ ] T112 [US2b] Create DiagramGroupEditor.razor for customizing groups in Semantico.UI/Components/Pages/Ai/DiagramGroupEditor.razor
- [ ] T113 [US2b] Create MermaidDiagramViewer.razor component in Semantico.UI/Components/Pages/Ai/MermaidDiagramViewer.razor
- [ ] T114 [US2b] Add HTML export option to ExportDocumentation.razor

### Tests for US2b

- [ ] T115 [P] [US2b] Create HtmlExportServiceTests in Semantico.Tests/Ai/HtmlExport/HtmlExportServiceTests.cs
- [ ] T116 [P] [US2b] Create MermaidDiagramServiceTests in Semantico.Tests/Ai/HtmlExport/MermaidDiagramServiceTests.cs
- [ ] T117 [P] [US2b] Create DiagramGroupingServiceTests in Semantico.Tests/Ai/HtmlExport/DiagramGroupingServiceTests.cs
- [ ] T118 [P] [US2b] Create CacheInvalidationTests in Semantico.Tests/Ai/HtmlExport/CacheInvalidationTests.cs

**Checkpoint**: User Story 2b complete - can export interactive HTML with ERD diagrams

---

## Phase 8: User Story 2c - Schema Change Detection (Priority: P2)

**Goal**: Detect schema changes, notify users, suggest renames, maintain change history

**Independent Test**: Modify data source schema, trigger schema check, verify changes detected with diff view, test rename suggestion

**Depends on**: US1

### Enums for US2c

- [ ] T119 [P] [US2c] Create SchemaChangeType enum in Semantico.Core/Data/Enums/SchemaChangeType.cs
- [ ] T120 [P] [US2c] Create SchemaObjectType enum in Semantico.Core/Data/Enums/SchemaObjectType.cs
- [ ] T121 [P] [US2c] Create RenameStatus enum in Semantico.Core/Data/Enums/RenameStatus.cs

### Entities for US2c

- [ ] T122 [P] [US2c] Create SchemaSnapshot entity in Semantico.Core/Data/Entities/SchemaSnapshot.cs
- [ ] T123 [P] [US2c] Create SchemaChange entity in Semantico.Core/Data/Entities/SchemaChange.cs
- [ ] T124 [US2c] Add SchemaSnapshot and SchemaChange DbSets and indexes to SemanticoContext

### Services for US2c

- [ ] T125 [US2c] Create ISchemaChangeDetectionService interface in Semantico.Core/Services/Ai/ISchemaChangeDetectionService.cs
- [ ] T126 [US2c] Implement SchemaChangeDetectionService in Semantico.Core/Services/Ai/SchemaChangeDetectionService.cs
- [ ] T127 [US2c] Create IRenameDetectionService interface in Semantico.Core/Services/Ai/IRenameDetectionService.cs
- [ ] T128 [US2c] Implement RenameDetectionService with AI rename suggestion in Semantico.Core/Services/Ai/RenameDetectionService.cs
- [ ] T129 [US2c] Register schema change services in ServiceConfiguration.cs

### Handlers for US2c

- [ ] T130 [US2c] Create CheckSchemaChangesQuery and handler in Semantico.Core/Handlers/Ai/SchemaChange/CheckSchemaChangesHandler.cs
- [ ] T131 [US2c] Create ConfirmRenameCommand and handler in Semantico.Core/Handlers/Ai/SchemaChange/ConfirmRenameHandler.cs
- [ ] T132 [US2c] Create GetSchemaChangeHistoryQuery and handler in Semantico.Core/Handlers/Ai/SchemaChange/GetSchemaChangeHistoryHandler.cs
- [ ] T133 [US2c] Create AcknowledgeSchemaChangesCommand and handler in Semantico.Core/Handlers/Ai/SchemaChange/AcknowledgeSchemaChangesHandler.cs

### UI for US2c

- [ ] T134 [US2c] Create SchemaChangeBanner.razor component in Semantico.UI/Components/Pages/Ai/SchemaChangeBanner.razor
- [ ] T135 [US2c] Create SchemaChangeDiff.razor page in Semantico.UI/Components/Pages/Ai/SchemaChangeDiff.razor
- [ ] T136 [US2c] Create SchemaChangeHistory.razor page in Semantico.UI/Components/Pages/Ai/SchemaChangeHistory.razor
- [ ] T137 [US2c] Create RenameConfirmationDialog.razor in Semantico.UI/Components/Pages/Ai/RenameConfirmationDialog.razor
- [ ] T138 [US2c] Integrate SchemaChangeBanner into ViewDocumentation.razor

### Tests for US2c

- [ ] T139 [P] [US2c] Create SchemaChangeDetectionServiceTests in Semantico.Tests/Ai/SchemaChange/SchemaChangeDetectionServiceTests.cs
- [ ] T140 [P] [US2c] Create RenameDetectionServiceTests in Semantico.Tests/Ai/SchemaChange/RenameDetectionServiceTests.cs
- [ ] T141 [P] [US2c] Create SchemaSnapshotTests in Semantico.Tests/Ai/SchemaChange/SchemaSnapshotTests.cs
- [ ] T142 [P] [US2c] Create SchemaDiffTests in Semantico.Tests/Ai/SchemaChange/SchemaDiffTests.cs

**Checkpoint**: User Story 2c complete - can detect and manage schema changes

---

## Phase 9: User Story 4 - AI Alert Query Refinement (Priority: P2)

**Goal**: Enable conversational refinement of AI-generated alert queries with feedback collection

**Independent Test**: Generate alert query, modify SQL, provide feedback, verify system stores feedback and incorporates it in future generations

**Depends on**: US3

### Handlers for US4

- [ ] T143 [US4] Create RefineAlertQueryCommand and RefineAlertQueryHandler in Semantico.Core/Handlers/Ai/RefineAlertQuery/RefineAlertQueryHandler.cs
- [ ] T144 [US4] Create SubmitQueryFeedbackCommand and handler in Semantico.Core/Handlers/Ai/SubmitQueryFeedback/SubmitQueryFeedbackHandler.cs
- [ ] T145 [US4] Create GetConversationHistoryQuery and handler in Semantico.Core/Handlers/Ai/GetConversationHistory/GetConversationHistoryHandler.cs

### Services for US4

- [ ] T146 [US4] Extend AiAlertGenerationService with refinement and feedback methods

### UI for US4

- [ ] T147 [US4] Create AiAlertConversation.razor chat interface in Semantico.UI/Components/Pages/Ai/AiAlertConversation.razor
- [ ] T148 [US4] Create QueryFeedbackForm.razor component in Semantico.UI/Components/Pages/Ai/QueryFeedbackForm.razor
- [ ] T149 [US4] Integrate conversation view into AiAlertReview.razor

### Tests for US4

- [ ] T150 [P] [US4] Create RefineAlertQueryHandlerTests in Semantico.Tests/Ai/RefineAlertQueryHandlerTests.cs
- [ ] T151 [P] [US4] Create QueryFeedbackTests in Semantico.Tests/Ai/QueryFeedbackTests.cs

**Checkpoint**: User Story 4 complete - can refine AI queries through conversation

---

## Phase 10: User Story 6 - Unsupervised AI Monitoring (Priority: P2)

**Goal**: AI autonomously monitors data sources for anomalies, creates draft alerts or sends notifications

**Independent Test**: Enable AI monitoring on data source with known anomalies, verify AI detects them and creates appropriate draft alerts/notifications

**Depends on**: US3

### Enums for US6

- [ ] T152 [P] [US6] Create MonitoringMode enum (TaskMode, NotificationMode) in Semantico.Core/Data/Enums/MonitoringMode.cs
- [ ] T153 [P] [US6] Create MonitoringScheduleFrequency enum in Semantico.Core/Data/Enums/MonitoringScheduleFrequency.cs
- [ ] T154 [P] [US6] Create VerbosityLevel enum in Semantico.Core/Data/Enums/VerbosityLevel.cs
- [ ] T155 [P] [US6] Create AnomalyType enum in Semantico.Core/Data/Enums/AnomalyType.cs
- [ ] T156 [P] [US6] Create InsightSeverity enum in Semantico.Core/Data/Enums/InsightSeverity.cs
- [ ] T157 [P] [US6] Create InsightStatus enum in Semantico.Core/Data/Enums/InsightStatus.cs
- [ ] T158 [P] [US6] Create BaselineType enum in Semantico.Core/Data/Enums/BaselineType.cs
- [ ] T159 [P] [US6] Create TrendDirection enum in Semantico.Core/Data/Enums/TrendDirection.cs

### Entities for US6

- [ ] T160 [P] [US6] Create AiMonitoringConfiguration entity in Semantico.Core/Data/Entities/AiMonitoringConfiguration.cs
- [ ] T161 [P] [US6] Create AiMonitoringBaseline entity in Semantico.Core/Data/Entities/AiMonitoringBaseline.cs
- [ ] T162 [P] [US6] Create AiInsight entity in Semantico.Core/Data/Entities/AiInsight.cs
- [ ] T163 [US6] Add IsAiGenerated and AiInsightId fields to AiAlertConfiguration entity
- [ ] T164 [US6] Add monitoring entities DbSets and indexes to SemanticoContext

### Models for US6

- [ ] T165 [P] [US6] Create MonitoringLimits model in Semantico.Core/Models/Ai/MonitoringLimits.cs
- [ ] T166 [P] [US6] Create MonitoringUsage model in Semantico.Core/Models/Ai/MonitoringUsage.cs
- [ ] T167 [P] [US6] Create InsightFilter model in Semantico.Core/Models/Ai/InsightFilter.cs
- [ ] T168 [P] [US6] Create AlertAdjustment model in Semantico.Core/Models/Ai/AlertAdjustment.cs

### Services for US6

- [ ] T169 [US6] Create IAiMonitoringService interface in Semantico.Core/Services/Ai/IAiMonitoringService.cs
- [ ] T170 [US6] Implement AiMonitoringService in Semantico.Core/Services/Ai/AiMonitoringService.cs
- [ ] T171 [US6] Create IBaselineLearningService interface in Semantico.Core/Services/Ai/IBaselineLearningService.cs
- [ ] T172 [US6] Implement BaselineLearningService in Semantico.Core/Services/Ai/BaselineLearningService.cs
- [ ] T173 [US6] Create IAnomalyDetectionService interface in Semantico.Core/Services/Ai/IAnomalyDetectionService.cs
- [ ] T174 [US6] Implement AnomalyDetectionService in Semantico.Core/Services/Ai/AnomalyDetectionService.cs
- [ ] T175 [US6] Register monitoring services in ServiceConfiguration.cs

### Handlers for US6

- [ ] T176 [US6] Create EnableAiMonitoringCommand and handler in Semantico.Core/Handlers/Ai/Monitoring/EnableAiMonitoringHandler.cs
- [ ] T177 [US6] Create DisableAiMonitoringCommand and handler in Semantico.Core/Handlers/Ai/Monitoring/DisableAiMonitoringHandler.cs
- [ ] T178 [US6] Create ListAiInsightsQuery and handler in Semantico.Core/Handlers/Ai/Monitoring/ListAiInsightsHandler.cs
- [ ] T179 [US6] Create GetAiInsightDetailQuery and handler in Semantico.Core/Handlers/Ai/Monitoring/GetAiInsightDetailHandler.cs
- [ ] T180 [US6] Create ReviewAiInsightCommand and handler in Semantico.Core/Handlers/Ai/Monitoring/ReviewAiInsightHandler.cs
- [ ] T181 [US6] Create ConvertInsightToAlertCommand and handler in Semantico.Core/Handlers/Ai/Monitoring/ConvertInsightToAlertHandler.cs
- [ ] T182 [US6] Create TriggerManualAnalysisCommand and handler in Semantico.Core/Handlers/Ai/Monitoring/TriggerManualAnalysisHandler.cs

### UI for US6

- [ ] T183 [US6] Create AiInsights.razor page in Semantico.UI/Components/Pages/Ai/AiInsights.razor
- [ ] T184 [US6] Create AiInsightDetail.razor page in Semantico.UI/Components/Pages/Ai/AiInsightDetail.razor
- [ ] T185 [US6] Create MonitoringBaselines.razor page in Semantico.UI/Components/Pages/Ai/MonitoringBaselines.razor
- [ ] T186 [US6] Add AI Insights link to main navigation

### Tests for US6

- [ ] T187 [P] [US6] Create AiMonitoringServiceTests in Semantico.Tests/Ai/Monitoring/AiMonitoringServiceTests.cs
- [ ] T188 [P] [US6] Create BaselineLearningServiceTests in Semantico.Tests/Ai/Monitoring/BaselineLearningServiceTests.cs
- [ ] T189 [P] [US6] Create AnomalyDetectionServiceTests in Semantico.Tests/Ai/Monitoring/AnomalyDetectionServiceTests.cs
- [ ] T190 [P] [US6] Create StatisticalAnomalyTests in Semantico.Tests/Ai/Monitoring/StatisticalAnomalyTests.cs
- [ ] T191 [P] [US6] Create TrendChangeDetectionTests in Semantico.Tests/Ai/Monitoring/TrendChangeDetectionTests.cs
- [ ] T192 [P] [US6] Create InsightConversionTests in Semantico.Tests/Ai/Monitoring/InsightConversionTests.cs

**Checkpoint**: User Story 6 complete - AI monitoring detects anomalies and creates insights

---

## Phase 11: User Story 6b - AI Monitoring Configuration (Priority: P2)

**Goal**: Configure monitoring limits, baseline settings, and cost controls

**Independent Test**: Configure limits, enable monitoring, verify limits are enforced and warnings sent at 80%

**Depends on**: US6

### Handlers for US6b

- [ ] T193 [US6b] Create GetMonitoringConfigurationQuery and handler in Semantico.Core/Handlers/Ai/Monitoring/GetMonitoringConfigurationHandler.cs
- [ ] T194 [US6b] Create UpdateMonitoringConfigurationCommand and handler in Semantico.Core/Handlers/Ai/Monitoring/UpdateMonitoringConfigurationHandler.cs
- [ ] T195 [US6b] Create GetMonitoringUsageQuery and handler in Semantico.Core/Handlers/Ai/Monitoring/GetMonitoringUsageHandler.cs

### Services for US6b

- [ ] T196 [US6b] Add limit enforcement logic to AiMonitoringService
- [ ] T197 [US6b] Create monitoring warning notification integration with existing notification channels

### UI for US6b

- [ ] T198 [US6b] Create MonitoringConfiguration.razor page in Semantico.UI/Components/Pages/Ai/MonitoringConfiguration.razor
- [ ] T199 [US6b] Create MonitoringUsageDashboard component for limit visualization
- [ ] T200 [US6b] Add monitoring toggle to DataSource detail page

### Tests for US6b

- [ ] T201 [P] [US6b] Create LimitEnforcementTests in Semantico.Tests/Ai/Monitoring/LimitEnforcementTests.cs
- [ ] T202 [P] [US6b] Create MonitoringConfigurationTests in Semantico.Tests/Ai/Monitoring/MonitoringConfigurationTests.cs

**Checkpoint**: User Story 6b complete - monitoring is fully configurable with cost controls

---

## Phase 12: User Story 2d - Prompt Template Versioning (Priority: P3)

**Goal**: Version documentation generation prompts with rollback capability

**Independent Test**: Edit prompt, create version, view history, rollback to previous version

**Depends on**: US1

### Entities for US2d

- [ ] T203 [US2d] Create PromptTemplateVersion entity in Semantico.Core/Data/Entities/PromptTemplateVersion.cs
- [ ] T204 [US2d] Add PromptTemplateVersion DbSet and indexes to SemanticoContext

### Handlers for US2d

- [ ] T205 [US2d] Create CreatePromptVersionCommand and handler in Semantico.Core/Handlers/Ai/PromptVersioning/CreatePromptVersionHandler.cs
- [ ] T206 [US2d] Create ListPromptVersionsQuery and handler in Semantico.Core/Handlers/Ai/PromptVersioning/ListPromptVersionsHandler.cs
- [ ] T207 [US2d] Create GetPromptVersionQuery and handler in Semantico.Core/Handlers/Ai/PromptVersioning/GetPromptVersionHandler.cs
- [ ] T208 [US2d] Create RestorePromptVersionCommand and handler in Semantico.Core/Handlers/Ai/PromptVersioning/RestorePromptVersionHandler.cs

### UI for US2d

- [ ] T209 [US2d] Create PromptVersionManager.razor page in Semantico.UI/Components/Pages/Admin/PromptVersionManager.razor
- [ ] T210 [US2d] Add prompt versioning link to Admin navigation

### Tests for US2d

- [ ] T211 [P] [US2d] Create CreatePromptVersionTests in Semantico.Tests/Ai/PromptVersioning/CreatePromptVersionTests.cs
- [ ] T212 [P] [US2d] Create ListPromptVersionsTests in Semantico.Tests/Ai/PromptVersioning/ListPromptVersionsTests.cs
- [ ] T213 [P] [US2d] Create RestorePromptVersionTests in Semantico.Tests/Ai/PromptVersioning/RestorePromptVersionTests.cs
- [ ] T214 [P] [US2d] Create VersionNumberSequenceTests in Semantico.Tests/Ai/PromptVersioning/VersionNumberSequenceTests.cs

**Checkpoint**: User Story 2d complete - prompts are versioned with rollback support

---

## Phase 13: User Story 5 - AI-Assisted Alert Template Library (Priority: P3)

**Goal**: Maintain library of AI-generated alert templates for common monitoring patterns

**Independent Test**: Save successful alert as template, browse library, apply template to new data source

**Depends on**: US3

### Entities for US5

- [ ] T215 [US5] Create AlertTemplate entity in Semantico.Core/Data/Entities/AlertTemplate.cs
- [ ] T216 [US5] Create AlertTemplateCategory entity in Semantico.Core/Data/Entities/AlertTemplateCategory.cs
- [ ] T217 [US5] Add AlertTemplate and AlertTemplateCategory DbSets and indexes to SemanticoContext

### Handlers for US5

- [ ] T218 [US5] Create SaveAlertAsTemplateCommand and handler in Semantico.Core/Handlers/Ai/AlertTemplates/SaveAlertAsTemplateHandler.cs
- [ ] T219 [US5] Create ListAlertTemplatesQuery and handler in Semantico.Core/Handlers/Ai/AlertTemplates/ListAlertTemplatesHandler.cs
- [ ] T220 [US5] Create ApplyAlertTemplateCommand and handler in Semantico.Core/Handlers/Ai/AlertTemplates/ApplyAlertTemplateHandler.cs
- [ ] T221 [US5] Create GetTemplateUsageStatsQuery and handler in Semantico.Core/Handlers/Ai/AlertTemplates/GetTemplateUsageStatsHandler.cs

### UI for US5

- [ ] T222 [US5] Create AlertTemplateLibrary.razor page in Semantico.UI/Components/Pages/Ai/AlertTemplateLibrary.razor
- [ ] T223 [US5] Create SaveAsTemplateDialog.razor in Semantico.UI/Components/Pages/Ai/SaveAsTemplateDialog.razor
- [ ] T224 [US5] Create ApplyTemplateDialog.razor in Semantico.UI/Components/Pages/Ai/ApplyTemplateDialog.razor
- [ ] T225 [US5] Add template library link to main navigation

### Tests for US5

- [ ] T226 [P] [US5] Create SaveAlertAsTemplateTests in Semantico.Tests/Ai/AlertTemplates/SaveAlertAsTemplateTests.cs
- [ ] T227 [P] [US5] Create ApplyAlertTemplateTests in Semantico.Tests/Ai/AlertTemplates/ApplyAlertTemplateTests.cs
- [ ] T228 [P] [US5] Create TemplateAdaptationTests in Semantico.Tests/Ai/AlertTemplates/TemplateAdaptationTests.cs

**Checkpoint**: User Story 5 complete - alert template library is available

---

## Phase 14: Admin & Cross-Cutting Concerns

**Purpose**: Admin configuration and system-wide features

### Admin Configuration

- [ ] T229 Create AiConfiguration.razor admin page in Semantico.UI/Components/Pages/Admin/AiConfiguration.razor
- [ ] T230 Create ConfigureAiProviderCommand and handler in Semantico.Core/Handlers/Ai/Admin/ConfigureAiProviderHandler.cs
- [ ] T231 Create GetAiUsageMetricsQuery and handler in Semantico.Core/Handlers/Ai/Admin/GetAiUsageMetricsHandler.cs
- [ ] T232 Add AI configuration link to Admin navigation

### Error Handling & Logging

- [ ] T233 Add structured logging for all AI operations:
  - [ ] T233a Create IAiAuditLogger interface in Semantico.Core/Services/Ai/IAiAuditLogger.cs
  - [ ] T233b Implement AiAuditLogger (logs: request ID, user ID, operation type, prompt hash, token counts, response time, success/failure) in Semantico.Core/Services/Ai/AiAuditLogger.cs
  - [ ] T233c Add AiAuditLogAttribute for handler decoration in Semantico.Core/Handlers/Ai/AiAuditLogAttribute.cs
  - [ ] T233d Register AiAuditLogger in ServiceConfiguration.cs
  - [ ] T233e Add audit log viewer to Admin section in Semantico.UI/Components/Pages/Admin/AiAuditLog.razor
- [ ] T234 Implement fallback behavior when AI service unavailable:
  - [ ] T234a Add circuit breaker pattern to LlmProviderFactory in Semantico.Core/Services/LlmProviders/LlmProviderFactory.cs
  - [ ] T234b Create AiServiceUnavailableException with user-friendly message in Semantico.Core/Exceptions/AiServiceUnavailableException.cs
  - [ ] T234c Add fallback UI state to GenerateDocumentation.razor showing "AI service temporarily unavailable" with retry button
  - [ ] T234d Add fallback UI state to CreateAiAlert.razor with manual SQL entry option when AI unavailable
  - [ ] T234e Implement health check endpoint for AI provider status in Semantico.Core/Services/LlmProviders/LlmHealthCheck.cs
- [ ] T235 Add retry logic with exponential backoff to LLM providers

### Performance Optimization

- [ ] T236 Implement schema filtering for large databases (>50 tables)
- [ ] T237 Enable prompt caching for Claude provider
- [ ] T238 Add response streaming support for long-running operations

---

## Phase 15: Database Migration

**Purpose**: Create EF Core migrations for all AI entities

**IMPORTANT**: Migrations will be created by user manually per project conventions

- [ ] T239 Document migration requirements for all new entities
- [ ] T240 Verify all entity configurations and indexes are properly defined
- [ ] T241 Test migration on PostgreSQL provider
- [ ] T242 Test migration on SQL Server provider

---

## Phase 16: Final Verification

**Purpose**: End-to-end testing and documentation

- [ ] T243 Run full build: `dotnet build --property WarningLevel=0`
- [ ] T244 Execute all AI tests: `dotnet test --filter "FullyQualifiedName~Semantico.Tests.Ai"`
- [ ] T245 Manual testing per quickstart.md scenarios
- [ ] T246 Update CLAUDE.md with AI Integration documentation
- [ ] T247 Update README.md with AI Integration features

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1 (Setup)**: No dependencies - can start immediately
- **Phase 2 (Foundational)**: Depends on Phase 1 - BLOCKS all user stories
- **Phase 3-13 (User Stories)**: All depend on Phase 2 completion
- **Phase 14 (Admin)**: Can run after Phase 2, enhances all user stories
- **Phase 15 (Migration)**: Depends on all entity tasks being complete
- **Phase 16 (Verification)**: Depends on all implementation being complete

### User Story Dependencies

```
Foundational (Phase 2)
    ├── US1 (Phase 3) - AI Documentation
    │   ├── US1b (Phase 4) - Field Analysis
    │   ├── US2 (Phase 6) - Edit/Export
    │   │   └── US2b (Phase 7) - HTML/ERD Export
    │   ├── US2c (Phase 8) - Schema Change Detection
    │   └── US2d (Phase 12) - Prompt Versioning
    │
    └── US3 (Phase 5) - AI Alerts
        ├── US4 (Phase 9) - Query Refinement
        ├── US5 (Phase 13) - Alert Templates
        └── US6 (Phase 10) - Unsupervised Monitoring
            └── US6b (Phase 11) - Monitoring Config
```

### Parallel Opportunities

**Within Phase 2 (Foundational)**:
- All enum tasks (T005-T012) can run in parallel
- LLM provider implementations (T015-T017) can run in parallel
- Core model tasks (T020-T021) can run in parallel

**After Phase 2 (User Stories)**:
- US1 and US3 can start in parallel (both depend only on Foundational)
- Within each user story, tasks marked [P] can run in parallel

---

## Summary

| Phase | User Story | Tasks | Priority |
|-------|------------|-------|----------|
| 1 | Setup | 4 | - |
| 2 | Foundational | 22 | - |
| 3 | US1 - AI Documentation | 24 | P1 |
| 4 | US1b - Field Analysis | 19 | P1 |
| 5 | US3 - AI Alerts | 18 | P1 |
| 6 | US2 - Edit/Export | 12 | P2 |
| 7 | US2b - HTML/ERD | 24 | P2 |
| 8 | US2c - Schema Change | 24 | P2 |
| 9 | US4 - Query Refinement | 9 | P2 |
| 10 | US6 - AI Monitoring | 41 | P2 |
| 11 | US6b - Monitoring Config | 10 | P2 |
| 12 | US2d - Prompt Versioning | 12 | P3 |
| 13 | US5 - Alert Templates | 14 | P3 |
| 14 | Admin | 17 | - |
| 15 | Migration | 4 | - |
| 16 | Verification | 5 | - |
| **Total** | | **259** | |

### MVP Scope (Recommended)

For initial delivery, implement only P1 stories:
- **Phase 1**: Setup (4 tasks)
- **Phase 2**: Foundational (22 tasks)
- **Phase 3**: US1 - AI Documentation (24 tasks)
- **Phase 5**: US3 - AI Alerts (18 tasks)

**MVP Total**: ~68 tasks

This delivers core AI documentation and natural language alerts without the complexity of monitoring, schema detection, or template libraries.
