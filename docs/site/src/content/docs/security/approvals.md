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
| `CoversDataSourceConnections` | `false` | New data sources, and connections changed in anything but their secrets, connect to nothing until approved. |
| `CoversLlmConnection` | `false` | A change of the stored LLM connection's provider, endpoint, region or authentication mode is staged until a live Admin approves it. |
| `CoversAdminRoleGrants` | `false` | Giving a user the Admin role waits until another live Admin approves it, whatever `AllowSelfApproval` says. |
| `LlmConnectionRequestDays` | `7` | Days a staged LLM connection change waits for a decision before it expires (held between 1 and 30). |

The per-kind switches apply only while the workflow is enabled. Configuration is in code; there are no `appsettings` keys for it.

## Who approves

Decisions need the **Approver** [functional permission](/security/authorization/). Admins hold it too.

- The **requester never approves their own change**, however either of them signed in, and neither does anyone else who shaped it (for an AI actor's change: the plan's reviewer and whoever asked for its revisions). Admins are no exception.
- Four eyes binds per Beacon user. Do not give one person two accounts that can approve.
- API keys hold no functional permission and MCP has no approval tool, so neither can approve anything.
- Approvers see every request on **Approvals** (`/beacon/approvals`), including requests for queries they otherwise cannot read; other users see the requests of resources they can read. A change of the LLM connection and a grant of the Admin role are the exception: only a live Admin reads and decides them, their requester included.
- A decision on a change request (approving or rejecting) checks the reviewer again in the user store when it is made, after the lock on what it decides: a reviewer disabled, archived, signed out everywhere or stripped of the Admin role or the Approver permission while the decision waited is refused (`403`) and nothing is decided. A role change, a disable or an archive of the reviewer that starts meanwhile waits until the decision commits.

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
| Data-source connection (`CoversDataSourceConnections`) | New data source, or any change of its connection but the secrets (host, port, database, login, any other setting) | The source stays listed but connects to nothing: every query, run and metadata scan of it is refused. A new source has no catalog until approved. |
| LLM connection (`CoversLlmConnection`) | A change of the provider, endpoint, region or Bedrock authentication mode in [Admin settings](/security/admin-settings/), or a first stored provider over the host's `Beacon:LLM` | Nothing changes until approved: the stored connection and models stay in use. The change is staged, encrypted. |
| Admin role grant (`CoversAdminRoleGrants`) | A user created with the Admin role, or the Admin role added to a user's roles | The user does not hold the Admin role. The rest of the save (other roles, the new user) applies at once. |

When a change lies only in a part the review shows masked (a token, a header value), the approval must carry a comment saying how it was checked.

A data-source connection is reviewed by its host, port, database and login and the names of the other settings that changed, never a secret, with the projects the source is linked to now. A change of the secrets alone (a rotated password) applies at once; a connector that cannot read the connection setting by setting (BigQuery, Databricks, CloudWatch) shows only a fingerprint, so every change of such a source needs approval with a comment. Approving checks the server against the declared engine first, once the source still holds the connection the request proposes: a mismatch, a server that cannot be reached or whose engine cannot be read, and a disabled source (enable it first) answer `409` and the request stays pending. A source created under the workflow and never approved shows that it waits for its first approval to everyone who reads it, also when no request waits any more (its first request was rejected): save its connection again to submit it. A change that waits for approval needs a requester Beacon can name (`403` otherwise); a rename, a metadata option or a rotation of the secrets does not. After the approval the source's metadata is refreshed (an API source created while held imports its endpoints). Test connection stays outside the approval. A requester who is no Approver withdraws a held change by deleting the source or saving the approved connection back.

