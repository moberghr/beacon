---
title: Data Quality
description: Data contracts with scheduled, rule-based checks that produce a weighted quality score, track trends over time, and alert on threshold breaches.
---

Beacon monitors the health of your tables through **data contracts**: scheduled, rule-based checks that produce a
weighted quality score, track trends over time, and alert your team when a table drops below its agreed threshold.

## Overview

A **data contract** is an agreement about the expected state of one table: how fresh its data should be, how many rows
it should hold, which columns must be unique or non-null, and so on. Each contract:

- targets **one table** (schema and table) on one database [data source](/features/data-sources/);
- contains one or more **rules**, each with its own severity and weight;
- runs on a **cron schedule** and on demand;
- produces an **overall score (0–100%)** per evaluation, a severity- and weight-adjusted average of the rule scores;
- optionally **alerts recipients** when the score falls below a failure threshold.

Every evaluation is stored, so each contract has a full history, and each table has a latest score with a trend
(improving, stable, degrading).

:::note
Data contracts evaluate on **PostgreSQL, SQL Server and MySQL** data sources. On other database engines their rules
fail with "Rule type … is not supported on …". A contract on a non-database data source (a REST API, for example)
fails with "Data contract's data source must be a database type".
:::

## Ownership and access

A contract's **owner** is the user who created it (an API key's owner, for a key), or the user it was handed to. Only
the owner or an Admin can change, disable, retarget, evaluate on demand (**Evaluate now**) or delete it. To anyone but an
Admin, a contract that does not exist is refused (`403`) like one that is not theirs.

An Admin or a UserAdmin gives a contract a new owner with **Change owner** on its page
(`PUT /beacon/api/data-quality/contracts/{id}/owner` with `{ "userId": <user id> }`). The new owner must be an existing,
enabled user who may run saved SQL in a project that links the contract's data source; otherwise the request is refused
(`409`) with a message saying so, because every scheduled evaluation would be skipped. If someone else changed the owner,
or archived the contract, since it was read, nothing is changed and the call answers `409`; choosing the owner the
contract already has changes nothing. Contract lists and details name the owner by display name, or as "User {id}"
(`hasOwner`, `ownerDisplayName`), never by user name or external id.

A contract belongs to the projects that link its data source. It is listed, evaluated and changed only through such a
project the caller may use (see [Security model](/security/)); in a restricted project an Editor deletes
only contracts they own. While Beacon user management is on, a caller without a Beacon user (a host principal, or a bearer
token not bound to a user) cannot create a contract (`403`).

### Scheduled evaluations run as the owner

Right before any rule runs, Beacon checks that the owner, as they are now, may still run saved SQL in a project that
links the contract's data source. When the owner may not (disabled or archived, no longer a member of a restricted
project, or without the role), the evaluation is skipped:

- no rule runs, and no score, history or alert is written;
- the job does not fail, and a Warning is logged (event 7111 `ScheduledWorkSkipped`, ids only);
- the contract records the skip until an evaluation runs again (`ownerNotAuthorized`): its row on the Data Quality page
  shows **Owner not authorized**, its page shows a warning with **Change owner**, the overview counts such contracts
  (**Skipped (owner)**, `ownerNotAuthorizedContracts`), and the [Control Tower](/features/control-tower/) says how many
  contracts are not evaluated.

A contract without an owner is evaluated only while its data source is openly reachable, and never with
`Beacon:Authorization:RequireProjectMembership: true`. **Evaluate now** runs as you, the caller.

## Rule types

Each rule has a **type**, an optional **column** and a **configuration** (JSON).

| Rule type | What it checks | Example configuration |
|-----------|----------------|-----------------------|
| **Freshness** | The newest value in a timestamp column is no older than a maximum age | `{"column": "updated_at", "maxAgeMinutes": 60}` |
| **Volume** | Row count is within an expected range (`minRows`, `maxRows`, or both) | `{"minRows": 100}` |
| **Null rate** | Percentage of NULLs in a column stays at or below a threshold | `{"column": "email", "maxNullPercent": 5}` |
| **Uniqueness** | A column contains no duplicate values | `{"column": "id"}` |
| **Referential** | Every non-null value in a column exists in a reference table (no orphans) | `{"column": "user_id", "referenceTable": "users", "referenceColumn": "id"}` |
| **Range** | Numeric values stay between `min` and `max` (either bound optional) | `{"column": "age", "min": "0", "max": "150"}` |
| **Pattern** | All values match a pattern | `{"column": "email", "pattern": "^[^@]+@[^@]+$"}` |
| **Custom SQL** | Any check you can express as SQL | `{"sql": "SELECT CASE WHEN COUNT(*) > 0 THEN 1 ELSE 0 END AS passed FROM ..."}` |

