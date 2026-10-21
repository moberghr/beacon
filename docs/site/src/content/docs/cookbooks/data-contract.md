---
title: Score a table with a data contract
description: "A data contract on one table: rules for freshness, volume, nulls, uniqueness and more, evaluated on a schedule into a 0 to 100 score, with an alert to Slack, Teams or email when the score drops."
---

| | |
|---|---|
| **You end up with** | A contract on one table with a handful of rules, evaluated on a cron schedule. Each evaluation stores a weighted score, the table shows a trend, and a drop below your threshold alerts a channel. |
| **Time** | about 20 minutes |
| **You need** | A PostgreSQL, SQL Server or MySQL [data source](/features/data-sources/) whose metadata is loaded, and the right to run saved SQL on it through a project. For alerts, a [recipient](/features/notifications/#recipients). A Custom SQL rule needs an Admin. |
| **Applies to** | Beacon 5.0 · Full Beacon |

:::tip[Let your AI do it]
This recipe is done in Beacon's web UI, so the agent guides you instead of doing it. Paste this into Claude or another assistant.

```text wrap
Guide me through https://moberghr.github.io/beacon/cookbooks/data-contract.md one step at a time. Read it first.
Ask me which table to cover and which problems matter most (stale data, missing rows, nulls, duplicates, orphans)
before proposing rules, and show me each rule's JSON configuration for approval before I type it in. After each step,
ask me to confirm what I see against the "Check it works" list. Do not suggest a Custom SQL rule unless I am an Admin
and a built-in rule type cannot express the check.
```
:::

## Before you start

| Decision | Options | Default |
|---|---|---|
| Table | One table per contract: a schema and a table of the data source | Required |
| Rules | Freshness, Volume, Null rate, Uniqueness, Referential, Range, Pattern, Custom SQL | Start with two or three |
| Schedule | A 5-field cron expression | `0 */6 * * *` (every 6 hours) |
| Alert | **Alert on failure** and a **Failure threshold (%)** | On, 80 |

Data contracts evaluate on PostgreSQL, SQL Server and MySQL only. On another engine the rules fail. The table and each
rule's columns must exist in the data source's stored metadata: refresh the metadata first when the table is new.

## 1. Open the Data Quality page

Open **Data Quality** and click **New contract**.

## 2. Describe the contract

| Field | What to enter |
|---|---|
| **Contract name** | For example `Orders freshness and volume` |
| **Data source** | Your database data source |
| **Schema name** / **Table name** | For example `public` / `orders` |
| **Cron expression** | `0 */6 * * *`, or `0 3 * * *` for nightly |
| **Failure threshold (%)** | `80` is the default; alerts fire when the score is below it |
| **Enabled** | On, so the schedule is active |
| **Alert on failure** | On, then pick **Notification recipients** |

## 3. Add rules

Click **Add rule** for each. Each has a **Name**, **Type**, optional **Column**, **Severity**, **Weight** and a
**Configuration (JSON)**. You do not write the schema or table: Beacon takes them from the contract.

| Type | Checks | Configuration |
|---|---|---|
| **Freshness** | Newest timestamp is not older than a limit | `{"column": "updated_at", "maxAgeMinutes": 60}` |
| **Volume** | Row count in range | `{"minRows": 100}` |
| **Null rate** | NULL percentage at most a limit | `{"column": "email", "maxNullPercent": 5}` |
| **Uniqueness** | No duplicate values | `{"column": "id"}` |
| **Referential** | Every value exists in a reference table | `{"column": "user_id", "referenceTable": "users", "referenceColumn": "id"}` |
| **Range** | Numbers between `min` and `max` | `{"column": "age", "min": "0", "max": "150"}` |
| **Pattern** | Every value matches a pattern | `{"column": "email", "pattern": "^[^@]+@[^@]+$"}` |
| **Custom SQL** | Any check you can write as SQL | See [Custom SQL rules](#custom-sql-rules) |

Pattern behaves differently by engine: a regular expression on PostgreSQL and MySQL, `LIKE` wildcards on SQL Server.

**Severity** multiplies a rule's influence (Low ×1, Medium ×2, High ×3, Critical ×4) and **Weight** (0.1 to 10, default 1)
tunes it within a severity. Make the rules whose failure you cannot tolerate Critical.

A passing Critical freshness rule and a failing Low volume rule, both weight 1, score
`(100×4 + 0×1) / (4+1) = 80%`. Most rules score 100 or 0; Null rate and Range lose points in proportion to the
overrun. See [Scoring](/features/data-quality/#scoring).

Click **Create**. An enabled contract is scheduled at once.

## 4. Evaluate it now

On the contract's page, click **Evaluate now**. It runs every enabled rule once as you, stores the evaluation and
updates the table's score. It sends no alert: only scheduled runs alert.

The page's tabs show the result:

- **Rules**: the rule set.
- **Evaluations**: the history, newest first, with the overall score and pass and fail counts.
- **Latest results**: per rule, pass or fail, score, expected and actual value and message.

A rule that cannot run (a bad configuration, a missing column, a timeout of 60 seconds) is recorded as failed with
score 0, and the other rules still run.

## 5. Alert on a drop

With **Alert on failure** on and a recipient attached, a **scheduled** evaluation scoring below the threshold sends a
notification titled `[Data Quality] {contract name}` to each recipient. It carries the overall score, the threshold,
the table, pass and fail counts and every failed rule with its expected and actual value.

To check it end to end, set **Failure threshold (%)** to 100 with a rule that currently fails, wait for the next
scheduled run (or set a near cron time), and watch the channel. Then put the threshold back.

The table's trend is **Improving**, **Stable** or **Degrading** by more than 1 point against the previous score. See
[Table scores and trends](/features/data-quality/#table-scores-and-trends).

## Custom SQL rules

Use one when no built-in type fits. Only an **Admin** can create, change or delete a contract that contains one.

1. Add a rule of type **Custom SQL**.
2. Put a single read-only `SELECT` in the configuration:

```json
{"sql": "SELECT CASE WHEN COUNT(*) = 0 THEN 1 ELSE 0 END AS passed, COUNT(*) AS actual_value, 'orders without a customer' AS message FROM orders WHERE customer_id IS NULL"}
```

- It must return `passed` (`1` or `0`). `score` (0 to 100), `actual_value` and `message` are optional.
- Do not set your own `LIMIT`, `TOP` or `FETCH`: Beacon reads one row.
- Name only the data source's own database. Other qualifiers are refused.
- PII in `actual_value` and `message` is masked before it is stored or alerted.

:::note[Approval]
If your Beacon has the [approval workflow](/security/approvals/) on, a Custom SQL rule that is new or whose SQL changes
waits for a second person (an Approver or Admin other than you) and is not evaluated until then. The save says
"Submitted for approval", the rule shows **Awaiting approval**, and the reviewer decides on **Approvals** under
**Custom SQL rules**. Evaluations that leave a waiting rule out are marked **Partial** and do not replace the table's
score. See [Approval of Custom SQL](/features/data-quality/#approval-of-custom-sql).
:::

## Over the REST API

The same contract can be managed under `/beacon/api/data-quality`: `POST /contracts` to create,
`POST /contracts/{id}/evaluate` to run now, `GET /contracts/{id}/evaluations` for the history and `GET /overview` for
scores per data source. A scoped API key needs the Execute scope for writes. See
[Data Quality → REST API](/features/data-quality/#rest-api).

## Check it works

- [ ] The contract page shows **Latest score** after **Evaluate now**, and **Evaluations** lists the run.
- [ ] **Latest results** shows each enabled rule as pass or fail with an actual value.
- [ ] The **Data Quality** overview shows the table with its score.
- [ ] After a scheduled run below the threshold, the recipient's channel gets `[Data Quality] {contract name}`.

## If something goes wrong

| Symptom | Cause and fix |
|---|---|
| Saving says "Table … is not in the data source's metadata" | The table or a column is not in the stored metadata. Refresh the data source's metadata, or fix the spelling. |
| "Data contract's data source must be a database type" | The data source is not a database (for example an API). Pick a database data source. |
| A rule fails with "Rule type … is not supported on …" | The engine is not PostgreSQL, SQL Server or MySQL. |
| "Invalid rule configuration" | The JSON is incomplete for the type. Compare it with the table above. |
| "Custom SQL must not set its own LIMIT, TOP or FETCH" | Remove the outer row limit. |
| "Rule timed out." | The check ran over 60 seconds. Narrow it or index the column. |
| No alert despite a low score | Alerts need **Alert on failure**, a recipient, a score strictly below the threshold and a scheduled run. A recipient waiting for approval receives nothing. |
| **Owner not authorized** on the contract | The owner may no longer run saved SQL on the data source, so evaluations are skipped. An Admin or UserAdmin uses **Change owner**. |
| "Every enabled rule … is a Custom SQL rule waiting for approval" | Approve it on **Approvals**, or enable a rule that does not wait. |

## Next

- [Data Quality](/features/data-quality/): ownership, scoring and every rule option
- [Alert Slack or Teams when a nightly check fails](/cookbooks/alert-on-failed-check/): the query-based alternative
- [Notifications](/features/notifications/): channels and destination approval
- [Approvals](/security/approvals/#migration-jobs-custom-sql-rules-and-recipients): reviewing Custom SQL rules
