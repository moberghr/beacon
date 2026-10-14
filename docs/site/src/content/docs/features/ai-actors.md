---
title: AI Actors
description: Experimental LLM-driven monitoring agents that design queries and subscriptions, with every proposed change gated by a human approval workflow.
---

:::caution[Experimental feature]
AI Actors are experimental and driven by large language models, which may produce incorrect, incomplete, or misleading results — including invalid SQL and inaccurate analysis. Keep the approval workflow enabled and review every proposed plan before approving it. See [AI Integration](/features/ai-integration/) for general guidance on AI features in Beacon.
:::

An **AI Actor** is an LLM-driven monitoring agent attached to a single data source. Given high-level instructions ("monitor transactions for unusual activity"), it autonomously creates queries and subscriptions, analyzes results, refines its own monitoring over time, and flags findings for notification — with a human approval workflow gating its proposed changes.

## Overview

**What an AI Actor does:**
- Designs SQL monitoring queries against your data source schema based on your plain-English instructions
- Creates [Subscriptions](/features/subscriptions/) so those queries run on a schedule
- Runs a **think cycle** after subscription executions: analyzes recent results and decides whether to create, refine, or archive queries and subscriptions
- Records every decision, action, token count, and cost in an execution history
- Proposes **plans** — reviewable bundles of analysis + proposed actions — that a human approves, rejects, or sends back for revision

**What an AI Actor cannot do:**
- Modify queries it doesn't own — refinements only apply to queries created by that actor
- Modify **locked** queries — locked query IDs are excluded in the prompt and hard-checked again at execution time
- Exceed its configured limits (`MaxQueries`, `MaxSubscriptionsPerQuery`)
- Archive queries or subscriptions it doesn't own, or archive locked queries — ownership and lock are checked again when an action executes

:::note[Interim behavior: actions are proposed, not executed]
Every actor currently has **Requires approval** on, and in that mode the think cycle, refine, and initial setup **only record** the actions the LLM chose. Each recorded action is marked `proposed` in the execution history and nothing is created, refined, or archived. Proposed actions are recorded on the execution for review; executing them needs the approval workflow, which arrives in a later release. Until then, an actor's think cycles produce analysis and proposals but no changes to your queries or subscriptions.
:::

All LLM calls go through the configured provider (see [AI Integration](/features/ai-integration/)) and are metered — each actor tracks total tokens used and estimated cost.

## Creating an Actor

Go to **AI Actors** in the React UI (`/ai-actors`) and create a new actor:

| Field | Description | Default |
|-------|-------------|---------|
| **Name** | Display name for the actor | — (required) |
| **Instructions** | High-level monitoring goal, e.g. "monitor for failed payments" | — (required) |
| **Data source** | The single data source this actor monitors | — (required) |
| **Additional context** | Business rules, table hints, monitoring requirements | None |
| **Max queries** | Maximum queries the actor may create | 10 |
| **Max subscriptions per query** | Maximum subscriptions per query | 3 |
| **Default recipients** | Notification recipients for created subscriptions | None |
| **Activate immediately** | Run initial setup right away | Yes |

When activated immediately, the actor runs an **initial setup**: the LLM reads the data source schema and your instructions, then creates a first set of monitoring queries and subscriptions. If setup fails, the actor moves to `Failed` status with the error recorded in its **Last error** field.

The REST endpoint is `POST /beacon/api/ai-actors` (one MediatR handler per endpoint, like the rest of the API).

The actor's **creator** is the user who created it (an API key's owner, for a key), or the user an Admin made its creator. Only the creator or an Admin can pause, resume, archive, refine or run it on demand, approve, reject or ask for a revision of one of its plans, or run one of the subscriptions it manages on demand (`POST /subscriptions/{id}/execute`). An Admin or a UserAdmin can give an actor a new creator with **Change owner** on its page (`PUT /beacon/api/ai-actors/{id}/owner` with `{ "userId": <user id> }`); the user must be an existing, enabled user who may work ad hoc (Editor or above) in a project that links the actor's data source. A user who may not is refused with `409` and a message saying so, because every background think cycle would be skipped. The actor's detail names the creator by display name, or as "User {id}" without one (`hasOwner`, `ownerDisplayName`), never by user name or external id.