You do not need `schema` or `table` in the configuration: Beacon takes them from the contract's target table.
**Referential** rules default the reference table to the contract's schema; add `"referenceSchema": "..."` for another
schema.

**Rules stay on the contract's data source.** The contract's table, a rule's column, and a Referential rule's reference
table and column must be objects of the contract's data source as its stored metadata lists them.

- A name that differs from the metadata only in case is resolved to the metadata's spelling, both when the contract is
  saved and when a rule stored earlier is evaluated. A name that matches several names differing only in case (different
  objects on a case-sensitive server) has to be written exactly.
- Saving a contract that names anything else answers `400` ("Table … is not in the data source's metadata …",
  "Column … is not a column of …", "… matches several …"). Columns are checked where the metadata lists the table's
  columns.
- The generated SQL quotes these names as identifiers of the data source, so a rule cannot reach another schema, database
  or catalog the data source's login can see.
- The same check runs on every evaluation: a rule that names an object outside the metadata is recorded as failed and not
  run. Refresh the data source's metadata first when the table is new.

:::note[Pattern matching depends on the engine]
On PostgreSQL the pattern is a regular expression (`!~`), on MySQL it uses `NOT REGEXP`, and on SQL Server it is
evaluated with `NOT LIKE`. Use `LIKE` wildcards (`%`, `_`) for SQL Server sources, not regex.
:::

### Custom SQL rules

A Custom SQL rule runs your query and reads the first row of the result.

- The query must be a single read-only `SELECT` in the data source's SQL dialect. It is checked when the contract is saved
  and again on every evaluation, and runs through the same read-only path as MCP queries (a `READ ONLY` transaction on
  PostgreSQL and MySQL).
- Beacon caps the result at one row itself, so the query must not set its own outer `LIMIT`, `TOP` or `FETCH`.
- It may name only the data source's own database: a name qualified with another database, catalog or project is refused
  on save, and the rule is recorded as failed on evaluation.
- Only an **Admin** can create, change or delete a contract that contains a Custom SQL rule.
- Custom SQL rules are not available on host-managed data sources.

Return these columns:

| Column | Required | Meaning |
|--------|----------|---------|
| `passed` | Yes | `1` = pass, `0` = fail |
| `score` | No | Score 0–100; defaults to 100 on pass, 0 on fail |
| `actual_value` | No | Shown as the observed value in results |
| `message` | No | Shown as the result message |

`actual_value` and `message` reach the evaluation history and the contract's alerts. A PII column read into them (by a PII
name such as `email`, an alias over one such as `MAX(email) AS actual_value`, or a project's custom PII pattern) is masked
before the result is stored, following the PII detection setting of the projects that link the data source (the
strictest applies). `passed` and `score` are read as numbers.

If you keep reusable checks as saved [queries](/features/queries/), a Custom SQL rule folds the same logic into a quality
score.

### Approval of Custom SQL

The [approval workflow](/security/approvals/) is off by default; a host turns it on in code with
`options.EnableApprovalWorkflow(...)`. While it is on, its `CoversCustomSqlRules` switch (on by default) makes the SQL a
Custom SQL rule runs a reviewed change:

- **A Custom SQL rule that is new, or whose SQL or data source changes, waits for a second person's approval** and is not
  evaluated meanwhile. It is left out like a disabled rule (each evaluation logs a Warning with the contract and rule
  ids), and the contract's other rules keep running. The contract page marks a waiting rule **Awaiting approval**.
