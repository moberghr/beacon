# API Contracts: Documentation Operations

**Feature**: 005-ai-integration
**Category**: AI-powered data source documentation

---

## 1. Generate Documentation

**Operation**: Generate AI-powered documentation for a data source by analyzing schema and sample data.

### Command: GenerateDocumentationCommand

```csharp
public record GenerateDocumentationCommand : IRequest<GenerateDocumentationResult>
{
    public int DataSourceId { get; init; }
    public string? Title { get; init; }
    public GenerationOptions Options { get; init; } = new();
}

public record GenerationOptions
{
    public int MaxTables { get; init; } = 50;
    public int SampleRowsPerTable { get; init; } = 10;
    public List<string>? ExcludedTables { get; init; }
    public List<string>? IncludedTablesOnly { get; init; }
    public bool IncludeSampleData { get; init; } = true;
    public bool IncludeRelationships { get; init; } = true;
    public bool IncludeDataQuality { get; init} = true;
}
```

### Response: GenerateDocumentationResult

```csharp
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

### Validation Rules

- `DataSourceId` must exist and be accessible by current user
- `MaxTables` must be between 1 and 500
- `SampleRowsPerTable` must be between 1 and 100
- If `IncludedTablesOnly` is specified, tables must exist in data source
- If both `ExcludedTables` and `IncludedTablesOnly` are specified, return error

### Error Responses

| Error Code | HTTP Status | Description |
|------------|-------------|-------------|
| `DATA_SOURCE_NOT_FOUND` | 404 | Data source does not exist |
| `DATA_SOURCE_UNAUTHORIZED` | 403 | User does not have access to data source |
| `INVALID_TABLE_LIST` | 400 | Specified tables do not exist |
| `LLM_SERVICE_UNAVAILABLE` | 503 | LLM API is unavailable |
| `RATE_LIMIT_EXCEEDED` | 429 | Too many concurrent requests |
| `INSUFFICIENT_PERMISSIONS` | 403 | User lacks schema access permission |

### Business Rules

1. Only users with "Read" permission on data source can generate documentation
2. Generation is performed asynchronously for >20 tables
3. If data source schema has changed since last documentation, warn user
4. Automatically exclude system tables unless explicitly included
5. Cost estimate must be approved by user if exceeds configured threshold

### Example Request

```json
{
  "dataSourceId": 42,
  "title": "Production Database Documentation",
  "options": {
    "maxTables": 30,
    "sampleRowsPerTable": 5,
    "excludedTables": ["__EFMigrationsHistory", "sysdiagrams"],
    "includeSampleData": true,
    "includeRelationships": true,
    "includeDataQuality": false
  }
}
```

### Example Response

```json
{
  "documentationId": 101,
  "title": "Production Database Documentation",
  "tablesAnalyzed": 28,
  "sectionsGenerated": 142,
  "tokensUsed": 18450,
  "estimatedCost": 0.061,
  "generationTime": "00:00:12.5",
  "generatedByModel": "claude-sonnet-4.5",
  "warnings": [
    "Table 'legacy_data' has no sample rows",
    "Foreign key relationship not detected between Orders and Customers"
  ]
}
```

---

## 2. Update Documentation

**Operation**: Update user-edited sections of documentation while preserving AI-generated content.

### Command: UpdateDocumentationCommand

```csharp
public record UpdateDocumentationCommand : IRequest<UpdateDocumentationResult>
{
    public int DocumentationId { get; init; }
    public List<SectionUpdate> SectionUpdates { get; init; } = new();
    public string? ChangeDescription { get; init; }
    public bool PublishAfterUpdate { get; init; }
}

public record SectionUpdate
{
    public int SectionId { get; init; }
    public string UserEditedContent { get; init; } = null!;
}
```

### Response: UpdateDocumentationResult

```csharp
public record UpdateDocumentationResult
{
    public int DocumentationId { get; init; }
    public int SectionsUpdated { get; init; }
    public int VersionNumber { get; init; }
    public DateTime ModifiedAt { get; init; }
    public DocumentationStatus Status { get; init; }
}
```

### Validation Rules

- `DocumentationId` must exist and be accessible by current user
- `SectionUpdates` must not be empty
- Each `SectionId` must exist and belong to the specified documentation
- `UserEditedContent` must be non-empty and max 10,000 characters
- Cannot update archived documentation

### Error Responses

| Error Code | HTTP Status | Description |
|------------|-------------|-------------|
| `DOCUMENTATION_NOT_FOUND` | 404 | Documentation does not exist |
| `DOCUMENTATION_UNAUTHORIZED` | 403 | User does not have edit permission |
| `DOCUMENTATION_ARCHIVED` | 409 | Cannot update archived documentation |
| `SECTION_NOT_FOUND` | 404 | Section ID does not exist |
| `CONTENT_TOO_LONG` | 400 | Section content exceeds max length |

### Business Rules

1. Only users with "Edit" permission on data source can update documentation
2. Updates create a new version automatically
3. Original AI-generated content is preserved in `AiGeneratedContent` field
4. If `PublishAfterUpdate` = true, status changes to Published
5. Maximum 50 section updates per request

### Example Request

```json
{
  "documentationId": 101,
  "sectionUpdates": [
    {
      "sectionId": 1501,
      "userEditedContent": "# Orders Table\n\nStores customer orders with payment information. **Critical**: This table contains PII data and requires special handling under GDPR."
    },
    {
      "sectionId": 1502,
      "userEditedContent": "Primary key for orders. Auto-incremented integer starting from 10000 (legacy reasons)."
    }
  ],
  "changeDescription": "Added GDPR compliance notes and explained OrderId numbering",
  "publishAfterUpdate": true
}
```

### Example Response

```json
{
  "documentationId": 101,
  "sectionsUpdated": 2,
  "versionNumber": 3,
  "modifiedAt": "2026-01-03T18:45:22Z",
  "status": "Published"
}
```

---

## 3. Export Documentation

**Operation**: Export documentation in various formats (Markdown, HTML, PDF, JSON).

### Query: ExportDocumentationQuery

```csharp
public record ExportDocumentationQuery : IRequest<DocumentationExportResult>
{
    public int DocumentationId { get; init; }
    public DocumentationExportFormat Format { get; init; }
    public ExportOptions Options { get; init; } = new();
}

