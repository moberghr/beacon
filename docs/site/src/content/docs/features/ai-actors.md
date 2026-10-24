---
title: AI Actors
description: Experimental LLM-driven monitoring agents attached to a data source. They analyze it and propose queries and subscriptions; the proposals wait in a plan until a person approves them.
---

:::caution[Experimental feature]
AI actors are experimental and driven by a large language model, which can produce incorrect or misleading analysis
and invalid SQL. Review what an actor proposes before you act on it.
:::

An **AI actor** is an LLM-driven monitoring agent attached to one data source. You give it a goal in plain English
("monitor transactions for unusual activity"); on each **think cycle** it reads the schema, its queries and recent
results, writes an analysis, and chooses actions such as creating a query or a subscription.

**Actions are proposed, not executed.** An actor has **Requires approval** on by default (the create request's
`requiresApproval`; it cannot be changed afterwards). In that mode the initial setup, a think cycle and a refinement
only **record** the actions the LLM chose: each is marked proposed on the cycle's execution record, the proposals are
stored together as one [plan](#plans) awaiting approval, and nothing is created, refined or archived until a person
approves that plan. An actor created with `requiresApproval: false` runs the actions it chooses as it chooses them;
only an Admin can create one (anyone else gets `403`), and every creation is recorded in the security audit trail as
`approvals.ai_actor_created` with the flag.

AI actors need the AI layer and a configured provider (see [AI Integration](/features/ai-integration/)).

## Creating an actor

Go to **AI Actors** (`/beacon/ai-actors`) and choose **New actor**:

| Field | What it does | Default |
|---|---|---|
| **Name** | Display name | required |
| **Data source** | The one data source the actor monitors | required |
| **Instructions** | The monitoring goal | required |
| **Additional context** | Business rules, table hints | none |
| **Max queries (optional)** | Most queries the actor may create | 10 when left blank |
| **Activate immediately** | Run the initial setup now | on |

The REST endpoint is `POST /beacon/api/ai-actors`; it also takes `maxSubscriptionsPerQuery` (default 3),
`defaultRecipientIds` and `requiresApproval` (default `true`; `false` takes an Admin; the response returns it), which
the dialog does not show.

With **Activate immediately** on, the actor becomes `Active` and runs its **initial setup**: the LLM reads the schema
and your instructions and proposes a first set of queries and subscriptions. If setup fails, the actor moves to
`Failed` with the error recorded. With it off, the actor stays in `Draft`.

### Creator and access

The actor's **creator** is the signed-in caller (an API key's owner, for a key); a `createdByUserId` in the request is
ignored. While Beacon user management is on, a caller without a Beacon user cannot create an actor (`403`).

Only the creator or an Admin can pause, resume, archive, refine or run the actor, decide one of its plans, or run one of
the subscriptions it manages on demand (`POST /beacon/api/subscriptions/{id}/execute`). An Admin or a UserAdmin can give
an actor a new creator with **Change owner** on its page (`PUT /beacon/api/ai-actors/{id}/owner` with
`{ "userId": <user id> }`). The new creator must be an existing, enabled user who may work ad hoc (Editor or above) in
a project that links the actor's data source; otherwise the change answers `409`, because every background think cycle
would be skipped. If someone else changed the creator, or archived the actor, since it was read, nothing is changed and
the call answers `409`; choosing the creator the actor already has changes nothing. The actor's page names the creator
by display name, or "User {id}" without one.

