# Database Explorer + Monaco SQL Autocomplete

Adds a left-side schema/tables panel to `/queries/new` and `/queries/{id}/edit`,
plus Monaco SQL completion driven by live data-source metadata.

## Backend

- New handler `src/Beacon.Core/Handlers/DataSources/GetDataSourceMetadataHandler.cs`
  — wraps `IDatabaseMetadataService.GetMetadataAsync`. `internal sealed class`
  with primary-ctor injection. Throws `InvalidOperationException` for invalid id.
- New endpoint `GET /beacon/api/data-sources/{id:int}/metadata`
  in `src/Beacon.SampleProject/Endpoints/DataSourcesEndpoints.cs` named
  `GetDataSourceMetadata` (matches `OpenApiContractTests` heuristic).
  Returns `DatabaseMetadataSnapshot` directly.
- The first call may be slow (live DB fetch when cache is cold) — UI shows a
  loading state.

## Frontend

- `routes/data-sources/queries.ts`: added DTO types
  (`DatabaseMetadataSnapshot`, `TableMetadataDto`, `ColumnMetadataDto`,
  `IndexMetadataDto`) plus `useDataSourceMetadataQuery(id)` hook
  (`staleTime: 60_000`, disabled when id missing, `retry: false`).
- New `components/ui/DatabaseExplorer.tsx`:
  - Header with table count + filter input.
  - Schema groups (collapsible), tables (`Icon.Box`), columns expand on chevron.
  - Click table → `onInsert("schema.table")`. Click column → `onInsert("col")`.
  - Loading + error states are non-fatal.
- `components/ui/SqlEditor.tsx` extended:
  - New props: `metadata`, `parameterNames`, `crossStepResultCount`,
    `onEditorReady`.
  - SQL completion provider registered **once globally** per language; latest
    snapshot pulled from a module-scope ref so we don't accumulate providers
    across navigations and don't re-register on prop changes.
  - Triggers: ` `, `.`, `{`, `@`.
  - Suggestion logic: `{paramName}`, `@resultN`, `<table>.<column>`, qualified
    table names after `FROM/JOIN/INTO/UPDATE/TABLE`, plus baseline keywords +
    bare table names.
- `routes/queries/QueryEditorPage.tsx` and `routes/queries/NewQueryPage.tsx`:
  each step now renders `<DatabaseExplorer>` to the LEFT of `<SqlEditor>`
  inside `.sql-explorer-shell`. `onInsert` calls `editor.executeEdits` at the
  current cursor through a ref bridged via `onEditorReady`.
- `styles-beacon.css`: added `.sql__column-row` and `.sql-explorer-shell`
  (with `<= 900px` breakpoint stacking explorer on top).

## Tests

- New `components/ui/DatabaseExplorer.test.tsx` (smoke):
  - Renders schema groups + tables from MSW-mocked metadata; clicking a table
    fires `onInsert('public.users')`.
  - Returns `null` when `dataSourceId` is null.
- All 16 vitest tests pass; all 35 `Beacon.Tests` pass (incl. OpenAPI contract
  test that enforces every MediatR handler is exposed via HTTP).

## Deferrals / nuances

- Column quoting nuance (uppercase / non-ASCII) — suggestions insert column
  names plain. Documented as follow-up.
- Engine-specific keyword sets (BigQuery/Snowflake/etc.) — single SQL
  keyword list shared across engines.
- `@result_<step-name>` named-ref suggestions — only numeric `@resultN` is
  suggested; named refs need step-context plumbing.
- Drag-insert from explorer — click-to-insert only.
