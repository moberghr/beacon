---
layout: default
title: AI Actors
parent: Features
nav_order: 15
---

# AI Actors (Experimental)
{: .no_toc }

{: .warning }
> **⚠️ Experimental Feature**
>
> AI Actors are experimental and driven by large language models, which may produce incorrect, incomplete, or misleading results — including invalid SQL and inaccurate analysis. Keep the approval workflow enabled and review every proposed plan before approving it. See [AI Integration](ai-integration) for general guidance on AI features in Beacon.

An **AI Actor** is an LLM-driven monitoring agent attached to a single data source. Given high-level instructions ("monitor transactions for unusual activity"), it autonomously creates queries and subscriptions, analyzes results, refines its own monitoring over time, and flags findings for notification — with a human approval workflow gating its proposed changes.

<details open markdown="block">
  <summary>Table of contents</summary>
  {: .text-delta }
- TOC
{:toc}
</details>

---

## Overview

**What an AI Actor does:**
- Designs SQL monitoring queries against your data source schema based on your plain-English instructions
- Creates [Subscriptions](subscriptions) so those queries run on a schedule
- Runs a **think cycle** after subscription executions: analyzes recent results and decides whether to create, refine, or archive queries and subscriptions
- Records every decision, action, token count, and cost in an execution history
- Proposes **plans** — reviewable bundles of analysis + proposed actions — that a human approves, rejects, or sends back for revision

**What an AI Actor cannot do:**
- Modify queries it doesn't own — refinements only apply to queries created by that actor
- Modify **locked** queries — locked query IDs are excluded in the prompt and hard-checked again at execution time
- Exceed its configured limits (`MaxQueries`, `MaxSubscriptionsPerQuery`)

All LLM calls go through the configured provider (see [AI Integration](ai-integration)) and are metered — each actor tracks total tokens used and estimated cost.

---

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

---

## Actor Statuses

| Status | Meaning |
|--------|---------|
| `Draft` | Being configured, not yet active |
| `Active` | Will run a think cycle after its subscriptions execute |
| `Paused` | Temporarily suspended — no think cycles run |
| `Failed` | A think cycle or setup failed; the error is stored on the actor |
| `Archived` | Soft-deleted, no longer active |

Pause, resume, and archive are available from the actor's edit page and via `POST /beacon/api/ai-actors/{id}/pause`, `/resume`, and `DELETE /beacon/api/ai-actors/{id}`.

---

## The Think Cycle

A think cycle is one autonomous reasoning pass. It is triggered:

- **Automatically** — when a subscription owned by an `Active` actor finishes executing, a think cycle is enqueued as a Hangfire background job so the subscription pipeline never blocks on LLM round-trips. Paused, draft, or failed actors are skipped.
- **Manually** — via the actor detail page or `POST /beacon/api/ai-actors/{id}/think`.

Each cycle is persisted as an execution record that moves through these phases:

| Phase | What happens |
|-------|--------------|
| `Analyzing` | Gathers the data source schema, the actor's existing queries, and recent subscription results |
| `Planning` | Sends the context to the LLM, which returns an analysis, findings, and a list of actions |
| `Executing` | Applies the actions (create/refine/archive queries and subscriptions) |
| `Notifying` | Records notification intent if the LLM flagged important findings |
| `Completed` | Cycle finished successfully |
| `Failed` | Cycle failed — the error is stored and the actor moves to `Failed` status |

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

{: .note }
> The phase checkpoints are saved to the database before and after each external LLM call, so if a call hangs or the worker dies mid-cycle, the execution record still shows where it stopped.

---

## Refining an Actor

You can steer an actor conversationally. From the actor's edit page (or `POST /beacon/api/ai-actors/{id}/refine`), submit feedback such as:

> "Focus on orders over $10,000 and check for duplicate payment attempts"

The feedback is stored in the actor's conversation history, sent to the LLM together with the current schema and query context, and the actor adjusts its queries and subscriptions accordingly.

---

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

The reviewer has three options:

**Approve** — `POST /beacon/api/ai-actors/plans/{id}/approve` (optional comment)
: The plan moves to `Executing`, the reviewer and timestamp are recorded, and an execution record is created and linked to the plan. Actions run one by one; refinements targeting locked queries are skipped and recorded as failed actions. On success the plan becomes `Executed`; if execution throws, the execution is marked `Failed` and the plan is marked `Rejected`.

