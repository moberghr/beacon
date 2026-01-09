# Feature Specification: AI Integration

**Feature Branch**: `005-ai-integration`
**Created**: 2026-01-03
**Status**: Draft
**Input**: User description: "we want to add AI to our project. The idea is to utilize the power of LLM to do more than simple queries and data migrations. First we will use AI to enable user to analyze his data sources by querying schema and first 10 rows of each table to be able to create comprehensive documentation about the data source. Also the user should be able to edit and export it for external use. The other usecase will be allowing AI to use querying feature to track data and send alerts based on some non-trivial mechanism described by user"

## User Scenarios & Testing *(mandatory)*

### User Story 1 - AI-Generated Data Source Documentation (Priority: P1)

As a data analyst, I want AI to automatically analyze my data sources and generate comprehensive documentation so that I can understand the structure and content of my databases without manually exploring each table.

**Why this priority**: This is the foundational capability that enables users to leverage AI for understanding their data. Without automatic documentation generation, users cannot benefit from AI-powered insights.

**Independent Test**: Can be fully tested by connecting to a data source with multiple tables, triggering AI analysis, and verifying that documentation is generated with schema information and sample data for all tables. Delivers immediate value by providing automatic documentation.

**Acceptance Scenarios**:

1. **Given** a user has configured a data source, **When** they request AI documentation generation, **Then** the system analyzes all accessible tables and generates a comprehensive documentation report including table schemas, column types, relationships, and sample data
2. **Given** a data source with 10 tables, **When** AI analysis is initiated, **Then** the system queries the schema and first 10 rows of each table to generate contextual documentation
3. **Given** AI has analyzed a data source, **When** the documentation is displayed, **Then** it includes AI-generated descriptions of table purposes, column meanings, and potential data quality issues
4. **Given** complex relationships between tables, **When** AI generates documentation, **Then** it identifies and documents foreign key relationships and suggests potential join patterns

---

### User Story 1b - AI-Powered Field Quality Analysis (Priority: P1)

As a data analyst, I want AI to run actual queries against my data to validate field usage and detect data quality issues so that I can identify unused columns, data type mismatches, and potential schema improvements.

**Why this priority**: Documentation based only on schema and sample rows can miss critical insights. Running targeted queries reveals actual data quality issues like unused fields, wrong data types, and inconsistent patterns.

**Independent Test**: Can be tested by running field analysis on a table with known issues (empty columns, dates stored as strings), verifying AI detects and reports these issues with accurate metrics.

**Acceptance Scenarios**:

1. **Given** a data source with tables containing unused columns, **When** AI runs field analysis, **Then** it identifies columns with 0% non-null values as "unused" and columns with <1% non-null values as "potentially unused"
2. **Given** a table with >10,000 rows, **When** AI runs field analysis, **Then** it uses sampling (10% of rows, min 1,000, max 100,000) unless user approves full table scan
3. **Given** a large table requiring full scan, **When** AI requests full scan, **Then** user is prompted per-table with "Yes / No / Yes to all" options
4. **Given** a VARCHAR column containing date strings, **When** AI analyzes the data, **Then** it detects the pattern, suggests migration to DATE type, and estimates impact (e.g., "99.2% valid, 0.8% would need cleanup")
5. **Given** AI detects patterns (emails, phones, JSON, URLs, UUIDs, numbers-as-strings), **When** displaying results, **Then** it documents finding, suggests appropriate data type, and shows conversion feasibility
6. **Given** field analysis is complete, **When** viewing documentation, **Then** summary appears in DocumentationSection and detailed metrics are available in FieldAnalysis records

---

### User Story 2 - Editable and Exportable Documentation (Priority: P2)

As a database administrator, I want to edit AI-generated documentation and export it in multiple formats so that I can refine the AI's insights and share documentation with my team and stakeholders.

**Why this priority**: While AI-generated documentation is valuable, users need the ability to refine, correct, and customize it. Export capability makes the documentation useful beyond the Semantico platform.

