---
layout: default
title: Data Quality
parent: Features
nav_order: 14
---

# Data Quality
{: .no_toc }

Beacon monitors the health of your tables through **data contracts** — scheduled, rule-based checks that produce a weighted quality score, track trends over time, and alert your team when a table drops below its agreed threshold.

<details open markdown="block">
  <summary>Table of contents</summary>
  {: .text-delta }
- TOC
{:toc}
</details>

---

## Overview

A **data contract** is an agreement about the expected state of a single table: how fresh its data should be, how many rows it should contain, which columns must be unique or non-null, and so on. Each contract:

- Targets **one table** (schema + table) on one database [data source](data-sources)
- Contains one or more **rules** (checks), each with its own severity and weight
- Runs on a **cron schedule** and/or on demand
- Produces an **overall score (0–100%)** per evaluation, computed as a severity- and weight-adjusted average of rule scores
- Optionally **alerts recipients** when the score falls below a failure threshold

Every evaluation is stored, so you get a full history per contract plus a per-table latest score with a trend direction (improving / stable / degrading).

{: .note }
> Data contracts apply to **database data sources** (PostgreSQL, SQL Server, MySQL). REST API data sources cannot be targeted by a contract.

---

## Rule Types

Each rule has a **type**, an optional **column**, and a **configuration** (JSON). The table below lists all supported types with example configurations.

| Rule Type | What it checks | Example configuration |
|-----------|----------------|-----------------------|
| **Freshness** | The newest value in a timestamp column is no older than a maximum age | `{"column": "updated_at", "maxAgeMinutes": 60}` |
| **Volume** | Row count is within an expected range (`minRows`, `maxRows`, or both) | `{"minRows": 100}` |
| **Null rate** | Percentage of NULLs in a column stays at or below a threshold | `{"column": "email", "maxNullPercent": 5}` |
| **Uniqueness** | A column contains no duplicate values | `{"column": "id"}` |
| **Referential** | Every non-null value in a column exists in a reference table (no orphans) | `{"column": "user_id", "referenceTable": "users", "referenceColumn": "id"}` |
| **Range** | Numeric values stay between `min` and `max` (either bound optional) | `{"column": "age", "min": "0", "max": "150"}` |
| **Pattern** | All values match a pattern | `{"column": "email", "pattern": "^[^@]+@[^@]+$"}` |
| **Custom SQL** | Any check you can express as SQL | `{"sql": "SELECT CASE WHEN COUNT(*) > 0 THEN 1 ELSE 0 END AS passed FROM ..."}` |

You do not need to include `schema` or `table` in the configuration — Beacon injects them automatically from the contract's target table.

**Referential rules** default the reference table to the contract's schema; add `"referenceSchema": "..."` to point at a different schema.

{: .note }
> **Pattern matching is engine-dependent.** On PostgreSQL the pattern is a regular expression (`!~`), on MySQL it uses `NOT REGEXP`, but on SQL Server it is evaluated with `NOT LIKE` — use `LIKE` wildcard syntax (`%`, `_`) for SQL Server sources, not regex.

### Custom SQL rules

A Custom SQL rule runs your query verbatim and reads the first row of the result. Return these columns:

| Column | Required | Meaning |
|--------|----------|---------|
| `passed` | Yes | `1` = pass, `0` = fail |
| `score` | No | Score 0–100; defaults to 100 on pass, 0 on fail |
| `actual_value` | No | Shown as the observed value in results |
| `message` | No | Shown as the result message |

If you already maintain reusable checks as saved [queries](queries), a Custom SQL rule is the natural way to fold the same logic into a quality score.

### Severity and weight

Each rule carries a **severity** and a **weight** — both feed the scoring math (see below).

| Setting | Values | Effect |
|---------|--------|--------|
| **Severity** | Low, Medium, High, Critical | Multiplies the rule's influence: Low ×1, Medium ×2, High ×3, Critical ×4 |
| **Weight** | 0.1 – 10 (default 1.0) | Fine-grained influence within the same severity |
| **Enabled** | on/off per rule | Disabled rules are skipped entirely and don't affect the score |

