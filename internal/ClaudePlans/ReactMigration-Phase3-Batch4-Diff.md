# React Migration — Phase 3 Batch 4 Behavioral Diff

**Date:** 2026-05-08
**Branch:** `feat/react-phase3`
**Spec:** `ClaudePlans/ReactMigration-Phase3.md` (§ Batch 4)
**Predecessors:** `ClaudePlans/ReactMigration-Phase3-Batch{1,2,3}-Diff.md`

## Goal achieved

Six pages plus the migration's first Mermaid integration. Project detail tabs
(Overview/Repositories/Documentation/AI Actors) are now real, with secret-safe
repository token entry and a Mermaid-aware documentation editor. Subscriptions,
Data sources, Notification detail, Admin settings (admin-gated, write-only
secrets), and per-user Settings all ship with hand-typed fetch wrappers.

## Pages added or completed

| Route | File | Notes |
|---|---|---|
| `/app/projects/:id` | `routes/projects/ProjectDetailPage.tsx` | Tabs filled in. Repositories tab adds `SetRepositoryTokenDialog`. Documentation tab uses `MermaidDiagram` and `EditDocumentationSectionDialog`. AI Actors tab is read-only — links out to Blazor data-source pages until Batch 6. |
| `/app/subscriptions` | `routes/subscriptions/SubscriptionsListPage.tsx` + `AddSubscriptionDialog.tsx` | List with search, delete, single-form add. Multi-step add deferred to Batch 5c per spec. |
| `/app/data-sources` | `routes/data-sources/DataSourcesListPage.tsx` | List + delete. Add and detail intentionally link to Blazor (Batch 5d). |
| `/app/notifications/:id` | `routes/notifications/NotificationDetailPage.tsx` | Notification detail with three info sections and a results table that JSON-parses stored payloads (truncated to 100 rows like the Blazor page). |
| `/app/admin-settings` | `routes/admin-settings/AdminSettingsPage.tsx` | Sectioned form: General, LLM provider, LLM credentials (write-only secrets — empty string keeps existing value), rate-limits + budget. Recent-changes log. |
| `/app/settings` | `routes/settings/SettingsPage.tsx` | Account read-only fields + change-password card (internal users only). |

`MIGRATED_PAGES` now adds: `subscriptions`, `data-sources`, `admin-settings`,
`settings`. Sidebar entries for the first three already existed and now flip
from `/beacon/...` to `/...`.

## Cross-cutting infrastructure

| File | Role |
|---|---|
| `components/ui/MermaidDiagram.tsx` | Lazy-imports `mermaid` on first mount, initializes once with `securityLevel: 'strict'`, theme `neutral`. Renders into a `useId`-derived container; re-renders on `chart` prop change; renders an inline error card when parsing fails. Mermaid lives in its own chunk (`mermaid.core-*.js` ~610 kB raw / 148 kB gzip) — only fetched when a Documentation tab loads. |
| `auth/useAuth.ts` (extended) | `useIsAdmin()` returns `boolean | undefined` (undefined while auth is loading). Used to gate `/admin-settings` at the route level. |

The hand-typed `fetchJson<T>` pattern from Batch 3 is reused for every new
endpoint group because the subagent has no live backend to run `npm run codegen`
against. Each `queries.ts` carries the standard "swap to `beaconApi()` once
codegen runs" comment.

## Backend (D3) handlers + endpoints added

### Subscriptions (`/beacon/api/subscriptions`)

| Handler | Op id | Method |
|---|---|---|
| `GetSubscriptionsHandler` | `GetSubscriptions` | `GET /?search=` |
| `CreateSubscriptionHandler` | `CreateSubscription` | `POST /` |
| `DeleteSubscriptionHandler` | `DeleteSubscription` | `DELETE /{id}` |

### Data sources (`/beacon/api/data-sources`)

| Handler | Op id | Method |
|---|---|---|
| `GetDataSourcesHandler` | `GetDataSources` | `GET /` |
| `DeleteDataSourceHandler` | `DeleteDataSource` | `DELETE /{id}` |

Test-connection handler deferred — none exists in `IDataSourceService` today
and the Blazor list page doesn't expose a test action either.

### Admin settings (`/beacon/api/admin-settings`)

| Handler | Op id | Method |
|---|---|---|
| `GetAdminSettingsHandler` | `GetAdminSettings` | `GET /` |
| `UpdateAdminSettingsHandler` | `UpdateAdminSettings` | `PUT /` |

Both require `BeaconApiEndpoints.AdminPolicyName` (Cookie + `Admin` role) at
the endpoint group. The route is also gated client-side via `useIsAdmin()` to
avoid flashing the form before the 403 lands. `Get` returns booleans for
`LlmApiKeySet` / `LlmEndpointSet` / `LlmSessionTokenSet` instead of the raw
secret values; `Update` accepts `null` for "leave as is" or a string for
"replace" on each secret field. CLAUDE.md §1.1, §1.2, §1.3.

### User settings (`/beacon/api/user-settings`)

| Handler | Op id | Method |
|---|---|---|
| `GetUserSettingsHandler` | `GetUserSettings` | `GET /` |
| `ChangeOwnPasswordHandler` | `ChangeOwnPassword` | `POST /change-password` |

`ChangeOwnPassword` throws `InvalidOperationException` when the user is not
internal or the current password is wrong — surfaces as a 4xx + RFC 7807 to the
React layer.

