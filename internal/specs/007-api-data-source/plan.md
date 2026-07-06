# Implementation Plan: API Data Source Connector

**Branch**: `feature/api-data-source` | **Date**: 2026-03-06 | **Spec**: [spec.md](spec.md)

## Summary

Add `DataSourceType.Api` as a new connector that imports REST API endpoints from OpenAPI specs and integrates them into the existing query, notification, MCP, and data catalog pipelines. The "query" is a structured JSON request definition; responses are tabularized via JSONPath for compatibility with all existing workflows.

## Implementation Phases

### Phase 1: Core Enum + Models (no dependencies)

**Goal**: Define the data types and models the entire feature depends on.

#### Task 1.1: Add `DataSourceType.Api` enum value

**File**: `src/Beacon.Core/Data/Enums/DataSourceType.cs`
- Add `Api = 8` to the enum

#### Task 1.2: Create API connection config models

**File**: `src/Beacon.Connector.Api/Models/ApiConnectionConfig.cs`
```csharp
public class ApiConnectionConfig
{
    public required string BaseUrl { get; set; }
    public required string OpenApiSpecUrl { get; set; }
    public ApiAuthConfig? Auth { get; set; }
    public ApiEndpointFilter? EndpointFilter { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
}
```

**File**: `src/Beacon.Connector.Api/Models/ApiAuthConfig.cs`
```csharp
public class ApiAuthConfig
{
    public required string Type { get; set; } // "apiKey", "bearer", "basic", "none"
    // Bearer
    public string? Token { get; set; }
    // API Key
    public string? ApiKeyName { get; set; }
    public string? ApiKeyValue { get; set; }
    public string? ApiKeyLocation { get; set; } // "header" or "query"
    // Basic
    public string? Username { get; set; }
    public string? Password { get; set; }
}
```

**File**: `src/Beacon.Connector.Api/Models/ApiEndpointFilter.cs`
```csharp
public class ApiEndpointFilter
{
    public List<string> IncludePathPatterns { get; set; } = new();
    public List<string> ExcludePathPatterns { get; set; } = new();
}
```

#### Task 1.3: Create API query definition models

**File**: `src/Beacon.Connector.Api/Models/ApiQueryDefinition.cs`
```csharp
public class ApiQueryDefinition
{
    public required string Method { get; set; }
    public required string Path { get; set; }
    public ApiQueryParameters? Parameters { get; set; }
    public string? Body { get; set; }
    public required ApiResultMapping ResultMapping { get; set; }
}

public class ApiQueryParameters
{
    public Dictionary<string, string> Query { get; set; } = new();
    public Dictionary<string, string> Header { get; set; } = new();
    public Dictionary<string, string> Path { get; set; } = new();
}
```

**File**: `src/Beacon.Connector.Api/Models/ApiResultMapping.cs`
```csharp
public class ApiResultMapping
{
    public required string ArrayPath { get; set; } // JSONPath to the array, e.g. "$.data"
    public List<ApiColumnMapping>? Columns { get; set; } // null = auto-detect
}

public class ApiColumnMapping
{
    public required string Name { get; set; }
    public required string Path { get; set; } // JSONPath relative to each array element
    public string Type { get; set; } = "string";
}
```

#### Task 1.4: Add API endpoint metadata to `DataSourceMetadata`

**File**: `src/Beacon.Core/Models/Providers/DataSourceMetadata.cs`
- Add a new property for API endpoints:
```csharp
/// <summary>
/// For API types: discovered endpoints from OpenAPI spec
/// </summary>
public List<ApiEndpointMetadata>? Endpoints { get; set; }
```