- **An evaluation that leaves rules out is partial.** It records how many enabled rules it left out
  (`awaitingApprovalRules`, `isPartial`), and the contract page marks it **Partial** with that count. A partial score
  **does not replace the table's score**: the contract's latest score and the table's trend stay as they were, so a held
  rule never improves them silently. For a contract that alerts, a scheduled partial evaluation at or above the failure
  threshold logs a Warning; one below the threshold alerts as usual, and the alert's payload carries the number of rules
  left out.
- **A contract whose every enabled rule waits is not evaluated.** **Evaluate now** answers `400` with *"Every enabled rule
  of this data contract is a Custom SQL rule waiting for approval, so it was not evaluated. Approve the change on the
  Approvals page, or enable a rule that does not wait."*; the scheduled run is skipped with a Warning. No history, score
  or alert is written.
- **The waiting rules form one change request per contract**, submitted with the save (the Admin who saved it is the
  requester). Creating a contract with Custom SQL rules answers with `awaitingApproval: true` and the message
  *"Submitted for approval: the contract's Custom SQL rules are not evaluated until a second person approves them."*
- **An Approver (or an Admin) other than the requester reviews it** on the **Approvals** page under **Custom SQL rules**,
  which shows the contract's Custom SQL rules before and after the change, and approves exactly that SQL on that data
  source; a rule changed again since is not approved (`409`). Someone without the Approver permission who may read the
  contract's data source can open the request, but cannot decide it.
- **An unchanged rule keeps its approval.** A save rewrites the rules, but a rule whose SQL and data source stay the same
  keeps its state (approved, waiting or ungoverned), so changing a schedule or a threshold submits nothing. A rule whose
  SQL changes keeps the SQL approved for it (by its name), so putting that SQL back needs no approval. A rule renamed and
  changed in the same save is a new rule. Moving the contract to another data source makes every Custom SQL rule wait
  again. Removing the waiting rules, or putting their approved SQL back, withdraws the requester's own pending request; a
  change of the waiting rules while another person's request waits is refused (`409`).
