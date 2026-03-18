# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands
- Build solution: `dotnet build --property WarningLevel=0`
- Run application: `dotnet run --project Semantico.SampleProject`
- Watch for changes: `dotnet watch run --project Semantico.SampleProject`
- Run tests: `dotnet test`

## Project Structure

| Project | Purpose |
|---------|---------|
| **Semantico.Core** | Domain entities, handlers (MediatR/CQRS), services, EF Core DbContext |
| **Semantico.Core.PostgreSql** | PostgreSQL migrations and context snapshot |
| **Semantico.Core.SqlServer** | SQL Server migrations and context snapshot |
| **Semantico.AI** | AI/LLM services: documentation generation, GitHub scanning, knowledge graph, semantic search, dbt integration, AI actors |
| **Semantico.MCP** | MCP (Model Context Protocol) server — SSE transport with 5 tools + 2 resources for AI client integration |
| **Semantico.Connector.Api** | REST API data source connector — OpenAPI spec import, HTTP query execution, JSON response tabularization |
| **Semantico.UI** | Blazor Server UI using MudBlazor components |
| **Semantico.Web** | ASP.NET Core host, middleware, routing |
| **Semantico.SampleProject** | Development host project with configuration |
| **Semantico.Tests** | Test project |

## Architecture Patterns

- **CQRS via MediatR**: All UI actions go through `IMediator.Send()` with query/command records and handlers in `Semantico.Core/Handlers/`
- **EF Core**: `SemanticoContext` in `Semantico.Core/Data/`. No need for `.Include()` when using `.Select(new ...)` projections
- **Blazor UI**: MudBlazor component library. Pages in `Semantico.UI/Components/Pages/`. Dialogs use `IDialogService`
- **DI Registration**: Each project has a `ServiceConfiguration.cs` with `AddSematico*()` extension methods

## Key Features

### AI Actors (Autonomous Monitoring Agents)
- AI-powered agents that autonomously monitor data sources and generate insights
- Plan-approve-reject workflow: LLM generates analysis plan → human reviews → approved plans create queries/subscriptions
- Think cycle execution for autonomous analysis
- Refinement via feedback, pause/resume, archive
- Handlers: `Semantico.Core/Handlers/Ai/AiActor/`, `Semantico.AI/Handlers/Ai/AiActor/`
- UI: `/ai-actors`, `/ai-actors/{ActorId:int}`, `/ai-actors/data-source/{DataSourceId:int}`

### Queries & Query Management
- CRUD for SQL queries with parameter binding
- Multi-step queries across data sources (results cached in in-memory SQLite, joined in final step)
- Hierarchical folder organization (nested folders with path tracking)
- Version control: snapshots, diff between versions, restore to previous version
- Lock/unlock queries to prevent edits
- Change history audit trail with source tracking (Manual, AiActor, etc.)
- Handlers: `Semantico.Core/Handlers/Queries/`, `Semantico.Core/Handlers/QueryVersions/`, `Semantico.Core/Handlers/QueryFolders/`
- UI: `/queries`, `/queries/{id:int}`, `/queries/add`

### Query Approvals
- Review and approval workflow for query changes
- Approve/reject with reason, pending approval counts
- Handlers: `Semantico.Core/Handlers/Approvals/`
- UI: `/approvals`

### Subscriptions
- Scheduled query execution via cron expressions
- Recipient routing for result delivery
- Handlers: `Semantico.Core/Handlers/Subscriptions/` (via services)
- UI: `/subscriptions`, `/subscriptions/{id:int}`, `/subscriptions/add/{QueryId:int}`

### Notifications
- Alert delivery via Teams, Slack, Email, Webhook
- Execution history and statistics
- UI: `/notifications`, `/notifications/{id:int}`

### Dashboards & Widgets
- Custom dashboards with configurable refresh intervals
- Widget types: KPI Card, Chart, Gauge, Table
- Role-based permissions: View, Edit, Admin
- Clone dashboards (creates private copy with all widgets)
- Share with specific users at different permission levels
- Handlers: `Semantico.Core/Handlers/Dashboards/`
- UI: `/dashboards`, `/dashboards/builder/{DashboardId:int}`, `/dashboards/view/{DashboardId:int}`

### Data Quality
- Data contracts with rules (freshness, volume, null rate, uniqueness, referential, range, pattern, custom SQL)
- Weighted scoring with severity multipliers
- Scheduled evaluations via cron expressions
- Alert recipients on failure threshold breach
- Handlers: `Semantico.Core/Handlers/DataQuality/`
- UI: `/data-quality`, `/data-quality/{Id:int}`

### Projects & Knowledge Graph
- Multi-source projects linking data sources and GitHub repositories
- Code-to-data lineage via `CodeReference` entities (EF Core, Dapper, raw SQL, migrations, API endpoints)
- GitHub repository scanning with `CSharpCodeAnalyzer`
- **Project Documentation** (AI-generated living knowledge base):
  - Multi-pass LLM generation: Project Overview, Business Domains, Data Model, Data Flows, Code Lineage, Data Quality, API Documentation, Glossary
  - Entities: `ProjectDocumentation`, `ProjectDocumentationSection` in `Semantico.Core/Data/Entities/Projects/`
  - Service: `ProjectDocumentationService` in `Semantico.AI/Services/Documentation/`
  - Used by MCP `get_documentation` tool as knowledge base (supports `project_id` parameter)
  - Exportable as Markdown or HTML
- Handlers: `Semantico.Core/Handlers/Projects/`, `Semantico.AI/Handlers/Projects/`
- UI: `/semantico/projects`, `/semantico/projects/{ProjectId:int}`