**Independent Test**: Can be tested by generating documentation, making edits to various sections, and exporting to different formats (Markdown, HTML, PDF). Delivers value by allowing users to maintain and share customized documentation.

**Acceptance Scenarios**:

1. **Given** AI-generated documentation exists for a data source, **When** the user views the documentation, **Then** they can edit table descriptions, column descriptions, and AI-generated insights inline
2. **Given** edited documentation, **When** the user saves changes, **Then** the system preserves both AI-generated content and user edits, marking user-edited sections
3. **Given** complete documentation (AI + user edits), **When** the user exports, **Then** they can choose from multiple formats: Markdown, HTML, PDF, or JSON
4. **Given** exported documentation, **When** shared externally, **Then** it includes proper formatting, table of contents, and attribution to both AI generation and manual edits
5. **Given** a user re-runs AI analysis, **When** new documentation is generated, **Then** the system offers to merge with existing user edits or create a new version

---

### User Story 2b - Interactive HTML Export with ERD Diagrams (Priority: P2)

As a data analyst, I want to export documentation as interactive HTML with embedded ERD diagrams so that I can navigate complex schemas visually and share self-contained documentation files.

**Why this priority**: Static exports (PDF, Markdown) are useful but lack interactivity. Interactive HTML with diagrams enables better exploration of large schemas and provides visual context for relationships.

**Independent Test**: Can be tested by generating HTML export for a data source with multiple related tables, verifying collapsible sections work, TOC anchors navigate correctly, and Mermaid diagrams render with proper table relationships.

**Acceptance Scenarios**:

1. **Given** documentation exists for a data source, **When** user exports to HTML, **Then** the output includes collapsible sections for each table and a table of contents with anchor links
2. **Given** a data source with foreign key relationships, **When** HTML is exported, **Then** embedded Mermaid ERD diagrams show tables, columns, data types, and PK/FK indicators
3. **Given** a large schema with 50+ tables, **When** AI generates diagram groups, **Then** it suggests logical groupings based on FK relationships, naming conventions, and semantic analysis from documentation
4. **Given** AI-suggested diagram groups, **When** user reviews them, **Then** they can customize which tables appear in which diagram groups
5. **Given** documentation has not changed since last export, **When** user requests HTML export, **Then** the cached version is returned immediately
6. **Given** documentation has changed since last export, **When** user requests HTML export, **Then** a fresh HTML is generated and cached

---

### User Story 2c - Schema Change Detection (Priority: P2)

As a data analyst, I want to be notified when the underlying data source schema changes so that I can keep documentation accurate and up-to-date.

**Why this priority**: Documentation becomes stale when schemas change. Automatic detection prevents users from relying on outdated documentation and enables proactive maintenance.

**Independent Test**: Can be tested by modifying a data source schema (add/remove/rename table or column), triggering a schema check, and verifying changes are detected and displayed correctly with diff view.

**Acceptance Scenarios**:

1. **Given** documentation exists for a data source, **When** user views/edits/exports documentation, **Then** the system checks for schema changes and shows a banner if changes detected
2. **Given** schema changes are detected, **When** user views the notification banner, **Then** they see a "Review Changes" button to view diff and a "Regenerate" button to update documentation
3. **Given** a scheduled schema check runs, **When** changes are detected, **Then** the system records the changes and notifies documentation owners
4. **Given** a table or column was renamed, **When** AI analyzes the change, **Then** it suggests possible renames (e.g., "user_name → username") for user confirmation
5. **Given** user confirms a rename suggestion, **When** regeneration runs, **Then** documentation preserves relevant content under the new name
6. **Given** schema changes over time, **When** user views change history, **Then** they see a full audit trail with timestamps and diff views

---

### User Story 2d - Prompt Template Versioning (Priority: P3)

As an administrator, I want to version the documentation generation prompt so that I can track changes over time and rollback to previous versions if needed.

