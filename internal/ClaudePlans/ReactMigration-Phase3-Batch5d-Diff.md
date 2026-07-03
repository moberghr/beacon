# Phase 3 Batch 5d — AddDataSourceDialog (multi-engine)

## Scope
React port of `Beacon.UI/Components/Pages/DataSources/AddDataSourceDialog.razor`.
Replaces the `/beacon/data-sources/add` Blazor link with a `StepperDialog` mounted
from `DataSourcesListPage`.

## Files

### Frontend (new)
- `Beacon.SampleProject/web/src/routes/data-sources/AddDataSourceDialog.tsx` —
  three-step dialog (Type → Connection → Test & save). Form schema is a
  `z.discriminatedUnion('kind', …)` covering five branches:

  | Kind | Backend `DataSourceType` | Engine handling |
  |---|---|---|
  | Database | 1 | nested `DatabaseEngineType` (Postgres / MSSQL / MySQL / Azure Synapse / Snowflake) plus connection-string |
  | CloudWatch | 2 | JSON config (region, log groups, optional credentials/profile) |
  | Databricks | 6 | JSON config (host, http path, token, optional catalog/schema) |
  | BigQuery | 7 | JSON config (project id, optional dataset/location, service account JSON) |
  | Api | 8 | JSON config (base URL, OpenAPI URL, auth = none / apiKey / bearer / basic, optional include/exclude path patterns) |

  The non-Database branches serialize their kind-specific fields to JSON
  inside `connectionString` (mirrors the Razor `BuildDataSourceData` switch),
  which is what the existing connector providers expect.

- `Beacon.SampleProject/web/src/routes/data-sources/AddDataSourceDialog.test.tsx` —
  Vitest suite. Asserts (a) switching kind reveals/hides the right fields and
  (b) the Database flow POSTs to `/beacon/api/data-sources` with the expected
  payload.

### Frontend (changed)
- `Beacon.SampleProject/web/src/routes/data-sources/DataSourcesListPage.tsx` —
  the "+ New data source" link is replaced with a button that opens the
  dialog. Toast + cache invalidation come from `useCreateDataSource`.

- `Beacon.SampleProject/web/src/routes/data-sources/queries.ts` (already
  staged from a previous slot) — adds `useCreateDataSource`,
  `useTestDataSourceConnection`, payload types, and the
  `DATA_SOURCE_TYPE` / `DATABASE_ENGINE` constants the dialog relies on.

### Backend (changed/new — already staged from a previous slot)
- `Beacon.Core/Handlers/DataSources/CreateDataSourceHandler.cs` — MediatR
  handler wrapping `IDataSourceService.CreateDataSource`. Throws
  `InvalidOperationException` on failure (per §2.9). `internal sealed class`
  + primary ctor.
- `Beacon.Core/Handlers/DataSources/TestDataSourceConnectionHandler.cs` —
  same shape, wraps `IDataSourceService.TestConnectionAsync`. Returns the
  service result verbatim so the UI can render a Connected/Failed pill.
- `Beacon.SampleProject/Endpoints/DataSourcesEndpoints.cs` — `POST
  /beacon/api/data-sources` (`.WithName("CreateDataSource")`) and `POST
  /beacon/api/data-sources/test-connection`
  (`.WithName("TestDataSourceConnection")`). Names match the contract
  heuristic so `OpenApiContractTests` keeps passing.

## Decisions / deferrals
- The Razor source loads available engines from `ConnectorRegistry` at runtime;
  the React shell hard-codes the five database engines listed in
  `DatabaseEngineType.cs`. If a deployment removes a connector the user can
  still pick its enum value but `Test connection` will surface the failure
  before save — acceptable tradeoff for slot capacity.
- All mutations re-use the existing `IDataSourceService`. The encryption-at-
  rest path (§1.1) is unchanged: the handler hands the plaintext value to the
  service, which encrypts via `Beacon:EncryptionKey` before persisting.
- No fake/seed/demo data introduced (§9.1 / §0.3).
- No Tailwind / shadcn primitives — the dialog uses the project's existing
  `q-*` form classes and the shared `StepperDialog` shell.

## Verification
- `dotnet build --property WarningLevel=0` — succeeded (4 pre-existing NuGet
  warnings, 0 errors).
- `dotnet test Beacon.Tests/Beacon.Tests.csproj` — 35 / 35 passed.
- `npm run build` — clean (existing chunk-size warning unrelated).
- `npm test` (vitest) — 9 / 9 across 6 files. The new AddDataSourceDialog
  suite contributes 2 of those.
- `wwwroot/app/` resynced from `web/dist/`; DSWA cache cleared
  (`*.dswa.cache.json` + `*.Up2Date`) to avoid stale chunk-hash references.