### Notifications (extended)

`GetNotificationDetailHandler` was added (returns `null` payload → 404 at the
endpoint). Notification list endpoint already existed.

## Project detail dialogs

| File | Behaviour |
|---|---|
| `routes/projects/SetRepositoryTokenDialog.tsx` | Token field is `type=password`, autocomplete off; never echoed back. Sets via `updateRepositoryToken` mutation. |
| `routes/projects/EditDocumentationSectionDialog.tsx` | Markdown/text + optional embedded mermaid. Saves via `updateDocumentationSection`. |
| `routes/projects/DocSectionContent.tsx` | Splits a section body on Mermaid fenced blocks and renders alternating prose / `<MermaidDiagram>` segments. |

## Tests

- New: `routes/subscriptions/AddSubscriptionDialog.test.tsx` — two cases:
  1. Mocks `/beacon/api/recipients` + `POST /beacon/api/subscriptions`, asserts
     payload contains `queryId`, `cronExpression`, `recipientIds`.
  2. Asserts the "Pick at least one recipient" Zod error blocks submit.
- Vitest: `3 → 5 passing`.
- `dotnet test` Beacon.Tests: `35/35 passing`. The `OpenApiContractTests`
  tripwire continues to pass — every new MediatR request has a `WithName(...)`
  endpoint matching the Get/Update/Delete heuristic.

## Forms

- All forms use RHF + Zod 4 (`z.email()`, no `z.string().email()`).
- Numeric fields use `z.number()` + `register('field', { valueAsNumber: true })`.
  `z.coerce.number()` was tried first — it produces `unknown` in the inferred
  RHF resolver type and fails strict typing. Documented in this batch in case
  it bites again.
- AdminSettings empty-string-keeps-secret pattern: the form starts with
  empty strings for `llmApiKey`, `llmEndpoint`, `llmSessionToken`; non-empty
  values are sent as the new secret, empties are sent as `null` (= keep
  existing). Placeholders read "Set — leave blank to keep" or "Not set".

## Deferred / out of scope

| Item | Reason | Tracked for |
|---|---|---|
| Subscription detail page | Heavy MudBlazor flow | Batch 5b |
| Multi-step Add Subscription stepper | Heavy UX | Batch 5c |
| Add Data Source dialog (multi-engine) | Heavy form | Batch 5d |
| Data Source detail | Out of scope here | Batch 5 |
| Notification mark-read / dismiss actions | Blazor side has no such actions either; `MapNotificationActionEndpoints` is a stub | When the action ships server-side |
| AI Actor management from project tab | Blocked on Batch 6 AI Actor page port | Batch 6 |
| User-chip dropdown linking to `/settings` | Sidebar user-chip is decorative today | Cosmetic |

## Files touched

### Backend
- `Beacon.Core/Handlers/Subscriptions/{Get,Create,Delete}SubscriptionHandler.cs`
- `Beacon.Core/Handlers/DataSources/{Get,Delete}DataSourceHandler.cs`
- `Beacon.Core/Handlers/AdminSettings/{Get,Update}AdminSettingsHandler.cs`
- `Beacon.Core/Handlers/UserSettings/{GetUserSettings,ChangeOwnPassword}Handler.cs`
- `Beacon.Core/Handlers/Notifications/GetNotificationDetailHandler.cs`
- `Beacon.SampleProject/Endpoints/{Subscriptions,DataSources,AdminSettings,UserSettings}Endpoints.cs`
- `Beacon.SampleProject/Endpoints/NotificationsEndpoints.cs` (detail + stub)
- `Beacon.SampleProject/Endpoints/BeaconApiEndpoints.cs` (group registrations + `AdminPolicyName`)

### Frontend (new directories)
- `web/src/routes/subscriptions/{queries.ts, SubscriptionsListPage.tsx, AddSubscriptionDialog.tsx, AddSubscriptionDialog.test.tsx}`
- `web/src/routes/data-sources/{queries.ts, DataSourcesListPage.tsx}`
- `web/src/routes/admin-settings/{queries.ts, AdminSettingsPage.tsx}`
- `web/src/routes/settings/{queries.ts, SettingsPage.tsx}`

### Frontend (modified)
- `web/src/App.tsx` — six new lazy routes (incl. `/notifications/:id`).
- `web/src/feature-flags.ts` — `MIGRATED_PAGES += subscriptions, data-sources, admin-settings, settings`.
- `web/src/auth/useAuth.ts` — `useIsAdmin()`.
- `web/src/components/ui/MermaidDiagram.tsx` — new.
- `web/src/routes/projects/{ProjectDetailPage,queries,SetRepositoryTokenDialog,EditDocumentationSectionDialog,DocSectionContent}.tsx` — new dialogs + tab content.
- `web/src/routes/notifications/{NotificationsPage,queries,NotificationDetailPage}.tsx` — row click + detail page.
- `web/package.json` / `package-lock.json` — `mermaid` ^11.

## Acceptance gate

- `dotnet build Beacon.SampleProject -c Release --property WarningLevel=0` — green
- `dotnet test Beacon.Tests -c Release` — 35/35 passing (incl. `OpenApiContractTests`)
- `npm run build` — green
- `npm test` — 5/5 passing (3 → 5)
- `web/dist/` synced into `wwwroot/app/` via the `BuildReactApp` + `StageReactBuild` MSBuild targets (kicked off automatically by the dotnet build above).
