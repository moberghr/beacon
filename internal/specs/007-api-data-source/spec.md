# Spec: API Data Source Connector

**Date**: 2026-03-06 | **Status**: Draft

## Problem

Beacon currently supports databases and cloud monitoring services as data sources. Users who want to monitor, document, or query data exposed through REST APIs have no way to integrate those APIs into the same workflows (notifications, data quality, MCP, AI documentation) that work for databases.

## Goal

Add a new `DataSourceType.Api` that treats REST API endpoints as first-class data sources. Users provide an OpenAPI spec URL, the system imports endpoint metadata, and the same query/notification/MCP/documentation pipelines work on API data as they do on database data.

## Requirements

### Must Have (v1)

1. **OpenAPI spec import** (required for adding an API data source)
   - Parse OpenAPI 2.0 (Swagger) and 3.x specs via `Microsoft.OpenApi`
   - Import all endpoints with their paths, methods, parameters, and response schemas
   - Store endpoint metadata in the same pattern as database table/column metadata
   - Support spec URL (fetched at import time) and raw JSON/YAML paste

2. **Endpoint filtering**
   - Path prefix filter (e.g., `/api/v2/**` to include only v2 endpoints)
   - Users can exclude specific endpoints after import
   - Consistent with database schema/table filtering UX

3. **Static authentication**
   - API Key (header or query parameter)
   - Bearer token
   - Basic auth (username/password)
   - Auth credentials stored encrypted in `EncryptedConnectionData`

4. **Structured query execution**
   - Query definition is a JSON structure: method, path, parameters, result mapping
   - `resultMapping.arrayPath` (JSONPath) extracts the array from the response
   - `resultMapping.columns` (optional) maps nested JSON fields to flat columns
   - If `columns` is null, auto-detect from first object in array
   - Returns tabular data (`ProviderQueryResult`) compatible with existing pipelines

5. **IDataSourceProvider implementation**
   - `TestConnectionAsync`: GET to base URL or OpenAPI spec URL, verify reachable + auth works
   - `ExecuteQueryAsync`: call endpoint, apply result mapping, return tabular result
   - `GetMetadataAsync`: return imported endpoint catalog
   - `ValidateQueryAsync`: validate endpoint exists in spec, required params present
   - `GetQueryLanguageName`: returns `"HTTP"`

6. **Notification/alerting integration**
   - Subscriptions can use API data source queries on cron schedules
   - Same comparison logic (row count, value thresholds) works on tabularized API responses
   - No changes needed to notification infrastructure

7. **MCP exposure**
   - API endpoints appear in `list_datasources` alongside databases
   - `query` tool works with API query definitions
   - `get_documentation` returns AI-generated endpoint documentation

8. **Data Catalog integration**
   - API endpoints appear in the unified data catalog
   - Show endpoint path, method, description (from OpenAPI), parameter count
   - No column-level detail in catalog view (unlike database tables)

9. **AI documentation**
   - AI documentation service can generate descriptions for API endpoints
   - Uses OpenAPI descriptions, parameter schemas, and response schemas as context

### Won't Have (v1)