- Add the metadata class in the same file (follows existing pattern with `LogFieldMetadata` and `MetricMetadata`):
```csharp
public class ApiEndpointMetadata
{
    public string Method { get; set; } = null!;
    public string Path { get; set; } = null!;
    public string? Summary { get; set; }
    public string? Description { get; set; }
    public string? Tag { get; set; } // OpenAPI tag, used as schema grouping
    public List<ApiParameterMetadata> Parameters { get; set; } = new();
    public List<ApiResponseFieldMetadata> ResponseFields { get; set; } = new();
}

public class ApiParameterMetadata
{
    public string Name { get; set; } = null!;
    public string In { get; set; } = null!; // "query", "path", "header"
    public string Type { get; set; } = "string";
    public bool Required { get; set; }
    public string? Description { get; set; }
}

public class ApiResponseFieldMetadata
{
    public string Name { get; set; } = null!;
    public string Type { get; set; } = "string";
    public string? Description { get; set; }
}
```

---

### Phase 2: Connector Project + Provider (depends on Phase 1)

**Goal**: Create `Beacon.Connector.Api` project with the `IDataSourceProvider` implementation.

#### Task 2.1: Create the project

**File**: `src/Beacon.Connector.Api/Beacon.Connector.Api.csproj`
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <Version>2.0.5.0</Version>
    <IsPackable>true</IsPackable>
    <Description>REST API connector for Beacon</Description>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Beacon.Core\Beacon.Core.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.OpenApi.Readers" Version="2.0.0-preview.2" />
    <PackageReference Include="JsonPath.Net" Version="1.1.6" />
  </ItemGroup>
</Project>
```

- Add project reference to `Beacon.sln`
- Add project reference from `Beacon.SampleProject` to `Beacon.Connector.Api`

#### Task 2.2: OpenAPI import service

**File**: `src/Beacon.Connector.Api/Services/OpenApiImportService.cs`

Responsibilities:
- Fetch OpenAPI spec from URL using `HttpClient`
- Parse with `Microsoft.OpenApi.Readers.OpenApiStreamReader`
- Extract all endpoints: iterate `openApiDocument.Paths`, for each path iterate operations
- For each operation, extract: method, path, parameters (from path + operation), response schema (from `200` response)
- Flatten response schema into `ApiResponseFieldMetadata` list (handle `$ref`, nested objects up to 2 levels)
- Apply `ApiEndpointFilter` include/exclude patterns using simple glob matching on path
- Return `List<ApiEndpointMetadata>`

Key method signature:
```csharp
public async Task<List<ApiEndpointMetadata>> ImportAsync(
    string specUrl,
    ApiEndpointFilter? filter,
    CancellationToken ct)
```

Also support parsing from raw string (for paste scenario):
```csharp
public List<ApiEndpointMetadata> ImportFromString(
    string specContent,
    ApiEndpointFilter? filter)
```

#### Task 2.3: JSON response tabularizer

**File**: `src/Beacon.Connector.Api/Services/JsonResponseTabularizer.cs`

Responsibilities:
- Takes raw JSON response string + `ApiResultMapping`
- Evaluates `arrayPath` JSONPath against the JSON to extract the array
- If `Columns` is null: auto-detect from first element's keys (flat properties only)
- If `Columns` is specified: for each element, evaluate each column's `Path` JSONPath
- Return `List<Dictionary<string, object?>>` — same format as `ProviderQueryResult.Rows`
- Apply row limit (configurable, default 1000)
- Handle gracefully: missing fields → null, non-array result → error, empty array → empty list

Key method signature:
```csharp
public List<Dictionary<string, object?>> Tabularize(
    string jsonResponse,
    ApiResultMapping mapping,
    int maxRows = 1000)
```

#### Task 2.4: HTTP client helper

**File**: `src/Beacon.Connector.Api/Services/ApiHttpClientFactory.cs`

Responsibilities:
- Create `HttpRequestMessage` from `ApiQueryDefinition` + `ApiConnectionConfig`
- Build full URL: `baseUrl + path` with path parameter substitution
- Add query parameters
- Add auth headers based on `ApiAuthConfig.Type`
- Add custom headers from query parameters
- Set request body if present
- Set timeout from config

Key method signature:
```csharp
public HttpRequestMessage CreateRequest(
    ApiConnectionConfig config,
    ApiQueryDefinition query)