**Reject** — `POST /beacon/api/ai-actors/plans/{id}/reject` (reason required)
: The plan moves to `Rejected` with the reviewer, timestamp, and reason recorded. No actions are executed.

**Request revision** — `POST /beacon/api/ai-actors/plans/{id}/request-revision` (feedback required)
: The actor generates a fresh plan through a new LLM call that incorporates your feedback. The revision gets an incremented version number, links back to the original plan, and enters `PendingApproval` again.

Only plans in `PendingApproval` can be approved, rejected, or revised — any other status returns an error.

{: .warning }
> **Review the SQL, not just the summary.** For refinements, the plan shows current vs. proposed SQL side by side. LLM-generated SQL can be subtly wrong; validate it against your schema before approving.

---

## Query Change Approvals

Separate from actor plans, Beacon has a general **query change approval workflow** for edits to query SQL. When the approval workflow is enabled in configuration (`EnableApprovalWorkflow()` / the `ApprovalWorkflow` options on `BeaconConfiguration`), any change to a query's SQL is intercepted:

1. Instead of applying the edit, Beacon creates a new query version in `PendingApproval` status — the live query keeps running unchanged.
2. An approval request appears at **Approvals** in the React UI (`/approvals`), showing the change summary and who requested it.
3. A reviewer opens the request (`/approvals/{id}`), compares the current active version with the proposed one, and approves or rejects with an optional comment.
4. **Approve** archives the current active version and activates the proposed one. **Reject** leaves the active version untouched.

The REST endpoints are `GET /beacon/api/approvals/pending`, `GET /beacon/api/approvals/{id}`, and `POST /beacon/api/approvals/{id}/approve` / `/reject`. Approve and reject require the **Admin** role.

This workflow also protects you when an AI Actor's changes touch queries governed by versioning — every SQL change is versioned, so you can always see what changed and roll back through query versions. See [Queries](queries) for the versioning model.

---

## Real-Time Updates (`ApprovalUpdated`)

Approval decisions are pushed in real time over the SignalR hub at `/beacon/api/hub`. When a query change approval is approved or rejected, the server sends an `ApprovalUpdated` event:

```json
{ "approvalId": 42, "status": "approved" }
```

Delivery is targeted, not broadcast: only the **reviewer** (so their other open tabs update) and the **requester** (so they see the decision live) receive the event. In the React shell, `ApprovalUpdated` invalidates the approvals cache, so the approvals list and detail pages refresh automatically without a manual reload. Events missed during a transient disconnect are reconciled when the connection comes back.

---

## Cost Tracking

Every LLM interaction is metered:

- **Per execution / per plan** — tokens used, estimated cost, and the model that generated it
- **Per actor** — cumulative `TotalTokensUsed` and `TotalCost` across all think cycles, shown on the actor detail page

Use the actor's limits (`MaxQueries`, `MaxSubscriptionsPerQuery`) plus the global LLM rate limits described in [AI Integration](ai-integration) to keep costs bounded.

---

## Troubleshooting

**Actor is in `Failed` status**
: A think cycle or initial setup threw an error — check the **Last error** field on the actor detail page. Fix the underlying issue (often LLM configuration or data source connectivity), then resume the actor.

**"Plan is not pending approval" error**
: The plan was already approved, rejected, revised, or expired. Only `PendingApproval` plans accept decisions — fetch the actor's pending plans for the current revision.

**A refinement in an approved plan failed with "Query is locked"**
: Locked queries cannot be modified by AI. This is by design — the rest of the plan still executes, and the skipped action is recorded with its error message.

**Actor never thinks automatically**
: Automatic think cycles fire only for `Active` actors, and only after one of the actor's own subscriptions executes. Verify the actor is active and its subscriptions are scheduled and running.

**Approvals page doesn't update in real time**
: `ApprovalUpdated` is delivered only to the reviewer and requester of that approval. Other users see updates on their next page load or query refetch.

---

## Related Features

- [AI Integration](ai-integration) — LLM provider configuration, rate limits, cost estimation, and data privacy considerations
- [Queries](queries) — the query and versioning model actors build on
- [Subscriptions](subscriptions) — scheduling and notification pipeline used by actor-created subscriptions
- [Notifications](notifications) — how findings reach recipients
- [Data Sources](data-sources) — connect the data source an actor monitors