An actor belongs to the projects that link its data source: the actor, its plans and its think cycles are visible only
through such a project the caller may use, and refining it or running it on demand needs Query there (see
[Project access](/security/authorization/#project-access)). To anyone but an Admin, an actor or plan that does not exist
is refused (`403`) like one that is not theirs. An actor created without a recorded creator can be changed or run only
by an Admin until an Admin sets its creator.

## Actor statuses

| Status | Meaning |
|---|---|
| `Draft` | Created with **Activate immediately** off |
| `Active` | Runs think cycles |
| `Paused` | No background think cycles run |
| `Failed` | Setup or a think cycle failed; the error is stored on the actor |
| `Archived` | Soft-deleted |

Pause applies to an `Active` actor; resume takes a `Paused` or `Failed` actor back to `Active`. On the actor's edit
page (`/beacon/ai-actors/{id}/edit`) use **Pause actor** / **Resume actor**; the routes are
`POST /beacon/api/ai-actors/{id}/pause` and `/resume`. Archive with `DELETE /beacon/api/ai-actors/{id}` (the **Archive** button on the actor's page, after a confirmation); the list
shows archived actors with **Include archived**.

## The think cycle

A think cycle is one reasoning pass, recorded as an execution:

| Phase | What happens |
|---|---|
| `Analyzing` | Gathers the schema, the actor's queries and recent subscription results |
| `Planning` | Sends that context to the LLM, which returns an analysis, findings and actions |
| `Executing` | Records each chosen action as proposed (or runs it, for an actor that does not require approval) |
| `Notifying` | Records notification intent when the LLM flagged important findings |
| `Completed` | The cycle finished |
| `Failed` | The cycle failed; the error is stored and the actor moves to `Failed` |
| `Skipped` | Nothing ran: a background cycle whose creator may no longer work with the data source, or a cycle of an actor that requires approval while one of its plans is pending (the reason names the plan) |

Phase checkpoints are saved before and after each LLM call, so a cycle that hangs or whose worker dies still shows
where it stopped.

The actions an actor can choose:

| Action | Meaning |
|---|---|
| `CreateQuery` | A new monitoring query |
| `CreateSubscription` | A subscription for one of its queries |
| `RefineQuery` | New SQL for a query the actor owns |
| `ArchiveQuery` | Retire one of its queries |
| `ArchiveSubscription` | Retire one of its subscriptions |
| `SendNotification` | Flag findings for notification |

Each execution stores the decision summary, the analysis (markdown), key findings, each action with its reasoning,
tokens used, estimated cost and the model. The actor's page (`/beacon/ai-actors/{id}`) shows think cycles, tokens used,
total cost and the recent think cycles.

**When cycles run.**

- **On demand**: the actor's page has a **Think now** button, which starts the cycle as a background task:
  `POST /beacon/api/ai-actors/{id}/think-tasks` answers `202` with the task (its `jobId` correlates the `JobStatusChanged`
  events the user gets), and `GET .../think-tasks/{taskId}` reads its state (`Queued`, `Running`, `Succeeded`, `Failed`
  or `Skipped`, with a `failureCode`: `cooldown`, `plan_pending`, `inactive` (the actor was paused or archived meanwhile), `forbidden`, `not_found`, `abandoned`, `cancelled`,
  `failed`; never the provider's message, the cycle's own reason is on its execution). Only the creator or an Admin may start
  one; a second start while one is queued or running answers `429`. The task runs as you and re-checks that you may
  still run the actor and reach its data source right before the LLM call; if not it is skipped, never run as the
  system. The older `POST /beacon/api/ai-actors/{id}/think` runs the cycle inside the request and answers with its
  result (kept for 5.0 callers). Starting a task is recorded as `approvals.ai_actor_think_started`.
- **After a subscription run**: when a subscription linked to an `Active` actor finishes, a think cycle is enqueued as
  a background job. Only an executed `CreateSubscription` action links a subscription to an actor, so an actor whose
  actions are proposed has no linked subscriptions and its cycles run on demand.

**Cooldown.** An actor thinks at most once per `Beacon:AiActors:ThinkCooldownMinutes` (default `15`, `0` = off, at most
`1440`; validated at startup), counted from its last think time. A cycle within the cooldown is skipped: a background
one is only logged at Debug (actor id), and a subscription run within it is dropped, not deferred: nothing is enqueued for later. A cycle you request (**Think now**, or
`POST .../think`) does **not** bypass it: it answers `success: false` with the minutes left. The cooldown starts when a cycle starts, so a failed cycle consumes it too; the initial setup of a new actor does not count towards it. The cycle claims the actor
with one conditional update, so two hosts or job runs cannot both start one.

A background cycle runs as the actor's creator. Before any LLM call or SQL, Beacon checks that the creator may still
work ad hoc with the data source (Query in a project that links it). When the creator may not (disabled or archived,
no longer a member of a restricted project, or only a Viewer), the cycle is recorded as `Skipped` with
"Skipped: owner not authorized.", and the actor keeps its status. An actor without a recorded creator runs background
cycles only while its data source is openly reachable, and never with
`Beacon:Authorization:RequireProjectMembership: true`.

## Refining an actor

Steer an actor with feedback on its edit page (**Refine actor**, field **Refinement feedback**) or with
`POST /beacon/api/ai-actors/{id}/refine`:

> "Focus on orders over $10,000 and check for duplicate payment attempts"

The feedback is stored in the actor's conversation history and sent to the LLM with the schema and query context. Like
a think cycle, the refinement records the actions the LLM chose as proposed, in a plan that keeps the feedback as its
instruction (a revision of the plan carries it on). While the actor has a plan pending, a refinement is refused with
`409` naming that plan, before anything reaches the LLM.

## Plans

A **plan** is a stored bundle of analysis, key findings and proposed actions (for a query refinement: current SQL,
proposed SQL and whether the query is locked) that a reviewer approves, rejects or sends back for revision. Approving a
plan is the path that **executes** actions.

For an actor that requires approval, the initial setup, a think cycle and a refinement that propose at least one action
store them as one `PendingApproval` plan, saved together with the cycle's execution, whose analysis names the plan. A
cycle that proposes nothing creates no plan. While the actor has a plan pending, it does not propose another: a think
cycle is recorded as `Skipped` naming the pending plan and a refinement answers `409`, both before any LLM call. A plan
is added only in a serializable transaction that first checks the actor has no plan pending, so two cycles,
refinements or revisions finishing together never leave two pending plans. The one refused completes all the same: a
think cycle or the initial setup keeps its proposals on its execution record, which names the pending plan; a
refinement answers `409` naming it; a revision answers `409` saying the original plan was sent back but no revised plan
was created. A revision for which the model proposes no action it can take creates no plan and fails.

In the UI, the actor's page lists its pending plans under **Plans waiting for approval**. **Review** opens the plan:
its analysis, findings and proposed actions with their SQL (a refinement shows the query's current SQL next to the
proposed SQL), read-only, and its earlier versions with the feedback that shaped them. The dialog offers only the
decisions the server says the caller may take: **Approve and run** (optional comment), **Reject** (reason required) and
**Request revision** (feedback required); when the caller cannot approve, it says why. In the sidebar, **AI Actors**
carries a badge with the number of plans waiting for the caller's decision.

The same through the API:

| Route | What it does |
|---|---|
| `GET /beacon/api/ai-actors/{id}/pending-plans` | The actor's plans awaiting review |
| `GET /beacon/api/ai-actors/plans/{id}` | One plan with its analysis and actions, and what the caller may decide on it: `mayApprove` (with `approveBlockedReason` when not), `mayRejectOrRevise` |
| `GET /beacon/api/ai-actors/pending-plans/count` | How many plans wait for the caller's decision: pending, not expired, of actors whose data source the caller may write; an Admin, or an Approver while the approval workflow is on, counts them all, anyone else only those of the actors they created |
| `POST /beacon/api/ai-actors/plans/{id}/approve` | Approve (optional comment): the plan moves to `Executing`, its actions run, then `Executed`; if execution throws, the plan becomes `Rejected` |
| `POST /beacon/api/ai-actors/plans/{id}/reject` | Reject (reason required); no action runs |
| `POST /beacon/api/ai-actors/plans/{id}/request-revision` | Ask for changes (feedback required): a new plan is generated with an incremented version and a link to its parent |

Plan statuses: `PendingApproval`, `Executing`, `Executed`, `Rejected`, `Expired`, `RevisionRequested`. Only a
`PendingApproval` plan accepts a decision; the decision is a conditional update from that status, so a plan already
decided (or decided by someone else at the same moment) answers `409`.

A plan expires `Beacon:AiActors:PlanExpiryDays` days (default `7`, 1–365, validated at startup) after it was proposed.
Expiry is computed when a plan is read, not written to the plan: an expired plan keeps the status `PendingApproval` in
its row, but it is left out of `pending-plans`, approving, rejecting or revising it answers `409` saying it expired, and
it no longer holds back the actor's next cycle. A plan read and each pending-plan summary carry `expiresAt`; a plan
read also carries `isExpired`.

The creator or an Admin decides a plan. While the [approval workflow](/security/approvals/) is enabled, approving takes
a second person: an Approver (an Admin holds the permission) with write access to the data source, other than the
creator and anyone who asked for a revision of the plan, unless the host allows self-approval
(`ApprovalWorkflow.AllowSelfApproval`).

### Query change approvals

When an approved plan's actions run, refinements of locked queries are skipped and recorded as failed actions, and
the actor only touches queries and subscriptions it owns, within `MaxQueries` and `MaxSubscriptionsPerQuery`. Query SQL
is validated without running it (the save checks and the engine's non-executing validation, such as `EXPLAIN` without
`ANALYZE`). With the approval workflow enabled, a refined or created query's SQL is submitted for approval with the
actor's creator as requester, and the plan's reviewer and revision requesters are recorded as contributors who cannot
approve it; a subscription is created only for a query whose active version is approved for its exact content. See
[Queries → Approval workflow](/features/queries/#approval-workflow).

## Cost tracking

Each execution and plan records tokens used, estimated cost and the model; the actor keeps cumulative totals
(`TotalTokensUsed`, `TotalCost`) shown on its page. `MaxQueries` and `MaxSubscriptionsPerQuery` bound what an actor may
create.

## Troubleshooting

**Actor is `Failed`.** Setup or a think cycle threw; the actor's last error has the message. Fix the cause (often the
LLM configuration or data source connectivity), then resume the actor.

**Think cycles show `Skipped`.** The creator may no longer work ad hoc with the data source. Restore their access, or
have an Admin or UserAdmin change the owner.

**"This AI actor thought recently".** The cooldown above; wait for the minutes the message names or change `Beacon:AiActors:ThinkCooldownMinutes`.

**No automatic think cycles.** Background cycles follow runs of subscriptions linked to the actor, and an actor whose
actions are proposed has none. Start cycles with `POST /beacon/api/ai-actors/{id}/think`.

**"This plan is no longer pending approval".** The plan was already decided or revised. **"This plan expired before it
was decided"**: it passed `Beacon:AiActors:PlanExpiryDays`. Fetch the actor's pending plans.

**"This AI actor has a plan pending approval".** Decide that plan (or let it expire) before refining the actor; its
think cycles are skipped meanwhile.

## Related features

- [AI Integration](/features/ai-integration/): provider configuration and what reaches the provider
- [Queries](/features/queries/): the query and versioning model
- [Subscriptions](/features/subscriptions/): scheduling and delivery
- [Approvals](/security/approvals/): the approval workflow