public enum DocumentationExportFormat
{
    Markdown,
    Html,
    Pdf,
    Json
}

public record ExportOptions
{
    public bool IncludeTableOfContents { get; init; } = true;
    public bool IncludeSampleData { get; init; } = true;
    public bool HighlightUserEdits { get; init; } = false;
    public string? CustomCssUrl { get; init; }  // For HTML/PDF
    public PdfPageSize? PageSize { get; init; } = PdfPageSize.A4;  // For PDF
}

public enum PdfPageSize
{
    A4,
    Letter,
    Legal
}
```

### Response: DocumentationExportResult

```csharp
public record DocumentationExportResult
{
    public byte[] FileData { get; init; } = null!;
    public string ContentType { get; init; } = null!;
    public string FileName { get; init; } = null!;
    public long FileSizeBytes { get; init; }
    public TimeSpan GenerationTime { get; init; }
}
```

### Validation Rules

- `DocumentationId` must exist and be accessible by current user
- `Format` must be valid enum value
- For PDF export, `PageSize` must be specified
- `CustomCssUrl` must be valid URL if provided
- Cannot export Draft documentation unless user is owner

### Error Responses

| Error Code | HTTP Status | Description |
|------------|-------------|-------------|
| `DOCUMENTATION_NOT_FOUND` | 404 | Documentation does not exist |
| `DOCUMENTATION_UNAUTHORIZED` | 403 | User does not have read permission |
| `EXPORT_FORMAT_UNSUPPORTED` | 400 | Requested format not supported |
| `PDF_GENERATION_FAILED` | 500 | PDF generation library error |
| `CUSTOM_CSS_INVALID` | 400 | Custom CSS URL is unreachable |

### Business Rules

1. Only users with "Read" permission on data source can export documentation
2. Exports are cached for 1 hour to improve performance
3. PDF exports >100 pages may take 10-30 seconds
4. If `HighlightUserEdits` = true, user-edited sections are visually distinguished
5. JSON format includes full metadata (tokens, costs, versions)

### Content-Type Headers

| Format | Content-Type |
|--------|--------------|
| Markdown | `text/markdown; charset=utf-8` |
| HTML | `text/html; charset=utf-8` |
| PDF | `application/pdf` |
| JSON | `application/json; charset=utf-8` |

### Example Request

```json
{
  "documentationId": 101,
  "format": "Pdf",
  "options": {
    "includeTableOfContents": true,
    "includeSampleData": false,
    "highlightUserEdits": true,
    "pageSize": "A4"
  }
}
```

### Example Response Headers

```http
Content-Type: application/pdf
Content-Disposition: attachment; filename="production-database-documentation-2026-01-03.pdf"
Content-Length: 524288
```

---

## 4. Regenerate Documentation

**Operation**: Regenerate AI documentation while optionally preserving user edits.

### Command: RegenerateDocumentationCommand

```csharp
public record RegenerateDocumentationCommand : IRequest<GenerateDocumentationResult>
{
    public int DocumentationId { get; init; }
    public RegenerationStrategy Strategy { get; init; }
    public GenerationOptions Options { get; init; } = new();
}

