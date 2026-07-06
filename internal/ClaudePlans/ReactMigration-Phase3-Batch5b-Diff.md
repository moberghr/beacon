# Phase 3 — Batch 5b: SubscriptionDetails

Port of `src/Beacon.UI/Components/Pages/Subscriptions/SubscriptionDetails.razor` (824 LOC)
to React. Read-mostly detail view; create/edit dialogs (multi-step add) deferred to 5c.

## Audit table — Blazor element → data source / endpoint

| Blazor element | Data field | Backend handler / endpoint |
|---|---|---|
| Hero · breadcrumb / id | `Model.SubscriptionId` | `GET /subscriptions/{id}` (new) → `GetSubscriptionDetailQuery` |
| Hero · ACTIVE / ARCHIVED pill | `Model.Status` | same |
| Hero · AI pill | `Model.AiActorId`, `Model.AiActorName` | same |
| Hero · cron description | `_cronDescription` (server-computed `GetCronDescription`) | included in detail DTO |
| Hero · "next at" | `_cronNextAt` (server-computed `GetCronNextAt`) | included in detail DTO |
| Hero action: **Test now** | `JobService.ExecuteQuery(id)` | `POST /subscriptions/{id}/execute` (new) → `TestSubscriptionCommand` |
| Hero action: **Archive** | `Service.DeleteSubscription(id)` | existing `DELETE /subscriptions/{id}` |
| KPI · Total executions | `List.Data.Count` (current page only — buggy in Blazor) | derived from `GET /notifications?subscriptionId=...&pageSize=200` |
| KPI · Notifications sent | sum `Notifications.Count` | derived from same |
| KPI · Recipients | `Model.Recipients.Count` | from detail |
| KPI · Anomaly / Latest results | `Model.AnomalyConfig` / latest `ResultCount` | from detail / notifications |
| Overview tab · Query config (Query, Schedule, MaxRows, MinRows, Timeout) | various fields on `Model` | from detail |
| Overview tab · Notification settings (Trigger, Attachment, ShowQuery, StoreResults, CreateTasks) | various fields on `Model` | from detail |
| Recipients tab · cards | `Model.Recipients` | from detail |
| Recipients tab · Add | `Service.AddRecipients(...)` | `POST /subscriptions/{id}/recipients` (new) → `AddSubscriptionRecipientsCommand` |
| Recipients tab · Remove | `Service.RemoveRecipient(...)` | `DELETE /subscriptions/{id}/recipients/{recipientId}` (new) → `RemoveSubscriptionRecipientCommand` |
| Anomaly tab · config + chart | `Model.AnomalyConfig` + `IAnomalyDetectionService.GetAnomalyChartDataAsync` | `GET /subscriptions/{id}/anomaly-chart?days=30` (new) → `GetSubscriptionAnomalyChartQuery` |
| Execution History tab · grid | `INotificationService.GetQueryExecutionHistory` | existing `GET /notifications?subscriptionId={id}` |

## Backend — new handlers / endpoints

All handlers wrap existing services. No re-implementation, no new migrations.

| Handler | File | Endpoint name |
|---|---|---|
| `GetSubscriptionDetailHandler` | `src/Beacon.Core/Handlers/Subscriptions/GetSubscriptionDetailHandler.cs` | `GetSubscriptionDetail` |
| `TestSubscriptionHandler` | `src/Beacon.Core/Handlers/Subscriptions/TestSubscriptionHandler.cs` | `TestSubscription` |
| `AddSubscriptionRecipientsHandler` | `src/Beacon.Core/Handlers/Subscriptions/AddSubscriptionRecipientsHandler.cs` | `AddSubscriptionRecipients` |
| `RemoveSubscriptionRecipientHandler` | `src/Beacon.Core/Handlers/Subscriptions/RemoveSubscriptionRecipientHandler.cs` | `RemoveSubscriptionRecipient` |
| `GetSubscriptionAnomalyChartHandler` | `src/Beacon.Core/Handlers/Subscriptions/GetSubscriptionAnomalyChartHandler.cs` | `GetSubscriptionAnomalyChart` |

## Frontend — components

```
routes/subscriptions/
  SubscriptionDetailPage.tsx      (lazy route at /subscriptions/:id)
  queries.ts                      (extended with detail / recipients / test / anomaly hooks)
  parts/
    SubscriptionHero.tsx          (eyebrow, title, cron, actions: Test, Archive)
    SubscriptionKpiGrid.tsx       (4 KPI tiles, real data only)
    SubscriptionInfoCard.tsx      (query config + notification settings; two-column row)
    SubscriptionTabsCard.tsx      (Recipients / Anomaly / Executions)
    RecipientsTab.tsx             (cards + add/remove)
    AnomalyTab.tsx                (config + thresholds; chart deferred to inline list/table)
    ExecutionsTab.tsx             (notification history table by subscriptionId)
    RightRail.tsx                 (heuristic suggestions, gated on real conditions)
    SubscriptionSaveBar.tsx       (status / id / counts / Test / Archive)
  SubscriptionDetailPage.test.tsx (smoke + recipients-add MSW test)
```

## Behavioral diff vs Blazor

- **Total executions KPI fixed**: Blazor counted only the current grid page. React fetches `pageSize=200` and exposes `totalCount` from the API.
- **Cron description/next** computed server-side and returned in the detail DTO (Blazor used a client `JSRuntime` round-trip).
- **MudChart anomaly line chart deferred**: replaced with summary cards + recent-detection-events list. Chart proper lands in 5c with a lightweight Recharts wiring.
- **Add Recipient stepper deferred** to 5c. The Add button posts known recipient ids via a simple list dialog wrapping `useRecipientsQuery`. (If 5b runs short on time, the Add button surfaces "Add recipient → coming in 5c" and only Remove is wired — see implementation note.)
- **Permissions**: write/archive gating uses `useAuth().data.canWrite` plus `roles?.includes('Admin')` checks identical to TaskDetail.
- **Realtime hub events**: deferred to a later batch (parity with QueryDetail).

## Deferred (out of scope)

- Multi-step Add Recipient dialog (5c).
- Inline anomaly chart visualization (5c).
- Pause/resume subscription (no backend support today).
- Realtime hub for subscription updates.

