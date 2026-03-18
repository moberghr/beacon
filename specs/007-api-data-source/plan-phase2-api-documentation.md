# Implementation Plan: API-Aware Documentation & Knowledge Graph

**Date**: 2026-03-07 | **Depends on**: Phase 1-7 of API Data Source Connector (complete)

## Summary

Make the AI documentation generation, knowledge graph, and MCP documentation tools fully API-aware. Currently these systems use database-centric language ("tables", "columns", "PK", "FK"). For API data sources, they should use API-appropriate language ("endpoints", "response fields", "parameters", "HTTP method").

## Context: Key Files & Current Behavior

### Knowledge Graph Service
- **File**: `Semantico.AI/Services/Knowledge/KnowledgeGraphService.cs`
- `GetContextForLlmAsync()` (line ~319): Builds text context for LLM. Uses "Table:", "Columns:", "PK", "FK" labels. For API sources, should use "Endpoint:", "Response Fields:", etc.
- `GetDataSourceKnowledgeAsync()` (line ~122): Returns `DataSourceKnowledge` with `DatabaseEngine` and `TableCount`. For API sources, `DatabaseEngine` should be "REST API" and the count label should reflect endpoints.
- `GetTableKnowledgeAsync()` (line ~13): Returns `TableKnowledge` for a specific table/endpoint. Works via `DatabaseMetadata` entity which already stores API endpoints. FK/index lookups will return empty for API sources (fine). The `BusinessPurpose` maps to OpenAPI summary.

### Get Documentation MCP Tool
- **File**: `Semantico.MCP/Tools/GetDocumentationTool.cs`
- `GetTableDocumentationAsync()` (line ~69): Formats table knowledge as markdown. Column table header says "Column | Type | Nullable | PK | Description". For API endpoints, should say "Field | Type | Description" (no PK/FK/Nullable for API fields).
- `GetDataSourceDocumentationAsync()` (line ~168): Shows data source overview. Says "Tables: N". For API, should say "Endpoints: N".
- `ResolveDefaultSchemaAsync()` (line ~54): Returns "public" or "dbo" based on engine type. For API sources, should return "default" (the tag we use when no OpenAPI tag exists).
- Tool description says "table" — should mention endpoints too.

### AI Documentation Service
- **File**: `Semantico.AI/Services/Ai/AiDocumentationService.cs`
- `GenerateDocumentationAsync()` (line ~50): Calls `_metadataService.GetMetadataAsync()` which only works for database types (throws for non-database). Need to handle API sources.
- `BuildSchemaAnalysisPrompt()` (line ~289): Prompt says "Database Documentation Task", "Database Name", "Database Schema", "Table: schema.table". For API, needs "API Documentation Task", "API Name", "Endpoints", "Endpoint: GET /path".
- `AppendTableSchema()` (line ~314): Formats table columns with PK/FK groups. For API endpoints, should list parameters and response fields instead.

### Query Editor UI
- **File**: `Semantico.UI/Components/Pages/DataSources/QueryEditor.razor`
- Line ~101: "Generate Documentation" buttons gated on `DataSourceType.Database`. Should also show for `DataSourceType.Api`.
- Line ~131: Active agent runs gated on `DataSourceType.Database`. Should also show for `DataSourceType.Api`.
- Line ~154: AI Documentation History gated on `DataSourceType.Database`. Should also show for `DataSourceType.Api`.

### Database Metadata Service
- **File**: `Semantico.Core/Services/DatabaseMetadataService.cs`
- `GetMetadataAsync()` (line ~30+): Only works for database types (checks `DatabaseEngineType.HasValue`). For API sources, should load from stored `DatabaseMetadata` rows directly.

## Tasks

### Task A: Make DatabaseMetadataService work for API sources

**File**: `Semantico.Core/Services/DatabaseMetadataService.cs`

In `GetMetadataAsync()`, after the `DatabaseEngineType.HasValue` check, add a branch for API:

```
if (dataSource.DataSourceType == DataSourceType.Api)
{
    // Load metadata from DatabaseMetadata table (already stored during import)
    // Convert DatabaseMetadata entities to TableMetadataDto list
    // Return DatabaseMetadataSnapshot with tables
}
```

This requires:
1. Check `DataSourceType` before checking `DatabaseEngineType`
2. For API sources, query `DatabaseMetadata` + `ColumnMetadata` for this data source
3. Map to `TableMetadataDto` / `ColumnMetadataDto` records
4. Return as `DatabaseMetadataSnapshot` (use a placeholder `DatabaseEngineType` — or make the snapshot more generic)

**Key concern**: `DatabaseMetadataSnapshot` has a `DatabaseEngineType` field. For API sources, we can't provide one. Options:
- Make `DatabaseEngineType` nullable in the snapshot record
- OR: keep it non-nullable and use a dummy value — bad
- Best: make it nullable. The record is: `public record DatabaseMetadataSnapshot(int DataSourceId, DatabaseEngineType DatabaseEngineType, IReadOnlyList<TableMetadataDto> Tables, DateTime RefreshedAt)`

Change to: `public record DatabaseMetadataSnapshot(int DataSourceId, DatabaseEngineType? DatabaseEngineType, IReadOnlyList<TableMetadataDto> Tables, DateTime RefreshedAt)`