**Why this priority**: Prompt engineering is iterative. Having version control for prompts enables safe experimentation and easy recovery from poor prompt changes.

**Independent Test**: Can be tested by editing the documentation generation prompt, verifying a new version is created, viewing version history, and rolling back to a previous version.

**Acceptance Scenarios**:

1. **Given** a user edits the documentation generation prompt, **When** they save changes and click "Create Version", **Then** a new version is created with the current content
2. **Given** multiple prompt versions exist, **When** user views version history, **Then** they see a list of all versions with version number, created date, and created by
3. **Given** a user wants to rollback, **When** they select a previous version and click "Restore", **Then** a new version is created based on the old one (non-destructive)
4. **Given** prompt versioning is enabled, **When** system is configured, **Then** all versions are retained indefinitely (no automatic deletion)

---

### User Story 3 - AI-Powered Smart Alerts with Natural Language Configuration (Priority: P1)

As a business user, I want to describe complex alerting conditions in natural language so that AI can create sophisticated data monitoring queries without requiring SQL expertise.

**Why this priority**: This is the core innovation - allowing non-technical users to leverage Semantico's query and alerting infrastructure through AI-powered natural language interpretation. It dramatically expands the user base.

**Independent Test**: Can be tested by providing natural language alert descriptions (e.g., "Alert me when sales drop more than 20% compared to last week"), verifying AI generates appropriate SQL queries, and confirming alerts trigger correctly. Delivers immediate value by democratizing complex alerting.

**Acceptance Scenarios**:

1. **Given** a user describes an alert in natural language, **When** the AI processes the description, **Then** it generates an appropriate SQL query that implements the alert logic
2. **Given** a natural language description like "notify me when error rates exceed 5% of total requests in the last hour", **When** AI generates the query, **Then** it includes proper time windows, calculations, and threshold comparisons
3. **Given** an ambiguous or incomplete alert description, **When** AI processes it, **Then** the system asks clarifying questions before generating the query
4. **Given** AI-generated alert query, **When** the user reviews it, **Then** they can see both the natural language description and the generated SQL with explanations of the logic
5. **Given** an AI-generated alert is active, **When** conditions are met, **Then** notifications are sent through configured channels (Email, Teams, Slack, Jira) as per existing functionality

---

### User Story 4 - AI Alert Query Refinement (Priority: P2)

As a data analyst, I want to provide feedback on AI-generated alert queries so that the system learns from corrections and improves over time.

**Why this priority**: Enables continuous improvement of AI-generated queries and allows users to fine-tune complex alerting logic. This creates a feedback loop that improves the system.

**Independent Test**: Can be tested by generating an alert query, modifying the SQL, and providing feedback to the AI about the changes. Delivers value by enabling users to refine AI output and potentially improve future generations.

**Acceptance Scenarios**:

1. **Given** an AI-generated alert query, **When** the user modifies the SQL, **Then** they can provide structured feedback about why changes were needed
2. **Given** user feedback on a query, **When** saved, **Then** the system stores the original natural language, AI query, modified query, and reasoning for future reference
3. **Given** multiple feedback instances for similar alert patterns, **When** AI generates new queries, **Then** it incorporates learned patterns from past feedback
4. **Given** a complex alert description, **When** AI generates a query, **Then** it can reference similar past examples to improve accuracy

---

### User Story 5 - AI-Assisted Alert Template Library (Priority: P3)

As a system administrator, I want to maintain a library of AI-generated alert templates so that users can quickly set up common monitoring scenarios without describing them from scratch.

**Why this priority**: Provides efficiency for common use cases while still allowing custom AI-generated alerts for unique needs. This is an optimization rather than a core requirement.

**Independent Test**: Can be tested by creating alert templates from successful AI-generated queries, browsing the template library, and applying templates to new data sources. Delivers value by accelerating alert setup for common patterns.

**Acceptance Scenarios**:

