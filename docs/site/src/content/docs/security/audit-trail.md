---
title: Security audit trail
description: An append-only, signed record of who signed in, changed identities, keys, access, data sources, delivery, settings, saved queries, approvals, ownership, tasks, curation, data quality and migrations, for an Auditor, verified on a schedule, with a copy on the Beacon.Audit log stream for your SIEM.
---

Beacon records every security-relevant action in one append-only trail: sign-ins and sign-outs, changes to users,
roles, API keys and project access, data sources, recipients and subscriptions, settings, saved queries, approvals,
ownership, alert tasks, grounding curation, data contracts and migration jobs, and every read of audit data. Each event
says who acted, what they did, how it ended and what it concerned, carries a keyed signature over its fields, and is
copied to the `Beacon.Audit` log stream once it is committed, so a SIEM holds a copy outside Beacon's database.

The trail is read by an **Auditor** (an Admin holds the permission too, see
[Functional permissions](/security/authorization/#functional-permissions)) on the **Security Audit** page or through
the API. It is separate from the [MCP audit](/mcp/observability-and-audit/), which records every MCP tool call
and the statements it ran.

**The database is the source of truth.** The `SecurityAuditEvents` table is the trail; the log-stream copy is a
best-effort second copy (see [What the trail does and does not prove](#what-the-trail-does-and-does-not-prove)).

## What an event records

Events are stored in the `SecurityAuditEvents` table (`security_audit_events` on PostgreSQL), one row per event. No
column has a foreign key: an event outlives the user, key or project it names.

| Field | Meaning |
|---|---|
| `Id`, `EventGuid` | The row id, and a unique id the log-stream copy carries too. |
| `CreatedTime` | When the event was recorded (UTC, kept to the microsecond). |
| `Category`, `Action` | The event family and what happened, named `{category}.{what happened}` in the past tense; see the [catalogue](#event-catalogue). |
| `Outcome` | `success`, `failure` (the attempt did not succeed, such as a wrong password), `refused` (Beacon refused it by rule: local login off, a setup token that does not match, a purge of events that fail verification) or `empty` (there was nothing to act on: a verification that found no event to check, which is not a success). |
| `ActorKind` | How the actor was authenticated: `user`, `session` (a signed-in session without a stored Beacon user), `api_key`, `mcp_caller`, `system` (Beacon's own background work), `embed` (an embed grant or token, recorded as the grant's creator) or `anonymous`. |
| `ActorUserId` | The acting user's `Users.Id` (an API key's owner); `null` for a caller without a stored Beacon user. |
| `ApiKeyId` | The API key the actor used. |
| `CallerHash` | For a caller without a Beacon user (a single sign-on subject, a host session, an MCP caller, the name typed at a failed password sign-in): a keyed hash, never the subject or the name itself. For an MCP caller it is the same hash the MCP audit records. |
| `ProjectId` | The project the action concerns, when it concerns one. |
| `ResourceType`, `ResourceId` | What was acted on: `user`, `api_key`, `project`, `data_source`, `repository`, `query`, `subscription`, `recipient`, `data_contract`, `migration_job`, `ai_actor`, `ai_actor_plan`, `approval_request`, `change_approval_request`, `task`, `settings`, `learned_pattern`, `documentation_patch`, `eval_case`, `schema_relationship`, `glossary_term`, `documentation_section`, `mcp_audit`, `embed_grant` or `security_audit`. |
| `DetailsJson` | What else the event needs, as a small JSON object of identifiers, counts, flags, codes and keyed digests (below). |
| `TraceId` | The W3C trace id of the request or job that recorded the event. |
| `KeyId`, `Hmac` | The integrity key the event was signed with, and the signature; see [Integrity](#integrity-signatures-and-verification). |

The details hold identifiers (`addedRoleIds`, `removedRoleIds`, `recipientIds`, `changeApprovalId`, the settings-history
ids of a settings change), counts, flags (`secretChanged`, `destinationChanged`, `connectionChanged`, `forced`), times
(UTC, to the second) and short codes (a reason, a kind, the keys of the settings that changed, the fields of a data
source that changed, a status before and after). People a resource records by their identity-provider id (an owner, a
requester, an assignee) are written as their `Users.Id`, or `null` for nobody or an unknown id.

A settings change also carries, for each key it changed and in the order of `changedKeys`, a keyed digest of the value
before and after (`beforeDigests`, `afterDigests`: 32 hex characters under a key derived from the integrity key; an
absent value has a digest of its own). The values never go into the event; the digests let an Auditor tell whether the
settings history still holds the values the event was sealed over; a history row edited later no longer matches.

A glossary term's and a golden case's events carry their grounding text the same way: `contentFields` names the text
fields (a term's `term`, `synonyms`, `definition`, `targetSchema`, `targetTable`, `targetColumn` and `metricExpression`;
a case's `question` and `goldSql`), and `beforeDigests` / `afterDigests` hold a keyed digest of each one's stored text in
that order: before and after an edit, after a creation or a promotion, before a deletion. The text never goes into the
event; the digests show which field an edit changed. A digest is keyed: only a holder of the integrity key can check a disclosed text against it, and digests made under different keys do not compare.

### What is never stored

The trail names people by id only and keeps no secret. The details are restricted by construction, not left to care at
each event site:

- **Never stored:** names, display names and user names, e-mail addresses, the sign-in name someone typed, external
  (identity-provider) ids, IP addresses, user agents, SQL, questions, comments, notes, descriptions, free-text errors,
  setup tokens, passwords, API keys, credentials, connection strings, setting values and recipient destinations. A
  change of a recipient's destination or secret, or of a data source's connection, is recorded as a flag
  (`destinationChanged`, `secretChanged`, `connectionChanged`), never the value. A single sign-on subject is recorded
  only as its keyed `CallerHash`; so is the name typed at a failed password sign-in (trimmed and lower-cased, under a key
  of its own), so attempts on one name can be told from attempts spread over many without the name being stored.
- **Codes only.** A text detail is kept only when it is a code (letters, digits and `_ . : -`, at most 64 characters)
  and its key does not name text (a name, an e-mail address, mail, SQL, a question, a comment, a note, a description,
  text, a message, a destination, a URL, a token, a secret, a password, a header, a body, an address, an agent, a
  subject, an external id or a login, in any case and anywhere in the key). Anything else is stored as `[withheld]`, so a
  mistake at an event site cannot store free text; the Security Audit page shows such a detail as *withheld*.
- **Bounded.** A list of ids keeps its first 50 entries and records how many there were (`<key>Count`). The whole details
  object stays under 2,000 characters: when it would not, every single value is kept and the lists are shortened, the
  longest first, each one's full length recorded as `<key>Count`, and the details are marked `"truncated": true`.

## Event catalogue

Every action belongs to one category, the part of its name before the dot.

**Authentication** (`auth`), recorded on their own, since a sign-in has no change to join (see
[How events are written](#how-events-are-written)).

| Action | When |
|---|---|
| `auth.password_sign_in_succeeded` | A password sign-in issued a session. |
| `auth.password_sign_in_failed` | A password sign-in did not succeed (`failure`); names the user the sign-in name resolved to, when it did, and the attempt by a keyed hash of the name typed. |
| `auth.password_sign_in_refused` | A password sign-in refused by rule (`refused`): local login is off and the account is no break-glass account (`local_login_off`), or the password was right but an administrator set it and the user must choose a new one first (`password_change_required`), or the user is locked out after too many wrong passwords, whatever password was sent (`locked_out`). |
| `auth.break_glass_signed_in` | A break-glass account signed in with its password while local login is off. |
| `auth.signed_out` | A session signed out. |
| `auth.signed_out_everywhere` | Every session of a user was ended (**Sign out everywhere**, by the user or an administrator). |
| `auth.session_ended` | Beacon ended a session: refused at sign-in or at a check against the user store (the user was disabled, archived or their sessions revoked), with the reason. |
| `auth.sso_signed_in` | A single sign-on was admitted. |
| `auth.sso_refused` | A single sign-on was refused, with the reason; the subject only as its keyed hash. |
| `auth.setup_token_refused` | First-run setup was attempted with a setup token that does not match. |
| `auth.user_locked_out` | An internal user's password sign-in was locked after too many wrong passwords in a row (`Beacon:RateLimits:Lockout`); names the user, with the number of attempts, the lockout length in minutes and when it ends. |

**Identity** (`identity`)

| Action | When |
|---|---|
| `identity.super_admin_created` | First-run setup created the super admin. |
| `identity.user_created` | An administrator created a user. |
| `identity.user_provisioned` | Single sign-on created a user at their first sign-in. |
| `identity.user_updated` | A user's account changed (which fields changed, as flags, and whether their sessions and API keys ended with it). |
| `identity.user_enabled`, `identity.user_disabled`, `identity.user_archived` | A user was enabled, disabled or archived (with how many of their API keys were revoked). |
| `identity.last_super_admin_change_refused` | Disabling or archiving the last (enabled) super admin was refused; the event is `refused` and carries the attempt as a code. |
| `identity.password_changed` | A user changed their own password. |
| `identity.password_reset` | An administrator set a user's password. |
| `identity.roles_changed` | A user's roles changed (role ids added and removed). |
| `identity.owned_resources_read` | Someone read what a user owns, created or changed last (the kind and the page). |

**API keys** (`keys`): `keys.api_key_created`, `keys.api_key_revoked` (by its owner) and
`keys.api_key_revoked_by_administrator`.

**Project access** (`access`): `access.project_created`, `access.member_added`, `access.member_role_changed`,
`access.member_removed`, `access.access_mode_changed` (a project opened or restricted), `access.data_sources_linked`,
`access.data_source_unlinked`, `access.repository_token_changed` (whether a token is now set, never the token),
`access.repository_scan_requested`, `access.repository_scan_ran` (a background scan ended: its task, the ids of the
repositories it scanned and how many failed, were partial or were not scanned; `refused` with the reason when the task
was skipped). Events recorded before dashboards were removed keep their
`access.dashboard_shared` and `access.dashboard_share_revoked` actions and the `dashboard` resource type, and the page
still filters by them. The project-access log events (`ProjectAccessEvents`, 7101 onwards) are still written
as before; refused and hidden reads of a project (7108, 7109) and skipped scheduled work (7111) stay on the log only, for
their volume.

**Data sources** (`data_sources`): `data_sources.data_source_created`, `data_sources.data_source_updated` (which fields
changed, by name), `data_sources.credentials_rotated` (the connection changed: its credentials, host or database; never
a value), `data_sources.data_source_deleted`, `data_sources.metadata_refreshed` (a forced refresh someone started, and
whether they were exempt from its cooldown), `data_sources.migration_writes_changed`,
`data_sources.data_source_disabled` and `data_sources.data_source_enabled` (the data source id only). An edit that
changes the connection is recorded as `credentials_rotated` only, with the other fields it changed; both edit events
carry `targetChanged`, whether the connection now points at another engine, host, port or database (never which).

**Delivery** (`delivery`): `delivery.recipient_created`, `delivery.recipient_updated` (which fields changed, whether the
destination or the secret changed, and the change request that governs it), `delivery.recipient_deleted`,
`delivery.subscription_created`, `delivery.subscription_updated`, `delivery.subscription_archived`,
`delivery.subscription_reactivated`, `delivery.subscription_recipients_added`,
`delivery.subscription_recipient_removed` and `delivery.subscription_sla_changed` (the SLA hours before and after).

**Settings** (`settings`): one event per save that changes something (`settings.mcp_settings_changed`,
`settings.mcp_project_settings_changed` and `settings.admin_settings_changed`), naming the keys that changed, the
settings-history rows that hold the values before and after, and a keyed digest of each value (above). The values
themselves never go into the event; an Auditor reads them in the settings history.

**Saved queries** (`queries`): `queries.lock_changed` (a query locked or unlocked) and `queries.mcp_tool_changed` (a
query exposed to MCP callers as a tool, renamed, or withdrawn).

**Approvals** (`approvals`): a change submitted for a second person's review, and the decision on it.
`approvals.query_change_submitted`, `approvals.query_change_approved`, `approvals.query_change_rejected` (query
versions); `approvals.change_submitted`, `approvals.change_approved`, `approvals.change_rejected` (change requests of
migration jobs, Custom SQL rules, recipients, data-source connections and the LLM connection, with the requests a
submission supersedes);
`approvals.ai_plan_submitted`, `approvals.ai_plan_approved`, `approvals.ai_plan_rejected` and
`approvals.ai_plan_revision_requested` (AI actor plans); `approvals.ai_actor_created` (an AI actor created, with its
data source and whether its changes wait for approval); `approvals.ai_actor_think_started` (an on-demand think cycle
accepted as a background task, with the task id); `approvals.ai_actor_archived` (an AI actor archived; archiving one
already archived changes and records nothing).

**Ownership** (`ownership`): `ownership.owner_changed`: the owner of a subscription, data contract or AI actor changed,
with the previous and the new owner as `Users.Id`. Setting the owner a resource already has changes nothing and records
nothing.

**Alert tasks** (`tasks`): `tasks.task_claimed`, `tasks.task_reassigned`, `tasks.task_resolved` and
`tasks.task_auto_resolved` (recorded with the `system` actor when the data recovered).

**Grounding curation** (`curation`): `curation.pattern_status_changed` (status before and after),
`curation.documentation_patch_applied`, `curation.documentation_patch_rejected`, `curation.golden_case_promoted`,
`curation.golden_case_edited`, `curation.relationship_created`, `curation.relationship_verified`,
`curation.relationship_unverified`, `curation.relationship_edited`, `curation.relationship_deleted`,
`curation.grounding_rechecked` (a run of the grounding recheck, with what it checked and refused),
`curation.glossary_term_created`, `curation.glossary_term_updated`, `curation.glossary_term_deleted`,
`curation.documentation_section_edited`, `curation.documentation_generation_started` (a documentation regeneration someone
started, with its task id) and `curation.documentation_generated` (a documentation generation finished, on the project, by
the user who started it or the `system` actor: the new documentation's id, its section count, how many curated sections it
kept and how many sections failed to generate).

**Data quality** (`data_quality`): `data_quality.contract_created`, `data_quality.contract_updated`,
`data_quality.contract_archived` and `data_quality.contract_evaluated` (an evaluation someone started; scheduled
evaluations are not recorded).

**Data migrations** (`migrations`): `migrations.job_created`, `migrations.job_deleted`, `migrations.job_run_requested`
(a run started as a background task, with its task id and where the caller's MigrationOperator permission came from:
`role` or `idp`), `migrations.job_started` (a run
began (by the caller, or the `system` actor for a scheduled run), recorded before it touches a data source, with the
execution id and the source and destination data sources) and `migrations.job_run_ended` (`success`, `failure` with the
row counts, or `refused` before it started, with the reason: `not_approved`, `write_target_refused`, `run_in_progress`,
or for a background run task, with its task id, `forbidden` or `abandoned`).

**Embedding** (`embed`): `embed.grant_created` (with the project, query and version ids, the number of origins and
locked parameters and the days until it expires; never an origin or a parameter value) and `embed.grant_revoked`.
Revoking a revoked grant records nothing. `embed.token_issued` (a grant secret exchanged for a token, with its lifetime
in seconds), `embed.token_refused` (outcome `refused`, the `embed` actor never set: an embed credential that was presented
and refused, with the reason `expired`, `invalid_signature`, `revoked`, `grant_expired`, `superseded`, `unknown` or
`origin`, and the grant when the credential named one; a request from another origin that carries no credential is
refused without an event; at most 600 a minute are recorded, and a client past its refusal budget is answered 429 without
one) and `embed.query_rendered` (each render of a grant's query, with the query and version ids, the row count and whether
the rows were cut at the cap; outcome `failure` with the reason `render_failed` (also when the run throws), `busy` (no
render slot free; within the same 600-a-minute cap as refusals) or `unrunnable`, or `refused` with `parameters`). None carries a token, a secret, an origin, a parameter value or SQL.

**The audit itself** (`audit`): `audit.read` (every read of the trail, with the filters' shape and the page),
`audit.verified` (every verification, with its counts; outcome `failure` when an event failed, `empty` when nothing was
checked; the scheduled verification's also with its checkpoint id and the ids it passed as never committed),
`audit.purged` (every retention purge, with the number of events it verified, deleted and left for the next purge;
`refused`, deleting nothing, when an event it would delete fails verification) and
`audit.mcp_audit_read` (every read of the [MCP audit](/mcp/observability-and-audit/#reading-the-audit)
export, with the filters' shape and the page), and, by the system, the verification and sealing start of each sealed
audit table: `audit.mcp_audit_verified` and `audit.mcp_audit_sealing_started` ([The MCP audit rows](#the-mcp-audit-rows)),
`audit.execution_log_verified`, `audit.execution_log_sealing_started`, `audit.setting_history_verified` and
`audit.setting_history_sealing_started` ([The execution log and the settings history](#the-execution-log-and-the-settings-history)).

## How events are written

- **With the change they record.** An event that records a change is added to the same unit of work as the change
  (`ISecurityAuditRecorder.StageAsync`, or `SaveWithEventsAsync` when the event needs what the save produces, such as a
  new id): it commits with the change and rolls back with it, so a rolled-back change leaves no event and an event that
  cannot be stored fails its change. The actor is resolved before the change runs, so no second connection is opened
  while the change holds its locks.
- **Retries.** `SaveWithEventsAsync` opens its transaction through the context's execution strategy (unless the caller's
  transaction is open, whose own strategy then owns retries). A bulk update or delete is run again on a retried attempt,
  on a cleared change tracker, so no event of the failed attempt is saved twice. Tracked changes are saved again only
  when the failed attempt never saved them; once a save accepted them, the retry fails with nothing committed, and the
  request fails rather than saving a change without its event.
- **On their own.** An event without a change to join (a sign-in, a refusal) is written by
  `ISecurityAuditRecorder.RecordAsync`, which never fails the action it records. It is not cancelled with the request (an
  aborted sign-in attempt is still recorded) but is given up after five seconds, so a database that does not answer
  never holds up a sign-in. An event that could not be written goes to the log stream instead, with every field it would
  have been stored with (`Beacon.Audit` 9107 at Error, `Persisted=False`), and is counted on the
  `beacon.security_audit.write_failures` counter (tagged `beacon.security_audit.action`). Alert on that counter: each
  increment is an event the database does not hold.
- **Reads of audit data fail closed.** A read of the trail, of the MCP audit export or of a user's owned resources, and a
  verification, is recorded before anything is returned, and fails when its event cannot be written: audit data is never
  handed out without a record of who read it. An identical read (the same filters and page) by the same actor within 60
  seconds is recorded once per Beacon process, so paging back and forth does not flood the trail.
- **Append-only, inside Beacon.** Beacon refuses to change or delete an event through its data context (an
  `InvalidOperationException`, before any statement is sent); only the retention purge deletes events. See
  [What the trail does and does not prove](#what-the-trail-does-and-does-not-prove) for what lies outside Beacon.
- **The actor** is the request's caller as Beacon resolved it (`IBeaconActorAccessor`), never a value from the request
  body; sign-in events name the user they signed in.

A host that wires Beacon's sign-in endpoints without `AddBeaconServices` must register `ISecurityAuditRecorder` itself:
the sign-in events require it, and a sign-in without it fails instead of going unrecorded.

## Reading the trail

The **Security Audit** page (in the Admin section of the sidebar, shown only to an Auditor or an Admin) lists the
events newest first, 50 to a page, for a window of whole UTC days: the last seven by default, at most 93. Filter by
category, action (the actions of the chosen category), actor user id, resource type and id, and project id; click an
actor to show only their events. **Details** shows every field of an event and its details, a withheld value as
*withheld*. The page reads nothing while the window is invalid.

The same read is `GET /beacon/api/security-audit` (Auditor; any other caller gets `403`):

| Query parameter | Required | Notes |
|---|---|---|
| `from`, `to` | Yes | UTC (a value without an offset is taken as UTC). `to` must not be earlier than `from`, and the window must not exceed 93 days (`400` otherwise). |
| `category`, `action`, `actorUserId`, `resourceType`, `resourceId`, `projectId` | No | Exact-match filters. |
| `page`, `pageSize`, `sort` | No | Zero-based page, page size up to 200 (default 20), newest first unless `sort` says otherwise. |

Every read is recorded first (`audit.read`, and `Beacon.Audit` 9105 on the log stream with the count returned), with
which filters were used (a filter's text value only when it is a code) and fails when it cannot be. The trail is not
filtered by project membership: an Auditor reads every project's events, which hold no project data.

## Integrity: signatures and verification

Each event carries an HMAC-SHA256 over every other field but its database id (in a fixed order, with its time to the
microsecond and the id of the key) under the current integrity key, and the id of that key (`KeyId`, `k1-` and a digest
that reveals nothing of the key). The database id is not covered: it is generated by the database after the event is
sealed.

**Verify** checks events against their signatures:

- On the Security Audit page, **Verify shown events** checks the events of the current page and **Verify** on a row (or
  in its details) checks one. Each row then shows the verdict the server gave it: *Intact*, *Modified* (changed or
  forged), *Unknown key* (signed with a key this deployment does not hold), *Not found* (not in the window), or
  *Unverified* when the answer named no verdict for it; only *Intact* says the event verified. A banner gives the
  counts; a verification that found nothing to check says *Nothing was verified* (it is not a success), and while
  another verification runs the page says *Verification busy*.
- `POST /beacon/api/security-audit/verify` with `{ "from": "…", "to": "…" }` checks every event of a window (at most 93
  days), and with `"eventIds": [ … ]` (at most 200) only those events of it. Events are read in batches of 1,000. The
  answer:

| Field | Meaning |
|---|---|
| `checkedCount`, `intactCount` | Events checked, and how many of them verified. |
| `modifiedCount` | Events whose fields no longer match their signature: changed or forged, or written without the key. |
| `unknownKeyCount` | Events signed with a key this deployment does not hold (a key no longer configured, or a forged key id). It may be genuine; Beacon cannot tell. |
| `outOfRangeCount` | Events dated outside the range a genuine event can have (later than five minutes past now, or older than the retention plus two days, taking the longest retention accepted while none is set), counted as failing and not checked. |
| `outcome` | `success` (every checked event intact), `failure` (any event failed or was out of range) or `empty` (nothing was checked, which is not a success). |
| `failedEventIds` | The ids of the first 100 failing events; the counts say how many failed. |
| `verdicts` | For a verification by id: one `{ "eventId", "verdict" }` per requested id, in the order asked: `intact`, `modified`, `unknown_key` or `not_found` (not in the window: never stored, deleted, or out of range). `null` for a whole window. |

One verification runs at a time per Beacon process, the scheduled one included; another request answers `429` until it
has finished. Every verification is recorded (`audit.verified`, and 9105) before its result is returned, and fails when
it cannot be. A verification finds changed and forged events; **it cannot find a deleted one**, which leaves nothing to
check; see the checkpoints below.

### Scheduled verification

`ISecurityAuditVerificationService.VerifyNewEventsAsync` verifies the events written since its last run, so a changed or
forged event is found within the schedule's period without anyone asking:

- It continues after the event id its last intact checkpoint names (a `system` `audit.verified` event; one that does not
  verify itself is passed over and logged) and checks every newer event, in id order, at most 50,000 per run; a backlog
  is worked off over the following runs. Events are selected by id alone, never by their time: the time is one of the
  fields the signature covers, so an event whose time was changed fails as *modified*, and one that verifies but is
  dated where no genuine event can be is counted as out of range.
- **The checkpoint advances only as far as every lower id has been checked.** An id with no event below a stored one
  (an insert whose transaction has not committed yet) is waited for: the checkpoint stays below it, and the events above
  it, already checked, are checked again by the next run, which also checks the late event once it is there. Once the
  nearest intact event above the missing id is an hour old, the id is taken as **never committed** (an insert that rolled
  back still consumes its id) and passed; the checkpoint records how many ids it passed this way and the first and last
  of them, and the id still waited for, if any.
- Events that fail are logged at Error (`Beacon.Audit` 9108, with the counts and the first failing ids) and counted on
  `beacon.security_audit.verification_failures` (tagged with the action, `audit.verified`). Alert on that counter.
- Every run writes a **checkpoint** to the log stream (9109: the event id it started after and its checkpoint id, how
  many it checked and how many failed, the signature and key id of the checkpoint event, and the ids passed as never
  committed). Kept in your SIEM, the checkpoints anchor the trail outside the database: an event deleted later leaves a
  gap below a checkpoint that once covered it, and an id passed as never committed is named there.
- A run that checked events records them as a `system` `audit.verified` event, which is the next run's starting point.
- Each run then verifies the [MCP audit](#the-mcp-audit-rows) rows written since its own last run, so the one job covers
  both.

The sample host schedules it hourly as the Warp recurring job `security-audit-verify` (`15 * * * *`,
`VerifySecurityAuditEventsJob` in `Beacon.SampleProject/Warp/Jobs/McpMaintenanceJobs.cs`). **A host embedding Beacon
schedules it itself**, for example with a Warp job of its own like the sample's:

```csharp
await recurringJobPublisher.AddOrUpdateRecurringJob(new VerifySecurityAuditEventsJob(), "security-audit-verify", "15 * * * *");
```

or by calling `ISecurityAuditVerificationService.VerifyNewEventsAsync(cancellationToken)` from its own scheduler.

### Keys and rotation

```json
{
  "Beacon": {
    "Audit": {
      "IntegrityKey": "<a secret of at least 32 characters>",
      "PreviousIntegrityKeys": [ "<the integrity key used until now>" ],
      "RetentionDays": null
    }
  }
}
```

- **`Beacon:Audit:IntegrityKey`** is the secret events are signed with: at least 32 characters, supplied like the
  encryption key (a secret store or `Beacon__Audit__IntegrityKey`), never committed. A value that is too short or
  contains `${` stops the host at startup, with a message that never contains the key. Unset, Beacon derives the key, for
  this purpose only, from `Beacon:Mcp:CallerHashKey`, else from `Beacon:EncryptionKey`; no secret is used as the
  signing key itself.
- **`Beacon:Audit:PreviousIntegrityKeys`** lists keys used earlier: accepted when verifying, never used to sign.
  Verification also accepts the keys derived from the caller hash key, the encryption key and each
  `Beacon:PreviousEncryptionKeys` entry, so events signed under the derived key keep verifying while that secret is
  configured.
- **Set `IntegrityKey` before you rotate the encryption key or the caller hash key.** While it is unset, events are
  signed under a key derived from one of them, and once the old secret leaves the configuration the events signed under
  it verify as *unknown key*. With a dedicated integrity key, events written from then on do not depend on either. To
  keep verifying events signed before, keep their secret configured (as the caller hash key, a previous encryption key,
  or in `PreviousIntegrityKeys`).
- **To rotate the integrity key,** set the new value and move the old one to `PreviousIntegrityKeys`. Remove a previous
  key only once no event you still need to verify was signed with it.
- **At startup** Beacon logs, at Information, the id of the key events are sealed under and where it comes from
  (`integrity_key`, `derived_from_caller_hash_key` or `derived_from_encryption_key`), never the key, and, while
  `IntegrityKey` is unset, a Warning naming the setting the key is derived from.

An event under a key Beacon does not hold is reported as *unknown key*, not as intact: it may be genuine, but Beacon
cannot tell.

## Retention

Events are kept **forever** by default. Set `Beacon:Audit:RetentionDays` (between 365 and 36,500; any other value stops
the host at startup) to delete older events with `ISecurityAuditRetentionService.PurgeExpiredAsync`, which does nothing
while `RetentionDays` is unset:

- **Verified before deleted.** The purge first verifies every event it is about to delete, in the transaction that
  deletes them. While any of them fails (changed, or under an unknown key), it deletes nothing: it logs 9108 at Error with
  the counts and the first failing ids, counts them on `beacon.security_audit.verification_failures` (action
  `audit.purged`), records a `refused` `audit.purged` event with the failing ids, and throws, so its scheduled job fails
  visibly. Investigate the events it names before purging: tampered evidence is never deleted unseen.
- **Only what it verified is deleted.** Otherwise the purge reads the expired events again, batch by batch, verifies
  each, and deletes the ones that verify, each by its id together with the signature it verified, never by date. An
  event changed after it was verified (its signature replaced), one that fails now, and one that became expired while the
  purge ran (an event whose time was moved back) are not deleted: they are left for the next purge, which verifies them
  before it deletes anything.
- **Deleted with its record.** The `audit.purged` event (by the `system` actor, with the number of events it verified,
  deleted and left, and the retention) commits with the deletion, in one transaction; `Beacon.Audit` 9106 follows, at
  Warning when it deleted fewer events than it verified or left expired events behind.
- **A purge that does not run** (the database is unreachable, say) is logged at Error (9110, the failure's type and code
  only), counted on `beacon.security_audit.write_failures` (action `audit.purged`) and rethrown.
- **At startup**, a host with a retention set logs that the purge deletes nothing until the host schedules it.

The sample host schedules it as the Warp recurring job `security-audit-retention` (`45 3 * * *`,
`PurgeExpiredSecurityAuditEventsJob` in `Beacon.SampleProject/Warp/Jobs/McpMaintenanceJobs.cs`). **A host embedding
Beacon schedules it itself**, like the [MCP audit purge](/mcp/observability-and-audit/#retention), for example
with a Warp job of its own like the sample's:

```csharp
await recurringJobPublisher.AddOrUpdateRecurringJob(new PurgeExpiredSecurityAuditEventsJob(), "security-audit-retention", "45 3 * * *");
```

or by calling `ISecurityAuditRetentionService.PurgeExpiredAsync(cancellationToken)` from its own scheduler.

## The execution log

Besides the trail, Beacon keeps an **execution log** (`manual_query_execution_logs`) of the statements it runs on a data
source for its own features: every statement of a query preview, a query run (from the editor, an AI actor's check, a
migration's read), a subscription run and a column value sample, every documentation enum discovery statement and every
data contract rule. (MCP calls have their own [audit](/mcp/observability-and-audit/).)

- **Written before the statement is sent.** Each statement gets a row with `status = Started` (its SQL as sent, without
  parameter values, the data source and what ran it) before it reaches the data source, and the row is closed when it
  ends: `Succeeded` (with its row count), `Failed` (with the database's error redacted as it is with PII detection on,
  whatever the caller's PII setting, so no quoted value is kept; a host data source's generic text as it is), `Cancelled`
  (the caller stopped it) or `TimedOut` (its statement timeout, on every path), and `completed_time`. A statement the
  gate refuses never runs and has no row.
- **Abandoned rows.** A row still `Started` longer than `Beacon:Query:StatementTimeoutSeconds` plus 10 minutes is a
  statement whose end was never recorded (the process stopped, the log store failed): the read and the export show it as
  `Abandoned`, and `status=Abandoned` filters for it (nothing is written; the stored status stays `Started`).
- **Who it ran for.** `triggered_by_user_id` is the `Users.Id` the statement ran for: the request's caller, or the owner
  scheduled work runs as; null for work the system does on its own. Subscription runs record the same id on their run
  history (`query_execution_history.triggered_by_user_id`).
- **Fail-open by default.** A statement whose `Started` row cannot be written still runs, and a Warning is logged. Set
  `Beacon:Audit:ExecutionLog:Required: true` to refuse such a statement instead (its caller gets an error); with it on,
  the host fails to start when no execution log (`ISqlExecutionAudit`) is registered. The end of a statement that ran is
  recorded whatever the caller's cancellation, and never in place of its result.
- **Read and export.** `GET /beacon/api/execution-log` (paged, newest first) and `GET /beacon/api/execution-log/export`
  (CSV, at most 10,000 rows; the `X-Beacon-Truncated` header says when more matched) take `from` and `to` (UTC, at most
  93 days apart) and optional `dataSourceId`, `triggeredByUserId`, `status` and `executionContext`. Both are an Auditor's
  or a SecurityAdmin's, list only the data sources the caller may read, and record an `audit.execution_log_read` or
  `audit.execution_log_exported` event (the filters' shape, the row count) before they return; they fail when the event
  cannot be written.
- **Values masked on the way out.** The stored SQL keeps the values written into it (bind values are never stored); the
  read and the export replace its string literals and numbers with `?` and drop its comments, read with the data
  source's dialect (every quoted span and number when the source or its dialect is unknown). The export also cuts each
  statement at 4,000 characters (marked ` [cut]`), and writes a cell that starts with `=`, `+`, `-`, `@`, a tab or a line
  break with a leading `'`, so a spreadsheet reads it as text.
- **Retention.** Rows are kept forever unless `Beacon:Audit:ExecutionLog:RetentionDays` is set (greater than zero);
  schedule `IExecutionLogRetentionService.PurgeExpiredAsync` daily (the sample host's `execution-log-retention` job),
  which deletes the older rows and logs `Beacon.Audit` event 9111. The log grows with every statement Beacon runs, one row
  per column value sample of a metadata scan included, so set a retention that fits your storage.
- **Not sealed.** The execution log is evidence, not signed like the trail.

## Shipping to a SIEM

Every event is logged at category **`Beacon.Audit`** once the transaction that wrote it has committed; an event whose
change rolled back is never logged. Point your log shipping at this category (JSON console logs into CloudWatch, a
collector, a SIEM agent; see [CloudWatch and collectors](/mcp/observability-and-audit/#cloudwatch-and-collectors))
to keep a copy Beacon's database cannot change.

| EventId | Name | Level | When | Fields |
|---|---|---|---|---|
| 9104 | `SecurityAuditRecorded` | Information | An event was committed. | `Action`, `Outcome`, `EventGuid`, `AuditId`, `Persisted` (`True`), `ActorKind`, `ActorUserId`, `ApiKeyId`, `CallerHash`, `ProjectId`, `ResourceType`, `ResourceId`, `Details`, `TraceId` |
| 9105 | `SecurityAuditRead` | Information | The trail was read or verified. | The window, which filters were used, the counts |
| 9106 | `SecurityAuditPurged` | Information (Warning when it left expired events behind) | The retention purge ran. | Deleted and verified counts, cutoff, retention days, expired events left |
| 9107 | `SecurityAuditWriteFailed` | Error | An event written on its own could not be stored. | The fields of 9104 with `Persisted=False` and no `AuditId`, and `Failure` (the failure's type and code) |
| 9108 | `SecurityAuditVerificationFailed` | Error | The scheduled verification or the retention purge found stored events that fail verification (or a checkpoint that does). | The counts by verdict, the first failing ids |
| 9109 | `SecurityAuditCheckpoint` | Information | Each run of the scheduled verification. | `FromEventId`, `MaxEventId`, `Checked`, `Failed`, `LastHmac`, `KeyId`, `NeverCommitted`, `FirstNeverCommittedId`, `LastNeverCommittedId`, `WaitingForEventId` |
| 9110 | `SecurityAuditPurgeFailed` | Error | The retention purge did not run. | The cutoff, the failure's type and code |
| 9111 | `McpAuditVerificationFailed` | Error | The scheduled verification found MCP audit rows that fail verification (or an MCP audit checkpoint that does). | The counts by verdict, the first failing row ids |
| 9112 | `McpAuditCheckpoint` | Information | Each run of the MCP audit verification. | `FromAuditId`, `MaxAuditId`, `Checked`, `Failed`, `Unsealed`, `LastHmac`, `KeyId`, `SealedFromAuditId`, `NeverCommitted`, `WaitingForAuditId` |
| 9113 | `AuditTableVerificationFailed` | Error | The scheduled verification found execution log or settings history rows that fail verification (or a checkpoint of theirs that does). | `Store`, the counts by verdict, the first failing row ids |
| 9114 | `AuditTableCheckpoint` | Information | Each run of the verification, once per sealed table. | `Store`, `FromRowId`, `MaxRowId`, `Checked`, `Failed`, `Unsealed`, `LastHmac`, `KeyId`, `SealedFromRowId`, `NeverCommitted`, `WaitingForRowId` |

Match a log-stream copy to its row by `EventGuid`. The copy holds the same identifiers, codes and digests as the row,
nothing more.

| Metric | Type | Alert |
|---|---|---|
| `beacon.security_audit.write_failures` | Counter, tagged `beacon.security_audit.action` | On any increment: an event written on its own was not stored (its 9107 copy holds it), or a purge did not run (`audit.purged`). |
| `beacon.security_audit.verification_failures` | Counter, tagged `beacon.security_audit.action` | On any increment: stored events failed verification (`audit.verified`, `audit.purged`). |

**The copy is best-effort.** 9104 is written by the application after the commit, so it is missing when the log
category is filtered out or below Information, when the process stops between the commit and the log write, and for a
transaction committed directly on the underlying database transaction rather than through Beacon's data context. The
database is the source of truth; the copy, the 9107 lines and the checkpoints are what lets you notice when the two
disagree.

## What the trail does and does not prove

- **Changes and forgeries are found.** An event whose fields were changed, or that was written without the integrity
  key, fails verification: on request, every hour, and before the purge deletes it.
- **Deletions are not found by verification.** Append-only is enforced inside Beacon: its data context refuses to change
  or delete an event. A database account with write access to the table can still delete rows, and a deleted row leaves
  nothing to verify. The checkpoints (9109) and the 9104 copies in your SIEM are the anchor outside the database. To make
  the table append-only at the database itself, give Beacon's connection a role that may not update or delete
  `SecurityAuditEvents` rows other than through the purge, or add triggers; that is the host's to configure. A chain of
  signatures across events, which would make a deletion visible inside the trail, is not part of this version.
- **The execution log and the settings history are sealed too**, and verified the same way as the MCP audit rows: see
  [The execution log and the settings history](#the-execution-log-and-the-settings-history), which also shows the DDL a
  DBA can apply to make the audit tables append-only at the database.
- **MCP audit rows are sealed too.** See [The MCP audit rows](#the-mcp-audit-rows): a changed row or statement is found
  by the next scheduled run, and again later by the rolling re-check; a deleted row that was already checked is found by
  the row count, one not yet checked only through the checkpoints and the 9100 copies. Rows written before this version
  are reported as unsealed. The security audit trail records who read that export.

## The MCP audit rows

The [MCP audit](/mcp/observability-and-audit/) rows (`McpAuditLogs`) are sealed the same way as events, with the same
keys and rotation: each row carries an HMAC-SHA256 (`Hmac`) under a key derived from the current integrity key for the
MCP audit only, the id of that key (`HmacKeyId`), the version of the field list it covers (`SealVersion`) and a guid of
its own (`AuditGuid`, unique in the table, so a row cannot be copied into a second one). The seal covers every field of
the row but its database id, and every statement the row holds (`McpAuditStatements`): a statement changed, removed,
added or moved to another row fails its row. A row is sealed as it is saved, with what the content retention lock leaves
of it. Beacon's data context refuses to change or delete a row or a statement, or to add a statement to a row already
saved; the MCP audit retention purge deletes whole rows by age.

Each run of the scheduled verification (`ISecurityAuditVerificationService.VerifyNewEventsAsync`) then checks the MCP
audit rows written since the last intact `audit.mcp_audit_verified` checkpoint, by id, with their statements, the same
way: *intact*, *modified*, *unknown key*, or out of range (a sealed row dated in the future, or older than
`Beacon:Mcp:Audit:RetentionDays` allows), waiting for a missing id and passing it as never committed after an hour.
Rows checked by an earlier run are checked again too, 10,000 per run, continuing where the last run stopped and starting
over at the checkpoint, so a row changed after it was checked is found within one cycle (at an hourly schedule, a table
of 240,000 rows is re-checked every day). Each run also counts the rows up to its checkpoint (under a retention, those
dated at least two days inside it, which the purge cannot reach before the next run) and compares the count the last
run recorded: fewer rows means a checked row was deleted, more that one was written below a checkpoint; both are
failures. After a pause longer than two days the comparison is skipped once (a Warning). A row deleted before any run
checked it, or one in the oldest two days of the retention, is not found inside the database: the 9100 copies and the
checkpoints are the anchor.

Rows written before this version carry no seal and are reported as **unsealed**, never as modified. When the host first
starts with sealing in place, Beacon records a `system` `audit.mcp_audit_sealing_started` event naming the highest row id
written before it; rows up to that id (or up to the first intact sealed row, when that is lower) are unsealed. While an
upgrade rolls out, a node still on the version before writes rows without a seal: those dated, by their own time, within
a day of that start, and below the first sealed row dated after that day, are unsealed too. Any other row without a seal
is a row whose seal was removed, and is reported as modified. A row sealed under the next seal version (written by a
node already on a later version) is reported at Warning as *unknown version*, never as modified; one that stays after
the upgrade is worth a look. Failures are logged at Error (`Beacon.Audit` 9111) and counted on
`beacon.security_audit.verification_failures` (tagged `audit.mcp_audit_verified`); every run writes a checkpoint to the
log stream (9112); a run that read rows records them as a `system` `audit.mcp_audit_verified` event, sealed in this
trail, which is the next run's starting point.

At startup Beacon warns when its data context runs without the interceptor that seals (`SecurityAuditInterceptor`,
registered by `UsePostgreSql` / `UseSqlServer`), and when rows exist but no MCP audit verification was recorded for two
days (the host does not schedule the verification). Beacon does not migrate its schema at startup: while the seal
columns are missing it logs an Error, and writes each MCP audit row without its seal rather than lose it (a Warning per
row); those rows are reported as unsealed. Apply the migrations before, or with, the upgrade.

A user deleted at the database, outside Beacon, sets `UserId` to null on that user's MCP audit rows (the foreign key's
`ON DELETE SET NULL`), which changes sealed rows: they are reported as modified. Beacon itself never deletes a user (a
removed user is archived); remove users through Beacon, or expect those findings.

## The execution log and the settings history

Two more audit tables are sealed the same way as the MCP audit rows, each under a key of its own derived from the
current integrity key: the execution log (`ManualQueryExecutionLogs`, the statements Beacon ran on a data source for its
own features) and the settings history (`AppSettingHistory`, one row per changed setting of a save, sensitive values as a
mask). Each row carries `Hmac`, `HmacKeyId`, `SealVersion` and its own `AuditGuid` (unique in its table); the seal covers
every field but the database id. A row is sealed as it is saved, and Beacon's data context refuses to change or delete
one.

The same scheduled verification checks both tables after the MCP audit, by id, with the rolling re-check and the row
count described above, each from its own checkpoint (`audit.execution_log_verified`, `audit.setting_history_verified`,
system events in this trail). Rows written before this version are **unsealed**: at or below the row the once-recorded
`audit.execution_log_sealing_started` / `audit.setting_history_sealing_started` event names, or within a day of that start
while an upgrade rolls out; any other row without a seal is reported as modified. Failures are logged at Error
(`Beacon.Audit` 9113, naming the table) and counted on `beacon.security_audit.verification_failures` (tagged with the
table's verified action); every run writes a checkpoint per table to the log stream (9114). The execution log's retention is
the data retention job's `execution-log` window (`Beacon:Retention:Targets:execution-log`, else `Beacon:Retention:Days`);
nothing purges the settings history. A row the purge deletes past that window is never counted, so it never fails the
verification; a counted row deleted inside it is reported as missing. An intact row older than the window (the
retention job late, failing or not scheduled) is reported at Warning, never as a failure; a sealed row dated in the
future is out of range and fails.

The content expiry (`Beacon:Retention:ContentDays`) clears an execution log row's statement text and reduces its error
to its class in place. A row whose seal verifies is sealed again over what is left, by the same update, which applies
only while the row still carries the seal that was verified; the cleared row keeps verifying, and a later change to it
is still found. A row whose seal does not verify is cleared without a new seal, so it keeps failing verification; a row
without a seal stays unsealed. A row whose seal the node cannot read (a later seal version during an upgrade, or a key
it no longer holds) is left whole, logged at Information, until a node that can verify it clears it. During a rolling
upgrade from a version without these seals, run the data retention job only on upgraded nodes: an older node clears
sealed rows without sealing them again.

Beacon does not migrate its schema at startup. While a table's seal columns are missing (read once from the database's
catalog and kept; a missing finding is read again every five minutes), Beacon logs one Warning per table and keeps
working without the seal: rows are written without it (no row is lost), the settings history reads as before, the
verification passes over the table (the job still completes) and the content expiry clears it as an unsealed store.
Apply the migration `AddAuditTableSeal` with the upgrade; rows written before it are unsealed. Beacon.Api records each
table's sealing start once the host has started, whether or not the host runs MCP.

Two kinds of row are accepted without a seal by design: a row at or below the sealing-started row, and one dated
within the upgrade grace before a run has seen a row sealed past it. Someone with write access to the database could
strip the seal from such a row unnoticed; the append-only measures below close that.
A data source deleted at the database, outside Beacon, sets `DataSourceId` to null on its execution log rows, which is
reported as modified; Beacon itself only archives data sources.

### Append-only at the database (for the DBA)

Inside Beacon these tables are append-only; a database account that may write them can still change or delete rows. The
seals find a change, the row count and the checkpoints a deletion of a row already checked. To refuse both at the
database itself, give Beacon's connection a role without `UPDATE` and `DELETE` on the audit tables, and grant `DELETE`
only to the account a retention purge runs under, if any. Beacon ships no such DDL; for example, on PostgreSQL:

```sql
REVOKE UPDATE, DELETE ON beacon.security_audit_events, beacon.mcp_audit_logs, beacon.mcp_audit_statements,
    beacon.manual_query_execution_logs, beacon.app_setting_history FROM beacon_app;

CREATE FUNCTION beacon.refuse_audit_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'audit rows are append-only';
END $$;

CREATE TRIGGER app_setting_history_append_only
    BEFORE UPDATE OR DELETE ON beacon.app_setting_history
    FOR EACH ROW EXECUTE FUNCTION beacon.refuse_audit_change();
```

On SQL Server, `DENY UPDATE, DELETE ON [beacon].[AppSettingHistory] TO [beacon_app];` (and the same for the other
tables) does the same. A trigger that refuses `DELETE` also refuses a retention purge of that table: add it only where
nothing purges the table, or let it allow the purge's account. Changing data sources and users only through Beacon keeps
the foreign keys from rewriting sealed rows.

## Table growth

The trail grows with use and, by default, is never trimmed. Sign-ins and reads of audit data are the most frequent
events; changes are rarer. A row is a few hundred bytes (details stay under 2,000 characters), indexed by time, by
actor and time, by resource, and by `EventGuid`. Plan for it like any audit table: watch its size, set
`RetentionDays` to what your retention policy requires (at least a year), and keep the long-term copy in your SIEM.

## Who created and changed what

Queries, subscriptions, recipients, data contracts and migration jobs also record who **created** them, who **changed
them last**, and when (`CreatedByUserId`, `ModifiedByUserId`, `ModifiedTime`; the caller's identity-provider id, like a
resource's owner, never a value from the request). An Auditor or a UserAdmin lists the resources a user owns, created or
changed last (for handing them over when the user leaves) in the user dialog or with
`GET /beacon/api/users/{id}/owned-resources` (see
[User Management → Owned resources](/security/user-management/#owned-resources)). Each read is recorded first
(`identity.owned_resources_read`); a UserAdmin sees the names only of the resources its projects reach, the others by id
and kind.

## Upgrading

This version records security-relevant actions in an append-only, signed trail an Auditor reads and verifies, verifies
new events on a schedule, records who created and last changed queries, subscriptions, recipients, data contracts and
migration jobs, records every read of audit data, keeps the statements every audited MCP call ran on a data source, and
lets a deployment pin the MCP audit's content on. See
[MCP observability and audit](/mcp/observability-and-audit/).

- **Events start at the upgrade.** Nothing is backfilled: the trail holds the actions taken after the upgrade only.
- **The execution log writes a row before each statement.** From `AddExecutionLogStatus` (PostgreSQL `20261024210000`,
  SQL Server `20261024210001`, additive: nullable `status`, `completed_time` and `triggered_by_user_id` on
  `manual_query_execution_logs` plus an index on `triggered_by_user_id` and `created_time`, and a nullable
  `triggered_by_user_id` on `query_execution_history`; nothing is backfilled) every statement Beacon runs for its own
  features is in the [execution log](#the-execution-log), subscription steps, column value samples, enum discovery and
  data contract rules included, so the table grows faster. Older rows keep a null `status`. A step of a query run is
  recorded as `QueryStep` (the per-step `QueryStepPreview` rows written after the step are gone; a step preview's own
  statements are `QueryStepPreview`).
  Earlier actions are only in the logs you kept (the project-access events 7101 onwards, the `Beacon.Audit` events 9100
  onwards), which are written as before.
- **Existing resources have no attribution.** Queries, subscriptions, recipients, data contracts and migration jobs that
  exist at the upgrade record no creator, no last modifier and no modification time until they are next changed; a
  user's owned resources find them by owner only.
- **Set `Beacon:Audit:IntegrityKey`, before you change the encryption key or the caller hash key.** Without it, events
  are signed with a key derived from `Beacon:Mcp:CallerHashKey`, else from `Beacon:EncryptionKey`; once the old secret
  leaves the configuration, events signed under it verify as *unknown key*. Supply it as a secret (at least 32
  characters); see [Keys and rotation](#keys-and-rotation). The host logs the key's id and
  source at startup, and a Warning while it is unset.
- **Schedule two jobs.** The sample host schedules the Warp jobs `security-audit-verify` (hourly at `:15`; it verifies
  the events written since its last run and writes a checkpoint to the log stream) and `security-audit-retention`
  (daily; a no-op until `Beacon:Audit:RetentionDays` is set). A host embedding Beacon schedules
  `ISecurityAuditVerificationService.VerifyNewEventsAsync` and `ISecurityAuditRetentionService.PurgeExpiredAsync`
  itself; nothing is verified on a schedule, and nothing is purged, until it does. The verification also checks the MCP
  audit rows.
- **MCP audit rows are sealed.** The migration `AddMcpAuditSeal` adds `AuditGuid`, `SealVersion`, `HmacKeyId` and `Hmac`
  to `McpAuditLogs` (nullable, no backfill) and a unique index on `AuditGuid`. Rows written before it are reported as
  unsealed. Upgrade every node within a day: past that, a row without a seal is reported as modified.
- **The execution log and the settings history are sealed.** The migration `AddAuditTableSeal` adds the same four columns
  to `ManualQueryExecutionLogs` and `AppSettingHistory` (nullable, no backfill) and a unique index on each `AuditGuid`.
  Rows written before it are reported as unsealed. Apply it with the upgrade: until it is, rows are written without their
  seal and not verified (one Warning per table).
- **Retention needs no action now.** Events are kept forever until you set `Beacon:Audit:RetentionDays` (365 to 36,500
  days). The purge verifies the events it is about to delete first and, while any fails verification, deletes nothing,
  records a refused `audit.purged` event and throws (its job fails): investigate the events it names.
- **Alert on two counters.** `beacon.security_audit.write_failures` (an event written on its own was not stored, and the
  log stream holds it as 9107 with `Persisted=False`; or a purge did not run, 9110) and
  `beacon.security_audit.verification_failures` (stored events failed verification, 9108). Ship the `Beacon.Audit` events
  9104 to 9114 to your SIEM; the database stays the source of truth, the 9104 copy is best-effort.
- **Reads of audit data fail closed.** `GET /security-audit`, `POST /security-audit/verify`, `GET /mcp/audit` and
  `GET /users/{id}/owned-resources` are recorded in the trail before they return anything and fail when that record
  cannot be written (so they fail while the database cannot take writes). An identical read by the same actor within 60
  seconds is recorded once. One verification runs at a time per process; another answers `429`.
- **Owner changes are made only from the owner read.** `PUT /subscriptions/{id}/owner`, `PUT
  /data-quality/contracts/{id}/owner` and `PUT /ai-actors/{id}/owner` answer `409` when the owner was changed by someone
  else (or the resource archived) since it was read; nothing is changed. Setting the owner a resource already has stays a
  no-op: it changes nothing, records nothing and does not check whether that owner may still do the work.
- **A host that wires sign-in without `AddBeaconServices`** must register `ISecurityAuditRecorder`: the sign-in, sign-out
  and single sign-on events now require it, and fail the request without it instead of going unrecorded.
- **Plan for the table's growth.** One row per event, kept forever by default; sign-ins and reads of audit data are the
  most frequent.
- **Where content is retained, the MCP audit now stores the SQL of every statement a call ran.** Besides the question, an
  `ask` keeps the text of its first execution, its repairs, its self-consistency candidates, its value-grounding probes
  and its dry runs (one `McpAuditStatement` row each, at most 100 per call; past that, a `truncated` marker row counts the
  rest), and every audit row keeps a keyed `ContentHash` of its parameters. Review this against your privacy policy:
  switch content retention off for projects that must not keep it, pin it off with `Beacon:Mcp:ForceNoContentRetention`,
  or bound it with `Beacon:Mcp:Audit:RetentionDays`. Under no retention, statements keep their keyed `SqlHash` and no
  text. Audit rows written before the upgrade have no statements and no content hash.
- **Audit content can be pinned on.** `Beacon:Mcp:Audit:RequireContent: true` pins `RetainQueryContent` on, globally and
  for every project; a save that switches it off is refused with `409`, an Admin's too, and the MCP Settings page shows
  the switch disabled with the reason. A call whose project settings cannot be read keeps its content under the lock
  (logged at Error). With `Beacon:Mcp:ForceNoContentRetention` on as well, the host does not start.
- **New pages for an Auditor.** The **Security Audit** page lists, filters and verifies the trail (shown to an Auditor
  or an Admin only), marking each verified row with the server's verdict; the user dialog of User Management shows a
  user's **Owned resources** to an Auditor or a UserAdmin, a UserAdmin seeing the names only of resources its projects
  reach.
- **Sign-in attempts name their user, never their name.** A failed or refused password sign-in of an internal user is
  recorded with the `Users.Id` the sign-in name resolved to and a keyed hash of the name typed, never the name:
  `AuthenticationResult.BeaconUserId` is set on such a result for the audit (it never signs anyone in), also when the
  password was right but must be changed first, and `AuthenticationResult.LocalLoginRefused` marks a password refused
  only because local login is off.
- **More actions are recorded**: data source edits (`data_sources.data_source_updated`) and connection changes
  (`data_sources.credentials_rotated`, recorded from this version on), saved-query locks and MCP tool exposure
  (`queries.*`), relationships created, glossary terms, documentation sections, migration job runs
  (`migrations.job_started`, `migrations.job_run_ended`) and reads of the MCP audit and of owned resources; settings
  changes carry keyed digests of each value before and after. See the [catalogue](#event-catalogue).

**Migration.** `AddSecurityAuditTrail` (PostgreSQL `20261014130000`, SQL Server `20261014130001`) is additive and changes
no stored value: it creates `security_audit_events` (indexed by `created_time`, by `actor_user_id` and `created_time`, by
`resource_type` and `resource_id`, and uniquely by `event_guid`; no foreign keys) and `mcp_audit_statements` (a foreign
key to `mcp_audit_logs` that deletes a row's statements with it, and an index on it), adds the nullable
`mcp_audit_logs.content_hash` (`character varying(64)` / `nvarchar(64)`), and adds the nullable `created_by_user_id`,
`modified_by_user_id` (`character varying(200)` / `nvarchar(200)`) and `modified_time` to `queries`, `subscriptions`,
`recipients`, `data_contracts` and `migration_jobs` (PascalCase names on SQL Server). Nothing is backfilled. Rolling it
back drops the tables and columns, and with them the trail. Append-only is enforced inside Beacon; to enforce it at the
database as well, give Beacon's connection a role that cannot update or delete `security_audit_events` rows outside the
purge, or add triggers.

**Public API.**

- New types and members:
  - `Beacon.Core.SecurityAudit`: `ISecurityAuditRecorder` (`StageAsync`, `SaveWithEventsAsync` (two overloads),
    `RecordAsync`, `CallerHashOf`; registered with `TryAdd`, so a host may replace it), `SecurityAuditEntry` (`On`,
    `InProject`, `With`, `Ending`, `By`, `Naming`), `SecurityAuditDetails`, `SecurityAuditActor` (`Anonymous`, `System`,
    `OfUser`, `OfCaller`), `SecurityAuditActions`, `SecurityAuditCategories` (with `Queries`), `SecurityAuditOutcomes`
    (with `Empty`), `SecurityAuditResources` (with `GlossaryTerm`, `DocumentationSection`, `McpAudit`; stored events from earlier versions that name the `dashboard` resource type remain listed),
    `SecurityAuditActorKinds`, `SecurityAuditIntegrity` (`CurrentKeyId`, `KeySource`, `Seal`, `Verify`, `Digest`,
    `StoredTime`), `SecurityAuditKeySources`, `SecurityAuditVerdict`, `SecurityAuditVerdicts` (`intact`, `modified`,
    `unknown_key`, `not_found`), `SecurityAuditLogEvents` (9104–9110: `Recorded`, `Read`, `Purged`, `WriteFailed`,
    `VerificationFailed`, `Checkpoint`, `PurgeFailed`), `ISecurityAuditRetentionService`,
    `ISecurityAuditVerificationService` and `SecurityAuditCheckpoint`, `SecurityAuditUserIds` and `Attribution`
    (`RecordChangeBy`, for a `BeaconActor` or the caller's id).
  - `SecurityAuditOptions` (`Beacon:Audit`: `IntegrityKey`, `PreviousIntegrityKeys`, `RetentionDays`) and
    `McpAuditOptions.RequireContent`; `BeaconTelemetry.SecurityAuditWriteFailures`
    (`beacon.security_audit.write_failures`) and `BeaconTelemetry.SecurityAuditVerificationFailures`
    (`beacon.security_audit.verification_failures`).
  - Entities: `SecurityAuditEvent`, `McpAuditStatement`, `IAttributedEntity` (implemented by `Query`, `Subscription`,
    `Recipient`, `DataContract` and `MigrationJob`, which gain `CreatedByUserId`, `ModifiedByUserId` and
    `ModifiedTime`), `McpAuditLog.ContentHash` and `Statements`; `BeaconContext.SecurityAuditEvents` and
    `McpAuditStatements`; `SecurityAuditInterceptor` (`Beacon.Core.Data.Interceptors`, added by both database providers
    beside the content-retention interceptor).
  - `Beacon.Core.Mcp`: `IMcpStatementLog` (scoped), `McpStatement`, `McpStatementsTaken` (`Dropped`),
    `McpStatementPurposes` (with `Truncated`), `McpStatementTables` (`Unparsed`, `More`, `IsMarker`),
    `McpStatementLogExtensions` and `McpContentHasher`.
  - Requests: `GetSecurityAuditEventsQuery` and `SecurityAuditEventItem`; `VerifySecurityAuditCommand(From, To,
    EventIds)` and `VerifySecurityAuditResult` (with `OutOfRangeCount`, `Outcome` and `Verdicts`, a list of
    `SecurityAuditEventVerdict(EventId, Verdict)`); `GetUserOwnedResourcesQuery`, `OwnedResourceItem` (with
    `NameWithheld`) and `OwnedResourceKind`; `McpAuditStatementItem`; `McpDeploymentLock` and
    `GetMcpDeploymentLocksResult.Locks`.
  - `AuthenticationResult.LocalLoginRefused`.
  - The sample host's Warp jobs `PurgeExpiredSecurityAuditEventsJob` and `VerifySecurityAuditEventsJob`, with their
    handlers (the sample is not shipped; copy them, or schedule the services another way).
- Changed signatures (code that constructs or implements these must change):
  - `TaskService` and `AppSettingsService` take an `ISecurityAuditRecorder` (registered by `AddBeaconServices`); a host
    that builds either itself passes one.
  - `McpAuditLogItem` (a positional record) gains `ContentHash` and `Statements`.
  - `IMcpStatementLog.TakeAll()` returns `McpStatementsTaken`, an `IReadOnlyList<McpStatement>` that also counts the
    statements not kept: code that reads the list compiles unchanged; an implementation of the interface changes.
- Changed behaviour:
  - The changes the trail records stage their event in the same unit of work, so an event that cannot be stored fails
    that change; events written on their own (sign-ins, refusals) never fail their action and give up after five
    seconds, logging the event (9107) instead. Reads of audit data fail when their event cannot be written.
  - `ISecurityAuditRecorder.SaveWithEventsAsync` replays a retried attempt only when the failed attempt never saved its
    tracked changes; otherwise the retry fails with nothing committed.
  - `ISecurityAuditRetentionService.PurgeExpiredAsync` throws `InvalidOperationException` when it refuses (an expired
    event fails verification), and rethrows when it cannot run.
  - The sign-in events resolve `ISecurityAuditRecorder` as a required service.
  - `AuthenticationResult.BeaconUserId` is also set on a failed password check, and on a right password that must be
    changed first, as above; saving MCP settings that contradict `Audit:RequireContent` throws
    `SettingLockedException` (`409`).
  - Owner changes of subscriptions, data contracts and AI actors throw `BeaconConflictException` (`409`) when the owner
    changed since it was read.
  - `McpContentHasher.Hash(null)` differs from `Hash("")`.
- REST and OpenAPI:
  - New routes: `GET /beacon/api/security-audit` (`from`, `to`, `category`, `action`, `actorUserId`, `resourceType`,
    `resourceId`, `projectId`, `page`, `pageSize`, `sort`; a paged list of `SecurityAuditEventItem`) and
    `POST /beacon/api/security-audit/verify` (`from`, `to`, optional `eventIds`; a `VerifySecurityAuditResult` with
    `checkedCount`, `intactCount`, `modifiedCount`, `unknownKeyCount`, `outOfRangeCount`, `outcome`, `failedEventIds` and
    `verdicts`; `429` while another verification runs), both for an Auditor; `GET /beacon/api/users/{id}/owned-resources`
    (`kind` as a number, `page`, `pageSize`, `sort`; a paged list of `OwnedResourceItem`, with `nameWithheld`), for an
    Auditor or a UserAdmin.
  - New schema `SecurityAuditEventVerdict` (`eventId`, `verdict`).
  - New response fields: `locks` on `GET /beacon/api/mcp/settings/locks`; `contentHash` and `statements` on each row of
    `GET /beacon/api/mcp/audit`.
  - New `409`s: `PUT /beacon/api/mcp/settings` and `PUT /beacon/api/mcp/projects/{projectId}/settings` that switch content
    retention off under `Audit:RequireContent`; `PUT /beacon/api/subscriptions/{id}/owner`,
    `PUT /beacon/api/data-quality/contracts/{id}/owner` and `PUT /beacon/api/ai-actors/{id}/owner` when the owner changed
    meanwhile.
  - `GET /beacon/api/mcp/audit` and the security audit and owned-resources routes answer an error, and return nothing,
    when their read cannot be recorded.