public enum RegenerationStrategy
{
    ReplaceAll,         // Discard all edits, start fresh
    MergeWithEdits,     // Regenerate, preserve user-edited sections
    UpdateNewTablesOnly // Only analyze new tables, keep existing
}
```

### Response: GenerateDocumentationResult

Same as Generate Documentation.

### Validation Rules

- `DocumentationId` must exist
- User must have "Edit" permission on data source
- If `Strategy` = MergeWithEdits, at least one user-edited section must exist

### Business Rules

1. Creates new version automatically
2. Old version preserved in DocumentationVersion table
3. If schema has changed significantly (>20% tables added/removed), recommend ReplaceAll
4. Tokens/costs from regeneration are tracked separately

---

## 5. List Documentation

**Operation**: Get all documentation for a data source.

### Query: ListDocumentationQuery

```csharp
public record ListDocumentationQuery : IRequest<ListDocumentationResult>
{
    public int DataSourceId { get; init; }
    public DocumentationStatus? Status { get; init; }
    public int PageNumber { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}
```

### Response: ListDocumentationResult

```csharp
public record ListDocumentationResult
{
    public List<DocumentationSummary> Items { get; init; } = new();
    public int TotalCount { get; init; }
    public int PageNumber { get; init; }
    public int PageSize { get; init; }
}

public record DocumentationSummary
{
    public int Id { get; init; }
    public string Title { get; init; } = null!;
    public DocumentationStatus Status { get; init; }
    public int TablesAnalyzed { get; init; }
    public int SectionsCount { get; init; }
    public DateTime GeneratedAt { get; init; }
    public string GeneratedByModel { get; init; } = null!;
    public DateTime? LastModifiedAt { get; init; }
    public int VersionCount { get; init; }
}
```

### Example Response

```json
{
  "items": [
    {
      "id": 101,
      "title": "Production Database Documentation",
      "status": "Published",
      "tablesAnalyzed": 28,
      "sectionsCount": 142,
      "generatedAt": "2026-01-03T12:30:00Z",
      "generatedByModel": "claude-sonnet-4.5",
      "lastModifiedAt": "2026-01-03T18:45:22Z",
      "versionCount": 3
    }
  ],
  "totalCount": 1,
  "pageNumber": 1,
  "pageSize": 20
}
```

---

## 6. Run Field Analysis

**Operation**: Run AI-powered field quality analysis on data source columns to detect usage patterns, data quality issues, and type mismatches.

### Command: RunFieldAnalysisCommand

```csharp
public record RunFieldAnalysisCommand : IRequest<RunFieldAnalysisResult>
{
    public int DocumentationId { get; init; }
    public FieldAnalysisOptions Options { get; init; } = new();
}

public record FieldAnalysisOptions
{
    public List<string>? IncludedTablesOnly { get; init; }  // Analyze only these tables
    public List<string>? ExcludedTables { get; init; }      // Skip these tables
    public bool UseSampling { get; init; } = true;          // Use sampling for large tables
    public bool DetectPatterns { get; init; } = true;       // Detect data patterns (emails, dates, etc.)
    public bool CalculateMigrationImpact { get; init; } = true;  // Estimate type conversion feasibility
}
```

### Response: RunFieldAnalysisResult

```csharp
public record RunFieldAnalysisResult
{
    public int DocumentationId { get; init; }
    public int TablesAnalyzed { get; init; }
    public int ColumnsAnalyzed { get; init; }
    public int UnusedFieldsFound { get; init; }
    public int PotentiallyUnusedFieldsFound { get; init; }
    public int PatternsDetected { get; init; }
    public int MigrationSuggestionsCount { get; init; }
    public List<TableRequiringApproval> TablesRequiringFullScan { get; init; } = new();
    public TimeSpan AnalysisTime { get; init; }
    public FieldAnalysisSummary Summary { get; init; } = null!;
}

public record TableRequiringApproval
{
    public string TableName { get; init; } = null!;
    public long RowCount { get; init; }
    public string EstimatedScanTime { get; init; } = null!;
}

public record FieldAnalysisSummary
{
    public int TotalFields { get; init; }
    public int UsedFields { get; init; }
    public int PotentiallyUnusedFields { get; init; }
    public int UnusedFields { get; init; }
    public Dictionary<string, int> PatternsByType { get; init; } = new();  // e.g., { "Email": 5, "Date": 12 }
}
```

### Validation Rules

- `DocumentationId` must exist and belong to current user
- Cannot run analysis on archived documentation
- If both `IncludedTablesOnly` and `ExcludedTables` are specified, return error

### Error Responses

| Error Code | HTTP Status | Description |
|------------|-------------|-------------|
| `DOCUMENTATION_NOT_FOUND` | 404 | Documentation does not exist |
| `DOCUMENTATION_UNAUTHORIZED` | 403 | User does not have access |
| `DOCUMENTATION_ARCHIVED` | 409 | Cannot analyze archived documentation |
| `DATA_SOURCE_UNAVAILABLE` | 503 | Cannot connect to data source |
| `ANALYSIS_IN_PROGRESS` | 409 | Analysis already running for this documentation |

### Business Rules

1. Uses sampling for tables > 10,000 rows (10% of rows, min 1,000, max 100,000)
2. Returns list of tables requiring user approval for full scan
3. Analysis results stored in FieldAnalysis entity
4. Summary also added to DocumentationSection (SectionType = DataQuality)
5. Patterns detected: emails, phones, dates, JSON, URLs, numeric-as-strings, UUIDs

### Example Request

```json
{
  "documentationId": 101,
  "options": {
    "includedTablesOnly": null,
    "excludedTables": ["__EFMigrationsHistory"],
    "useSampling": true,
    "detectPatterns": true,
    "calculateMigrationImpact": true
  }
}
```

### Example Response

```json
{
  "documentationId": 101,
  "tablesAnalyzed": 28,
  "columnsAnalyzed": 342,
  "unusedFieldsFound": 12,
  "potentiallyUnusedFieldsFound": 8,
  "patternsDetected": 15,
  "migrationSuggestionsCount": 9,
  "tablesRequiringFullScan": [
    {
      "tableName": "orders",
      "rowCount": 2500000,
      "estimatedScanTime": "~45 seconds"
    },
    {
      "tableName": "audit_logs",
      "rowCount": 8000000,
      "estimatedScanTime": "~2 minutes"
    }
  ],
  "analysisTime": "00:01:23",
  "summary": {
    "totalFields": 342,
    "usedFields": 322,
    "potentiallyUnusedFields": 8,
    "unusedFields": 12,
    "patternsByType": {
      "Email": 3,
      "Date": 7,
      "Json": 2,
      "Uuid": 3
    }
  }
}
```

---

## 7. Approve Full Table Scan

**Operation**: User approves or rejects full table scan for specific tables during field analysis.

### Command: ApproveFullScanCommand

```csharp
public record ApproveFullScanCommand : IRequest<ApproveFullScanResult>
{
    public int DocumentationId { get; init; }
    public List<TableScanDecision> Decisions { get; init; } = new();
}

public record TableScanDecision
{
    public string TableName { get; init; } = null!;
    public ScanApproval Approval { get; init; }
}

public enum ScanApproval
{
    Approve,        // Run full scan on this table
    Reject,         // Skip full scan, use sampling only
    ApproveAll      // Approve this and all remaining tables
}
```

### Response: ApproveFullScanResult

```csharp
public record ApproveFullScanResult
{
    public int DocumentationId { get; init; }
    public int TablesApproved { get; init; }
    public int TablesRejected { get; init; }
    public bool AnalysisResumed { get; init; }
    public string Message { get; init; } = null!;
}
```

### Validation Rules

- `DocumentationId` must have pending full scan approvals
- All `TableName` values must be in the pending approval list

### Business Rules

1. Called after RunFieldAnalysisCommand returns tables requiring approval
2. If `ApproveAll` is selected for any table, all remaining tables are approved
3. After decisions are made, field analysis resumes automatically
4. Rejected tables use sampled results only

### Example Request

```json
{
  "documentationId": 101,
  "decisions": [
    { "tableName": "orders", "approval": "Approve" },
    { "tableName": "audit_logs", "approval": "ApproveAll" }
  ]
}
```

### Example Response

```json
{
  "documentationId": 101,
  "tablesApproved": 2,
  "tablesRejected": 0,
  "analysisResumed": true,
  "message": "Full scan approved for 2 tables. Analysis resumed."
}
```

---

## 8. Get Field Analysis Results

**Operation**: Retrieve detailed field analysis results for a documentation.

### Query: GetFieldAnalysisQuery

```csharp
public record GetFieldAnalysisQuery : IRequest<GetFieldAnalysisResult>
{
    public int DocumentationId { get; init; }
    public FieldAnalysisFilter? Filter { get; init; }
    public int PageNumber { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}

public record FieldAnalysisFilter
{
    public string? TableName { get; init; }
    public FieldUsageStatus? UsageStatus { get; init; }
    public DetectedDataPattern? DetectedPattern { get; init; }
    public bool? HasMigrationSuggestion { get; init; }
}
```

### Response: GetFieldAnalysisResult

```csharp
public record GetFieldAnalysisResult
{
    public int DocumentationId { get; init; }
    public List<FieldAnalysisItem> Items { get; init; } = new();
    public int TotalCount { get; init; }
    public int PageNumber { get; init; }
    public int PageSize { get; init; }
    public FieldAnalysisSummary Summary { get; init; } = null!;
}

public record FieldAnalysisItem
{
    public int Id { get; init; }
    public string TableName { get; init; } = null!;
    public string ColumnName { get; init; } = null!;
    public string DataType { get; init; } = null!;
    public long TotalRows { get; init; }
    public long SampledRows { get; init; }
    public bool UsedFullScan { get; init; }
    public decimal NullPercentage { get; init; }
    public long DistinctValues { get; init; }
    public FieldUsageStatus UsageStatus { get; init; }
    public DetectedDataPattern? DetectedPattern { get; init; }
    public decimal? PatternMatchPercentage { get; init; }
    public string? SuggestedDataType { get; init; }
    public decimal? MigrationFeasibility { get; init; }
    public int? MigrationIssueCount { get; init; }
    public string? AiRecommendation { get; init; }
    public decimal? AiConfidenceScore { get; init; }
    public DateTime AnalyzedAt { get; init; }
}
```

### Validation Rules

- `DocumentationId` must exist and be accessible by current user
- `PageSize` must be between 1 and 100

### Example Request

```json
{
  "documentationId": 101,
  "filter": {
    "usageStatus": "Unused"
  },
  "pageNumber": 1,
  "pageSize": 20
}
```

### Example Response

```json
{
  "documentationId": 101,
  "items": [
    {
      "id": 5001,
      "tableName": "users",
      "columnName": "legacy_flag",
      "dataType": "varchar(10)",
      "totalRows": 50000,
      "sampledRows": 5000,
      "usedFullScan": false,
      "nullPercentage": 100.00,
      "distinctValues": 0,
      "usageStatus": "Unused",
      "detectedPattern": null,
      "patternMatchPercentage": null,
      "suggestedDataType": null,
      "migrationFeasibility": null,
      "migrationIssueCount": null,
      "aiRecommendation": "Column appears unused. Consider deprecation or removal after confirming with stakeholders.",
      "aiConfidenceScore": 0.95,
      "analyzedAt": "2026-01-03T14:30:00Z"
    },
    {
      "id": 5002,
      "tableName": "orders",
      "columnName": "created_date_str",
      "dataType": "varchar(50)",
      "totalRows": 2500000,
      "sampledRows": 100000,
      "usedFullScan": false,
      "nullPercentage": 2.30,
      "distinctValues": 1825,
      "usageStatus": "Used",
      "detectedPattern": "Date",
      "patternMatchPercentage": 99.20,
      "suggestedDataType": "datetime",
      "migrationFeasibility": 99.20,
      "migrationIssueCount": 2000,
      "aiRecommendation": "Column contains date strings. Recommend migration to datetime type. 99.2% of values (2.48M) are valid dates. ~2,000 values would need cleanup.",
      "aiConfidenceScore": 0.92,
      "analyzedAt": "2026-01-03T14:30:00Z"
    }
  ],
  "totalCount": 12,
  "pageNumber": 1,
  "pageSize": 20,
  "summary": {
    "totalFields": 342,
    "usedFields": 322,
    "potentiallyUnusedFields": 8,
    "unusedFields": 12,
    "patternsByType": {
      "Email": 3,
      "Date": 7,
      "Json": 2,
      "Uuid": 3
    }
  }
}
```

---

## 9. Export Interactive HTML

**Operation**: Generate interactive HTML export with embedded Mermaid ERD diagrams, collapsible sections, and table of contents.

### Command: ExportHtmlCommand

```csharp
public record ExportHtmlCommand : IRequest<ExportHtmlResult>
{
    public int DocumentationId { get; init; }
    public bool ForceRegenerate { get; init; } = false;  // Ignore cache
}
```

### Response: ExportHtmlResult

```csharp
public record ExportHtmlResult
{
    public int DocumentationId { get; init; }
    public byte[] HtmlContent { get; init; } = null!;
    public string ContentType { get; init; } = "text/html; charset=utf-8";
    public string FileName { get; init; } = null!;
    public long FileSizeBytes { get; init; }
    public bool WasCached { get; init; }
    public TimeSpan GenerationTime { get; init; }
    public int DiagramGroupCount { get; init; }
    public List<string> MermaidDiagrams { get; init; } = new();  // Raw Mermaid code per group
}
```

### Validation Rules

- `DocumentationId` must exist and be accessible by current user
- Documentation must have at least one section

### Error Responses

| Error Code | HTTP Status | Description |
|------------|-------------|-------------|
| `DOCUMENTATION_NOT_FOUND` | 404 | Documentation does not exist |
| `DOCUMENTATION_UNAUTHORIZED` | 403 | User does not have read permission |
| `MERMAID_RENDERING_FAILED` | 500 | Mermaid diagram generation failed |
| `HTML_GENERATION_FAILED` | 500 | HTML template rendering failed |

### Business Rules

1. Returns cached HTML if documentation hash matches (unless ForceRegenerate = true)
2. Generates Mermaid ERD diagrams for each diagram group
3. Creates collapsible sections with `<details>` HTML elements
4. Includes table of contents with anchor links at the top
5. Caches result in DocumentationExport entity
6. HTML is self-contained (includes Mermaid JS library inline)

### Example Request

```json
{
  "documentationId": 101,
  "forceRegenerate": false
}
```

### Example Response

```json
{
  "documentationId": 101,
  "htmlContent": "<base64-encoded-html>",
  "contentType": "text/html; charset=utf-8",
  "fileName": "production-database-documentation-2026-01-04.html",
  "fileSizeBytes": 245678,
  "wasCached": true,
  "generationTime": "00:00:00.123",
  "diagramGroupCount": 4,
  "mermaidDiagrams": [
    "erDiagram\n    ORDERS ||--o{ ORDER_ITEMS : contains\n    ...",
    "erDiagram\n    USERS ||--o{ USER_ROLES : has\n    ..."
  ]
}
```

---

## 10. Generate Diagram Groups

**Operation**: Use AI to suggest logical table groupings for ERD diagrams based on relationships, naming conventions, and semantic analysis.

### Query: GenerateDiagramGroupsQuery

```csharp
public record GenerateDiagramGroupsQuery : IRequest<GenerateDiagramGroupsResult>
{
    public int DocumentationId { get; init; }
    public bool IncludeOrphanTables { get; init; } = true;  // Tables with no relationships
}
```

### Response: GenerateDiagramGroupsResult

```csharp
public record GenerateDiagramGroupsResult
{
    public int DocumentationId { get; init; }
    public List<DiagramGroupSuggestion> SuggestedGroups { get; init; } = new();
    public List<string> OrphanTables { get; init; } = new();  // Tables not in any group
    public int TokensUsed { get; init; }
    public decimal EstimatedCost { get; init; }
}

public record DiagramGroupSuggestion
{
    public string Name { get; init; } = null!;
    public string Description { get; init; } = null!;
    public List<string> TableNames { get; init; } = new();
    public DiagramGroupingCriteria PrimaryCriteria { get; init; }
    public decimal ConfidenceScore { get; init; }
}
```

### Validation Rules

- `DocumentationId` must exist and have schema information

### Error Responses

| Error Code | HTTP Status | Description |
|------------|-------------|-------------|
| `DOCUMENTATION_NOT_FOUND` | 404 | Documentation does not exist |
| `NO_TABLES_AVAILABLE` | 400 | Documentation has no tables to group |
| `LLM_SERVICE_UNAVAILABLE` | 503 | AI service is unavailable |

### Business Rules

1. AI analyzes: foreign key relationships, table name prefixes (e.g., `order_`, `user_`), semantic meaning from AI-generated descriptions
2. Suggests 3-8 groups for typical schemas (fewer for small, more for large)
3. Each table appears in exactly one suggested group
4. Orphan tables (no relationships) placed in "Other" group or returned separately
5. Results are suggestions only - not persisted until user confirms

### Example Request

```json
{
  "documentationId": 101,
  "includeOrphanTables": true
}
```

### Example Response

```json
{
  "documentationId": 101,
  "suggestedGroups": [
    {
      "name": "Order Management",
      "description": "Core order processing tables including orders, items, and fulfillment",
      "tableNames": ["orders", "order_items", "order_status", "shipments"],
      "primaryCriteria": "ForeignKey",
      "confidenceScore": 0.95
    },
    {
      "name": "User & Authentication",
      "description": "User accounts, roles, and authentication related tables",
      "tableNames": ["users", "user_roles", "roles", "sessions", "tokens"],
      "primaryCriteria": "Naming",
      "confidenceScore": 0.88
    },
    {
      "name": "Product Catalog",
      "description": "Product information and categorization",
      "tableNames": ["products", "categories", "product_images", "inventory"],
      "primaryCriteria": "Semantic",
      "confidenceScore": 0.82
    }
  ],
  "orphanTables": ["audit_log", "system_config"],
  "tokensUsed": 2450,
  "estimatedCost": 0.0081
}
```

---

## 11. Save Diagram Groups

**Operation**: Save AI-suggested or user-customized diagram groups for a documentation.

### Command: SaveDiagramGroupsCommand

```csharp
public record SaveDiagramGroupsCommand : IRequest<SaveDiagramGroupsResult>
{
    public int DocumentationId { get; init; }
    public List<DiagramGroupInput> Groups { get; init; } = new();
}

public record DiagramGroupInput
{
    public int? Id { get; init; }  // Null for new groups
    public string Name { get; init; } = null!;
    public string? Description { get; init; }
    public List<string> TableNames { get; init; } = new();
    public int SortOrder { get; init; }
}
```

### Response: SaveDiagramGroupsResult

```csharp
public record SaveDiagramGroupsResult
{
    public int DocumentationId { get; init; }
    public int GroupsCreated { get; init; }
    public int GroupsUpdated { get; init; }
    public int GroupsDeleted { get; init; }
    public List<DiagramGroupSummary> Groups { get; init; } = new();
}

public record DiagramGroupSummary
{
    public int Id { get; init; }
    public string Name { get; init; } = null!;
    public int TableCount { get; init; }
    public bool IsAiGenerated { get; init; }
    public bool IsUserModified { get; init; }
}
```

### Validation Rules

- `DocumentationId` must exist and be accessible
- Each `Name` must be unique within the documentation
- `TableNames` must reference existing tables in the documentation
- A table can only belong to one group

### Error Responses

| Error Code | HTTP Status | Description |
|------------|-------------|-------------|
| `DOCUMENTATION_NOT_FOUND` | 404 | Documentation does not exist |
| `DUPLICATE_GROUP_NAME` | 400 | Group name already exists |
| `INVALID_TABLE_NAME` | 400 | Table does not exist in documentation |
| `DUPLICATE_TABLE_ASSIGNMENT` | 400 | Table assigned to multiple groups |

### Business Rules

1. Groups with `Id = null` are created as new
2. Existing groups are updated (sets `IsUserModified = true` if AI-generated)
3. Groups not in input list are deleted
4. Invalidates cached HTML export (hash will no longer match)

### Example Request

```json
{
  "documentationId": 101,
  "groups": [
    {
      "id": 501,
      "name": "Order Management",
      "description": "All order-related tables",
      "tableNames": ["orders", "order_items", "order_status"],
      "sortOrder": 0
    },
    {
      "id": null,
      "name": "Reporting",
      "description": "Custom group for reporting tables",
      "tableNames": ["reports", "report_schedules"],
      "sortOrder": 1
    }
  ]
}
```

### Example Response

```json
{
  "documentationId": 101,
  "groupsCreated": 1,
  "groupsUpdated": 1,
  "groupsDeleted": 2,
  "groups": [
    {
      "id": 501,
      "name": "Order Management",
      "tableCount": 3,
      "isAiGenerated": true,
      "isUserModified": true
    },
    {
      "id": 510,
      "name": "Reporting",
      "tableCount": 2,
      "isAiGenerated": false,
      "isUserModified": false
    }
  ]
}
```

---

## 12. Get Diagram Groups

**Operation**: Retrieve existing diagram groups for a documentation.

### Query: GetDiagramGroupsQuery

```csharp
public record GetDiagramGroupsQuery : IRequest<GetDiagramGroupsResult>
{
    public int DocumentationId { get; init; }
}
```

### Response: GetDiagramGroupsResult

```csharp
public record GetDiagramGroupsResult
{
    public int DocumentationId { get; init; }
    public List<DiagramGroupDetail> Groups { get; init; } = new();
    public List<string> UnassignedTables { get; init; } = new();
}

public record DiagramGroupDetail
{
    public int Id { get; init; }
    public string Name { get; init; } = null!;
    public string? Description { get; init; }
    public List<string> TableNames { get; init; } = new();
    public int SortOrder { get; init; }
    public bool IsAiGenerated { get; init; }
    public bool IsUserModified { get; init; }
    public DiagramGroupingCriteria GroupingCriteria { get; init; }
    public decimal? ConfidenceScore { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime ModifiedAt { get; init; }
}
```

### Example Response

```json
{
  "documentationId": 101,
  "groups": [
    {
      "id": 501,
      "name": "Order Management",
      "description": "Core order processing tables",
      "tableNames": ["orders", "order_items", "order_status"],
      "sortOrder": 0,
      "isAiGenerated": true,
      "isUserModified": true,
      "groupingCriteria": "ForeignKey",
      "confidenceScore": 0.95,
      "createdAt": "2026-01-04T10:00:00Z",
      "modifiedAt": "2026-01-04T14:30:00Z"
    }
  ],
  "unassignedTables": ["audit_log", "system_config"]
}
```

---

## 13. Check Schema Changes

**Operation**: Compare current data source schema against stored snapshot to detect changes.

### Query: CheckSchemaChangesQuery

```csharp
public record CheckSchemaChangesQuery : IRequest<CheckSchemaChangesResult>
{
    public int DocumentationId { get; init; }
    public bool IncludeRenameDetection { get; init; } = true;  // Use AI to suggest renames
}
```

### Response: CheckSchemaChangesResult

```csharp
public record CheckSchemaChangesResult
{
    public int DocumentationId { get; init; }
    public bool HasChanges { get; init; }
    public int TotalChanges { get; init; }
    public List<SchemaChangeItem> Changes { get; init; } = new();
    public List<RenameSuggestion> RenameSuggestions { get; init; } = new();
    public DateTime LastSnapshotAt { get; init; }
    public DateTime CheckedAt { get; init; }
}

public record SchemaChangeItem
{
    public int Id { get; init; }
    public SchemaChangeType ChangeType { get; init; }
    public SchemaObjectType ObjectType { get; init; }
    public string ObjectName { get; init; } = null!;
    public string? PreviousValue { get; init; }  // JSON
    public string? CurrentValue { get; init; }   // JSON
    public DateTime DetectedAt { get; init; }
}

public record RenameSuggestion
{
    public int ChangeId { get; init; }
    public string FromName { get; init; } = null!;
    public string ToName { get; init; } = null!;
    public SchemaObjectType ObjectType { get; init; }
    public decimal ConfidenceScore { get; init; }
    public string Reasoning { get; init; } = null!;  // AI explanation
}
```

### Validation Rules

- `DocumentationId` must exist and be accessible

### Error Responses

| Error Code | HTTP Status | Description |
|------------|-------------|-------------|
| `DOCUMENTATION_NOT_FOUND` | 404 | Documentation does not exist |
| `NO_SNAPSHOT_AVAILABLE` | 400 | No schema snapshot exists (never generated) |
| `DATA_SOURCE_UNAVAILABLE` | 503 | Cannot connect to data source |

### Business Rules

1. Compares current schema against stored SchemaSnapshot
2. If `IncludeRenameDetection` = true, AI analyzes removed+added pairs for possible renames
3. Changes are persisted to SchemaChange table for audit trail
4. Quick hash comparison first; detailed diff only if hashes differ

### Example Response

```json
{
  "documentationId": 101,
  "hasChanges": true,
  "totalChanges": 3,
  "changes": [
    {
      "id": 5001,
      "changeType": "ColumnRemoved",
      "objectType": "Column",
      "objectName": "users.user_name",
      "previousValue": "{\"name\":\"user_name\",\"type\":\"varchar(100)\"}",
      "currentValue": null,
      "detectedAt": "2026-01-04T14:30:00Z"
    },
    {
      "id": 5002,
      "changeType": "ColumnAdded",
      "objectType": "Column",
      "objectName": "users.username",
      "previousValue": null,
      "currentValue": "{\"name\":\"username\",\"type\":\"varchar(100)\"}",
      "detectedAt": "2026-01-04T14:30:00Z"
    },
    {
      "id": 5003,
      "changeType": "TableAdded",
      "objectType": "Table",
      "objectName": "audit_logs",
      "previousValue": null,
      "currentValue": "{\"name\":\"audit_logs\",\"columns\":[...]}",
      "detectedAt": "2026-01-04T14:30:00Z"
    }
  ],
  "renameSuggestions": [
    {
      "changeId": 5001,
      "fromName": "users.user_name",
      "toName": "users.username",
      "objectType": "Column",
      "confidenceScore": 0.92,
      "reasoning": "Same table, similar name (user_name → username), identical data type (varchar(100))"
    }
  ],
  "lastSnapshotAt": "2026-01-01T10:00:00Z",
  "checkedAt": "2026-01-04T14:30:00Z"
}
```

---

## 14. Confirm Rename

**Operation**: User confirms or rejects an AI-suggested rename.

### Command: ConfirmRenameCommand

```csharp
public record ConfirmRenameCommand : IRequest<ConfirmRenameResult>
{
    public int DocumentationId { get; init; }
    public List<RenameDecision> Decisions { get; init; } = new();
}

public record RenameDecision
{
    public int ChangeId { get; init; }
    public bool IsConfirmed { get; init; }  // true = rename, false = delete+add
}
```

### Response: ConfirmRenameResult

```csharp
public record ConfirmRenameResult
{
    public int DocumentationId { get; init; }
    public int ConfirmedRenames { get; init; }
    public int RejectedRenames { get; init; }
    public string Message { get; init; } = null!;
}
```

### Validation Rules

- `DocumentationId` must exist
- All `ChangeId` values must reference existing SchemaChange records with `RenameStatus = Pending`

### Business Rules

1. Confirmed renames update SchemaChange.RenameStatus to Confirmed
2. Rejected renames update SchemaChange.RenameStatus to Rejected
3. When regenerating documentation, confirmed renames preserve content under new name

### Example Request

```json
{
  "documentationId": 101,
  "decisions": [
    { "changeId": 5001, "isConfirmed": true }
  ]
}
```

---

## 15. Get Schema Change History

**Operation**: Retrieve history of schema changes for a documentation.

### Query: GetSchemaChangeHistoryQuery

```csharp
public record GetSchemaChangeHistoryQuery : IRequest<GetSchemaChangeHistoryResult>
{
    public int DocumentationId { get; init; }
    public DateTime? FromDate { get; init; }
    public DateTime? ToDate { get; init; }
    public SchemaChangeType? ChangeType { get; init; }
    public int PageNumber { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}
```

### Response: GetSchemaChangeHistoryResult

```csharp
public record GetSchemaChangeHistoryResult
{
    public int DocumentationId { get; init; }
    public List<SchemaChangeHistoryItem> Items { get; init; } = new();
    public int TotalCount { get; init; }
    public int PageNumber { get; init; }
    public int PageSize { get; init; }
    public SchemaChangeSummary Summary { get; init; } = null!;
}

public record SchemaChangeHistoryItem
{
    public int Id { get; init; }
    public SchemaChangeType ChangeType { get; init; }
    public SchemaObjectType ObjectType { get; init; }
    public string ObjectName { get; init; } = null!;
    public string? PreviousValue { get; init; }
    public string? CurrentValue { get; init; }
    public bool IsRename { get; init; }
    public RenameStatus? RenameStatus { get; init; }
    public DateTime DetectedAt { get; init; }
    public bool IsAcknowledged { get; init; }
}

public record SchemaChangeSummary
{
    public int TablesAdded { get; init; }
    public int TablesRemoved { get; init; }
    public int TablesRenamed { get; init; }
    public int ColumnsAdded { get; init; }
    public int ColumnsRemoved { get; init; }
    public int ColumnsRenamed { get; init; }
    public int ColumnsTypeChanged { get; init; }
    public int RelationshipsAdded { get; init; }
    public int RelationshipsRemoved { get; init; }
}
```

### Example Response

```json
{
  "documentationId": 101,
  "items": [...],
  "totalCount": 45,
  "pageNumber": 1,
  "pageSize": 50,
  "summary": {
    "tablesAdded": 5,
    "tablesRemoved": 2,
    "tablesRenamed": 1,
    "columnsAdded": 23,
    "columnsRemoved": 8,
    "columnsRenamed": 3,
    "columnsTypeChanged": 2,
    "relationshipsAdded": 4,
    "relationshipsRemoved": 1
  }
}
```

---

## 16. Acknowledge Schema Changes

**Operation**: Mark schema changes as acknowledged (user has seen them).

### Command: AcknowledgeSchemaChangesCommand

```csharp
public record AcknowledgeSchemaChangesCommand : IRequest<AcknowledgeSchemaChangesResult>
{
    public int DocumentationId { get; init; }
    public List<int>? ChangeIds { get; init; }  // Null = acknowledge all unacknowledged
}
```

### Response: AcknowledgeSchemaChangesResult

```csharp
public record AcknowledgeSchemaChangesResult
{
    public int DocumentationId { get; init; }
    public int AcknowledgedCount { get; init; }
}
```

---

## 17. Create Prompt Version

**Operation**: Manually create a new version of the documentation generation prompt.

### Command: CreatePromptVersionCommand

```csharp
public record CreatePromptVersionCommand : IRequest<CreatePromptVersionResult>
{
    public int PromptTemplateId { get; init; }
    public string PromptContent { get; init; } = null!;
    public string? SystemPromptContent { get; init; }
}
```

### Response: CreatePromptVersionResult

```csharp
public record CreatePromptVersionResult
{
    public int VersionId { get; init; }
    public int VersionNumber { get; init; }
    public DateTime CreatedAt { get; init; }
}
```

### Validation Rules

- `PromptTemplateId` must reference the documentation generation prompt template
- `PromptContent` must be non-empty

### Error Responses

| Error Code | HTTP Status | Description |
|------------|-------------|-------------|
| `PROMPT_TEMPLATE_NOT_FOUND` | 404 | Prompt template does not exist |
| `PROMPT_CONTENT_EMPTY` | 400 | Prompt content cannot be empty |
| `UNAUTHORIZED` | 403 | User does not have admin permission |

### Business Rules

1. Only administrators can create prompt versions
2. New version is automatically set as the active version
3. Previous active version is deactivated
4. Version number is auto-incremented

### Example Request

```json
{
  "promptTemplateId": 1,
  "promptContent": "Analyze the following database schema and provide descriptions...",
  "systemPromptContent": "You are an expert database analyst."
}
```

### Example Response

```json
{
  "versionId": 15,
  "versionNumber": 15,
  "createdAt": "2026-01-04T15:30:00Z"
}
```

---

## 18. List Prompt Versions

**Operation**: Retrieve list of all versions for the documentation generation prompt.

### Query: ListPromptVersionsQuery

```csharp
public record ListPromptVersionsQuery : IRequest<ListPromptVersionsResult>
{
    public int PromptTemplateId { get; init; }
    public int PageNumber { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}
```

### Response: ListPromptVersionsResult

```csharp
public record ListPromptVersionsResult
{
    public int PromptTemplateId { get; init; }
    public List<PromptVersionSummary> Versions { get; init; } = new();
    public int TotalCount { get; init; }
    public int PageNumber { get; init; }
    public int PageSize { get; init; }
}

public record PromptVersionSummary
{
    public int Id { get; init; }
    public int VersionNumber { get; init; }
    public DateTime CreatedAt { get; init; }
    public string CreatedByUserName { get; init; } = null!;
    public bool IsActive { get; init; }
}
```

### Example Response

```json
{
  "promptTemplateId": 1,
  "versions": [
    {
      "id": 15,
      "versionNumber": 15,
      "createdAt": "2026-01-04T15:30:00Z",
      "createdByUserName": "admin@example.com",
      "isActive": true
    },
    {
      "id": 14,
      "versionNumber": 14,
      "createdAt": "2026-01-03T10:00:00Z",
      "createdByUserName": "admin@example.com",
      "isActive": false
    }
  ],
  "totalCount": 15,
  "pageNumber": 1,
  "pageSize": 20
}
```

---

## 19. Get Prompt Version

**Operation**: Retrieve full content of a specific prompt version.

### Query: GetPromptVersionQuery

```csharp
public record GetPromptVersionQuery : IRequest<GetPromptVersionResult>
{
    public int VersionId { get; init; }
}
```

### Response: GetPromptVersionResult

```csharp
public record GetPromptVersionResult
{
    public int Id { get; init; }
    public int PromptTemplateId { get; init; }
    public int VersionNumber { get; init; }
    public string PromptContent { get; init; } = null!;
    public string? SystemPromptContent { get; init; }
    public DateTime CreatedAt { get; init; }
    public string CreatedByUserName { get; init; } = null!;
    public bool IsActive { get; init; }
}
```

### Error Responses

| Error Code | HTTP Status | Description |
|------------|-------------|-------------|
| `VERSION_NOT_FOUND` | 404 | Version does not exist |

---

## 20. Restore Prompt Version

**Operation**: Restore a previous prompt version by creating a new version with its content (non-destructive rollback).

### Command: RestorePromptVersionCommand

```csharp
public record RestorePromptVersionCommand : IRequest<RestorePromptVersionResult>
{
    public int VersionId { get; init; }  // Version to restore from
}
```

### Response: RestorePromptVersionResult

```csharp
public record RestorePromptVersionResult
{
    public int NewVersionId { get; init; }
    public int NewVersionNumber { get; init; }
    public int RestoredFromVersionNumber { get; init; }
    public DateTime CreatedAt { get; init; }
}
```

### Validation Rules

- `VersionId` must reference an existing prompt version

### Error Responses

| Error Code | HTTP Status | Description |
|------------|-------------|-------------|
| `VERSION_NOT_FOUND` | 404 | Source version does not exist |
| `UNAUTHORIZED` | 403 | User does not have admin permission |

### Business Rules

1. Creates a new version with content copied from the specified version
2. New version becomes the active version
3. Original version remains unchanged (non-destructive)
4. Version number continues sequentially

### Example Request

```json
{
  "versionId": 10
}
```

### Example Response

```json
{
  "newVersionId": 16,
  "newVersionNumber": 16,
  "restoredFromVersionNumber": 10,
  "createdAt": "2026-01-04T16:00:00Z"
}
```

---

## Summary

**Total Contracts**: 20
- 8 Commands (Generate, Update, ApproveFullScan, SaveDiagramGroups, ConfirmRename, AcknowledgeSchemaChanges, CreatePromptVersion, RestorePromptVersion)
- 7 Queries (Export, List, GetFieldAnalysis, GetDiagramGroups, GetSchemaChangeHistory, ListPromptVersions, GetPromptVersion)
- 2 Hybrid Commands (Regenerate, RunFieldAnalysis)
- 3 Export/Detection Queries (ExportHtml, GenerateDiagramGroups, CheckSchemaChanges)

**Key Features**:
- Follows CQRS pattern with MediatR
- Request/Response records defined at file end
- Strong typing with validation rules
- Clear error handling with HTTP status codes
- Pagination support for list operations
- Export supports multiple formats (Markdown, HTML, PDF, JSON)
- Interactive HTML with collapsible sections and Mermaid ERD diagrams
- AI-suggested diagram groups with user customization
- Cached HTML exports with cache invalidation on documentation changes
- Field analysis with sampling, pattern detection, and migration impact estimates
- Per-table full scan approval workflow
- Schema change detection with diff view and AI rename suggestions
- Full schema change history with audit trail
- Prompt template versioning with manual version creation and non-destructive rollback

**Next File**: alert-contracts.md