1. **Given** a successful AI-generated alert, **When** the user saves it as a template, **Then** it becomes available in the template library with descriptive metadata
2. **Given** a template library, **When** a user browses templates, **Then** they see categories like "Data Quality", "Performance", "Business Metrics", "Anomaly Detection"
3. **Given** a selected template, **When** applied to a data source, **Then** AI adapts the template to the specific schema and column names
4. **Given** popular templates, **When** users search, **Then** the system shows usage statistics and ratings to help selection

---

### User Story 6 - Unsupervised AI Monitoring (Priority: P2)

As a data operations manager, I want AI to autonomously monitor my data sources for anomalies and create alerts without me having to define every condition, so that I can discover issues I didn't anticipate.

**Why this priority**: This extends AI capabilities beyond user-defined alerts to proactive discovery. It's transformative but builds on the foundation of supervised alerts (User Story 3).

**Independent Test**: Can be tested by enabling AI monitoring on a data source with known anomalies (injected test data), verifying AI detects them, creates appropriate draft alerts or sends notifications based on configuration.

**Acceptance Scenarios**:

1. **Given** a data source, **When** user enables AI monitoring via toggle, **Then** they can choose between "Task mode" (draft alerts for review) or "Notification mode" (direct alerts)
2. **Given** AI monitoring is enabled, **When** user configures the schedule, **Then** they set a base frequency (hourly/daily/weekly) and AI can increase frequency if data volatility warrants
3. **Given** AI monitoring is running, **When** AI detects a statistical anomaly (value outside 2-3 std deviations), **Then** it creates a draft alert or sends notification based on configured mode
4. **Given** AI monitoring is running, **When** AI detects trend changes, missing data patterns, volume anomalies, or threshold breaches, **Then** it creates appropriate findings
5. **Given** AI creates a draft alert, **When** user views the "AI Insights" page, **Then** they see all AI-discovered findings with configurable verbosity (minimal to full analysis)
6. **Given** an AI-created alert exists, **When** AI determines the threshold should be adjusted to reduce false positives, **Then** AI auto-adjusts the alert parameters
7. **Given** a user-created alert exists, **When** AI determines improvements are possible, **Then** AI only suggests changes (does not auto-modify)
8. **Given** AI monitoring is active, **When** usage reaches 80% of configured limits, **Then** system sends soft warning to admin
9. **Given** AI monitoring reaches 100% of limits (default: 100 queries/day, 100K tokens/day, $10/month), **Then** monitoring pauses until next period

---

### User Story 6b - AI Monitoring Configuration (Priority: P2)

As an administrator, I want to configure AI monitoring limits and baseline settings so that I can control costs and ensure AI learns from the right historical data.

**Why this priority**: Essential companion to User Story 6 - monitoring without controls could be expensive or produce poor results.

**Independent Test**: Can be tested by configuring limits, enabling monitoring, and verifying limits are enforced and baseline is established correctly.

**Acceptance Scenarios**:

1. **Given** enabling AI monitoring, **When** user configures limits, **Then** they can set: queries/day (default 100), tokens/day (default 100K), cost/month (default $10)
2. **Given** enabling AI monitoring, **When** user sets baseline period, **Then** AI analyzes that historical period to establish "normal" patterns
3. **Given** AI monitoring is running, **When** AI observes new data, **Then** it continuously refines its understanding of "normal" beyond initial baseline
4. **Given** AI monitoring configuration, **When** user sets verbosity level, **Then** AI findings include appropriate detail (minimal/standard/detailed/full)
5. **Given** limit soft warning (80%), **When** admin receives notification, **Then** they can increase limits or let monitoring pause at 100%

---

### Edge Cases