- **Concurrent saves.** A save that finds the contract gained a Custom SQL rule while it was being saved is refused
  (`409`, *"The contract's rules changed while it was being saved, and a change of its Custom SQL rules waits for
  approval: reload it and save again."*); nothing is written.
- **A rejected change** leaves its rules out of evaluations until they are changed back to approved SQL, removed, or saved
  again (which submits them again). The contract page shows the rejection with the reviewer's comment
  (`lastApprovalDecision` while no request is pending).
- **Alerts skip recipients that wait.** A recipient whose destination waits for approval (see
  [Notifications](/features/notifications/)) receives no alert. The evaluation records how many were skipped
  (`heldAlertRecipients`), and an alert that every recipient waits for is logged as a Warning instead of being sent.

:::caution[Upgrading]
Custom SQL rules that exist when the workflow is turned on are not reviewed: they run as before, as long as their SQL and
data source stay the same. A rule saved while the workflow is off is not reviewed later. Turning `CoversCustomSqlRules`
(or the workflow) off again lets waiting and rejected rules be evaluated as they are; startup logs their count.
:::

### Severity and weight

| Setting | Values | Effect |
|---------|--------|--------|
| **Severity** | Low, Medium, High, Critical | Multiplies the rule's influence: Low ×1, Medium ×2, High ×3, Critical ×4 |
| **Weight** | The dialog accepts 0.1–10 (default 1.0) | Fine-grained influence within the same severity |
| **Enabled** | on or off per rule | Disabled rules are skipped and do not affect the score |

## How evaluations run

1. **Scheduled.** Every enabled contract gets a recurring job on the host's scheduler, driven by its cron expression. The
   job is created when you save an enabled contract, updated when the schedule changes, and removed when you disable or
   delete the contract.
2. **On demand.** Click **Evaluate now** on the contract page (or
   `POST /beacon/api/data-quality/contracts/{id}/evaluate`).

During an evaluation, Beacon:

1. loads the contract and its **enabled** rules;
2. generates engine-specific SQL for each rule and runs it against the data source (60-second timeout per rule);
3. turns each result into pass or fail, a rule score, an actual and expected value, and a message;
4. computes the overall score, stores the evaluation with all rule results, and updates the table's latest score and
   trend;
5. sends alerts if the score is below the failure threshold (scheduled runs only; see [Alerting](#alerting)).

If one rule fails (bad configuration, missing column, timeout), it is recorded as **failed with score 0** and the other
rules still run.

A contract with no enabled rules is not evaluated: it keeps its last score, records no history and sends no alert.
**Evaluate now** reports that it has no enabled rules, and the scheduled run is skipped.

## Scoring

### Per-rule score

Most rule types score **100 on pass and 0 on fail**. Three score differently:

| Rule type | Score on failure |
|-----------|------------------|
| **Null rate** | `max(0, 100 − (actualNullPercent − maxNullPercent))`: small overruns cost little |
| **Range** | `(1 − outOfRangeRows / totalRows) × 100`: proportional to the rows that violate the range |
| **Custom SQL** | What your query returns in `score` (default 100 or 0) |

### Overall score

The overall score is a weighted average of rule scores. Each rule's effective weight is:

```text
effectiveWeight = ruleWeight × severityMultiplier
```

with Critical = 4, High = 3, Medium = 2, Low = 1. Then:

```text
overallScore = Σ(ruleScore × effectiveWeight) / Σ(effectiveWeight)
```

rounded to two decimals. Custom SQL rules waiting for approval are not part of the score.

**Example:** a passing Critical freshness rule (weight 1, score 100) and a failing Low volume rule (weight 1, score 0)
score `(100×4 + 0×1) / (4+1) = 80%`.

### Table scores and trends

After every evaluation that is not partial, the target table's **latest score** is updated (one score per data source,
schema and table). Beacon keeps the previous score and derives a trend:

| Trend | Condition |
|-------|-----------|
| **Improving** | New score more than 1 point above the previous |
| **Stable** | Within ±1 point |
| **Degrading** | New score more than 1 point below the previous |

Table scores also appear in the MCP `get_documentation` output for the table (see [MCP server](/mcp/server/)).

## Alerting

| Setting | Default | What it does |
|---------|---------|--------------|
| **Alert on failure** | on | Turns alerts for the contract on or off |
| **Failure threshold (%)** | 80 | Alerts fire when the overall score drops **below** this value |
| **Notification recipients** | none | Who is alerted (the same recipients [subscriptions](/features/subscriptions/) use) |

When a **scheduled** evaluation scores below the threshold, Beacon sends a notification titled
`[Data Quality] {contract name}` to every recipient. The payload has the overall score, the threshold, the target table,
pass and fail counts, and every failed rule (name, score, expected and actual value, message). For channels, see
[Notifications](/features/notifications/).

:::note
**Evaluate now** records the evaluation and updates the score but sends no alert. Only scheduled runs alert.
:::

## Using the UI

### Data Quality page

Open **Data Quality** in the navigation (`/beacon/data-quality`). The page shows:

- **overview cards per data source**: average score, table count, **Healthy** tables (score of at least 80),
  **Degrading** tables, **Contracts**, **Skipped (owner)** when any, and per-table scores;
- **the contracts list**, paged on the server, newest first: each contract with its target table, latest score and state,
  and **View**, **Edit** and **Delete**.

### Create a contract

1. Click **New contract**.
2. Fill in the details:

| Field | Required | Example |
|-------|----------|---------|
| **Contract name** | Yes | `Orders freshness & volume` |
| **Description** | No | |
| **Data source** | Yes | A PostgreSQL, SQL Server or MySQL data source |
| **Schema name** / **Table name** | Yes | `public` / `orders` |
| **Cron expression** | Yes | `0 */6 * * *` (every 6 hours) |
| **Failure threshold (%)** | No | `80` |
| **Enabled** | | on: the schedule is active |
| **Alert on failure** | | on: alert below the threshold |
| **Notification recipients** | | Shown while **Alert on failure** is on |

3. Add one or more rules: **Name**, **Type**, **Column (optional)**, **Severity**, **Weight** and
   **Configuration (JSON)** (the dialog shows an example for the type).
4. Click **Create**. An enabled contract is scheduled at once.

### Contract page

The contract page (`/beacon/data-quality/{id}`) shows the KPIs **Latest score**, **Rules**, **Schedule**,
**Failure threshold** and **Owner**, and three tabs:

- **Rules**: the rule set;
- **Evaluations**: history, newest first, with overall score, pass and fail counts and execution time, a **Partial**
  marker with the number of Custom SQL rules left out, and the number of alert recipients skipped;
- **Latest results**: per-rule results of the most recent evaluation (pass or fail, score, expected and actual value,
  message).

**Evaluate now**, **Edit**, **Change owner** (Admins and UserAdmins) and **Delete** sit in the header. Deleting a contract
archives it and removes its scheduled job.

:::note
**Editing a contract replaces its rule set.** A rule whose definition is unchanged stays as it is; the changed and removed rules are retired, not deleted: they are never evaluated
again and no longer show on the contract, but they stay stored with their definition (a Custom SQL rule's SQL included),
and the per-rule results of past evaluations stay in the **Evaluations** history under the name of the rule that produced
them. Moving the contract to another data source retires every rule and removes their stored results, so the history never shows one source's results under another. Beacon purges nothing else, so a retired rule is kept as long as the evaluation results that point at it. A save that
finds its rules already replaced by another save is refused with a conflict (409): reload the contract and save again.
:::

## REST API

Routes under `/beacon/api/data-quality`. A scoped API key needs the Execute scope for `POST`, `PUT` and `DELETE`.

| Method | Route | What it does |
|--------|-------|--------------|
| `GET` | `/overview` | Quality overview per data source (optional `dataSourceId`) |
| `GET` | `/contracts` | List contracts, paged: `page` (zero-based), `pageSize` (default 20, at most 200), `sort` (default `-createdTime`), optional `dataSourceId` |
| `GET` | `/contracts/{id}` | Contract detail with rules and latest score |
| `POST` | `/contracts` | Create a contract |
| `PUT` | `/contracts/{id}` | Update a contract (replaces rules and recipients) |
| `PUT` | `/contracts/{id}/owner` | Give the contract a new owner (UserAdmin or Admin), body `{ "userId": … }` |
| `DELETE` | `/contracts/{id}` | Archive a contract and remove its schedule |
| `GET` | `/contracts/{id}/evaluations` | Evaluation history (optional `take`, default 20) |
| `POST` | `/contracts/{id}/evaluate` | Run an evaluation now |

Lists and the overview cover only contracts in projects you can read.

## Troubleshooting

**A rule always fails with "Execution failed on the data source."**

The rule's SQL could not run. The server's error text is not shown or stored, because it can quote row values. Check
that the column exists and the data source connection works. Other failures name their cause:

- "Invalid rule configuration: …": the configuration JSON is incomplete for the rule type.
- "Rule type … is not supported on …": the rule type has no SQL for the data source's engine.
- "Rule SQL was rejected: …": the SQL is not a single read-only `SELECT`, or reads something a host-managed source does
  not expose.
- "Table … is not in the data source's metadata …" / "Column … is not a column of …": the contract or rule names an object
  the metadata does not list. Refresh the metadata or fix the name. "… matches several tables (columns) … that differ
  only in case": write the name exactly as the metadata spells it.
- "Custom SQL must not set its own LIMIT, TOP or FETCH; Beacon reads one row.": remove the outer row limit.
- "Rule timed out.": the query ran longer than 60 seconds.
- "Rule result did not have the expected columns or types.": a Custom SQL rule did not return `passed` (and the optional
  columns) as described above.
- "Query failed on the host database.": any failure on a host-managed data source.

**"Data contract's data source must be a database type"**

The contract targets a non-database data source (a REST API source, for example).

**"This data contract has no enabled rules, so it was not evaluated."**

Turn at least one rule's **Enabled** toggle on and save the contract.

**No alerts despite a failing score**

Alerts need **Alert on failure** on, at least one recipient, and an overall score strictly **below** the threshold. Only
scheduled runs alert; **Evaluate now** does not. A recipient whose destination waits for approval receives nothing.

**Pattern rule behaves differently across engines**

PostgreSQL and MySQL treat the pattern as a regular expression; SQL Server uses `LIKE` syntax.

**History shows fewer entries than expected**

The history route returns the 20 most recent evaluations by default; pass a larger `take`. Rule-level details of old
rules are removed when a contract is edited.
