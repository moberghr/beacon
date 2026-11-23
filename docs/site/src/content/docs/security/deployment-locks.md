---
title: Deployment locks
description: "Settings that the host's configuration pins so that no Admin, in the UI or through the API, can change them at runtime."
---

Most Beacon settings are stored in the database and an Admin can change them in the UI. That is a risk when an auditor needs a guarantee: "read-only enforcement is always on" is only true while every Admin leaves it on.

A **deployment lock** moves the decision out of the database and into the host's configuration (`appsettings.json`, environment variables, or code). Whoever deploys Beacon sets the lock. An Admin cannot lift it, because it is not a stored setting. Read this page when you need to pin a behaviour, when an Admin reports a disabled switch or a `409` on save, or when you prepare a configuration for the [regulated profile](/security/regulated-profile/).

There are two kinds of protection:

- A **lock** pins a value. The setting is on or off whatever is stored.
- A **ceiling** bounds a number. A stored value above the ceiling resolves to the ceiling.

## All locks at a glance

| Key | Type | Default | What it pins | Admin sees | Recorded |
|---|---|---|---|---|---|
| `Beacon:Mcp:ForceReadOnly` | bool | `true` | `EnforceReadOnly` on, globally and per project | Switch disabled; write answers `409` | A refused attempt saves nothing, so nothing is recorded |
| `Beacon:Mcp:ForcePiiDetection` | bool | `true` | `EnablePiiDetection` on | Same | Same |
| `Beacon:Mcp:ForceNoContentRetention` | bool | `false` | `RetainQueryContent` off | Same | Same |
| `Beacon:Mcp:Audit:RequireContent` | bool | `false` | `RetainQueryContent` on (lock name `Audit:RequireContent`) | Same | Same |
| `Beacon:Mcp:ForbidRowDataToLlm` | bool | `false` | `EnableSampleValueCollection`, `EnableValueGrounding`, `EnableEvalJudge` off | Same | Same |
| `Beacon:Mcp:Audit:Required` | bool | `false` | A tool result is withheld when its audit row cannot be written | No UI switch | Warning at startup while off |
| `Beacon:Mcp:Ceilings:*` | numbers | none | Upper bounds, see [Ceilings](#ceilings) | Stored value kept, effective value lower | Changes are in the history |
| `Beacon:Migrations:Enabled` | bool | `false` | The only write path | Feature hidden, routes unmapped | `data_sources.migration_writes_changed` when a target is marked |
| `Beacon:Embed:Enabled` | bool | `false` | The anonymous embed surface | Routes unmapped | Embed audit actions |
| `Beacon:RowSecurity:Enabled` | bool | `false` | Row-level security | Cannot be turned on | Startup failure |
| `Beacon:UserManagement:LocalLogin:Enabled` | bool | `true` | Password sign-in on the login form | No password form | `auth.break_glass_signed_in` for a break-glass sign-in; `auth.password_sign_in_refused` (reason `local_login_off`) for any other account |
| `Beacon:Retention:Locked` | bool | `false` | No retention window may keep data forever | Windows not editable at runtime | Startup failure |
| `Beacon:Authorization:RequireProjectMembership` | bool | `false` | Every project is members-only | Open projects not reachable | Nothing is recorded; startup fails when no project membership can exist |

The first five rows are the **MCP setting locks**. They share one code path (`McpLockPolicy`), so they behave the same way, described next.

## How an MCP setting lock behaves

When a lock is on:

1. **Resolution.** Whatever is stored, the effective value is the pinned one. This applies to the global setting and to every project override.
2. **Writes.** A write that changes a pinned field is refused. Nothing is saved.
3. **Stored values are kept.** A write never stores the pinned field while the lock is on. When you lift the lock, the value that was stored before takes effect again.

### The refusal

A write to a pinned field returns `409 Conflict` with a problem+json body, to an Admin too. The message names the field and the lock:

```http
HTTP/1.1 409 Conflict
Content-Type: application/problem+json

{"type":"/errors/setting-locked","title":"Setting 'EnforceReadOnly' is pinned by the deployment lock 'ForceReadOnly' and cannot be changed here.","status":409}
```

For `Beacon:Mcp:ForbidRowDataToLlm`, the title names the field that was in the request, for example `Setting 'EnableValueGrounding' is pinned by the deployment lock 'ForbidRowDataToLlm' and cannot be changed here.` The first pinned field the request contradicts is the one named.

On a **global** write, only a value that contradicts the lock is refused. On a **project** write, any non-null override of a pinned field is refused, even one that matches the lock today, because it would silently take effect the day the lock is lifted. A `null` override (inherit) is always allowed.

A refused write saves nothing, so it writes no history row. See [Settings history](#settings-history) for what is recorded for accepted writes.

### What an Admin sees

- On the global settings page a pinned switch is **disabled**, with a lock and a note naming the key.
- In a project's scope, pinned fields are not offered and a banner names them.
- `GET /beacon/api/mcp/settings/locks` lists every active lock (readable by an Auditor or a SecurityAdmin):

```json
{
  "lockedFields": ["EnforceReadOnly", "EnablePiiDetection"],
  "locks": [
    { "fieldName": "EnablePiiDetection", "lockName": "ForcePiiDetection" },
    { "fieldName": "EnforceReadOnly", "lockName": "ForceReadOnly" }
  ]
}
```

### Stored settings that a new lock overrides

If settings were stored before you turned a lock on, resolution replaces them silently. To make that visible, a background check at startup logs one Warning:

```text
Stored MCP settings contradict the deployment locks <lock names> (Beacon:Mcp) and are overridden: global settings rows <row ids>, overrides of projects <project ids>. They take effect again only if the host lifts the lock
```

The message carries ids and lock names only. It is logged once per start.

## One section per lock

### `Beacon:Mcp:ForceReadOnly` (default `true`)

Pins `EnforceReadOnly = true`, and makes the execution gate enforce read-only whatever a caller asks for. It is on without configuration. Setting it to `false` lets an Admin turn read-only enforcement off at runtime, and Beacon logs a Warning at startup:

```text
Beacon:Mcp:ForceReadOnly is off: an Admin can turn MCP read-only enforcement off at runtime, globally or per project
```

When the lock turned enforcement on over stored settings that had it off, and a statement is refused only because of read-only enforcement, the refusal says the lock is the cause. The settings page then shows read-only off in storage, and the lock is what to look at. See [Query safety](/mcp/server/) for the gate itself.

```json
{ "Beacon": { "Mcp": { "ForceReadOnly": true } } }
```

```bash
Beacon__Mcp__ForceReadOnly=true
```

### `Beacon:Mcp:ForcePiiDetection` (default `true`)

Pins `EnablePiiDetection = true`. Also on by default, and a `false` value logs a similar Warning at startup (`... MCP PII detection off at runtime, globally or per project`).

### `Beacon:Mcp:ForceNoContentRetention` (default `false`)

Pins `RetainQueryContent = false`. The MCP audit then keeps no question or SQL text, and retention clears stored result snapshots whatever their age. See [Data retention and content lock](/security/data-retention/#the-content-lock).

```json
{ "Beacon": { "Mcp": { "ForceNoContentRetention": true } } }
```

### `Beacon:Mcp:Audit:RequireContent` (default `false`)

The opposite of the lock above: pins `RetainQueryContent = true`, so no Admin can switch audit content off unseen. The MCP audit keeps the question and SQL of every call, and the statements it ran, until its retention purges them. In refusals, the locks list and warnings the lock is named `Audit:RequireContent`.

```json
{ "Beacon": { "Mcp": { "Audit": { "RequireContent": true, "RetentionDays": 365 } } } }
```

### `Beacon:Mcp:ForbidRowDataToLlm` (default `false`)

One lock, three fields. It pins `EnableSampleValueCollection`, `EnableValueGrounding` and `EnableEvalJudge` to `false`, so no value read from a data source goes to the LLM provider: no sample values are collected or shown, no value is probed for grounding, documentation discovers no column values, and no eval judge sees result rows. See [LLM and prompt safety](/security/llm-data-safety/).

The regulated profile fills it with `true` when it is unset.

```json
{ "Beacon": { "Mcp": { "ForbidRowDataToLlm": true } } }
```

### Conflicts the validator refuses at startup

`RequireContent` and `ForceNoContentRetention` pin the same field in opposite directions, so no stored value could satisfy both. With both on, the host fails to start:

```text
Beacon:Mcp:Audit:RequireContent and Beacon:Mcp:ForceNoContentRetention cannot both be on: one pins query content retention on, the other off.
```

Fix: turn one of them off. The other deployment-lock validations are in [Troubleshooting](#troubleshooting).

## `Beacon:Mcp:Audit:Required` (default `false`)

Fail closed. A successful `tools/call` whose audit row cannot be written is withheld: the caller gets an error and no result. With the lock off, the call still runs and the failure is only logged. The regulated profile fills it with `true`. Audit retention for the MCP audit is `Beacon:Mcp:Audit:RetentionDays` (see [Data retention](/security/data-retention/)). Details of what is recorded are in [Observability and audit](/mcp/observability-and-audit/).

## Ceilings

A ceiling bounds a numeric setting. It does not stop an Admin from saving a higher number. The write is stored **as sent**, and the effective value is lowered to the ceiling when settings are resolved. So the page edits what is stored and Beacon reports which fields were lowered. If you raise the ceiling later, the stored value applies again.

The same ceilings apply to the global settings and to every project override.

| Key | Type | Default | Range | What it bounds |
|---|---|---|---|---|
| `Beacon:Mcp:Ceilings:MaxRowLimit` | int | none | greater than 0 | Rows an MCP tool returns. |
| `Beacon:Mcp:Ceilings:StatementTimeoutSeconds` | int | none | greater than 0 | Statement timeout in seconds. |
| `Beacon:Mcp:Ceilings:MaxResultBytes` | int | none | greater than 0 | Result size in bytes. |
| `Beacon:Mcp:Ceilings:MaxExplainCost` | decimal | none | greater than 0 | Estimated query cost. A stored "no limit" resolves to the ceiling. |
| `Beacon:Mcp:Ceilings:MaxConcurrentQueriesPerKey` | int | none | greater than 0 | Concurrent queries per caller. |
| `Beacon:Mcp:Ceilings:LearningSignalRetentionDays` | int | none | greater than 0 | Longest learning-signal retention. Under `Beacon:Retention:Locked`, at most `Beacon:Retention:Days`. |
| `Beacon:Mcp:Ceilings:MaxSqlChars` | int | 100000 (always applies) | greater than 0 | Longest SQL text the execution gate evaluates. |
| `Beacon:Mcp:Ceilings:MaxQuestionChars` | int | 4000 (always applies) | greater than 0 | Longest question `ask` accepts. |
| `Beacon:Mcp:Ceilings:AskTimeoutSeconds` | int | 120 (always applies) | 1 to 3600 | Wall-clock time of one `ask` call. |
| `Beacon:Mcp:Ceilings:AskMaxLlmCalls` | int | 12 (always applies) | at least 2 | LLM calls one `ask` call may make: the intent classification and the first SQL generation need two. |
| `Beacon:Mcp:Ceilings:AskSchemaContextChars` | int | 120000 (always applies) | at least 10000 | Schema context one `ask` sends to the LLM. |

A ceiling left unset means no ceiling, except the always-applying ones, which fall back to the default shown. A value outside its range fails startup, for example:

```text
Beacon:Mcp:Ceilings:AskTimeoutSeconds must be between 1 and 3600 when set.
Beacon:Mcp:Ceilings:MaxRowLimit must be greater than zero when set.
```

An accepted and a lowered value, with `MaxRowLimit` ceiling `1000`:

```json
{ "Beacon": { "Mcp": { "Ceilings": { "MaxRowLimit": 1000, "StatementTimeoutSeconds": 30 } } } }
```

| An Admin saves | Stored | Effective | Reported as lowered |
|---|---|---|---|
| `MaxRowLimit: 500` | 500 | 500 | no |
| `MaxRowLimit: 5000` | 5000 | 1000 | yes (`clampedFields` on the settings GET) |

Without the profile, there are no ceilings beyond the always-applying ones. The profile fills the first four of the table (`MaxRowLimit` 1000, `StatementTimeoutSeconds` 30, `MaxResultBytes` 262144, `MaxConcurrentQueriesPerKey` 4) when they are unset, which makes them hold as ceilings an Admin cannot exceed.

## Migration is the only write path

Beacon reads data sources. Data migration is the one feature that writes to a connected database, so it has its own switch.

| Key | Type | Default | Range | What it does |
|---|---|---|---|---|
| `Beacon:Migrations:Enabled` | bool | `false` | | Maps the migration endpoints and allows runs. |
| `Beacon:Migrations:MaxRows` | int | `100000` | 1 to 10000000 | Rows one run may read and write. A larger source fails the run before anything is written. |

While `Enabled` is off, whoever asks, an Admin included:

- the migration endpoints are not mapped (they answer 404) and the shell hides the feature;
- no job can be created or run (the policy answers with `Data migration is turned off for this deployment (Beacon:Migrations:Enabled).`);
- no data source can be marked as a write target: the mark is refused with a `409` carrying the same message.

When it is on, an Admin marks a specific data source as a write target (`PUT` on the data source's migration-writes setting). That mark is stored, history-recorded (a key per data source) and audited as `data_sources.migration_writes_changed`. Beacon never reads from a write target. At startup, an enabled lock logs `Beacon:Migrations:Enabled is on: migrations write to the data sources an Admin marks as migration write targets, which Beacon never reads`. See [Data migration](/features/data-migration/).

```json
{ "Beacon": { "Migrations": { "Enabled": true, "MaxRows": 100000 } } }
```

## Other pinned surfaces

### Embed: `Beacon:Embed:Enabled` (default `false`)

Off, no embed route is mapped. On, `Beacon:Embed:SigningKey` is required and checked at startup, and the profile reports it under event 9132. Never on in gateway mode. See [Authorization](/security/authorization/).

### Row-level security: `Beacon:RowSecurity:Enabled` (default `false`)

The lock cannot be turned on in this release. Turning it on fails startup, so no deployment believes its rows are filtered when they are not:

```text
Beacon:RowSecurity:Enabled cannot be turned on: row-level security is not available in this release.
```

### Local login: `Beacon:UserManagement:LocalLogin:Enabled` (default `true`)

Off, the login page shows no password form and only the accounts in `BreakGlassUserNames` (at most 3, matched exactly with the stored user name) may sign in with a password. Each such sign-in is logged at Warning. See [User management](/security/user-management/).

```json
{ "Beacon": { "UserManagement": { "LocalLogin": { "Enabled": false, "BreakGlassUserNames": [ "REPLACE_ME" ] } } } }
```

### Retention: `Beacon:Retention:Locked` (default `false`)

No retention window may keep data forever. See [Data retention and content lock](/security/data-retention/#locked-retention).

### Project membership: `Beacon:Authorization:RequireProjectMembership` (default `false`)

Restricts every project, whatever its own setting: only members and Admins reach a project, and data sources no project links are Admin-only. It needs project memberships, so the host fails to start when none can exist:

```text
Beacon:Authorization:RequireProjectMembership requires project memberships: enable Beacon user management or register an IProjectMembershipSource.
```

See [Authorization](/security/authorization/).

## Settings history

Every change to the MCP settings, global or per project, is recorded in the settings history (`AppSettingHistory`, the `app_setting_history` table). One row is written per changed setting:

| Column | Content |
|---|---|
| `SettingKey` | `Mcp.{Setting}` for the global settings, `Mcp.Project.{projectId}.{Setting}` for a project |
| `OldValue`, `NewValue` | The value before and after, in invariant format. A long text is cut to 2000 characters |
| `ChangedAt` | UTC time |
| `ChangedByUserId` | The caller's log id |

Only the settings in the MCP settings field list are recorded, so a secret is never written by default. The history is sealed and append-only (it is never purged; see [Audit trail](/security/audit-trail/)).

The same save also writes one security audit event: `settings.mcp_settings_changed` for global settings, `settings.mcp_project_settings_changed` for a project. It names the keys that changed and the history rows that hold the values, plus a keyed digest of each value before and after. A line in the application log notes the change by setting name and ids only:

```text
MCP settings changed by <actor> for <the global settings | project <id>>: <setting keys, for example Mcp.MaxRowLimit>
```

To read the history, use `GET /beacon/api/admin-settings`, which returns the latest 200 rows to an Auditor, a SecurityAdmin or an Admin. See [Admin settings](/security/admin-settings/#change-history).

## Recommended values

| Setting | Development | Production | Regulated |
|---|---|---|---|
| `Beacon:Mcp:ForceReadOnly` | `true` | `true` | `true` (required) |
| `Beacon:Mcp:ForcePiiDetection` | `true` | `true` | `true` (required) |
| `Beacon:Mcp:ForceNoContentRetention` | `false` | by decision | by decision |
| `Beacon:Mcp:Audit:RequireContent` | `false` | by decision | by decision |
| `Beacon:Mcp:ForbidRowDataToLlm` | `false` | by decision | `true` (filled when unset) |
| `Beacon:Mcp:Audit:Required` | `false` | `true` | `true` (filled when unset) |
| `Beacon:Mcp:Ceilings:*` (the four numeric ones) | unset | set | set (filled with built-in defaults when unset) |
| `Beacon:Migrations:Enabled` | `true` when testing | `false` unless used | by decision (reported as event 9132) |
| `Beacon:Embed:Enabled` | `false` | `false` unless used | by decision (reported as event 9132) |
| `Beacon:UserManagement:LocalLogin:Enabled` | `true` | `false` with SSO | `false` (required) |
| `Beacon:Retention:Locked` | `false` | by decision | `true` (filled when unset) |
| `Beacon:Authorization:RequireProjectMembership` | `false` | `true` | `true` (required) |

`RequireContent` and `ForceNoContentRetention` are alternatives. Choose by what your audit needs: provable content for investigations, or no stored content.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Admin gets `409` with `... is pinned by the deployment lock '...'` | The write contradicts an active lock. | Change the key in the host configuration, or leave the field out of the request. |
| A project save returns `409` although the value equals the lock | A non-null override of a pinned field is refused, so it cannot start applying when the lock is lifted. | Send `null` for the field. |
| A switch on the settings page is disabled | Its lock is on. | Read the lock list at `GET /beacon/api/mcp/settings/locks`. |
| An older stored value no longer takes effect | A lock now overrides it. | See the startup Warning `Stored MCP settings contradict the deployment locks ...`. Lift the lock to restore it. |
| Startup fails with `RequireContent and ForceNoContentRetention cannot both be on` | Two locks pin the same field in opposite ways. | Keep one. |
| Startup fails with `Beacon:RowSecurity:Enabled cannot be turned on` | The lock cannot be on in this release. | Remove the key. |
| Startup fails with `Beacon:Mcp:Ceilings:... must be greater than zero when set` | A ceiling is zero or negative. | Use a positive value or remove the key. |
| Migration routes answer 404 | `Beacon:Migrations:Enabled` is off. | Turn it on and restart. |
| An Admin's save succeeded but the effective value is lower | A ceiling lowered it. | Read `clampedFields` on the settings GET. Raise the ceiling or lower the setting. |

## Related pages

- [Regulated profile](/security/regulated-profile/)
- [Data retention and content lock](/security/data-retention/)
- [LLM and prompt safety](/security/llm-data-safety/)
- [Admin settings](/security/admin-settings/)
- [Authorization](/security/authorization/)
- [MCP observability and audit](/mcp/observability-and-audit/)
- [Configuration reference: Deployment locks](/getting-started/configuration/#deployment-locks)