- What happens when a data source has hundreds of tables? Should AI analyze all of them or allow user selection?
- How does the system handle data sources where schema access is restricted but query execution is allowed?
- What happens when AI generates invalid SQL for an alert? How does the user recover?
- How does the system handle ambiguous natural language descriptions that could map to multiple query interpretations?
- What happens when underlying data source schema changes after documentation is generated?
- How are permissions and data access controls applied during AI analysis? (AI should only see data the user has access to)
- What happens when AI-generated queries have performance implications (e.g., full table scans on large tables)?
- How does the system handle time zone differences in alert descriptions like "when sales drop today compared to yesterday"?
- What happens when natural language uses domain-specific terminology that AI might misinterpret?
- How does field analysis handle tables with billions of rows where even sampling is expensive?
- What happens when field analysis detects sensitive data patterns (SSN, credit card numbers) - should it flag these for security review?
- How does the system handle columns with mixed data patterns (e.g., 50% emails, 50% phone numbers)?
- What happens when AI monitoring creates too many draft alerts (alert fatigue)? Should there be a cap?
- How does AI establish baseline when historical data is limited (new data source or new table)?
- What happens when data source is temporarily unavailable during scheduled AI monitoring?
- How does the system handle conflicting insights (AI detects anomaly, then data returns to normal before user reviews)?
- What happens when AI auto-adjusts an alert and user disagrees with the change? Can they revert?
- How does the system prioritize which anomalies to report when multiple are detected simultaneously?
- What happens when AI monitoring costs approach limits mid-analysis? Complete current run or stop immediately?
- How does HTML export handle schemas with 200+ tables? Should diagrams be paginated or limited?
- What happens when Mermaid rendering fails for complex diagrams (too many relationships)?
- How does AI handle orphan tables (no relationships) when suggesting diagram groups?
- What happens when user customizes diagram groups and schema changes (new tables added, tables removed)?
- How does schema change detection handle data sources that are temporarily unavailable?
- What happens when multiple schema changes occur between checks (cascade of changes)?
- How does AI distinguish between a rename and an unrelated add+delete (e.g., user_name removed, display_name added)?
- What happens when schema change history grows very large (years of changes)?
- How does the system handle schema changes during active documentation editing?
- What happens when the documentation generation prompt is empty or malformed?
- How does the system handle concurrent prompt version creation by multiple users?

## Requirements *(mandatory)*

### Functional Requirements

#### AI Documentation Generation

- **FR-001**: System MUST integrate an LLM service (e.g., OpenAI, Anthropic Claude, Azure OpenAI) to analyze data sources
- **FR-002**: System MUST query data source schema (tables, columns, data types, constraints, indexes) for AI analysis
- **FR-003**: System MUST query up to the first 10 rows of each table to provide context to the AI
- **FR-004**: System MUST handle data sources with sensitive data by allowing configuration of which tables/columns to exclude from AI analysis
- **FR-005**: System MUST generate documentation including: table purposes, column descriptions, data type explanations, relationship mappings, and potential data quality observations
- **FR-006**: System MUST support incremental documentation generation (analyze specific tables rather than entire data sources)
- **FR-007**: System MUST display AI confidence levels or uncertainty indicators in generated documentation

#### AI Field Quality Analysis

- **FR-007a**: System MUST analyze all columns in all tables for field usage and data quality metrics
- **FR-007b**: System MUST calculate null percentage for each column to identify unused (0% non-null) and potentially unused (<1% non-null) fields
- **FR-007c**: System MUST detect data patterns in text columns: email addresses, phone numbers, date/time strings, JSON, URLs, numeric values, and UUIDs
- **FR-007d**: System MUST use sampling for tables with >10,000 rows (10% of rows, minimum 1,000, maximum 100,000 rows)
- **FR-007e**: System MUST prompt user per-table when AI requests full table scan, with "Yes / No / Yes to all" options
- **FR-007f**: System MUST provide recommendations for detected patterns including suggested data type and migration impact estimate
- **FR-007g**: System MUST store field analysis results in both summary form (DocumentationSection) and detailed metrics (FieldAnalysis entity)
- **FR-007h**: System MUST track distinct value counts for each analyzed column
- **FR-007i**: System MUST estimate data type conversion feasibility with success percentage (e.g., "99.2% of values are valid dates")

