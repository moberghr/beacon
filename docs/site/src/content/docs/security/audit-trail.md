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
`curation.glossary_term_created`, `curation.glossary_term_updated`, `curation.glossary_term_deleted` and
`curation.documentation_section_edited` and `curation.documentation_generation_started` (a documentation regeneration someone
started, with its task id).

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
export, with the filters' shape and the page).

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
- **MCP audit rows are evidence, not sealed.** The [MCP audit](/mcp/observability-and-audit/) rows and their
  statements carry keyed hashes of their content, but no signature over the row: they record what a call ran, and an
  edit at the database is not detected. The security audit trail records who read that export.

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
  itself; nothing is verified on a schedule, and nothing is purged, until it does.
- **Retention needs no action now.** Events are kept forever until you set `Beacon:Audit:RetentionDays` (365 to 36,500
  days). The purge verifies the events it is about to delete first and, while any fails verification, deletes nothing,
  records a refused `audit.purged` event and throws (its job fails): investigate the events it names.
- **Alert on two counters.** `beacon.security_audit.write_failures` (an event written on its own was not stored, and the
  log stream holds it as 9107 with `Persisted=False`; or a purge did not run, 9110) and
  `beacon.security_audit.verification_failures` (stored events failed verification, 9108). Ship the `Beacon.Audit` events
  9104 to 9110 to your SIEM; the database stays the source of truth, the 9104 copy is best-effort.
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
  text. Audit rows written before the upgrade have no statements and no content hash. MCP audit rows are evidence but
  not sealed: they carry no signature.
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