An LLM connection change stages the whole resulting connection: the four governed fields, the models from the same save and every credential (a new one, or the stored one), encrypted with `Beacon:EncryptionKey`. The rest of the save (the base URL, the budget, the limits) applies at once. A change of credentials or models alone is not reviewed and applies at once, and so does testing the connection. Only a live Admin reads and decides it, the requester included, and never the requester while self-approval is off, so it needs a second Admin. The reviewer sees the provider, region, authentication mode and models, the endpoint as scheme and host with a fingerprint of the whole endpoint, a fingerprint of each credential and of the connection it was made from, and never a credential, the endpoint's path or the staged values; a change that lies only in the endpoint's path or query is approved with a comment. One change waits at a time: another person's save of the connection answers `409`, the requester's own earlier change is superseded. An approval applies only while the stored connection is still the one the change was made from (a key rotated since makes it stale: `409`, and the change is superseded), and only while its provider is installed. A save is judged against the stored connection, never a copy another Beacon process may still cache. Saves of the connection and decisions on its changes take one lock in Beacon's database (a transaction-scoped advisory lock on PostgreSQL, `sp_getapplock` on SQL Server) before they read it, so they run one after the other: a key rotated while an approval is being decided either lands first and makes the change stale, or waits and is judged against the connection approved. The approval writes the connection as the requester's change in the settings history, and applies it at once; when this process cannot apply it, the approval stands and answers `409` saying the previous connection stays in use here until Beacon restarts. What a change staged is wiped as soon as it can no longer be applied: approved, rejected, withdrawn, superseded, stale, or expired after `LlmConnectionRequestDays` (an expired change answers `409`). The re-encryption run moves the staged values to the current key; a waiting change made under a previous key keeps the previous key needed until it is decided or expires.

A grant of the Admin role is recorded by ids only: the user's `Users.Id` and the role's id and name, never a user name, e-mail address or external id; the reviewer sees the user by display name (or "User {id}"), read live. Removing the Admin role, and granting or removing a functional role, are not reviewed and apply at once. Only a live Admin reads and decides a grant, never its requester, also while `AllowSelfApproval` is on, so it needs a second Admin (startup warns with fewer than two); a caller Beacon cannot name cannot request one (`403`). Saving the same grant again while one waits adds no second request. An approval applies only while the user still exists, is not archived and does not hold the Admin role yet (`409` otherwise); it adds the role, recorded as assigned by the requester, ends every session the user holds, and records `identity.roles_changed` with `approvals.change_approved`. Under the switch a user provisioned at a first sign-in (SSO, MCP) is never given the Admin role: a host whose default role is Admin provisions the user with no role and logs a Warning (Beacon's own SSO and MCP defaults already refuse Admin at startup). `IUserManagementService.AssignRoleAsync` refuses the Admin role under the switch; first-run setup still makes the super admin an Admin. Identity-provider app roles map onto functional permissions only, never the Admin role.

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
| `GET /beacon/api/approvals/changes` | Approver, or readers of the resource | Change requests: migration jobs, Custom SQL rules, recipients, data-source connections; the LLM connection's and the Admin role's grants for a live Admin only. |
| `GET /beacon/api/approvals/changes/{id}` | Approver, or readers of the resource | Change request detail with its payload hash (`404` for an LLM connection change or an Admin role grant unless the caller is a live Admin). |
| `POST /beacon/api/approvals/changes/{id}/approve` | Approver (a live Admin for an LLM connection change, `403` otherwise) | Body `{ "comment": "…", "expectedPayloadHash": "…" }`. |
| `POST /beacon/api/approvals/changes/{id}/reject` | Approver (a live Admin for an LLM connection change, `403` otherwise) | Body `{ "comment": "…" }`. |

The reviewer is always the signed-in caller, never an id in the request. Decisions push an `ApprovalUpdated` event to the requester and reviewer over the realtime hub.

Every submission and decision is recorded in the [security audit trail](/security/audit-trail/#event-catalogue): `approvals.query_change_*` for query versions, `approvals.change_*` for change requests (with the kind, the resource id and the requests a submission supersedes) and `approvals.ai_plan_*` for AI actor plans. The reviewer is the event's actor, and a decision commits together with its event; a decision on a query change or a change request also names its requester by user id. Neither the proposed content nor a comment is stored in the event.

## Turning it on in an existing installation

1. Make sure at least two people hold Approver.
2. Enable the workflow; leave strict mode off for the first deployment.
3. Check the startup log: Beacon counts queries whose stored SQL is not versioned or approved.
4. Have owners save those queries (submitting them), approve them, then turn on `RequireApprovedVersionToRun`.