#### Documentation Editing and Export

- **FR-008**: Users MUST be able to edit all AI-generated text content inline with a rich text editor
- **FR-009**: System MUST distinguish between AI-generated content and user-edited content with visual indicators
- **FR-010**: System MUST support versioning of documentation with ability to view history and revert changes
- **FR-011**: System MUST export documentation in Markdown format
- **FR-012**: System MUST export documentation in HTML format with embedded CSS styling
- **FR-013**: System MUST export documentation in PDF format with proper formatting and pagination
- **FR-014**: System MUST export documentation in JSON format for programmatic access
- **FR-015**: System MUST allow users to regenerate AI analysis while preserving or merging user edits

#### Interactive HTML Export with ERD Diagrams

- **FR-015a**: System MUST export interactive HTML with collapsible sections for each table/entity
- **FR-015b**: System MUST include table of contents with anchor links for navigation in HTML exports
- **FR-015c**: System MUST embed Mermaid ERD diagrams in HTML exports showing tables, columns, data types, and PK/FK indicators
- **FR-015d**: System MUST use AI to suggest logical diagram groupings based on: foreign key relationships, naming conventions (common prefixes), and semantic analysis from AI documentation
- **FR-015e**: System MUST allow users to customize AI-suggested diagram groups (add/remove tables from groups)
- **FR-015f**: System MUST cache generated HTML exports and regenerate only when documentation has changed
- **FR-015g**: System MUST store the latest HTML export with version tracking (DocumentationExport entity)
- **FR-015h**: System MUST store user-customized diagram groups (DiagramGroup entity) that persist across regenerations

#### Schema Change Detection

- **FR-015i**: System MUST check for schema changes before documentation operations (view, edit, export)
- **FR-015j**: System MUST run scheduled background schema checks with user-configurable interval (default: daily)
- **FR-015k**: System MUST detect table changes (added, removed, renamed)
- **FR-015l**: System MUST detect column changes (added, removed, renamed, data type changed)
- **FR-015m**: System MUST detect relationship changes (foreign keys added or removed)
- **FR-015n**: System MUST display notification banner when schema changes are detected with "Review Changes" and "Regenerate" buttons
- **FR-015o**: System MUST use AI to suggest possible renames when a table/column disappears and similar one appears
- **FR-015p**: System MUST allow users to confirm or reject AI-suggested renames
- **FR-015q**: System MUST store full schema change history with timestamps (audit trail)
- **FR-015r**: System MUST display schema changes in diff view format (before/after)
- **FR-015s**: System MUST store schema snapshots at documentation generation time for comparison

#### Prompt Template Versioning

- **FR-015t**: System MUST version the documentation generation system prompt only (not other AI prompts)
- **FR-015u**: System MUST create new prompt versions only when user explicitly clicks "Create Version" (manual versioning)
- **FR-015v**: System MUST store basic metadata per version: version number, created date, created by user
- **FR-015w**: System MUST provide a list view of all prompt versions showing metadata
- **FR-015x**: System MUST implement non-destructive rollback by creating a new version based on selected old version
- **FR-015y**: System MUST retain all prompt versions indefinitely (no automatic deletion)

#### AI-Powered Alert Generation

- **FR-016**: System MUST accept natural language descriptions of alert conditions from users
- **FR-017**: System MUST use LLM to interpret natural language and generate corresponding SQL queries
- **FR-018**: System MUST provide the LLM with data source schema context when generating alert queries
- **FR-019**: System MUST validate generated SQL queries for syntax correctness before allowing activation
- **FR-020**: System MUST display both natural language description and generated SQL to users for review
- **FR-021**: System MUST support iterative refinement where users can ask AI to modify generated queries through conversation
- **FR-022**: System MUST handle temporal expressions in natural language (e.g., "last hour", "compared to yesterday", "week over week")
- **FR-023**: System MUST handle comparative expressions (e.g., "more than", "exceeds", "drops below", "percentage change")
- **FR-024**: System MUST handle aggregation expressions (e.g., "average", "total", "count", "maximum")
- **FR-025**: System MUST ask clarifying questions when natural language description is ambiguous
- **FR-026**: AI-generated queries MUST integrate with existing Subscription system for recurring execution
- **FR-027**: AI-generated queries MUST support all existing notification channels (Email, Teams, Slack, Jira)