An actor belongs to the projects that link its data source: it, its plans and its think cycles are visible only through such a project the caller may use, and refining it or running it on demand needs Query there ([Project access](/features/authorization/#project-access)). While Beacon user management is on, a caller without a Beacon user (a host principal, or a bearer token not bound to a user) cannot create an actor (`403`). Subscriptions an actor creates are owned by the actor's creator.

:::caution[Upgrading]
The creator is now always the signed-in caller: a `createdByUserId` in the create request is ignored, and a caller without a resolvable user cannot create an actor. Pausing, resuming, archiving, refining or running an actor, approving, rejecting or requesting a revision of one of its plans, and running a subscription the actor manages on demand need its creator or an Admin; an actor created before this version without a recorded creator can only be changed or run by an Admin until an Admin sets its creator. To anyone but an Admin, an actor or plan that does not exist is refused (403) like one that is not theirs.
:::

## Actor Statuses

| Status | Meaning |
|--------|---------|
| `Draft` | Being configured, not yet active |
| `Active` | Will run a think cycle after its subscriptions execute |
| `Paused` | Temporarily suspended — no think cycles run |
| `Failed` | A think cycle or setup failed; the error is stored on the actor |
| `Archived` | Soft-deleted, no longer active |

Pause, resume, and archive are available from the actor's edit page and via `POST /beacon/api/ai-actors/{id}/pause`, `/resume`, and `DELETE /beacon/api/ai-actors/{id}`.

## The Think Cycle

A think cycle is one autonomous reasoning pass. It is triggered:

- **Automatically** — when a subscription owned by an `Active` actor finishes executing, a think cycle is enqueued as a background job so the subscription pipeline never blocks on LLM round-trips. Paused, draft, or failed actors are skipped.
- **Manually** — via the actor detail page or `POST /beacon/api/ai-actors/{id}/think`.

A background think cycle runs as the actor's creator, as they are now: before any LLM call or SQL, Beacon checks that the creator may still work ad hoc with the actor's data source (Query in a project that links it). When the creator may not — disabled or archived, no longer a member of a restricted project, or only a Viewer — the cycle is recorded with the phase `Skipped` and the message "Skipped: owner not authorized.", nothing else happens, and the actor keeps its status. An actor without a recorded creator runs background cycles only while its data source is openly reachable, and never with `Beacon:Authorization:RequireProjectMembership: true`. A cycle you start with **Think** runs as you.

Each cycle is persisted as an execution record that moves through these phases:

| Phase | What happens |
|-------|--------------|
| `Analyzing` | Gathers the data source schema, the actor's existing queries, and recent subscription results |
| `Planning` | Sends the context to the LLM, which returns an analysis, findings, and a list of actions |
| `Executing` | Applies the actions (create/refine/archive queries and subscriptions) |
| `Notifying` | Records notification intent if the LLM flagged important findings |
| `Completed` | Cycle finished successfully |
| `Failed` | Cycle failed — the error is stored and the actor moves to `Failed` status |
| `Skipped` | A background cycle whose creator may no longer work with the data source: nothing ran, and the actor keeps its status |

The actions an actor can take:

| Action | Description |
|--------|-------------|
| `CreateQuery` | Create a new monitoring query |
| `CreateSubscription` | Schedule an existing query via a subscription |
| `RefineQuery` | Update the SQL of a query the actor owns |
| `ArchiveQuery` | Retire an underperforming query |
| `ArchiveSubscription` | Retire a subscription that is no longer needed |
| `SendNotification` | Flag important findings for notification |

Every execution stores the LLM's decision summary, detailed analysis (markdown), key findings, per-action results, tokens used, estimated cost, and the model that generated it. The actor detail page shows this execution history along with cumulative think count, token, and cost totals.

:::note
The phase checkpoints are saved to the database before and after each external LLM call, so if a call hangs or the worker dies mid-cycle, the execution record still shows where it stopped.
:::

## Refining an Actor

You can steer an actor conversationally. From the actor's edit page (or `POST /beacon/api/ai-actors/{id}/refine`), submit feedback such as:

> "Focus on orders over $10,000 and check for duplicate payment attempts"

The feedback is stored in the actor's conversation history, sent to the LLM together with the current schema and query context, and the actor adjusts its queries and subscriptions accordingly.

## Plans and the Approval Workflow

Instead of acting silently, an actor can propose a **plan**: a reviewable record containing its analysis, key findings, and the exact actions it wants to take. Plans are the human-in-the-loop mechanism — **a plan's actions are executed only after a human approves it**. While a plan is pending, nothing in it runs.

Each actor carries a **Requires approval** setting (on by default), shown on the actor detail page.

### Plan contents

A plan stores:
- The actor's **analysis** of the current state (markdown)
- **Key findings** (e.g. "Current query returns 0 results")
- **Proposed actions** with per-action reasoning — for query refinements this includes the current SQL, the proposed SQL, and whether the target query is locked
- The **user instruction** that triggered it (if any), token usage, estimated cost, and the model used
- A **version number** and a link to its parent plan when it is a revision

Depending on how it was generated, a plan can represent the actor's initial setup, a reaction to a subscription execution, or a response to a user instruction.

### Plan statuses

| Status | Meaning |
|--------|---------|
| `PendingApproval` | Awaiting human review — no actions have run |
| `Executing` | Approved; actions are currently being applied |
| `Executed` | Approved and fully executed |
| `Rejected` | Declined by the reviewer (also set if execution fails after approval) |
| `Expired` | Expired before review (e.g. context changed) |
| `RevisionRequested` | Reviewer asked for changes — a new revision is generated |

### Reviewing a plan

Pending plans for an actor are listed via `GET /beacon/api/ai-actors/{id}/pending-plans`; a single plan (with its full analysis and proposed actions) via `GET /beacon/api/ai-actors/plans/{id}`. The actor detail page shows the pending plan count.

The reviewer, the actor's [creator](#creating-an-actor) or an Admin, has three options. With the
[query change approval workflow](#query-change-approvals) on, **approving** takes a second person: a holder of the
**Approver** permission (an Admin holds it) other than the actor's creator and anyone who asked for a revision of the
plan, with write access to the actor's data source; the creator (or an Admin) still rejects or asks for a revision. A host
that allows self-approval (`ApprovalWorkflow.AllowSelfApproval`) lets a creator who is an Approver approve their own
actor's plans. Every decision records the reviewer as Beacon names the signed-in caller.

**Approve** — `POST /beacon/api/ai-actors/plans/{id}/approve` (optional comment)

The plan moves to `Executing`, the reviewer and timestamp are recorded, and an execution record is created and linked to the plan. Actions run one by one; refinements targeting locked queries are skipped and recorded as failed actions. On success the plan becomes `Executed`; if execution throws, the execution is marked `Failed` and the plan is marked `Rejected`.

**Reject** — `POST /beacon/api/ai-actors/plans/{id}/reject` (reason required)

The plan moves to `Rejected` with the reviewer, timestamp, and reason recorded. No actions are executed.

**Request revision** — `POST /beacon/api/ai-actors/plans/{id}/request-revision` (feedback required)

The actor generates a fresh plan through a new LLM call that incorporates your feedback. The revision gets an incremented version number, links back to the original plan, and enters `PendingApproval` again.

Only plans in `PendingApproval` can be approved, rejected, or revised: the decision is a conditional update from that
status, so a plan decided already (or by someone else at the same moment) answers `409`.

:::caution[Review the SQL, not just the summary]
For refinements, the plan shows current vs. proposed SQL side by side. LLM-generated SQL can be subtly wrong; validate it against your schema before approving.
:::

## Query Change Approvals

Separate from actor plans, Beacon has a general **query change approval workflow** for changes to what a query runs.
When the approval workflow is enabled in configuration (`EnableApprovalWorkflow()` / the `ApprovalWorkflow` options on
`BeaconConfiguration`), any change to a query's executable content (its steps, their order, names, data sources, SQL and
parameters, and the final query) is intercepted:

1. Instead of applying the edit, Beacon creates a new query version in `PendingApproval` status holding the submitted
   change — the live query keeps running unchanged. A requester's newer submission for the same query supersedes their
   pending one; while someone else's change waits, a submission is refused (`409`) until a reviewer decides it.
2. An approval request appears at **Approvals** in the React UI (`/approvals`), showing the change summary and who
   requested it.
3. A reviewer opens the request (`/approvals/{id}`), compares the current active version with the proposed one, and
   approves or rejects with an optional comment. The requester cannot approve their own change.
4. **Approve** archives the current active version and activates the proposed one, in one transaction, provided the
   active version is still the one the change was made on. **Reject** leaves the active version untouched.

The REST endpoints are `GET /beacon/api/approvals/pending`, `GET /beacon/api/approvals/{id}`, and
`POST /beacon/api/approvals/{id}/approve` / `/reject`. Approve and reject require the **Approver** permission (Admins
hold it; see [Functional permissions](/features/authorization/#functional-permissions)). An Approver lists and reads the
approval requests of every project; anyone else those of the projects they read. The full rules are in
[Queries → Approval workflow](/features/queries/#approval-workflow).

**An actor's refinements go through it too.** With the workflow on, a `REFINE_QUERY` action is validated without running
any of it: the refined SQL passes the checks of a save (the execution gate and the source's metadata) and the source's
own non-executing validation (`EXPLAIN` without `ANALYZE` on PostgreSQL, MySQL and Snowflake,
`sp_describe_first_result_set` on SQL Server and Azure Synapse — a plan or a description of the result, never a row;
SQLite has none and is left to the gate), so SQL nobody reviewed never executes. Without the workflow it is validated as
a draft, running through the same gates as an editor's preview. Either way the stored query is never changed to try it.
With the workflow on, the refinement is then submitted for approval with the actor's **creator** as its requester
(change source `AiActor`), so the creator cannot approve it and it goes live only once someone else approves it; it
never replaces another person's pending change of the query (the action fails while one waits), and an actor without a
creator cannot submit at all. Without the workflow it is saved as a new active version attributed to the creator, with
the step's change history. A `CREATE_QUERY` action is validated the same way before anything is stored, so a query whose
SQL does not pass is never written; with the workflow on, the query is created with nothing live and its SQL submitted
for approval (with the creator as requester), and a `CREATE_SUBSCRIPTION` action is refused for a query whose active
version was not approved for exactly its content — its content hash, even outside strict mode — so the action fails for
a query the same plan just created, and a later plan subscribes once its SQL is approved. When the changes come from an
approved plan, the plan's reviewer and everyone who asked for a revision of it are recorded on each request as its
contributors: none of them approves those query changes (unless the host allows self-approval), and the approval page
withholds **Approve** from them.

## Real-Time Updates (`ApprovalUpdated`)

Approval decisions are pushed in real time over the SignalR hub at `/beacon/api/hub` (wired automatically; see [Real-time](/getting-started/installation/#real-time-signalr) to opt out). When a query change approval is approved or rejected, the server sends an `ApprovalUpdated` event (status `approved` or `rejected`); when a requester's newer submission for the same query supersedes their pending request, they alone receive one with status `superseded`:

```json
{ "approvalId": 42, "status": "approved" }
```

Delivery is targeted, not broadcast: only the **reviewer** (so their other open tabs update) and the **requester** (so they see the decision live) receive the event. Both are recorded by the user id their sign-in presents to the hub, whichever way the change was submitted (a session or the owner's API key). In the React shell, `ApprovalUpdated` invalidates the approvals cache, so the approvals list and detail pages refresh automatically without a manual reload. Events missed during a transient disconnect are reconciled when the connection comes back.

## Cost Tracking

Every LLM interaction is metered:

- **Per execution / per plan** — tokens used, estimated cost, and the model that generated it
- **Per actor** — cumulative `TotalTokensUsed` and `TotalCost` across all think cycles, shown on the actor detail page

Use the actor's limits (`MaxQueries`, `MaxSubscriptionsPerQuery`) plus the global LLM rate limits described in [AI Integration](/features/ai-integration/) to keep costs bounded.

## Troubleshooting

**Actor is in `Failed` status** — A think cycle or initial setup threw an error — check the **Last error** field on the actor detail page. Fix the underlying issue (often LLM configuration or data source connectivity), then resume the actor.

**"Plan is not pending approval" error** — The plan was already approved, rejected, revised, or expired. Only `PendingApproval` plans accept decisions — fetch the actor's pending plans for the current revision.

**A refinement in an approved plan failed with "Query is locked"** — Locked queries cannot be modified by AI. This is by design — the rest of the plan still executes, and the skipped action is recorded with its error message.

**Actor never thinks automatically** — Automatic think cycles fire only for `Active` actors, and only after one of the actor's own subscriptions executes. Verify the actor is active and its subscriptions are scheduled and running.

**Approvals page doesn't update in real time** — `ApprovalUpdated` is delivered only to the reviewer and requester of that approval. Other users see updates on their next page load or query refetch.

## Related Features

- [AI Integration](/features/ai-integration/) — LLM provider configuration, rate limits, cost estimation, and data privacy considerations
- [Queries](/features/queries/) — the query and versioning model actors build on
- [Subscriptions](/features/subscriptions/) — scheduling and notification pipeline used by actor-created subscriptions
- [Notifications](/features/notifications/) — how findings reach recipients
- [Data Sources](/features/data-sources/) — connect the data source an actor monitors