```

#### Task 2.5: ApiProvider — `IDataSourceProvider` implementation

**File**: `src/Beacon.Connector.Api/ApiProvider.cs`

Follow the exact pattern from `CloudWatchProvider`:
- Constructor: inject `IEncryptionService`, `ILogger<ApiProvider>`, `IHttpClientFactory`, `OpenApiImportService`, `JsonResponseTabularizer`
- `SupportedType => DataSourceType.Api`
- `GetQueryLanguageName() => "HTTP"`

**`TestConnectionAsync`**:
- Parse `ApiConnectionConfig` from encrypted connection data
- Send GET to `baseUrl` (or OpenAPI spec URL) with auth headers
- Return success/failure with response status in `ConnectionInfo`

**`ExecuteQueryAsync(DataSource, string query, ...)`**:
- Deserialize `query` string as `ApiQueryDefinition` (same pattern as CloudWatch deserializing config)
- Parse `ApiConnectionConfig` from encrypted connection data
- Build `HttpRequestMessage` via `ApiHttpClientFactory`
- Send request via `IHttpClientFactory.CreateClient()`
- Read response body as string
- Pass to `JsonResponseTabularizer.Tabularize()`
- Return `ProviderQueryResult` with rows, timing metadata

**`GetMetadataAsync`**:
- Parse config, call `OpenApiImportService.ImportAsync()`
- Return `DataSourceMetadata` with `Type = DataSourceType.Api` and `Endpoints` populated

**`ValidateQueryAsync`**:
- Deserialize query JSON
- Validate: method is valid HTTP method, path is not empty, `resultMapping.arrayPath` is not empty
- Optionally: check if the endpoint exists in the imported spec (if metadata is available)

#### Task 2.6: ServiceCollectionExtensions + ConnectorRegistry

**File**: `src/Beacon.Connector.Api/ServiceCollectionExtensions.cs`

Follow exact pattern from CloudWatch:
```csharp
public static class ServiceCollectionExtensions
{
    public static BeaconBuilder AddApiConnector(this BeaconBuilder builder)
    {
        ConnectorRegistry.RegisterDataSourceType(DataSourceType.Api, "REST API");
        builder.Services.AddTransient<IDataSourceProvider, ApiProvider>();
        builder.Services.AddTransient<OpenApiImportService>();
        builder.Services.AddTransient<JsonResponseTabularizer>();
        return builder;
    }
}
```

#### Task 2.7: Register in SampleProject

**File**: `src/Beacon.SampleProject/Program.cs`
- Add `using Beacon.Connector.Api;`
- Add `.AddApiConnector()` in the connector chain (after `.AddBigQueryConnector()`)

---

### Phase 3: Metadata Storage (depends on Phase 2)

**Goal**: Store imported API endpoint metadata in `DatabaseMetadata` table so it's visible to data catalog, MCP, and AI documentation.

#### Task 3.1: Store API endpoints as DatabaseMetadata rows

**File**: `src/Beacon.Core/Services/DatabaseMetadataService.cs`

Modify `RefreshMetadataAsync` (or add new method `RefreshApiMetadataAsync`):
- When `DataSourceType == Api`, use the `IDataSourceProvider.GetMetadataAsync()` to get endpoints
- For each `ApiEndpointMetadata`, create/update a `DatabaseMetadata` row:
  - `SchemaName` = OpenAPI tag (or path prefix grouping like "/api/v2") — first tag, or "default" if no tags
  - `TableName` = `"{Method} {Path}"` (e.g., `"GET /api/users"`)
  - `TableDescription` = OpenAPI summary/description
- For each `ApiResponseFieldMetadata`, create `ColumnMetadata`:
  - `ColumnName` = field name
  - `DataType` = field type
  - `IsNullable` = true (API fields are always nullable)
  - Other DB-specific fields (`IsPrimaryKey`, `IsForeignKey`, etc.) = false/null
- Delete old metadata rows for this data source before inserting new ones (same as database refresh)

**Decision**: Rather than modifying the existing `RefreshMetadataAsync` which is tightly coupled to `IDatabaseMetadataExtractor`, add a new method and call it from `DataSourceService` after creation when type is Api.

**File**: `src/Beacon.Core/Services/DataSourceService.cs`
- In `CreateDataSource`: after saving, if `DataSourceType == Api`, trigger metadata import
- This requires injecting the provider factory and calling `GetMetadataAsync`, then storing results

#### Task 3.2: Update DataSourceService to handle API metadata import on creation

**File**: `src/Beacon.Core/Services/DataSourceService.cs`

After `await context.SaveChangesAsync()` in `CreateDataSource`:
```csharp
// For API data sources, import endpoint metadata from OpenAPI spec
if (dataSourceData.DataSourceType == DataSourceType.Api)
{
    try
    {
        var provider = providerFactory.GetProvider(DataSourceType.Api);
        var metadata = await provider.GetMetadataAsync(dataSource, cancellationToken);
        if (metadata.Endpoints?.Count > 0)
        {
            await StoreApiEndpointsAsMetadata(context, dataSource.Id, metadata.Endpoints, cancellationToken);
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to import API metadata for data source {Id}. Can be retried via metadata refresh.", dataSource.Id);
    }
}
```

Add private method `StoreApiEndpointsAsMetadata` that creates `DatabaseMetadata` + `ColumnMetadata` rows.

---

### Phase 4: UI — Add Data Source Dialog (depends on Phase 1)

**Goal**: Add API-specific form fields to the Add Data Source dialog.

#### Task 4.1: Add API fields to AddDataSourceDialog

**File**: `src/Beacon.UI/Components/Pages/DataSources/AddDataSourceDialog.razor`

Add a new `else if (_selectedDataSourceType == DataSourceType.Api)` block after the BigQuery section:

Form fields:
- **Base URL** (`_apiBaseUrl`, required) — `MudTextField`
- **OpenAPI Spec URL** (`_apiSpecUrl`, required) — `MudTextField` with helper text "e.g., https://api.example.com/swagger/v1/swagger.json"
- **Auth Type** (`_apiAuthType`) — `MudSelect` with options: None, API Key, Bearer Token, Basic Auth
- Conditional auth fields based on `_apiAuthType`:
  - API Key: `_apiKeyName`, `_apiKeyValue`, `_apiKeyLocation` (header/query)
  - Bearer: `_apiToken` (password field)
  - Basic: `_apiUsername`, `_apiPassword` (password field)
- **Endpoint Filter** (optional, in MudExpansionPanel like database metadata options):
  - Include path patterns (comma-separated)
  - Exclude path patterns (comma-separated)

Add backing fields in `@code` block.

Update `CanTestConnection()`:
```csharp
DataSourceType.Api => !string.IsNullOrWhiteSpace(_apiBaseUrl) && !string.IsNullOrWhiteSpace(_apiSpecUrl),
```

Update `BuildDataSourceData()`:
```csharp
case DataSourceType.Api:
    data.ConnectionString = JsonSerializer.Serialize(new
    {
        BaseUrl = _apiBaseUrl,
        OpenApiSpecUrl = _apiSpecUrl,
        Auth = _apiAuthType == "none" ? null : new { ... },
        EndpointFilter = new { ... },
        TimeoutSeconds = 30
    });
    break;
```

---

### Phase 5: UI — API Query Editor (depends on Phase 3)

**Goal**: Add a form-based query editor for API data sources on the QueryEditor page.

#### Task 5.1: Create ApiQueryEditor component

**File**: `src/Beacon.UI/Components/Pages/DataSources/ApiQueryEditor.razor`

New Blazor component (follows the pattern of `CloudWatchQueryEditor`):

**Parameters**:
- `int DataSourceId`
- `Func<int, string, Task> ExecuteAdHocQuery` callback

**UI Layout**:
- Left sidebar: **Endpoint Explorer** — tree view of endpoints grouped by tag/path prefix (like `DatabaseExplorer` for SQL). Click an endpoint to populate the form.
- Right panel: **Request Builder**
  - Endpoint display: method badge + path (read-only, set from explorer selection)
  - **Parameters section**: auto-generated from OpenAPI parameter metadata
    - For each parameter: label, type hint, required indicator, text input
    - Grouped by location (path params, query params, header params)
  - **Result Mapping section**:
    - `ArrayPath` text field (default: `$` or `$.data` if response has a data wrapper)
    - Toggle: "Auto-detect columns" (default) vs "Manual mapping"
    - Manual mode: editable table of column name / JSONPath / type
  - **Preview button**: builds the `ApiQueryDefinition` JSON, calls `ExecuteAdHocQuery`
  - **Raw JSON toggle**: shows the serialized `ApiQueryDefinition` for power users

**State**: loads endpoint metadata from `DatabaseMetadata` for this data source on init.

#### Task 5.2: Integrate ApiQueryEditor into QueryEditor page

**File**: `src/Beacon.UI/Components/Pages/DataSources/QueryEditor.razor`

Add condition (line ~213, where CloudWatch check is):
```razor
@if (_dataSourceInfo?.DataSourceType == DataSourceType.Api)
{
    <ApiQueryEditor DataSourceId="@DataSourceId"
                    ExecuteAdHocQuery="@ExecuteAdHocQueryWrapper" />
}
else if (_dataSourceInfo?.DataSourceType == DataSourceType.CloudWatch)
```

Also update the Overview card (line ~46) to handle API type:
- Show "REST API" as type
- Show endpoint count instead of table count
- Show "Refresh Endpoints" button (re-imports OpenAPI spec)

---

### Phase 6: Data Catalog Integration (depends on Phase 3)

**Goal**: Show API endpoints in the unified data catalog.

#### Task 6.1: Update GetDataCatalogHandler

**File**: `src/Beacon.Core/Handlers/DataCatalog/GetDataCatalogHandler.cs`

Current query filters `where ds.DataSourceType == DataSourceType.Database`. Change to:
```csharp
where (ds.DataSourceType == DataSourceType.Database || ds.DataSourceType == DataSourceType.Api)
    && dm.ArchivedTime == null && ds.ArchivedTime == null
```

Update the `DataCatalogEntry` record to include data source type:
```csharp
public record DataCatalogEntry(
    string DataSourceName,
    string SchemaName,
    string TableName,
    string? Description,
    int ColumnCount,
    double? QualityScore,
    int CodeReferenceCount,
    string DataSourceType); // "Database" or "Api"
```

#### Task 6.2: Update Data Catalog UI

**File**: `src/Beacon.UI/Components/Pages/DataCatalog/DataCatalog.razor`

- Add a type icon/badge to each card: database icon for Database, API icon (e.g., `Icons.Material.Filled.Api`) for API
- For API entries, show parameter count instead of column count (or hide column count if 0)
- Add "Type" filter dropdown alongside existing filters
- Update page subtitle: "Browse all tables and API endpoints across your connected data sources"

---

### Phase 7: MCP Integration (depends on Phase 3)

**Goal**: MCP tools work with API data sources.

#### Task 7.1: Update ListDataSourcesTool

**File**: `src/Beacon.MCP/Tools/ListDataSourcesTool.cs`

The `ListDataSourcesAsync` method already lists all data sources — no change needed for the list.

`ListTablesAsync` already queries `DatabaseMetadata` by data source ID — since API endpoints are stored as `DatabaseMetadata` rows, this works automatically. Just update the output text:
- If data source is API type, say "Endpoints" instead of "Tables"
- Show method + path instead of just table name

#### Task 7.2: Update ExecuteQueryTool

**File**: `src/Beacon.MCP/Tools/ExecuteQueryTool.cs`

Current tool expects `sql` parameter. For API data sources, the "query" is a JSON object.

Options:
1. Add a separate `api_query` parameter alongside `sql`
2. Detect data source type and interpret `sql` as JSON for API types

**Approach**: Option 1 is cleaner. Add `api_query` as an optional JSON parameter. Update `InputSchema`:
```csharp
["api_query"] = ToolHelper.ObjectProp("For API data sources: { method, path, parameters, resultMapping }")
```

In `ExecuteAsync`:
- If data source type is API, use `api_query` (or `sql` if it's valid JSON — for flexibility)
- Skip SQL guardrails (read-only check, PII detection) for API queries — they don't apply
- Otherwise, proceed as before

#### Task 7.3: Update GetDocumentationTool

**File**: `src/Beacon.MCP/Tools/GetDocumentationTool.cs`

Verify this works with API data sources. Since documentation is stored per data source and the AI documentation service reads from `DatabaseMetadata`, it should work if the AI documentation service is updated (Phase 8).

---

### Phase 8: AI Documentation Support (depends on Phase 3)

**Goal**: AI can generate documentation for API endpoints.

#### Task 8.1: Update AI documentation service context building

**File**: `src/Beacon.AI/Services/Ai/AiDocumentationService.cs` (or `MultiAgentDocumentationService.cs`)

When building context for the LLM:
- For API data sources, include endpoint descriptions, parameters, and response fields from `DatabaseMetadata`
- Format context appropriately: "Endpoint: GET /api/users — Returns a list of users. Parameters: status (query, string), limit (query, integer). Response fields: id (number), name (string), email (string)"

This is a minor change — the service already reads `DatabaseMetadata` for table/column context. The AI prompt just needs adjustment to say "endpoints" instead of "tables" when `DataSourceType == Api`.

---

### Phase 9: Wiring + Testing (depends on all above)

#### Task 9.1: Build verification

- Run `dotnet build --property WarningLevel=0` — ensure no compilation errors
- Verify all projects reference correctly

#### Task 9.2: Manual integration test

- Add a test API data source using a public OpenAPI spec (e.g., Petstore `https://petstore3.swagger.io/api/v3/openapi.json`)
- Verify: connection test, metadata import, endpoint catalog in data catalog, query execution, MCP tool access

#### Task 9.3: Unit tests

**File**: `src/Beacon.Tests/` — new test files:
- `JsonResponseTabularizerTests.cs` — test various JSON shapes, auto-detect, explicit mapping, edge cases
- `OpenApiImportServiceTests.cs` — test spec parsing, filtering, response schema extraction
- `ApiProviderTests.cs` — test query definition parsing, validation

---

## File Summary

### New Files
| File | Phase |
|------|-------|
| `src/Beacon.Connector.Api/Beacon.Connector.Api.csproj` | 2.1 |
| `src/Beacon.Connector.Api/ApiProvider.cs` | 2.5 |
| `src/Beacon.Connector.Api/ServiceCollectionExtensions.cs` | 2.6 |
| `src/Beacon.Connector.Api/Models/ApiConnectionConfig.cs` | 1.2 |
| `src/Beacon.Connector.Api/Models/ApiAuthConfig.cs` | 1.2 |
| `src/Beacon.Connector.Api/Models/ApiEndpointFilter.cs` | 1.2 |
| `src/Beacon.Connector.Api/Models/ApiQueryDefinition.cs` | 1.3 |
| `src/Beacon.Connector.Api/Models/ApiResultMapping.cs` | 1.3 |
| `src/Beacon.Connector.Api/Services/OpenApiImportService.cs` | 2.2 |
| `src/Beacon.Connector.Api/Services/JsonResponseTabularizer.cs` | 2.3 |
| `src/Beacon.Connector.Api/Services/ApiHttpClientFactory.cs` | 2.4 |
| `src/Beacon.UI/Components/Pages/DataSources/ApiQueryEditor.razor` | 5.1 |
| `src/Beacon.Tests/JsonResponseTabularizerTests.cs` | 9.3 |
| `src/Beacon.Tests/OpenApiImportServiceTests.cs` | 9.3 |

### Modified Files
| File | Phase | Change |
|------|-------|--------|
| `src/Beacon.Core/Data/Enums/DataSourceType.cs` | 1.1 | Add `Api = 8` |
| `src/Beacon.Core/Models/Providers/DataSourceMetadata.cs` | 1.4 | Add `Endpoints` property + metadata classes |
| `src/Beacon.Core/Services/DataSourceService.cs` | 3.2 | Import API metadata on creation |
| `src/Beacon.Core/Services/DatabaseMetadataService.cs` | 3.1 | Support API metadata storage/refresh |
| `src/Beacon.Core/Handlers/DataCatalog/GetDataCatalogHandler.cs` | 6.1 | Include API type, add DataSourceType to record |
| `src/Beacon.UI/Components/Pages/DataSources/AddDataSourceDialog.razor` | 4.1 | API form fields |
| `src/Beacon.UI/Components/Pages/DataSources/QueryEditor.razor` | 5.2 | Route to ApiQueryEditor |
| `src/Beacon.UI/Components/Pages/DataCatalog/DataCatalog.razor` | 6.2 | Type badge, API endpoint display |
| `src/Beacon.MCP/Tools/ListDataSourcesTool.cs` | 7.1 | API-aware output text |
| `src/Beacon.MCP/Tools/ExecuteQueryTool.cs` | 7.2 | `api_query` parameter, skip SQL guardrails |
| `src/Beacon.SampleProject/Program.cs` | 2.7 | Register `.AddApiConnector()` |
| `Beacon.sln` | 2.1 | Add project reference |

## Execution Order

```
Phase 1 (Tasks 1.1-1.4)  — enum + models, no dependencies
    ↓
Phase 2 (Tasks 2.1-2.7)  — connector project + provider
    ↓
Phase 3 (Tasks 3.1-3.2)  — metadata storage
    ↓ (Phase 4 can run in parallel with Phase 3)
Phase 4 (Task 4.1)        — Add Data Source UI
    ↓
Phase 5 (Tasks 5.1-5.2)  — API Query Editor UI
    ↓
Phase 6 (Tasks 6.1-6.2)  — Data Catalog integration
    ↓
Phase 7 (Tasks 7.1-7.3)  — MCP integration
    ↓
Phase 8 (Task 8.1)        — AI documentation
    ↓
Phase 9 (Tasks 9.1-9.3)  — build, test, verify
```

## NuGet Packages Required

| Package | Version | Purpose |
|---------|---------|---------|
| `Microsoft.OpenApi.Readers` | latest stable | Parse OpenAPI 2.0/3.x specs |
| `JsonPath.Net` | latest stable | JSONPath evaluation for result mapping |

## Risks and Mitigations

1. **OpenAPI spec parsing edge cases** — Some specs use `allOf`/`oneOf` composition, deeply nested `$ref`. Mitigation: flatten to 2 levels max, skip overly complex schemas with a warning.
2. **JSONPath library compatibility** — `JsonPath.Net` uses `System.Text.Json`. Mitigation: this aligns with our existing JSON usage.
3. **Large OpenAPI specs** — Some specs (e.g., AWS) have 1000+ endpoints. Mitigation: endpoint filter patterns + lazy loading in UI explorer.
4. **HTTP client lifecycle** — Use `IHttpClientFactory` (already registered in `Program.cs`) to avoid socket exhaustion.
