---
layout: default
title: Control Tower
parent: Features
nav_order: 12
---

# Control Tower
{: .no_toc }

The Control Tower is Beacon's operations view: a single, auto-refreshing screen that shows the **real-time health of every subscription** — success rates, run and failure counts, open tasks, and anomaly activity — so you can spot failing or silent monitors before your users do.

<details open markdown="block">
  <summary>Table of contents</summary>
  {: .text-delta }
- TOC
{:toc}
</details>

---

## Overview

Every [Subscription](subscriptions) in Beacon runs a query on a schedule. The Control Tower aggregates the execution history of all subscriptions over a selectable time window and answers three operational questions at a glance:

- **Is it working?** — success rate and run/failure counts per subscription, plus a health status (Green / Amber / Red / Stalled)
- **Does it need attention?** — open (unresolved) task counts per subscription
- **Is the data behaving?** — anomaly counts and per-day sparklines from [Anomaly Detection](anomaly-detection)

Open it at **Control Tower** in the left navigation (`/control-tower`).

---

## Health Statuses

Each subscription gets exactly one status, computed from its execution history **within the selected time window** (default: last 30 days).

| Status | Meaning | Rule |
|--------|---------|------|
| **Green** | Healthy | Success rate ≥ **90%** |
| **Amber** | Warning | Success rate ≥ **70%** and < 90% |
| **Red** | Critical | Success rate < **70%** |
| **Stalled** | Silent | **Zero executions** in the window, and the subscription was created more than **1 day** ago |

Two edge cases are worth knowing:

- **New subscriptions stay Green.** A subscription with zero executions that was created within the last day is treated as Green ("waiting for first run"), not Stalled — a 1-day grace period prevents false alarms right after creation.
- **Stalled is independent of the past.** A subscription that ran perfectly last quarter but has zero executions in the selected window shows as Stalled. Silence is a failure mode.

### What counts as a successful execution

The success rate is `successful executions / total executions × 100`, where an execution counts as **successful** if its notification status is one of:

- `NotificationSent` — the query ran and a notification went out
- `NoResults` — the query ran and returned nothing to alert on
- `NotificationSilenced` — the query ran but the notification was deliberately silenced

Every other outcome (`Failed`, `Timeout`, `Created`, `BelowThreshold`) counts against the subscription. The **failed** count shown in the UI is simply `total − successful`.

{: .note }
Both the health list and the KPI statistics use exactly the same status calculation, so the summary cards always agree with the rows below them.

---

## KPI Cards

The top of the page shows six KPI cards, all scoped to the current filters and time window:

| Card | Value |
|------|-------|
| **Total** | Number of subscriptions matching the current filters |
| **Healthy** | Green subscriptions, with the overall success rate (all successes ÷ all executions) as the subtitle |
| **Warning** | Amber subscriptions |
| **Critical** | Red subscriptions |
| **Stalled** | Subscriptions with no runs in the window |
| **Open tasks** | Total unresolved tasks across matching subscriptions, with the total anomaly count in the window as the subtitle |

{: .note }
Statistics are cached server-side for **30 seconds** per unique filter combination, matching the UI's refresh interval. When there are no executions at all, the overall success rate reports 100% rather than 0% — an empty system is not a failing system.

---

## The Health Table

Below the KPIs, every matching subscription appears as a row:

| Column | Content |
|--------|---------|
| **Status pill** | Green / Amber / Red / Stalled |
| **Subscription** | Query name, folder path, plus badges for an assigned AI actor and enabled anomaly detection |
| **Success** | Success rate in the window (— when there are no runs) |
| **Runs** | Total executions, with the failed count highlighted when > 0 |
| **Tasks** | Open (unresolved) task count as a warning pill, or the total task count when nothing is open |
| **Anomalies** | Sparkline of anomaly events per day across the window, with the window total next to it |
| **Last run** | Outcome pill (Sent, Failed, Timeout, No results, …) and relative time of the most recent execution — `never` if it has not run |

The table is paginated at **50 rows per page**.

### Sorting

By default the table sorts **worst-first**: most open tasks first, then lowest success rate, then name — the subscriptions that need attention float to the top. Clicking a column header switches to explicit sorting by name, success rate, runs, open tasks, anomalies, or last run.

### Row detail

Clicking a row opens a detail panel for that subscription showing:

