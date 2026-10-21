---
title: Approvals
description: "Four-eyes change approval: what it reviews (query changes, migration jobs, Custom SQL rules, notification destinations), how a decision is bound to exactly what the reviewer saw, who may approve, strict mode, and the API."
---

The approval workflow makes a second person review every change that decides **what SQL runs** or **where data goes** before it takes effect. It is **off by default**. A host cannot tell a new installation from an existing one, so turning it on is the host's decision, and Beacon logs a startup note while it is off.

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options =>
{
    options.EnableApprovalWorkflow(x =>
    {
        x.RequireApprovedVersionToRun = true;   // strict mode, recommended for regulated deployments
    });
});
```

| Option | Default | What it does |
|---|---|---|
| `Enabled` (set by `EnableApprovalWorkflow`) | `false` | Turns the workflow on. |
| `AllowSelfApproval` | `false` | Let the requester (and others who shaped a change) approve it themselves. Only for a deployment with a single possible approver; Beacon warns at startup while it is on, and warns when fewer than two users can approve while it is off. |
| `RequireApprovedVersionToRun` | `false` | Strict mode: a subscription (and a saved-query MCP tool) runs only a version that carries an approval for exactly its content. |
| `CoversMigrationJobs` | `true` | New and changed migration job definitions need approval before any run. |
| `CoversCustomSqlRules` | `true` | New and changed Custom SQL rules of data contracts need approval before any evaluation. |
| `CoversRecipientDestinations` | `true` | New recipients and changed destinations (type, address, headers) receive nothing until approved. |

The per-kind switches apply only while the workflow is enabled. Configuration is in code; there are no `appsettings` keys for it.

## Who approves

Decisions need the **Approver** [functional permission](/security/authorization/). Admins hold it too.

- The **requester never approves their own change**, however either of them signed in, and neither does anyone else who shaped it (for an AI actor's change: the plan's reviewer and whoever asked for its revisions). Admins are no exception.
- Four eyes binds per Beacon user. Do not give one person two accounts that can approve.
- API keys hold no functional permission and MCP has no approval tool, so neither can approve anything.
- Approvers see every request on **Approvals** (`/beacon/approvals`), including requests for queries they otherwise cannot read; other users see the requests of resources they can read.

## Query changes

**What counts as a change:** the query's executable content: its steps (added, removed, reordered), each step's name, data source, SQL and parameters, and the final query. Every character counts, whitespace and line endings included. A change to only the query's name or description goes live directly.

**Submitting.** Saving a change files a *PendingApproval* version with exactly the submitted content and its SHA-256 content hash, and an approval request naming the requester and the active version the change was made on. The live query is not touched.

- A query has one pending request. A new submission by the same person supersedes their earlier request; while **another** person's request waits, every submission of the query (a save, a restore, an AI actor's refinement) is refused with `409` until a reviewer decides.
- A **new query** goes live only once approved; until then it has no steps and runs nothing.
- A **version restore** is submitted like a save.
- Saving a query whose stored SQL differs from its active version (changed outside a versioned save) brings that SQL under review. Whoever saves it becomes its requester, so another Approver should review it.

**Reviewing.** The request detail shows the proposed version, the active version and a step-by-step diff (SQL, data source and parameters per step), the proposal's content hash, and whether the caller may approve and why not.

**Deciding.** Approving sends back the content hash the reviewer saw (`expectedContentHash`, required). The approval applies only if:

- the request is still pending (a second decision answers `409` and never overwrites the first);
- no other version went live since the change was made (otherwise it is stale; submit again);
- the proposal still holds the submitted content, and that content is what the reviewer was shown;
- every step passes the save checks again (the execution gate and the data source's metadata).

The decision, the move of the query to the new version and the version statuses commit in one transaction. Rejecting marks the proposal Rejected; a live version is never marked Rejected.

## Runs execute reviewed SQL

With the workflow on, runs that happen without the person who wrote the SQL (scheduled subscription runs, test deliveries, subscriptions an AI actor creates, and [saved-query MCP tools](/mcp/saved-query-tools/)) execute only reviewed content:

- A subscription run executes the stored steps only when they are the content of the query's active version. Otherwise the run is recorded as **Skipped** ("query change not approved"), the subscription and its Control Tower row show **Not approved**, and a test delivery answers `409`.
- A query stored before the workflow was on and never versioned since runs as stored outside strict mode, logging a warning each time; Beacon counts such queries at startup. Save the query to submit it.
- In **strict mode** a run also needs an approval for exactly the active version's content hash, so versions that went live while the workflow was off, or were approved before Beacon recorded hashes, do not run until approved.

Interactive previews run the caller's own SQL under the caller's permissions and are not compared.

## AI actors

With the workflow on, an actor's new query or refinement is validated without running it (the save checks plus the engine's non-executing validation: `EXPLAIN` without `ANALYZE` on PostgreSQL, MySQL and Snowflake, `sp_describe_first_result_set` on SQL Server and Synapse) and then submitted with the actor's creator as the requester. The creator cannot approve it. See [AI actors](/features/ai-actors/).

## Migration jobs, Custom SQL rules and recipients

These are reviewed as **change requests** with the same rules: four eyes, one decision while pending, and an approval bound to what the reviewer was shown (`expectedPayloadHash`) and to the definition the resource still holds.

| Kind | Reviewed | Until approved |
|---|---|---|
| Migration job | New job, or a changed source, steps, destination, mode or transformation | Every run, manual or scheduled, is refused and recorded as failed. |
| Custom SQL rule | New rule, or changed SQL or data source | The rule is left out of every evaluation. |
| Recipient destination | New recipient, or changed type, destination or headers | Deliveries skip the recipient; each skip is logged. Attaching an existing recipient to a subscription is not reviewed. |

When a change lies only in a part the review shows masked (a token, a header value), the approval must carry a comment saying how it was checked.

Resources stored before Beacon reviewed their kind, or last saved while the switch was off, keep working as before.

## Publishing a query as an MCP tool

While the workflow is on, whoever requested or approved the version a saved-query tool runs cannot be the one who enables the tool (unless `AllowSelfApproval`). See [Saved query tools](/mcp/saved-query-tools/).

## API

| Method and route | Who | What |
|---|---|---|
| `GET /beacon/api/approvals/pending` | Approver, or readers of the query | Pending query approvals (paged). |
| `GET /beacon/api/approvals/{id}` | Approver, or readers of the query | Request detail: proposal, active version, diff, `contentHash`, `mayApprove`. |
| `POST /beacon/api/approvals/{id}/approve` | Approver | Body `{ "comment": "…", "expectedContentHash": "…" }`. |
| `POST /beacon/api/approvals/{id}/reject` | Approver | Body `{ "comment": "…" }`. |
| `GET /beacon/api/approvals/changes` | Approver, or readers of the resource | Change requests: migration jobs, Custom SQL rules, recipients. |
| `GET /beacon/api/approvals/changes/{id}` | Approver, or readers of the resource | Change request detail with its payload hash. |
| `POST /beacon/api/approvals/changes/{id}/approve` | Approver | Body `{ "comment": "…", "expectedPayloadHash": "…" }`. |
| `POST /beacon/api/approvals/changes/{id}/reject` | Approver | Body `{ "comment": "…" }`. |

The reviewer is always the signed-in caller, never an id in the request. Decisions push an `ApprovalUpdated` event to the requester and reviewer over the realtime hub.

Every submission and decision is recorded in the [security audit trail](/security/audit-trail/#event-catalogue): `approvals.query_change_*` for query versions, `approvals.change_*` for change requests (with the kind, the resource id and the requests a submission supersedes) and `approvals.ai_plan_*` for AI actor plans. The reviewer is the event's actor, and a decision commits together with its event; a decision on a query change or a change request also names its requester by user id. Neither the proposed content nor a comment is stored in the event.

## Turning it on in an existing installation

1. Make sure at least two people hold Approver.
2. Enable the workflow; leave strict mode off for the first deployment.
3. Check the startup log: Beacon counts queries whose stored SQL is not versioned or approved.
4. Have owners save those queries (submitting them), approve them, then turn on `RequireApprovedVersionToRun`.
