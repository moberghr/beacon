---
layout: default
title: Tasks
parent: Features
nav_order: 13
---

# Tasks
{: .no_toc }

Tasks turn alerting subscription results into **trackable work items**. When a scheduled query finds rows that need attention, Beacon opens a task that stays open — accumulating executions, notifications, and investigation notes — until the underlying condition clears or someone resolves it.

<details open markdown="block">
  <summary>Table of contents</summary>
  {: .text-delta }
- TOC
{:toc}
</details>

---

## Overview

A task represents an **ongoing alert condition** for one subscription. Instead of receiving a separate notification for every scheduled run that finds rows, you get a single task that tracks the condition over time:

- **One open task per subscription** — repeated runs update the existing unresolved task rather than creating duplicates
- **Auto-resolution** — when a run returns 0 rows, the open task is automatically resolved
- **Full context** — each task links back to its subscription, query, execution history, and notifications
- **Team workflow** — priority, assignee, watchers, snooze, SLA tracking, and an investigation log

Tasks complement [Notifications](notifications): notifications tell you *something happened*, tasks track *whether it's been dealt with*.

---

## Enabling Task Creation

Tasks are created by subscriptions with the **Create Tasks** option enabled.

1. Create or edit a [subscription](subscriptions)
2. Enable **Create Tasks**
3. Save

On every scheduled (or manual) execution of that subscription, Beacon records the run in execution history and then creates or updates the task for that subscription — regardless of whether a notification was sent.

{: .note }
> When **Create Tasks** is enabled, recipients become optional on the subscription. A task-only subscription is valid: the task queue itself is the alerting surface. Without Create Tasks, at least one recipient is required.

---

## Task Lifecycle

### Creation

When a subscription run with Create Tasks enabled completes:

| Run result | Existing unresolved task? | What happens |
|------------|---------------------------|--------------|
| **> 0 rows** | No | A new unresolved task is created with the run's result count |
| **> 0 rows** | Yes | The existing task is updated — latest result count and last-notification timestamp refresh |
| **0 rows** | No | Nothing — there is no condition to alert on, so no task is created |
| **0 rows** | Yes | The task is **auto-resolved** |

This means the task list only ever shows at most one unresolved task per subscription, and that task always reflects the latest state of the alert condition.

### Auto-Resolution

When a run returns **0 rows** while a task is open, Beacon resolves it automatically and stamps the resolution notes with:

```
Auto-resolved: Query returned 0 results
```

The resolved timestamp is set to the time of the run. No user action is required — if the data problem fixed itself (or someone fixed it upstream), the task closes itself.

{: .note }
> Auto-resolved tasks stay in the task history with their resolution notes intact, so you can always see when a condition cleared and how long it was open.

### Manual Resolution

Any open task can be resolved manually from the task detail page:

1. Open the task and click **Resolve** (or press `R`)
2. Optionally add **resolution notes** (up to 2000 characters) describing what was done
3. Confirm

The task records who resolved it, when, and the notes. If the condition recurs on a later run, a **new** task is created — the resolved task is not reopened, so each incident keeps its own history.

---

## Task Data

Each task carries:

| Field | Description |
|-------|-------------|
| **Latest result count** | Row count from the most recent run — the current "size" of the alert condition |
| **Executions** | Number of subscription executions since the task was created |
| **Unique counts** | Number of *distinct* result counts seen since the task was created — a volatility signal |
| **Notifications** | Notifications sent while this task was open |
| **Priority** | `Critical`, `High`, `Normal` (default), or `Low` |
| **Assignee** | The user investigating the task (unassigned by default) |
| **Snoozed until** | When set in the future, the task is considered snoozed until that time |
| **Watchers** | Users following the task |
| **Resolution** | Resolved flag, timestamp, resolving user, and resolution notes |

**Reading executions vs. unique counts:** a task with 40 executions and 1 unique count has returned the same result count on every run — a stable condition. A task with 40 executions and 25 unique counts is fluctuating on nearly every run — likely a moving or growing problem.

---

## Tasks Page

Open **Tasks** in the left navigation (`/tasks`).

### List

The list shows one row per task with: id, created (relative time), subscription and query name, latest result count, execution count, unique result counts, and status (**Unresolved** / **Resolved**). Results are paginated (25 per page). Click any row to open the detail view.

### Filters

Filter by status: **Unresolved** (default), **Resolved**, or **All**.

---

## Task Detail

Clicking a task opens the detail view (`/tasks/{id}`) with:

### Header & SLA

- **Hero** — task identity, age, and a Resolve action
- **SLA banner** — shows time remaining against the SLA. The SLA comes from the subscription's **SLA hours** setting; if the subscription doesn't define one, a 24-hour default is used for display. For resolved tasks, the banner shows who resolved it and when.
- **KPI grid** — latest result count, execution count, notification count, and task age at a glance