---

## How Evaluations Run

Evaluations run in two ways:

1. **Scheduled** — every enabled contract gets a Hangfire recurring job driven by its cron expression. The job is created when you save an enabled contract, updated when you change the schedule, and removed when you disable or delete the contract.
2. **On demand** — click **Evaluate now** on the contract detail page (or `POST /beacon/api/data-quality/contracts/{id}/evaluate`).

During an evaluation, Beacon:

1. Loads the contract and its **enabled** rules
2. Generates engine-specific SQL for each rule and runs it directly against the data source (60-second timeout per rule)
3. Interprets each result into a pass/fail, a rule score, an actual vs. expected value, and a message
4. Computes the overall score, stores the evaluation with all rule results, and updates the table's latest score and trend
5. Sends alert notifications if the score is below the failure threshold (scheduled runs only — see [Alerting](#alerting))

If a single rule errors out (bad configuration, unreachable column, timeout), that rule is recorded as **failed with score 0** and the remaining rules still run — one broken rule never aborts the evaluation.

---

## Scoring Methodology

### Per-rule score

Most rule types are binary — **100 if the check passes, 0 if it fails**. Two types score proportionally:

| Rule Type | Score on failure |
|-----------|------------------|
| **Null rate** | `max(0, 100 − (actualNullPercent − maxNullPercent))` — small overruns cost little, large overruns cost a lot |
| **Range** | `(1 − outOfRangeRows / totalRows) × 100` — proportional to how many rows violate the range |
| **Custom SQL** | Whatever your query returns in `score` (defaults to 100/0) |

### Overall score

The overall score is a **weighted average** of rule scores, where each rule's effective weight is:

```
effectiveWeight = ruleWeight × severityMultiplier
```

with severity multipliers Critical = 4, High = 3, Medium = 2, Low = 1. Then:

```
overallScore = Σ(ruleScore × effectiveWeight) / Σ(effectiveWeight)
```

rounded to two decimals. A contract with no enabled rules scores 100.

**Example:** a contract with a passing Critical freshness rule (weight 1, score 100) and a failing Low volume rule (weight 1, score 0) scores `(100×4 + 0×1) / (4+1) = 80%` — the critical rule dominates.

### Table scores and trends

After every evaluation, the target table's **latest score** is updated (one score per data source + schema + table). Beacon keeps the previous score and derives a trend direction:

| Trend | Condition |
|-------|-----------|
| **Improving** | New score more than 1 point above the previous |
| **Stable** | Within ±1 point |
| **Degrading** | New score more than 1 point below the previous |

---

## Alerting

Each contract has three alert settings:

| Setting | Default | Description |
|---------|---------|-------------|
| **Alert on failure** | Enabled | Master switch for notifications on this contract |
| **Failure threshold (%)** | 80 | Notifications fire when the overall score drops **below** this value |
| **Recipients** | — | Who gets notified (same recipient list used by [subscriptions](subscriptions)) |

When a **scheduled** evaluation scores below the threshold, Beacon sends a notification titled `[Data Quality] {contract name}` to every configured recipient. The payload includes the overall score, the threshold, the target table, pass/fail counts, and a detailed breakdown of every failed rule (rule name, score, expected vs. actual value, message).

📚 [Learn more about delivery channels →](notifications)

{: .note }
> Manual **Evaluate now** runs record the evaluation and update the score but do not send alert notifications — only scheduled runs alert.

---

## Using the UI

### Data Quality page (`/data-quality`)

Click **Data Quality** in the left navigation. The page shows:

- **Per-data-source overview cards** — average score, table count, healthy tables (score ≥ 80), degrading tables, and active contract count, plus per-table scores
- **Contracts list** — each contract with its target table, latest score, enabled/disabled state, and **View / Edit / Delete** actions

### Creating a contract

1. Click **New contract**
2. Fill in the details:

| Field | Description | Example |
|-------|-------------|---------|
| **Contract name** | Descriptive name | `Orders freshness & volume` |
| **Data source** | Database source to check | Select from dropdown |
| **Schema / Table name** | The target table | `public` / `orders` |
| **Cron expression** | Evaluation schedule | `0 */6 * * *` (every 6 hours) |
| **Failure threshold (%)** | Alert cutoff | `80` |
| **Enabled** | Whether the schedule is active | ✓ |
| **Alert on failure** | Send notifications below threshold | ✓ |
| **Notification recipients** | Who gets alerted | Select recipients |

3. Add one or more **rules** — for each: name, type, optional column, severity, weight, and the configuration JSON (the dialog shows a type-specific example as a hint)
4. Click **Save** — if the contract is enabled, the recurring evaluation job is scheduled immediately

### Contract detail page (`/data-quality/{id}`)

The detail page shows four KPIs — **latest score**, **rule count**, **schedule**, and **failure threshold** — plus three tabs:

- **Rules** — the configured rule set
- **Evaluations** — evaluation history, newest first (each entry shows the overall score, pass/fail counts, and execution time)
- **Latest results** — per-rule results from the most recent evaluation: pass/fail pill, rule score, expected vs. actual value, and message

Use **Evaluate now** to trigger an immediate evaluation, or **Edit** / **Delete** to manage the contract. Deleting a contract archives it and removes its scheduled job.

{: .warning }
> **Editing a contract replaces its entire rule set.** Stored per-rule results for the old rules are purged as part of the update (evaluation summaries and their overall scores remain). If you need to preserve detailed rule-level history, export it before editing.

---

## API Endpoints

All endpoints live under `/beacon/api/data-quality` and require authentication.

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/data-quality/overview` | Per-data-source quality overview (optional `?dataSourceId=`) |
| `GET` | `/data-quality/contracts` | List contracts (optional `?dataSourceId=`) |
| `GET` | `/data-quality/contracts/{id}` | Contract detail with rules and latest score |
| `POST` | `/data-quality/contracts` | Create a contract |
| `PUT` | `/data-quality/contracts/{id}` | Update a contract (replaces rules and recipients) |
| `DELETE` | `/data-quality/contracts/{id}` | Archive a contract and unschedule it |
| `GET` | `/data-quality/contracts/{id}/evaluations` | Evaluation history (optional `?take=`, default 20) |
| `POST` | `/data-quality/contracts/{id}/evaluate` | Run an evaluation immediately |

Quality scores also surface elsewhere in Beacon: the [MCP server](mcp-server) exposes a per-project quality report resource, and table-level scores appear in search results and documentation.

---

## Troubleshooting

**A rule always fails with "Execution failed: ..."**
: The generated SQL could not run against the source. Check that the column named in the configuration exists, the configuration JSON has the required properties for the rule type, and the data source connection is healthy.

**"Data contract's data source must be a database type" error**
: The contract targets a REST API data source. Contracts can only be evaluated against database sources.

**Score is 100% but I expected checks to run**
: A contract with no *enabled* rules scores 100 by definition. Verify that at least one rule has its **Enabled** toggle on.

**No notifications despite a failing score**
: Notifications require all three: **Alert on failure** enabled, at least one recipient configured, and the overall score strictly **below** the failure threshold. Also note that manual *Evaluate now* runs never send notifications — only scheduled runs do.

**Pattern rule behaves differently across engines**
: PostgreSQL and MySQL treat the pattern as a regular expression; SQL Server uses `LIKE` syntax. Adjust the pattern to the target engine.

**Evaluation history shows fewer entries than expected**
: The history endpoint returns the 20 most recent evaluations by default; pass a larger `?take=` value to fetch more. Rule-level details for old rules are also removed when a contract is edited.
