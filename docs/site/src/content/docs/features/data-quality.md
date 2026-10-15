---
title: Data Quality
description: Data contracts with scheduled, rule-based checks that produce a weighted quality score, track trends over time, and alert on threshold breaches.
---

Beacon monitors the health of your tables through **data contracts** — scheduled, rule-based checks that produce a weighted quality score, track trends over time, and alert your team when a table drops below its agreed threshold.

## Overview

A **data contract** is an agreement about the expected state of a single table: how fresh its data should be, how many rows it should contain, which columns must be unique or non-null, and so on. Each contract:

- Targets **one table** (schema + table) on one database [data source](/features/data-sources/)
- Contains one or more **rules** (checks), each with its own severity and weight
- Runs on a **cron schedule** and/or on demand
- Produces an **overall score (0–100%)** per evaluation, computed as a severity- and weight-adjusted average of rule scores
- Optionally **alerts recipients** when the score falls below a failure threshold

Every evaluation is stored, so you get a full history per contract plus a per-table latest score with a trend direction (improving / stable / degrading).

:::note
Data contracts apply to **database data sources** (PostgreSQL, SQL Server, MySQL). REST API data sources cannot be targeted by a contract.
:::

A contract's **owner** is the user who created it (an API key's owner, for a key), or the user an Admin made its owner. Only the owner or an Admin can change, disable, retarget, evaluate on demand (**Evaluate now**) or delete it. An Admin or a UserAdmin can give a contract a new owner with **Change owner** on its page (`PUT /beacon/api/data-quality/contracts/{id}/owner` with `{ "userId": <user id> }`); the new owner must be an existing, enabled user who may run saved SQL in a project that links the contract's data source. A user who may not is refused with `409` and a message saying so, because every scheduled evaluation would be skipped. When the owner was changed by someone else (or the contract archived) since it was read, nothing is changed and the call answers `409`; choosing the owner the contract already has changes nothing. Contract lists and details name the owner by display name, or as "User {id}" without one (`hasOwner`, `ownerDisplayName`), never by user name or external id.