### Result Trend Chart

A chart of the subscription's result counts over the most recent 100 executions. Use it to see whether the condition is growing, shrinking, stable, or oscillating.

### Tabs

| Tab | Contents |
|-----|----------|
| **Activity** | Timeline of task events — creation, executions, comments, resolution |
| **Executions** | The last 50 subscription executions with timestamp, duration, notification status, and result count |
| **Notifications** | Notifications sent for this subscription while the task was open, with their result payloads |
| **Related** | Other tasks from subscriptions that use the **same query** — the last 20, including archived ones — so you can see how previous incidents of the same condition played out |

### Investigation Log (Comments)

Every task has a comment thread. Add notes as you investigate — findings, hypotheses, links to fixes. Comments record the author and timestamp and are shown newest first. They persist after resolution, so the next person hitting the same condition can read the history (the **Related** tab gets them there).

### Assignment, Watching, Snooze, Priority

From the detail page's side rail and actions:

- **Assign** — assign the task to a user (or yourself) to signal ownership
- **Watch / Unwatch** — follow a task you're not assigned to; the watcher count is visible on the task
- **Snooze** — suppress the task until a chosen time (useful when a fix is deployed but the next run hasn't confirmed it yet)
- **Priority** — set `Critical`, `High`, `Normal`, or `Low`

### Keyboard Shortcuts

On the task detail page (when not typing in a field):

| Key | Action |
|-----|--------|
| `R` | Open the Resolve dialog |
| `A` | Assign the task to yourself |
| `S` | Snooze for 1 hour |
| `C` | Focus the comment box |

---

## API

Tasks are available under `/beacon/api/tasks` (cookie or API-key authentication):

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/tasks` | List tasks — filter by `subscriptionId`, `resolved`; supports sorting and paging |
| `GET` | `/tasks/{id}` | Task detail |
| `POST` | `/tasks/{id}/resolve` | Resolve with optional `resolutionNotes` |
| `GET` | `/tasks/{id}/executions` | Recent executions for the task's subscription |
| `GET` | `/tasks/{id}/related` | Tasks from other subscriptions on the same query |
| `GET` | `/tasks/{id}/result-history` | Result-count data points for the trend chart |
| `GET` | `/tasks/{id}/comments` | Comment thread |
| `POST` | `/tasks/{id}/comments` | Add a comment |
| `POST` | `/tasks/{id}/assign` | Set (or clear) the assignee |
| `POST` | `/tasks/{id}/snooze` | Set (or clear) the snooze time |
| `POST` | `/tasks/{id}/priority` | Set priority |
| `POST` | `/tasks/{id}/watch` / `/unwatch` | Follow / unfollow the task |

---

## Example: Monitoring Failed Payments

**Subscription:**
- Query: `SELECT * FROM payments WHERE status = 'failed' AND created_at > NOW() - INTERVAL '1 hour'`
- Cron: `*/15 * * * *` (every 15 minutes)
- Create Tasks: ✓ enabled

**What happens:**

1. A run finds 12 failed payments → task opens with latest result count 12
2. The next runs find 12, 15, 18 → the same task updates; executions climb, unique counts show it's growing
3. An engineer assigns themselves (`A`), sets priority to High, and logs findings in the investigation log
4. After the payment provider recovers, a run returns 0 rows → the task auto-resolves with `Auto-resolved: Query returned 0 results`
5. If failures reappear next week, a **new** task opens — and the old one is one click away in the Related tab

{: .warning }
> Auto-resolution keys off a **0-row** run. If your query can legitimately return 0 rows while the underlying problem persists (for example, a time-windowed query during a quiet period), the task will close and a fresh one will open when rows reappear. Design the query window with that in mind.

---

## Troubleshooting

**No task appeared after the subscription ran**
: Check that **Create Tasks** is enabled on the subscription, and that the run actually returned rows — 0-row runs never open a new task.

**A second task didn't open while one is already unresolved**
: Expected. Beacon maintains at most one unresolved task per subscription; the existing task's latest result count and timestamps update instead.

**Task closed on its own**
: A run returned 0 rows and auto-resolution fired. The resolution notes will read `Auto-resolved: Query returned 0 results`.

**Execution count on the list differs from what I expected**
: The list counts executions **since the task was created**, not the subscription's lifetime executions.

---

## Related Documentation

- [Subscriptions](subscriptions) — schedule the queries that create tasks
- [Control Tower](control-tower) — see open task counts across all subscriptions at a glance
- [Notifications](notifications) — delivery channels for the same subscription runs
- [Anomaly Detection](anomaly-detection) — alert on unusual result-count patterns instead of fixed conditions