- The query name, folder path, and cron schedule
- **Recent executions** — the last 20 runs with outcome, timestamp, row count, execution time in milliseconds, and the error message for failed runs
- **Open tasks** — up to 20 unresolved tasks with priority, assignee, and snooze state
- **Recent anomalies** — up to 20 anomaly events in the window with severity, detected value, explanation, and acknowledged state

From the panel you can jump straight to the underlying query.

---

## Filtering

All filters combine, and apply to both the KPI cards and the table:

| Filter | Options | Behavior |
|--------|---------|----------|
| **Search** | Free text | Matches against the query name |
| **Folder** | Any query folder | Shows only subscriptions whose query lives in that folder |
| **Health** | All / Green / Amber / Red / Stalled | Shows only subscriptions in that status bucket |
| **Time range** | 24h / 7d / 30d / 90d | The execution window used for all counts, rates, statuses, and sparklines (default: 30d) |
| **Open tasks only** | Checkbox | Shows only subscriptions with at least one unresolved task |

Changing any filter resets the table to the first page. A **Clear** action resets everything back to the defaults.

{: .note }
The time range changes what "healthy" means. A subscription that failed once yesterday might be Red over 24h but Green over 30d. Use a short window for incident triage and a long window for trend review.

---

## Auto-Refresh

The Control Tower is designed to be left open on a screen:

- Data **auto-refreshes every 30 seconds** and also refetches when the browser tab regains focus
- A manual **Refresh** button forces an immediate reload
- If a selected subscription drops out of the refreshed result set (for example, it no longer matches the active filters), its detail panel closes automatically

---

## API

The Control Tower is backed by three read-only endpoints under `/beacon/api/control-tower`, using the same cookie authentication as the rest of the REST API.

| Endpoint | Purpose |
|----------|---------|
| `GET /beacon/api/control-tower/statistics` | Aggregate KPI counts for the current filters |
| `GET /beacon/api/control-tower/health` | Paginated per-subscription health rows |
| `GET /beacon/api/control-tower/subscriptions/{id}/detail` | Executions, open tasks, and anomalies for one subscription |

### Query parameters

Both `/statistics` and `/health` accept the same filter parameters:

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `timeRangeDays` | integer | `30` | Size of the execution window in days |
| `folderId` | integer | — | Restrict to one query folder |
| `dataSourceId` | integer | — | Restrict to one data source |
| `healthStatus` | enum | — | `Green`, `Amber`, `Red`, or `Stalled` |
| `hasUnresolvedTasks` | boolean | — | `true` = only subscriptions with open tasks |
| `searchKeyword` | string | — | Substring match on the query name |

`/health` additionally accepts `page` (default `0`), `pageSize` (default `100`), and `sortBy` (`Name`, `SuccessRate`, `Executions`, `OpenTasks`, `Anomalies`, `LastExecution`, or the default `WorstFirst`).

### Example

Illustrative request and response (values are examples, not real data):

```bash
curl "https://your-beacon-host/beacon/api/control-tower/statistics?timeRangeDays=7&healthStatus=Red" \
  --cookie "Beacon.Auth=..."
```

```json
{
  "totalSubscriptions": 3,
  "healthySubscriptions": 0,
  "warningSubscriptions": 0,
  "criticalSubscriptions": 3,
  "stalledSubscriptions": 0,
  "totalUnresolvedTasks": 5,
  "totalAnomalies30Days": 2,
  "overallSuccessRate": 41.7,
  "timeRangeDays": 7
}
```

---

## Recommended Workflow

1. **Keep the default sort.** Worst-first puts subscriptions with open tasks and low success rates at the top — start there.
2. **Triage Red and Stalled first.** Red means runs are failing; Stalled means nothing is running at all. Click the row and read the last error message in the recent executions list.
3. **Use "open tasks only" for follow-up.** After an incident, filter to subscriptions with unresolved tasks and work the list down.
4. **Watch the sparklines.** A rising anomaly sparkline on a Green subscription means the runs succeed but the data is drifting — see [Anomaly Detection](anomaly-detection).
5. **Widen the window for reviews.** Switch to 90d during a periodic health review to catch slow degradation that a 30d window smooths over.

---

## Related Features

- [Subscriptions](subscriptions) — schedule the queries the Control Tower monitors
- [Tasks](tasks) — the open (unresolved) tasks surfaced in the table and detail panel
- [Anomaly Detection](anomaly-detection) — the source of the anomaly counts and sparklines
- [Notifications](notifications) — the delivery outcomes that feed the success-rate calculation
- [Queries](queries) — the queries behind every subscription