#### Unsupervised AI Monitoring

- **FR-027a**: System MUST allow users to enable/disable AI monitoring per data source via toggle
- **FR-027b**: System MUST support two monitoring modes: "Task mode" (creates draft alerts) and "Notification mode" (sends direct notifications)
- **FR-027c**: System MUST allow users to configure monitoring schedule with base frequency (hourly, daily, weekly)
- **FR-027d**: System MUST allow AI to increase monitoring frequency if data volatility warrants (adaptive scheduling)
- **FR-027e**: System MUST detect statistical anomalies (values outside 2-3 standard deviations from historical norm)
- **FR-027f**: System MUST detect trend changes (growth stops, reverses, or accelerates unexpectedly)
- **FR-027g**: System MUST detect missing data patterns (tables stopped receiving updates, gaps in time series)
- **FR-027h**: System MUST detect threshold breaches (AI learns "normal" range and alerts when exceeded)
- **FR-027i**: System MUST detect volume anomalies (sudden spikes or drops in row counts)
- **FR-027j**: System MUST detect correlation breaks (metrics that usually move together start diverging)
- **FR-027k**: System MUST create draft alerts (`AiAlertConfiguration` with `IsAiGenerated = true`) in Task mode
- **FR-027l**: System MUST provide dedicated "AI Insights" page showing all AI-discovered findings
- **FR-027m**: System MUST auto-adjust parameters on AI-created alerts to reduce false positives/negatives
- **FR-027n**: System MUST only suggest (not auto-apply) adjustments for user-created alerts
- **FR-027o**: System MUST support configurable verbosity levels for findings (minimal, standard, detailed, full analysis)

#### AI Monitoring Limits and Baseline

- **FR-027p**: System MUST enforce configurable limits: queries/day (default 100), tokens/day (default 100K), cost/month (default $10) per data source
- **FR-027q**: System MUST send soft warning notification when usage reaches 80% of any limit
- **FR-027r**: System MUST pause monitoring (hard stop) when usage reaches 100% of any limit until next period
- **FR-027s**: System MUST allow users to configure baseline period for establishing "normal" patterns
- **FR-027t**: System MUST continuously refine baseline understanding as AI observes new data
- **FR-027u**: System MUST track and display limit usage (queries used, tokens used, cost incurred) in monitoring configuration

#### AI Configuration and Management

- **FR-028**: System MUST allow administrators to configure LLM provider (OpenAI, Claude, Azure OpenAI, etc.)
- **FR-029**: System MUST allow administrators to configure API keys and endpoints securely
- **FR-030**: System MUST track AI usage (tokens, API calls) for cost monitoring
- **FR-031**: System MUST implement rate limiting for AI requests to prevent abuse
- **FR-032**: System MUST provide fallback behavior when AI service is unavailable
- **FR-033**: System MUST log all AI interactions for auditing and debugging
- **FR-034**: System MUST allow configuration of maximum tables/rows to analyze per data source to control costs

### Key Entities