Check all usages of `DatabaseMetadataSnapshot.DatabaseEngineType` and handle null.

### Task B: Make KnowledgeGraphService.GetContextForLlmAsync API-aware

**File**: `Semantico.AI/Services/Knowledge/KnowledgeGraphService.cs`

In `GetContextForLlmAsync()`:
- At the start, look up the data source type
- If API type, use different labels throughout:
  - "# API Data Source: {name}" instead of "# Data Source: {name} ({engine})"
  - "## Endpoints:" instead of "## Tables:"
  - For each endpoint: "{tag}.{METHOD PATH}" instead of "{schema}.{table}"
  - Column info: just "field type" without PK/FK decorations
  - Skip the "NOT NULL" annotations for API response fields

Similarly in `GetDataSourceKnowledgeAsync()`:
- Set `DatabaseEngine` to "REST API" when data source type is Api
- The rest works since it queries `DatabaseMetadata` generically

### Task C: Make GetDocumentationTool API-aware

**File**: `Semantico.MCP/Tools/GetDocumentationTool.cs`

**C1. Update tool description and schema**:
- Change description to mention both tables and API endpoints
- Add note that `table_name` can be an endpoint like "GET /api/users"

**C2. Update `ResolveDefaultSchemaAsync()`**:
- Check data source type; if Api, return "default"

**C3. Update `GetTableDocumentationAsync()`**:
- Look up data source type
- If API, format the output differently:
  - Title: "# GET /api/users" instead of "# schema.table"
  - Instead of column table with PK/FK/Nullable: a simpler "Response Fields" table with just Field | Type | Description
  - Add "Parameters" section from column metadata that represents input params (we store these as ColumnMetadata too, but they have specific naming)
  - Skip FK relationships section (not applicable for API)
  - Skip index info

**C4. Update `GetDataSourceDocumentationAsync()`**:
- If API type, say "Endpoints: N" instead of "Tables: N"
- Say "Tags:" instead of "Schemas:"

### Task D: Make AiDocumentationService generate API documentation

**File**: `Semantico.AI/Services/Ai/AiDocumentationService.cs`

**D1. Update `GenerateDocumentationAsync()`**:
- Look up data source type before calling `_metadataService.GetMetadataAsync()`
- For API sources, call a new method `BuildApiAnalysisPrompt()` instead of `BuildSchemaAnalysisPrompt()`
- The metadata comes from the same `DatabaseMetadataSnapshot` (Task A makes this work)

**D2. Add `BuildApiAnalysisPrompt()` method**:
```
private string BuildApiAnalysisPrompt(string dataSourceName, List<TableMetadataDto> endpoints, GenerationOptions options)
{
    // "# API Documentation Task"
    // "## Context"
    // "- API Name: {name}"
    // "- Total Endpoints: {count}"
    // "- Objective: Generate comprehensive API documentation for developers"
    //
    // "## API Endpoints"
    // For each endpoint (stored as TableMetadataDto):
    //   "### Endpoint: `GET /api/users`"
    //   "**Description:** {TableDescription}" (from OpenAPI summary)
    //   "**Response Fields:**"
    //   For each column (stored as ColumnMetadataDto):
    //     "- **fieldName** (type) -- description"
}
```

**D3. Update `GetDocumentationSystemPrompt()`** or add an API variant:
- The system prompt should tell the LLM it's documenting a REST API, not a database
- Emphasize: endpoint purpose, request/response contracts, common usage patterns, error scenarios

### Task E: Enable documentation UI for API sources

**File**: `Semantico.UI/Components/Pages/DataSources/QueryEditor.razor`

Three conditions to update (all currently check `DataSourceType.Database`):
1. Line ~101: Generate Documentation buttons — change to `DataSourceType.Database or DataSourceType.Api`
2. Line ~131: Active agent runs — same change
3. Line ~154: AI Documentation History — same change

Also update the button labels conditionally:
- For API: "Generate API Documentation" instead of "Multi-Agent Workflow"

## Execution Order

```
Task A (DatabaseMetadataService) — unblocks D
    |
Task B (KnowledgeGraphService) — independent
Task C (GetDocumentationTool) — independent
    |
Task D (AiDocumentationService) — depends on A
Task E (UI) — independent
```

Tasks A, B, C, E can all be done in parallel. Task D depends on A.

## Files Changed

| File | Task | Change |
|------|------|--------|
| `Semantico.Core/Models/Metadata/DatabaseMetadataSnapshot.cs` | A | Make `DatabaseEngineType` nullable |
| `Semantico.Core/Services/DatabaseMetadataService.cs` | A | Handle API sources in `GetMetadataAsync()` |
| `Semantico.AI/Services/Knowledge/KnowledgeGraphService.cs` | B | API-aware labels in `GetContextForLlmAsync()` and `GetDataSourceKnowledgeAsync()` |
| `Semantico.MCP/Tools/GetDocumentationTool.cs` | C | API-aware formatting in all methods |
| `Semantico.AI/Services/Ai/AiDocumentationService.cs` | D | API branch in `GenerateDocumentationAsync()`, new `BuildApiAnalysisPrompt()` |
| `Semantico.UI/Components/Pages/DataSources/QueryEditor.razor` | E | Remove `DataSourceType.Database` gate for doc generation |

## Build verification
After all changes: `dotnet build --property WarningLevel=0`
