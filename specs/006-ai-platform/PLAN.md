# 006 - Beacon AI Platform Upgrade

## Overview

Evolve Beacon from a database monitoring tool into a comprehensive **data intelligence platform** that:
1. Scans GitHub repos to understand code-to-data relationships
2. Builds a unified knowledge graph per project (schema + code + docs + quality)
3. Exposes everything via MCP server for AI tool integration
4. Supports SSO/API key auth for secure programmatic access

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                    Beacon.MCP (new project)              │
│  SSE Transport │ MCP Tools │ MCP Resources │ Session Mgmt   │
├─────────────────────────────────────────────────────────────┤
│                    Beacon.AI (extended)                   │
│  GitHub Scanner │ Knowledge Graph │ Semantic Search          │
│  Project Report │ Schema Change Detection │ dbt Integration  │
├─────────────────────────────────────────────────────────────┤
│                    Beacon.Core (extended)                 │
│  New Entities │ API Key Auth │ Query Guardrails │ Audit Log  │
├─────────────────────────────────────────────────────────────┤
│                    Beacon.UI (extended)                   │
│  Data Catalog │ Project Dashboard │ Schema Changes │ API Keys│
└─────────────────────────────────────────────────────────────┘
```

### New Project: Beacon.MCP
- Razor Class Library (like Beacon.UI) that mounts at `/beacon/mcp`
- SSE transport for MCP protocol
- References Beacon.Core and Beacon.AI

## Phase 1: Core Entities

### New Entities in Beacon.Core

**Project** - ties together data sources, repos, documentation
- Id, Name, Description, CreatedAt, UpdatedAt
- List<ProjectDataSource> (many-to-many with DataSource)
- List<GitHubRepository>
- List<ProjectReport>

**GitHubRepository** - GitHub repo connected to a project
- Id, ProjectId, RepositoryUrl, Branch, PersonalAccessToken (encrypted)
- LastScanAt, ScanStatus, ScanCronExpression
- List<CodeReference>

**CodeReference** - extracted code-to-data mapping
- Id, GitHubRepositoryId, FilePath, LineNumber, ReferenceType (enum)
- SchemaName, TableName, ColumnName (nullable)
- CodeSnippet, ClassName, MethodName
- ReferenceType: EntityModel, DapperQuery, RawSql, Migration, ApiEndpoint

**ApiKeyCredential** - API key for MCP/programmatic access
- Id, UserId, Name, KeyHash, KeyPrefix (first 8 chars for identification)
- Scopes (JSON), ExpiresAt, LastUsedAt, IsRevoked
- CreatedAt, RevokedAt

**SchemaSnapshot** - point-in-time schema capture for change detection
- Id, DataSourceId, CapturedAt
- SchemaJson (full schema as JSON)

**SchemaChange** - detected schema differences
- Id, DataSourceId, DetectedAt, ChangeType (enum)
- SchemaName, TableName, ColumnName
- OldValue, NewValue, Description
- ChangeType: TableAdded, TableDropped, ColumnAdded, ColumnDropped, ColumnTypeChanged, IndexChanged

**McpSession** - active MCP session tracking
- Id, ApiKeyId, UserId, StartedAt, LastActivityAt
- TablesExplored (JSON), QueriesExecuted, TokensUsed

**McpAuditLog** - audit trail for MCP operations
- Id, SessionId, UserId, Tool, Parameters (JSON)
- DataSourceId, ExecutionTimeMs, ResultRowCount
- Timestamp

**ProjectReport** - generated comprehensive project report
- Id, ProjectId, GeneratedAt, ReportFormat (HTML/PDF/MD)
- Content (stored as text or binary)
- ReportType: Full, SchemaOnly, QualityOnly, LineageOnly

## Phase 2: GitHub Repository Scanner

### Services in Beacon.AI

**IGitHubScannerService**
- `ScanRepositoryAsync(int repoId)` - full scan
- `ScanIncrementalAsync(int repoId)` - only changed files since last scan
- `GetScanProgressAsync(int repoId)` - progress tracking

**GitHubApiClient** - wraps GitHub REST API (or uses Octokit)
- List files in repo (tree API)
- Get file content
- Get commits since date

**CodeAnalyzer** - language-specific code parsing
- `ICodeAnalyzer` interface with `AnalyzeFileAsync(string path, string content)`
- `CSharpCodeAnalyzer` - finds EF entities, DbContext configs, Dapper queries, raw SQL
- Returns List<CodeReference>

**Pattern Detection (C# first, extensible)**:
- EF Core: `DbSet<T>`, `HasOne/HasMany`, `ToTable()`, `.Include()`, entity classes
- Dapper: `connection.Query()`, `connection.Execute()`, SQL string literals
- Raw SQL: `ExecuteSqlRaw`, `FromSqlRaw`, string containing SELECT/INSERT/UPDATE
- Migrations: `migrationBuilder.CreateTable`, `AddColumn`, etc.
- API endpoints: `[HttpGet]`, `[HttpPost]`, `MapGet`, `MapPost` with related service calls

## Phase 3: Knowledge Graph

**IKnowledgeGraphService**
- `BuildGraphAsync(int projectId)` - rebuild full graph
- `GetTableKnowledgeAsync(int dataSourceId, string schema, string table)` - rich table info
- `SearchAsync(string query)` - search across all knowledge
- `GetLineageAsync(string schema, string table)` - data lineage

The knowledge graph is not a separate database - it's a **query-time aggregation** from existing data:
- DatabaseMetadata (schema) → tables, columns, indexes
- CodeReference (from scanner) → which code touches each table
- DocumentationSection (AI docs) → human-readable descriptions
- DataQualityScore (contracts) → quality metrics
- QueryExecutionHistory → access patterns

Returns a unified `TableKnowledge` model with all dimensions.

## Phase 4: API Key Auth + SSO

### API Keys
- Generate/revoke in Admin UI
- Stored as SHA-256 hash (like password hashing)
- Scopes: `read`, `execute`, `admin` + per-data-source restrictions
- Transmitted as `Authorization: Bearer sk-sem_XXXXXXXX`

### SSO/OAuth2
- New `OAuthOptions` in BeaconConfiguration
- Support providers: Microsoft Entra ID, Google, Okta, Generic OIDC
- Callback endpoint at `/beacon/auth/callback`
- Maps external identity to BeaconUser

### API Key Middleware
- New `ApiKeyAuthMiddleware` validates Bearer tokens
- Checks against ApiKeyCredential table
- Sets HttpContext.User with claims from the API key's user

## Phase 5: Query Guardrails

**IQueryGuardrailService**
- `ValidateQueryAsync(string sql, GuardrailOptions options)` → GuardrailResult
- Checks: read-only (no INSERT/UPDATE/DELETE/DROP/ALTER/TRUNCATE)
- Row limit injection: wraps with `SELECT * FROM (...) LIMIT N`
- Cost estimation (EXPLAIN for PostgreSQL)
- PII column detection (heuristic: column names containing email, phone, ssn, password, etc.)
- PII masking in results (replace with `***`)

**Rate Limiting**
- In-memory sliding window per API key
- Configurable: requests per minute, queries per hour

## Phase 6: MCP Server

### New Project: Beacon.MCP

**Transport**: SSE over HTTP
- Endpoint: `/beacon/mcp/sse` (GET for SSE stream)
- Endpoint: `/beacon/mcp/message` (POST for client messages)
- Auth: Bearer token (API key or OAuth token)

**MCP Tools:**

| Tool | Description |
|---|---|
| `list_datasources` | List accessible data sources |
| `describe_table` | Rich table description (schema + docs + quality + lineage) |
| `search_schema` | Search tables/columns by name or description |
| `execute_query` | Execute read-only SQL with guardrails |
| `get_data_quality` | Data quality scores and contract status |
| `get_sample_data` | Sample rows with PII masking |
| `explain_relationship` | Table relationships (FK + code-level) |
| `get_query_templates` | Pre-built query templates |
| `get_anomalies` | Recent anomaly detections |
| `ask` | Natural language question → SQL → results |
| `get_project_summary` | Full project knowledge summary |
| `get_schema_changes` | Recent schema changes |

**MCP Resources:**
| URI | Description |
|---|---|
| `beacon://datasources` | List of data sources |
| `beacon://datasource/{id}/schema` | Full schema |
| `beacon://datasource/{id}/documentation` | AI-generated docs |
| `beacon://datasource/{id}/quality` | Quality report |
| `beacon://project/{id}/report` | Full project report |

