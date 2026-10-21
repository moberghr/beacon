---
title: Alert Slack or Teams when a nightly check fails
description: "A saved query that returns the rows breaking a rule, a nightly subscription that posts them to Slack or Microsoft Teams, and a task that opens on failure and closes itself when the check passes again."
---

| | |
|---|---|
| **You end up with** | A nightly check on one of your databases. When it finds rows that break a rule, a message lands in a Slack or Teams channel and a task opens; when a later run finds none, the task resolves itself. |
| **Time** | about 20 minutes |
| **You need** | A running Beacon with a [data source](/features/data-sources/) for the database (or the rights to add one), a Slack or Teams channel you may add a webhook to, and the right to create queries and subscriptions. An Admin or SecurityAdmin creates the recipient. |
| **Applies to** | Beacon 5.0 · Full Beacon |

:::tip[Let your AI do it]
This recipe is done in Beacon's web UI, so the agent guides you instead of doing it. Paste this into Claude or another assistant.

```text wrap
Guide me through https://moberghr.github.io/beacon/cookbooks/alert-on-failed-check.md one step at a time. Read it first.
Ask me for the database, the rule to check and the channel before each step that needs them. After each step, ask me
to confirm what I see on screen against the recipe's "Check it works" list before moving on. Never ask me to paste a
webhook URL or a connection string into this chat: I type those into Beacon myself.
```
:::

## Before you start

| Decision | Options | Default |
|---|---|---|
| What counts as a failure | A query that returns **the rows that break the rule**; any row is a failure | Required: Beacon has no separate "check condition" |
| When to message you | **When result count changes**, **Every execution** or **When result count increases** | **When result count changes**: one message when the problem appears or changes, not one every night |
| Channel | Slack, Microsoft Teams, or both as two recipients | Required |
| Track it as a task | On or off | On: the task is the record of whether anyone dealt with it |

A subscription has no pass or fail setting of its own. It runs a query and looks at the row count. So write the check
as a query that returns the offenders, and an empty result means the check passed.

## 1. Add the data source

Skip this if the database is already connected.

1. Open **Data Sources** and click **New data source**.
2. Pick the **Data source type**, enter a **Name** and the connection details, and pick the **Projects** to link it into.
3. Click **Test connection**, then **Create data source**.