- OAuth2 flows / token refresh
- Auto-pagination (follow `next` links across pages)
- Query chaining across data source types (database result as API parameter)
- Response caching
- Rate limit management (user's responsibility)
- API endpoint discovery without OpenAPI spec
- GraphQL support
- Webhook/push-based data sources
- Data quality contracts on API endpoints (evaluate feasibility later)

## Data Model

### Connection config (stored encrypted in `EncryptedConnectionData`)

```json
{
  "baseUrl": "https://api.example.com",
  "openApiSpecUrl": "https://api.example.com/swagger/v1/swagger.json",
  "auth": {
    "type": "bearer",
    "token": "xxx"
  },
  "endpointFilter": {
    "includePathPatterns": ["/api/v2/**"],
    "excludePathPatterns": ["/api/v2/internal/**"]
  }
}
```

Auth variants:

```json
// API Key
{ "type": "apiKey", "name": "X-Api-Key", "value": "xxx", "location": "header" }

// Basic
{ "type": "basic", "username": "user", "password": "pass" }

// Bearer
{ "type": "bearer", "token": "xxx" }
```

### Query definition (stored as query text in QueryStep)

```json
{
  "method": "GET",
  "path": "/api/users",
  "parameters": {
    "query": { "status": "active", "limit": "100" },
    "header": {},
    "path": {}
  },
  "resultMapping": {
    "arrayPath": "$.data",
    "columns": null
  }
}
```

With explicit column mapping:

```json
{
  "resultMapping": {
    "arrayPath": "$.items",
    "columns": [
      { "name": "id", "path": "$.id", "type": "number" },
      { "name": "full_name", "path": "$.profile.name", "type": "string" },
      { "name": "email", "path": "$.contact.email", "type": "string" }
    ]
  }
}
```

### Metadata storage

API endpoint metadata maps to the existing `DatabaseMetadata` / `TableMetadata` pattern:

| Database concept | API mapping | Storage |
|---|---|---|
| Table | Endpoint (GET /api/users) | `TableMetadata` with `TableName = "GET /api/users"` |
| Schema | Path prefix grouping | `SchemaName = "/api/v2"` or tag from OpenAPI |
| Column | Response field | `ColumnMetadata` (for documentation/AI context only) |

This reuses existing metadata entities rather than creating parallel structures.

## Project Structure

```
src/Beacon.Connector.Api/
  ApiProvider.cs                    -- IDataSourceProvider implementation
  ApiMetadataExtractor.cs           -- IDatabaseMetadataExtractor equivalent
  Models/
    ApiConnectionConfig.cs          -- Deserialized connection config
    ApiAuthConfig.cs                -- Auth type + credentials
    ApiQueryDefinition.cs           -- Deserialized query definition
    ApiResultMapping.cs             -- JSONPath + column mappings
    ApiEndpointFilter.cs            -- Include/exclude path patterns
  Services/
    OpenApiImportService.cs         -- Parse OpenAPI spec into metadata
    JsonResponseTabularizer.cs      -- JSONPath extraction + flattening
    HttpClientFactory.cs            -- Create pre-configured HttpClient with auth
  ServiceCollectionExtensions.cs    -- DI registration + ConnectorRegistry
  Beacon.Connector.Api.csproj
```

## UI Changes

### Add Data Source dialog

When `DataSourceType.Api` is selected:
- **Base URL** text field
- **OpenAPI Spec URL** text field (required)
- **Auth type** dropdown (None, API Key, Bearer, Basic)
- **Auth fields** conditional on type
- **Import & Test** button: fetches spec, validates auth, shows endpoint count
- **Endpoint filter** (optional): include/exclude path patterns

### Query Editor

When data source is API type, replace SQL editor with:
- **Endpoint** dropdown (populated from imported metadata)
- **Parameters** section (auto-populated from OpenAPI spec with type hints)
- **Result Mapping** section:
  - `arrayPath` text field with JSONPath syntax
  - Toggle: "Auto-detect columns" vs "Manual column mapping"
  - Manual: table of column name / JSONPath / type
- **Preview** button: executes the call, shows tabularized result

### Data Catalog

- API endpoints listed alongside database tables
- Icon/badge distinguishing API endpoints from tables
- Columns: Endpoint (method + path), Data Source, Description, Parameters
- No column drill-down (unlike database tables)

## Dependencies

- `Microsoft.OpenApi` + `Microsoft.OpenApi.Readers` (OpenAPI parsing)
- `JsonPath.Net` or `Newtonsoft.Json` (JSONPath evaluation)
- `System.Net.Http` (HTTP calls, already available)

## Risks

1. **OpenAPI spec quality varies wildly** - some specs are incomplete, have wrong response schemas, or are out of date. The import should be best-effort and surface warnings, not fail hard.
2. **Response shape mismatch** - the actual API response may not match the OpenAPI spec. The tabularizer should handle this gracefully (nulls for missing fields, skip unexpected fields).
3. **Large responses** - APIs returning thousands of items without pagination could cause memory issues. Apply a configurable row limit (default 1000) at the tabularizer level.
4. **Timeout handling** - API calls can be slow. Need configurable timeout (default 30s) per data source.