### Data Catalog
- Aggregated view of all tables across data sources
- Shows column counts, code references, quality scores, descriptions
- Handlers: `Semantico.Core/Handlers/DataCatalog/`
- UI: `/semantico/data-catalog`

### Data Sources
- Multi-connector support: PostgreSQL, SQL Server, MySQL, BigQuery, Snowflake, Databricks, Azure Synapse, CloudWatch, REST API
- Connection testing, metadata loading, ad-hoc query execution
- Query editor with syntax highlighting (SQL for databases, HTTP for APIs)
- All connectors implement `IDataSourceProvider` interface
- Registered via builder pattern: `.AddPostgreSqlConnector()`, `.AddApiConnector()`, etc.
- `ConnectorRegistry` tracks all available engine types and data source types
- UI: `/data-sources`, `/data-sources/{DataSourceId:int}`

### Data Migration
- Migration job creation and management
- Execution history tracking
- UI: `/data-migration`, `/data-migration/create`, `/data-migration/edit/{JobId:int}`, `/data-migration/history`

### Tasks
- Task management with create/resolve/reopen lifecycle
- Comments and execution history
- UI: `/tasks`, `/tasks/{id:int}`

### Recipients
- Manage alert recipients (users, email, Teams channels)
- Used by subscriptions and data quality alerts
- UI: `/recipients`

### MCP Server (Project-Centric)
- SSE endpoint at `/semantico/mcp/sse`, messages at `/semantico/mcp/message`
- **Project-centric architecture**: API keys bind to projects, sessions carry project context, tools auto-resolve data sources
- **5 tools**: `get_context` (project overview), `ask` (NL-to-SQL with auto data source routing + cross-source joins), `query` (direct SQL by data source name/ID), `get_documentation` (project/table docs), `search` (project-scoped catalog search)
- **4 resources per project**: documentation, schema, quality report, comprehensive report
- `ask` tool: two-phase LLM — Phase 1 routes to correct data source(s), Phase 2 generates SQL. Single-source projects skip routing. Cross-source queries use in-memory SQLite joins.
- API key and user authentication
- Query guardrails: read-only enforcement, PII detection/masking, row limits
- Audit logging for all tool calls with project context via `McpAuditService`
- Configurable settings: prompts, tool descriptions, row limits, PII settings
- Handlers: `Semantico.Core/Handlers/McpSettings/`
- UI: `/semantico/settings/mcp` (admin)

### API Key Management
- Scoped API keys (Read, Execute, Admin) with project restrictions (not data source)
- Key generation with SHA256 hashing, expiration, revocation
- Handlers: `Semantico.Core/Handlers/ApiKeys/`
- UI: `/semantico/settings/api-keys` (admin only)

### AI Services
- **LLM Providers**: OpenAI, Azure OpenAI, Claude (Anthropic), AWS Bedrock — swappable at runtime via `LlmProviderManager`
- **Alert Generation**: Natural language → SQL alert configuration with multi-turn refinement
- **Semantic Search**: Natural language to SQL translation with schema context injection
- **Knowledge Graph**: Aggregates schema + code references + documentation for context
- **dbt Integration**: Parse dbt manifest.json, import models and tests as code references
- Rate-limited concurrent LLM requests via `LlmRequestQueue`

## Navigation Routes (UI)

| Route | Page | Access |
|-------|------|--------|
| `/dashboard` | Home | All |
| `/control-tower` | Control Tower | All |
| `/data-sources` | Data Sources | All |
| `/data-sources/{DataSourceId:int}` | Query Editor (per data source) | All |
| `/queries` | Queries | All |
| `/queries/{id:int}` | Query Details | All |
| `/queries/add` | Add Query | All |
| `/subscriptions` | Subscriptions | All |
| `/subscriptions/{id:int}` | Subscription Details | All |
| `/subscriptions/add/{QueryId:int}` | Add Subscription | All |
| `/notifications` | Notifications | All |
| `/notifications/{id:int}` | Notification Details | All |
| `/ai-actors` | AI Actors | All |
| `/ai-actors/{ActorId:int}` | AI Actor Details | All |
| `/ai-actors/data-source/{DataSourceId:int}` | AI Actors (filtered by data source) | All |
| `/dashboards` | Dashboards | All |
| `/dashboards/builder/{DashboardId:int}` | Dashboard Builder | All |
| `/dashboards/view/{DashboardId:int}` | Dashboard Viewer | All |
| `/data-quality` | Data Quality (BETA) | All |
| `/data-quality/{Id:int}` | Data Contract Details | All |
| `/semantico/projects` | Projects | All |
| `/semantico/projects/{ProjectId:int}` | Project Details | All |
| `/semantico/data-catalog` | Data Catalog | All |
| `/data-migration` | Migration Jobs | All |
| `/data-migration/create` | Create Migration Job | All |
| `/data-migration/history` | Migration History | All |
| `/tasks` | Tasks | All |
| `/tasks/{id:int}` | Task Details | All |
| `/recipients` | Recipients | All |
| `/approvals` | Pending Approvals | All |
| `/query-execution-history/{id:int}` | Query Execution History | All |
| `/settings` | User Settings | All |
| `/admin/settings` | Admin Settings | Admin |
| `/semantico/settings/api-keys` | API Keys | Admin |
| `/semantico/settings/mcp` | MCP Settings | Admin |
| `/users` | User Management | Admin |
| `/about` | About | All |
| `/setup` | Initial Setup | All |
| `/login` | Login | Public |
| `/logout` | Logout | All |