Use a read-only login: Beacon only ever reads. Details and TLS requirements are in [Data Sources](/features/data-sources/#create-a-data-source).

## 2. Write the check as a query

1. Open **Queries**, click **New query**, and enter a **Query name** and a **Description**. The name is what appears in
   the alert title.
2. In the first step, set a **Step name**, the **Target database** (your data source) and the **SQL query**.
3. Click **Save & run** to see the result.

```sql
-- Orders marked completed but not paid: any row is a failure.
SELECT id, status, payment_status
FROM orders
WHERE status = 'completed'
  AND payment_status <> 'paid'
```

- [ ] A healthy database returns no rows.
- [ ] The columns are ones you are happy to post to a channel. Beacon masks columns it detects as PII before rows leave,
  but leave out what the channel does not need.

Keep the window in the SQL itself (`created_at >= now() - interval '1 day'`). A task resolves on a run with zero rows,
so a window that is empty only because the night was quiet closes it too early. See
[Queries](/features/queries/) for parameters and multi-step checks.

## 3. Create the Slack or Teams recipient

A recipient is one destination on one channel. Only an Admin or a SecurityAdmin creates one.

1. Create the webhook:
   - **Slack:** create an app with **Incoming Webhooks** on, add a webhook to the channel, copy its URL.
   - **Teams:** in the channel, create a Workflows (Power Automate) flow that starts when a webhook request is received
     and posts the card it receives. Copy its URL. Old `outlook.office.com` connector URLs are refused.
2. In Beacon, open **Recipients** and click **Add recipient**.
3. Set **Notification type** to **Slack** or **Teams**, enter a **Name**, and paste the URL into **Slack webhook URL**
   or **Teams webhook URL**. Click **Create recipient**.

Beacon stores the URL encrypted and shows it masked afterwards. It must be HTTPS on a vendor host
(`hooks.slack.com`, or Teams hosts on `*.logic.azure.com`, `*.environment.api.powerplatform.com` or
`*.webhook.office.com`). The full rules are in [Destination policy](/features/notifications/#destination-policy).

:::note[Four-eyes approval of destinations]
If your Beacon has the [approval workflow](/security/approvals/) on, a new recipient receives nothing until a second
person (an Approver or Admin other than you) approves exactly where it delivers, on **Approvals** under **Recipients**.
The dialog then says "Submitted for approval". Ask the approver to compare the fingerprint on the **Recipients** page
with yours. Until it is approved, a run that has no other recipient is recorded as **Skipped**. See
[Approval of new and changed destinations](/features/notifications/#approval-of-new-and-changed-destinations).
:::

- [ ] The recipient appears in the **Recipients** list and is not marked **Awaiting approval**.

## 4. Schedule the check

1. Open the query and click **Add subscription** (or open **Subscriptions** and click **New subscription**).
2. **Query:** set the **Cron expression**. `0 2 * * *` runs every night at 02:00. Fill in the query's parameters, if any.
3. **Notify:** set **Send notification** (see the table above), pick your recipient under **Recipients**, and tick
   **Create a task and keep it open while the query returns rows**.
4. **Results:** leave **Show query text in notification** off unless the channel should see the SQL.
5. Click **Create subscription**.

You cannot edit a subscription's schedule or trigger afterwards. To change them, create a new subscription and archive
the old one. Recipients can be added and removed later.

The subscription runs as you. If you lose access to the query's data source, its runs are skipped; an Admin can
**Change owner**. See [Subscriptions](/features/subscriptions/#scheduled-runs-run-as-the-owner).

## 5. Test it

On the subscription's page, click **Test now**. It runs the subscription once, as you, and delivers to its recipients
without changing the schedule.

To see a failure, run a test while the check returns rows (insert a bad row in a test database, or temporarily loosen the
SQL). To see it pass, run a test on a clean result.

## What the message contains

- **Title:** `[Beacon] {data source} - {query name}`.
- **Rows:** a preview of the result's first rows, after PII masking, and the total row count. Slack shows up to 25 rows
  and 5 columns as a text table; Teams shows an Adaptive Card with the first 10 rows and 3 columns.
- **The query text**, only if you turned **Show query text in notification** on.
- **A link back to Beacon**, when the host has set a base URL that ends in `/beacon`.

Rows that reach recipients are masked by the PII settings of the projects that link the data source. See
[What a run reads and delivers](/features/subscriptions/#what-a-run-reads-and-delivers).

## 6. Watch it

- **Tasks** lists one open task for the subscription while the check keeps failing. Assign it to yourself, then
  **Resolve** it with notes when you have fixed the cause. A run with zero rows resolves it automatically with
  "Auto-resolved: Query returned 0 results"; a later failure opens a new task. See [Tasks](/features/tasks/).
- **Control Tower** shows the subscription's health (Green, Amber, Red, **Stalled**), its success rate and its open
  tasks. A run with no rows counts as a success. See [Control Tower](/features/control-tower/).
- **Notifications** lists every run with its status. Only **Sent** reaches the channel; **No results**,
  **Below threshold** and **Silenced** notify nobody by design.

Over the REST API, the same steps are `POST /beacon/api/recipients`, `POST /beacon/api/queries`,
`PUT /beacon/api/queries/{id}`, `POST /beacon/api/subscriptions` and `POST /beacon/api/subscriptions/{id}/execute`.
See [Routes](/reference/routes/).

## Check it works

- [ ] **Test now** on a failing check posts a message to the channel with the title `[Beacon] {data source} - {query name}`.
- [ ] A task for the subscription is listed under **Tasks** as **Unresolved**.
- [ ] **Test now** on a clean result posts nothing, the run shows **No results**, and the task is **Resolved**.
- [ ] The subscription is **Green** in **Control Tower** after its first scheduled run.

## If something goes wrong

| Symptom | Cause and fix |
|---|---|
| Saving the recipient is refused | The URL is outside the destination policy: not HTTPS, not a vendor host, or a retired Teams connector URL. The message names the problem. |
| **Test now** answers `409` | Every recipient waits for approval. Approve it on **Approvals**, or attach one that is approved. |
| Run shows **Failed** | Delivery to a recipient failed. Beacon does not retry; check the webhook, then use **Test now** or wait for the next run. |
| Run shows **Silenced** | The trigger did not fire: with **When result count changes**, the count equals the last run's. Pick **Every execution** if you want a message each night. |
| Run shows **Skipped** | The owner may no longer run the query, the query change is not approved, or every recipient waits for approval. The run's reason says which. |
| The link in the message is broken | The base URL is missing `/beacon`. An Admin sets it under **Admin Settings**. |
| The task closed but the problem persists | The query returned zero rows on one run. Widen its time window. |

## Next

- [Subscriptions](/features/subscriptions/): triggers, thresholds, attachments and anomaly detection
- [Notifications](/features/notifications/): every channel, webhook bodies and delivery history
- [Approvals](/security/approvals/): four-eyes review of queries and destinations
- [Score a table with a data contract](/cookbooks/data-contract/): scheduled quality rules with a score and alerts