## Phase 7: Semantic Search + Project Report

### Semantic Search
- Extends existing `AiAlertGenerationService` concept
- Uses knowledge graph context in LLM prompt (table descriptions, column docs, quality notes)
- Exposed as MCP `ask` tool and UI search bar

### Project Report Builder
- **IProjectReportService**
- Aggregates all knowledge into structured report
- Sections: Overview, Schema Catalog, Data Lineage, Quality Dashboard, Recommendations
- Export: HTML (Markdig), PDF (QuestPDF), Markdown
- Auto-refresh on cron schedule

## Phase 8: UI Pages

### Data Catalog (`/beacon/data-catalog`)
- Visual knowledge graph: tables as cards, relationships as lines
- Search/filter by schema, quality score, code references
- Click table → full knowledge detail panel

### Schema Changes (`/beacon/schema-changes`)
- Timeline of detected changes
- Before/after comparison
- Alert configuration for change types

### Multi-Project Dashboard (`/beacon/projects`)
- Project list with health scores
- Per-project: data sources, repos, doc coverage, quality scores
- Create/edit project dialog

### API Keys Management (`/beacon/settings/api-keys`)
- Generate new API keys (shown once)
- List active keys with last used, scopes
- Revoke keys

## Phase 9: Bonus Features

### Schema Change Detection
- `ISchemaChangeDetectionService`
- Takes snapshots on schedule, diffs against previous
- Generates SchemaChange records
- Sends alerts via existing notification infrastructure

### dbt Integration
- `IDbtIntegrationService`
- Parses `manifest.json`, `catalog.json` from dbt project
- Extracts model descriptions, tests, lineage
- Feeds into knowledge graph

### Slack/Teams Bot
- Extends existing webhook adapters
- Incoming webhook endpoint for questions
- Routes through semantic search
- Returns formatted results

## Phase 10: Integration

- Wire all services in DI
- Update SampleProject with MCP server setup
- Verify full solution builds
- Update CLAUDE.md with new capabilities