- **DataSourceDocumentation**: Represents AI-generated and user-edited documentation for a data source, includes version history, generation timestamp, AI model used
- **DocumentationSection**: Individual sections of documentation (table descriptions, column descriptions, relationships, etc.) with indicators for AI-generated vs user-edited
- **DocumentationExport**: Cached HTML export with version tracking, stores generated HTML content and generation metadata
- **DiagramGroup**: User-customizable groupings of tables for ERD diagrams, with AI-suggested vs user-modified flag
- **SchemaSnapshot**: Stores complete schema state at documentation generation time for change comparison
- **SchemaChange**: Individual detected schema changes with timestamps, change type, before/after values, and rename suggestions
- **FieldAnalysis**: Detailed per-column analysis results including null percentage, distinct values, detected patterns, suggested data types, and migration impact estimates
- **AiAlertConfiguration**: Stores natural language description, generated SQL query, AI reasoning, and user feedback for AI-powered alerts (extended with `IsAiGenerated` flag for unsupervised monitoring)
- **AiConversationHistory**: Tracks back-and-forth conversation between user and AI during alert refinement, enables context-aware query generation
- **AiMonitoringConfiguration**: Per-data-source settings for unsupervised AI monitoring including mode (Task/Notification), schedule, limits, baseline period, and verbosity
- **AiMonitoringBaseline**: Stores learned "normal" patterns for a data source including statistical baselines, trends, and thresholds
- **AiInsight**: Individual AI-discovered findings with anomaly type, severity, context, suggested action, and link to draft alert if created
- **AiUsageMetrics**: Tracks AI API calls, token usage, costs, and performance metrics for monitoring and billing
- **AiPromptTemplate**: Configurable prompt templates for different AI tasks (documentation generation, query generation, etc.)
- **PromptTemplateVersion**: Version history for the documentation generation prompt, with basic metadata (version number, created date, created by)

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Users can generate comprehensive documentation for a data source with 20 tables in under 2 minutes (excluding AI API latency)
- **SC-002**: 80% of AI-generated SQL queries for common alert patterns (simple thresholds, percentage changes) execute without syntax errors
- **SC-003**: Users can successfully create a working alert from natural language description in under 5 minutes for simple cases
- **SC-004**: System handles AI service outages gracefully with informative error messages and does not disrupt non-AI functionality
- **SC-005**: 90% of generated documentation requires minimal editing (less than 20% of content modified by users)
- **SC-006**: Documentation export formats maintain readability and proper formatting across Markdown, HTML, and PDF outputs
- **SC-007**: AI-powered alerts reduce time to create complex monitoring queries by 50% compared to manual SQL writing
- **SC-008**: System tracks AI costs per user/organization to enable transparent billing if needed
- **SC-009**: Field analysis correctly identifies >95% of unused columns (0% non-null) in test data sources
- **SC-010**: Pattern detection (emails, dates, JSON in text columns) achieves >90% accuracy on test data
- **SC-011**: Migration impact estimates are within 5% of actual conversion success rates
- **SC-012**: Unsupervised AI monitoring detects >80% of injected anomalies in test data within configured schedule
- **SC-013**: AI-generated draft alerts from monitoring have <30% false positive rate after baseline learning period
- **SC-014**: Limit enforcement stops monitoring within 1 minute of reaching 100% threshold
- **SC-015**: Soft warning notifications are sent within 5 minutes of reaching 80% usage threshold
- **SC-016**: AI self-adjustment of alert parameters reduces false positive rate by >20% over 30-day period
- **SC-017**: Interactive HTML export generates in <5 seconds for documentation with <50 tables
- **SC-018**: Mermaid ERD diagrams correctly render all foreign key relationships with >95% accuracy
- **SC-019**: AI-suggested diagram groups require <20% manual adjustment by users for typical schemas
- **SC-020**: Cached HTML exports are returned in <500ms when documentation has not changed
- **SC-021**: Schema change detection completes in <5 seconds for data sources with <100 tables
- **SC-022**: AI-suggested renames correctly identify >80% of actual renames in test scenarios
- **SC-023**: Scheduled schema checks complete successfully >99% of the time without user intervention
- **SC-024**: Schema change history is queryable and displays correctly for >1 year of changes
- **SC-025**: Prompt version creation completes in <1 second
- **SC-026**: Prompt version list displays correctly with >100 versions
- **SC-027**: Rollback creates new version with identical content to source version