A contract belongs to the projects that link its data source: it is visible, evaluated and changed only through such a project the caller may use ([Project access](/features/authorization/#project-access)); in a restricted project an Editor deletes only contracts they own. While Beacon user management is on, a caller without a Beacon user (a host principal, or a bearer token not bound to a user) cannot create a contract (`403`).

**Scheduled evaluations run as the owner.** Right before any rule runs, Beacon checks that the owner, as they are now, may still run saved SQL in a project that links the contract's data source. When the owner may not — disabled or archived, no longer a member of a restricted project, or without the role — the evaluation is skipped: no rule runs, no score, history or alert is written, the job does not fail, and a warning is logged (event 7111 `ScheduledWorkSkipped`, ids only). The contract records the skip until an evaluation runs again (`ownerNotAuthorized` on the contract): its row on the Data Quality page shows **Owner not authorized**, its page a warning with **Change owner** for Admins, the data-source overview counts such contracts (`ownerNotAuthorizedContracts`), and the Control Tower says how many contracts are not being evaluated. A contract without an owner is evaluated only while its data source is openly reachable, and never with `Beacon:Authorization:RequireProjectMembership: true`. **Evaluate now** runs as you, the caller.

:::caution[Upgrading]
The owner is now always the signed-in caller: an `ownerUserId` in the create request is ignored, and a caller without a resolvable user cannot create a contract. Changing, disabling, evaluating on demand (`POST /data-quality/contracts/{id}/evaluate`) or deleting a contract needs its owner or an Admin. A contract created before this version has no owner unless one was supplied, so only an Admin can change it until an Admin sets its owner. To anyone but an Admin, a contract that does not exist is refused (403) like one that is not theirs.
:::

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

**Rules stay on the contract's data source.** The contract's table, a rule's column, and a Referential rule's reference table and column must be objects of the contract's data source as its stored metadata lists them. A name that differs from the metadata only in case is resolved to the metadata's spelling — stored that way when the contract is saved, and used that way when a rule stored earlier is evaluated; a name that matches several names differing only in case (different objects on a case-sensitive server) has to be written exactly. Saving a contract that names anything else answers `400` ("Table … is not in the data source's metadata …", "Column … is not a column of …", "… matches several …"); columns are checked where the metadata lists the table's columns. The generated SQL quotes these names as identifiers of the data source, so a rule cannot reach another schema, database or catalog the data source's login can see. The same check runs on every evaluation: a rule saved before it existed that names an object outside the metadata is recorded as failed and not run. Refresh the data source's metadata first when the table is new. A Custom SQL rule's own SQL is checked by the read-only rules below.

:::note
**Pattern matching is engine-dependent.** On PostgreSQL the pattern is a regular expression (`!~`), on MySQL it uses `NOT REGEXP`, but on SQL Server it is evaluated with `NOT LIKE` — use `LIKE` wildcard syntax (`%`, `_`) for SQL Server sources, not regex.
:::

### Custom SQL rules

A Custom SQL rule runs your query and reads the first row of the result. The query must be a single read-only `SELECT` in the data source's SQL dialect: it is checked when the contract is saved and again on every evaluation, and it runs through the same read-only path as MCP queries (a `READ ONLY` transaction on PostgreSQL and MySQL). Beacon caps the result at one row itself, so the query must not set its own outer `LIMIT`, `TOP` or `FETCH`. It may name only the data source's own database: a name qualified with another database, catalog or project is refused on save and the rule is recorded as failed on evaluation (see [Database names in SQL](/features/authorization/#database-names-in-sql)). Only an **Admin** can create, change or delete a contract that contains a Custom SQL rule, and Custom SQL rules are not available on host-managed data sources. Return these columns:

| Column | Required | Meaning |
|--------|----------|---------|
| `passed` | Yes | `1` = pass, `0` = fail |
| `score` | No | Score 0–100; defaults to 100 on pass, 0 on fail |
| `actual_value` | No | Shown as the observed value in results |
| `message` | No | Shown as the result message |

`actual_value` and `message` reach the evaluation history and the contract's alerts, so a PII column read into them — by a PII name such as `email`, an alias over one (`MAX(email) AS actual_value`) or a project's custom PII pattern — is masked before the result is stored, following the PII detection setting of the projects that link the data source (the strictest applies). `passed` and `score` are read as numbers.

If you already maintain reusable checks as saved [queries](/features/queries/), a Custom SQL rule is the natural way to fold the same logic into a quality score.

:::caution[Upgrading]
Upgrading to this version disables every existing Custom SQL rule (its contract stays enabled). An Admin re-enables a rule by turning it back on and saving the contract, which checks its SQL against the rules above.

A contract with no enabled rules is not evaluated: it keeps its last score, records no history and sends no alert. **Evaluate now** reports that it has no enabled rules, and the scheduled run is skipped.
:::

### Approval of Custom SQL

While the [change approval workflow](/features/queries/#approval-workflow) is on and covers Custom SQL rules
(`ApprovalWorkflow.CoversCustomSqlRules`, on by default — see
[Configuration → Approval Workflow](/getting-started/configuration/#approval-workflow)), the SQL a Custom SQL rule runs
is a reviewed change:

- **A Custom SQL rule that is new, or whose SQL or data source changes, waits for a second person's approval** and is not
  evaluated meanwhile — it is left out like a disabled rule (each evaluation logs a Warning with the contract and rule
  ids), and the contract's other rules keep running. The contract page marks a waiting rule **Awaiting approval**.
- **An evaluation that leaves rules out is partial.** It records how many enabled rules it left out
  (`awaitingApprovalRules` on the evaluation, `isPartial` when there are any), and the contract page marks it
  **Partial** with that count in the evaluation history and the latest results. A partial score says nothing about the
  rules that wait, so it **does not replace the table's score** (the contract's latest score and the table's trend stay
  as they were, so a held rule never improves them silently). For a contract that alerts, a scheduled evaluation whose
  partial score is at or above the failure threshold logs a Warning that the score was partial; one below the threshold
  alerts as usual — the rules that did run failed — and the alert's payload carries the number of rules left out.
- **A contract whose every enabled rule waits is not evaluated** — nothing would be scored. **Evaluate now** answers
  `400` with *"Every enabled rule of this data contract is a Custom SQL rule waiting for approval, so it was not
  evaluated. Approve the change on the Approvals page, or enable a rule that does not wait."*; the scheduled run is
  skipped with a Warning (ids and the count only) instead of failing the job. No history, score or alert is written.
- **The rules that wait form one change request per contract**, submitted with the save (the Admin who saved it is the
  requester). Creating a contract with Custom SQL rules answers with `awaitingApproval: true` and the message
  *"Submitted for approval: the contract's Custom SQL rules are not evaluated until a second person approves them."*,
  which the dialog shows. An Approver (or an Admin) other than the requester reviews the request on the **Approvals**
  page under **Custom SQL rules**, which shows the contract's Custom SQL rules before and after the change, the waiting
  ones marked, and approves exactly that SQL on that data source; a rule changed again since is not approved (`409`).
  Someone without the Approver permission who may read the contract's data source can open the request too (from the
  contract's page or the Approvals page); they cannot decide it.
- **A save rewrites a contract's rules, but an unchanged rule keeps its approval**: a rule whose SQL and data source stay
  the same keeps the state it had (approved, waiting, or ungoverned), so changing a schedule or a threshold submits
  nothing. A rule whose SQL changes keeps the SQL approved for it (by its name; for a rule stored before, the SQL it ran),
  so putting that SQL back needs no approval. A rule renamed and changed in the same save is a new rule: no SQL is
  approved for it. Moving the contract to another data source makes every Custom SQL rule wait again. Removing the rules
  that waited, or putting their approved SQL back, withdraws the requester's own pending request; a change of the
  waiting rules while another person's request waits is refused (`409`).
- **Concurrent saves.** A save that finds the contract's rules gained a Custom SQL rule while it was being saved (another
  save in between) is refused (`409`, *"The contract's rules changed while it was being saved, and a change of its
  Custom SQL rules waits for approval: reload it and save again."*); nothing is written.
- **A rejected change** leaves its rules out of evaluations until they are changed back to approved SQL, removed, or saved
  again (which submits them again). The contract page says **Rejected — change it and save again**, with the reviewer's
  comment (`lastApprovalDecision` on the contract while no request of it is pending).
- **Alerts skip recipients that wait.** A recipient of the contract whose destination waits for approval (see
  [Notifications](/features/notifications/#approval-of-new-and-changed-destinations)) receives no alert; the evaluation
  records how many were skipped (`heldAlertRecipients`), shown with it on the contract page, and an alert that every
  recipient waits for is logged as a Warning instead of being sent.

:::caution[Upgrading]
Custom SQL rules that exist when you upgrade are not governed: they run as before (no approval, no data change), as long
as their SQL and data source stay the same. Once the workflow is on, a new or changed Custom SQL rule waits for a second
person's approval before it runs. A rule saved while the workflow is off is not reviewed later. Turning
`CoversCustomSqlRules` (or the workflow) off again lets the rules that wait — and the rejected ones — be evaluated as
they are; startup warns with their count (see
[Configuration → Approval Workflow](/getting-started/configuration/#approval-workflow)). Evaluations made before this
version record no rule left out and no alert recipient skipped.
:::

### Severity and weight

Each rule carries a **severity** and a **weight** — both feed the scoring math (see below).

| Setting | Values | Effect |
|---------|--------|--------|
| **Severity** | Low, Medium, High, Critical | Multiplies the rule's influence: Low ×1, Medium ×2, High ×3, Critical ×4 |
| **Weight** | 0.1 – 10 (default 1.0) | Fine-grained influence within the same severity |
| **Enabled** | on/off per rule | Disabled rules are skipped entirely and don't affect the score |

## How Evaluations Run

Evaluations run in two ways:

1. **Scheduled** — every enabled contract gets a recurring job (via the host's scheduler) driven by its cron expression. The job is created when you save an enabled contract, updated when you change the schedule, and removed when you disable or delete the contract.
2. **On demand** — click **Evaluate now** on the contract detail page (or `POST /beacon/api/data-quality/contracts/{id}/evaluate`).

During an evaluation, Beacon:

1. Loads the contract and its **enabled** rules
2. Generates engine-specific SQL for each rule and runs it directly against the data source (60-second timeout per rule)
3. Interprets each result into a pass/fail, a rule score, an actual vs. expected value, and a message
4. Computes the overall score, stores the evaluation with all rule results, and updates the table's latest score and trend
5. Sends alert notifications if the score is below the failure threshold (scheduled runs only — see [Alerting](#alerting))

If a single rule errors out (bad configuration, unreachable column, timeout), that rule is recorded as **failed with score 0** and the remaining rules still run — one broken rule never aborts the evaluation.

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

rounded to two decimals. A contract with no enabled rules is not evaluated and keeps its last score. Custom SQL rules
waiting for approval are not part of the score; an evaluation that left any out is
[partial](#approval-of-custom-sql) and does not update the table's score.

**Example:** a contract with a passing Critical freshness rule (weight 1, score 100) and a failing Low volume rule (weight 1, score 0) scores `(100×4 + 0×1) / (4+1) = 80%` — the critical rule dominates.

### Table scores and trends

After every evaluation that is not [partial](#approval-of-custom-sql), the target table's **latest score** is updated (one score per data source + schema + table). Beacon keeps the previous score and derives a trend direction:

| Trend | Condition |
|-------|-----------|
| **Improving** | New score more than 1 point above the previous |
| **Stable** | Within ±1 point |
| **Degrading** | New score more than 1 point below the previous |

## Alerting

Each contract has three alert settings:

| Setting | Default | Description |
|---------|---------|-------------|
| **Alert on failure** | Enabled | Master switch for notifications on this contract |
| **Failure threshold (%)** | 80 | Notifications fire when the overall score drops **below** this value |
| **Recipients** | — | Who gets notified (same recipient list used by [subscriptions](/features/subscriptions/)) |

When a **scheduled** evaluation scores below the threshold, Beacon sends a notification titled `[Data Quality] {contract name}` to every configured recipient. The payload includes the overall score, the threshold, the target table, pass/fail counts, and a detailed breakdown of every failed rule (rule name, score, expected vs. actual value, message).

For delivery channels, see [Notifications](/features/notifications/).

:::note
Manual **Evaluate now** runs record the evaluation and update the score but do not send alert notifications — only scheduled runs alert.
:::

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
- **Evaluations** — evaluation history, newest first (each entry shows the overall score, pass/fail counts, and execution time; a **Partial** marker with the number of Custom SQL rules left out while they wait for approval, and the number of alert recipients skipped while their destination waits)
- **Latest results** — per-rule results from the most recent evaluation: pass/fail pill, rule score, expected vs. actual value, and message

Use **Evaluate now** to trigger an immediate evaluation, or **Edit** / **Delete** to manage the contract. Deleting a contract archives it and removes its scheduled job.

:::caution
**Editing a contract replaces its entire rule set.** Stored per-rule results for the old rules are purged as part of the update (evaluation summaries and their overall scores remain). If you need to preserve detailed rule-level history, export it before editing.
:::

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

Quality scores also surface elsewhere in Beacon: the [MCP server](/features/mcp-server/) exposes a per-project quality report resource, and table-level scores appear in search results and documentation.

## Troubleshooting

**A rule always fails with "Execution failed on the data source."**

The rule's SQL could not run against the source. The server's error text is not shown (or stored), because it can quote row values. Check that the column named in the configuration exists and the data source connection is healthy. Other failures name their cause instead:

- "Invalid rule configuration: ..." — the configuration JSON is incomplete for the rule type.
- "Rule type ... is not supported on ..." — the rule type has no SQL for the data source's engine.
- "Rule SQL was rejected: ..." — the SQL is not a single read-only `SELECT`, or reads something a host-managed source does not expose.
- "Table ... is not in the data source's metadata ..." / "Column ... is not a column of ..." — the contract or rule names a table or column that the data source's metadata does not list (see [Rule Types](#rule-types)); refresh the metadata, or fix the name. "... matches several tables (columns) ... that differ only in case" — write the name exactly as the metadata spells it.
- "Custom SQL must not set its own LIMIT, TOP or FETCH; Beacon reads one row." — remove the outer row limit from the Custom SQL.
- "Rule timed out." — the query ran longer than 60 seconds.
- "Rule result did not have the expected columns or types." — a Custom SQL rule did not return `passed` (and the optional columns) as described above.
- "Query failed on the host database." — any failure on a host-managed data source.

**"Data contract's data source must be a database type" error**

The contract targets a REST API data source. Contracts can only be evaluated against database sources.

**"This data contract has no enabled rules, so it was not evaluated."**

A contract with no *enabled* rules is not evaluated, and its scheduled runs are skipped. Turn at least one rule's **Enabled** toggle on and save the contract.

**No notifications despite a failing score**

Notifications require all three: **Alert on failure** enabled, at least one recipient configured, and the overall score strictly **below** the failure threshold. Also note that manual *Evaluate now* runs never send notifications — only scheduled runs do.

**Pattern rule behaves differently across engines**

PostgreSQL and MySQL treat the pattern as a regular expression; SQL Server uses `LIKE` syntax. Adjust the pattern to the target engine.

**Evaluation history shows fewer entries than expected**

The history endpoint returns the 20 most recent evaluations by default; pass a larger `?take=` value to fetch more. Rule-level details for old rules are also removed when a contract is edited.
