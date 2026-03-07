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
| **Semantico.AI** | AI/LLM services: documentation generation, GitHub scanning, knowledge graph, semantic search, schema change detection, dbt integration, project reports |
| **Semantico.MCP** | MCP (Model Context Protocol) server — SSE transport with 11 tools for AI client integration |
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
- Project reports (Full, SchemaOnly, QualityOnly, LineageOnly) in Markdown/HTML
- Handlers: `Semantico.Core/Handlers/Projects/`
- UI: `/semantico/projects`, `/semantico/projects/{ProjectId:int}`

### Schema Change Detection
- Point-in-time schema snapshots
- Detects: table added/dropped, column added/dropped/type changed
- Timeline view grouped by date
- Handlers: `Semantico.Core/Handlers/SchemaChanges/`
- UI: `/semantico/schema-changes`

### Data Catalog
- Aggregated view of all tables across data sources
- Shows column counts, code references, quality scores, descriptions
- Handlers: `Semantico.Core/Handlers/DataCatalog/`
- UI: `/semantico/data-catalog`

### MCP Server
- SSE endpoint at `/semantico/mcp/sse`, messages at `/semantico/mcp/message`
- 4 tools: list_datasources, query, get_documentation, ask
- API key and user authentication
- Query guardrails: read-only enforcement, PII detection/masking, row limits, rate limiting
- Audit logging for all tool calls

### API Key Management
- Scoped API keys (Read, Execute, Admin) with optional data source restrictions
- Key generation with SHA256 hashing, expiration, revocation
- Handlers: `Semantico.Core/Handlers/ApiKeys/`
- UI: `/semantico/settings/api-keys` (admin only)

### Semantic Search
- Natural language to SQL translation via LLM
- Schema context injection from knowledge graph
- Query validation and PII masking before execution

## Navigation Routes (UI)

| Route | Page | Access |
|-------|------|--------|
| `/dashboard` | Home | All |
| `/control-tower` | Control Tower | All |
| `/data-sources` | Data Sources | All |
| `/data-quality` | Data Quality (BETA) | All |
| `/semantico/projects` | Projects | All |
| `/semantico/data-catalog` | Data Catalog | All |
| `/semantico/schema-changes` | Schema Changes | All |
| `/admin/settings` | Admin Settings | Admin |
| `/semantico/settings/api-keys` | API Keys | Admin |
| `/users` | User Management | Admin |
