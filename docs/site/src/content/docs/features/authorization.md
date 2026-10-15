---
title: Authorization
description: "How Beacon decides access: projects as the boundary, global and project roles, project actions, functional permissions with a route matrix, separation of duties, identity-provider role mapping, 404 versus 403, scheduled work and shared data sources."
---

Beacon authorizes in two layers. A caller's **global role** (read, write) comes from a pluggable `IBeaconAuthorizationProvider`. Its **project access** (which projects, and through them which data sources, it may use, and which action it may take there) comes from one evaluator, `IProjectAccessEvaluator`. On top of both, the Admin role's duties are split into six **functional permissions** that grant no project data.

This page is the reference for those rules. For accounts, sign-in and sessions see [User management](/features/user-management/); for how the controls fit together see [Security model](/features/security-model/).

## Overview

| Concept | Values | Decided by |
|---|---|---|
| Global role | Admin (level 3), Editor (2), Viewer (1); functional roles are level 0 | `IBeaconAuthorizationProvider` (`DatabaseAuthorizationProvider` with user management) |
| Project role | Viewer, Editor, ProjectAdmin | `ProjectMember` rows, or a host `IProjectMembershipSource` |
| Project action | Read, Run, Query, Write, Manage, ManageMembers, Curate | `IProjectAccessEvaluator` |
| Functional permission | Approver, Auditor, SecurityAdmin, UserAdmin, DataSourceAdmin, MigrationOperator | live stored roles, or an identity-provider app role mapped at sign-in |

Authorization, user management and the login form are all **off by default** (`options.Authorization.Enabled`, `options.UserManagement.Enabled`, `options.Authentication.EnableLoginForm`). With user management on, the stored roles cap every signed-in caller even when `Authorization.Enabled` is `false`; only with both off is every signed-in caller an Editor.

The building blocks:

- `IBeaconUserContext`: the current caller (`UserId`, `UserName`, `DisplayName`, `Email`, `IsAuthenticated`, `HasClaim`).
- `IBeaconAuthorizationProvider`: the caller's global read and write permission.
- `IProjectAccessEvaluator`: the caller's project access per action.
- `IBeaconPermissionAccessor`: the functional permissions the caller holds now.
- `IBeaconAuthenticationProvider`: how a login-form sign-in is checked (the sample host uses `DatabaseAuthenticationProvider`).

## Enabling authorization

With Beacon's [user management](/features/user-management/), roles come from Beacon's database and `DatabaseAuthorizationProvider` is registered for you:

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<BeaconScheduler>();
        options.Authorization.Enabled = true;
        options.Authentication.EnableLoginForm = true;
        options.AddAuthenticationProvider<DatabaseAuthenticationProvider>();
        options.EnableUserManagement();
    })
    .UsePostgreSql();
```

A host that signs users in itself registers its own provider (`options.AddAuthorizationProvider<MyAuthorizationProvider>()`, see [Custom authorization provider](#custom-authorization-provider)) and a claims transformation that adds Beacon's claims, passed to `AddBeaconHostInfrastructure<TClaimsTransformation>()`. The full wiring is on [Host wiring](/integrate/host-setup/) and [Access control](/integrate/access-control/).

```csharp
public sealed class MyClaimsTransformation : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var identity = (ClaimsIdentity)principal.Identity!;
        var role = principal.IsInRole("Administrator") ? "Admin" : principal.IsInRole("PowerUser") ? "Editor" : "Viewer";

        identity.AddClaim(new Claim(BeaconClaims.Role, role));
        identity.AddClaim(new Claim(ClaimTypes.Role, role)); // the Admin checks read this one
        identity.AddClaim(new Claim(BeaconClaims.UserId, principal.FindFirstValue(ClaimTypes.NameIdentifier)!));
        identity.AddClaim(new Claim(BeaconClaims.UserName, principal.Identity!.Name!));

        return Task.FromResult(principal);
    }
}

builder.Services.AddBeaconHostInfrastructure<MyClaimsTransformation>();
```

Beacon's claim types: `BeaconClaims.UserId` (`beacon:user_id`), `UserName` (`beacon:user_name`), `Role` (`beacon:role`), `Permission` (`beacon:permission`).

### Admin role claim

Admin-only routes (the `BeaconApiAdmin` policy) and the rules that let an Admin act on what another user owns read the standard `ClaimTypes.Role` claim with the value `Admin`; `beacon:role` alone is not enough. Beacon's own login and OIDC sign-in emit it from the user's roles. An API key never carries a role, so it is never an Admin.

While user management is on, a stored user is an Admin by the roles stored **now**, not by the claim the session carries: a demoted Admin loses the Admin bypass on the next request, and a session without a stored user reaches no project at all. The session's `Admin` claim counts only on a host without Beacon user management, for a caller without a stored user.

## Global roles

| Role | Level | Read | Write (create, edit, run ad hoc) |
|---|---|---|---|
| **Admin** | 3 | Yes | Yes |
| **Editor** | 2 | Yes | Yes |
| **Viewer** | 1 | Yes | No |
| Functional roles (Approver, Auditor, …) | 0 | No | No |

The provider answers only read (level 1 and up) and write (level 2 and up, or a super admin). There is no delete level: deleting and archiving are the project action **Manage** (see [Project roles](#project-roles)), which an Editor holds in an open project and as the recorded owner. A super admin (`IsSuperAdmin`, the first-run account) passes every check.

When `Authorization.Enabled` or user management is on, `/beacon/api` enforces the global role: `GET` needs read permission and every other method needs write permission. A Viewer may also run a stored query's preview (a draft's preview needs write permission), create Read API keys, revoke their own keys and change their own password. A route of a [functional permission](#functional-permissions) admits its holder instead.

## Project access

Projects are the authorization boundary. A caller reaches a project's queries, subscriptions, stored runs, tasks, data contracts, AI actors, documentation and data-source metadata only through a project it may use, and a data source only through a project that links it. `IProjectAccessEvaluator` decides this once per request for every caller; the REST API, the UI, the MCP playground and MCP tool calls all consult it.

### Open and restricted projects

- **Open**: everyone with a Beacon role uses the project at their global role. A Viewer reads and runs saved SQL; an Editor also writes and works ad hoc.
- **Restricted** (`requiresMembership`): only the project's members and Admins use it; to anyone else it does not exist (`404`).
- **New projects are created restricted.** When the creator is an active Beacon user and Beacon manages project membership, the project starts restricted with the creator as its project admin (an Admin who creates a project becomes its project admin too). A creator without an active Beacon user, or any creator while the host manages membership or membership is unavailable, gets an open project (nobody could be a member of a restricted one), and Beacon logs `ProjectCreatedOpen` (7112). The `POST /beacon/api/projects` answer carries `requiresMembership`.
- **Restricting and opening.** An Admin or a UserAdmin restricts a project with the **Access** card on the project page (`PUT /beacon/api/projects/{id}/access` with `{ "requiresMembership": true }`). Opening a restricted project is an Admin's alone (`403` for anyone else). Restricting answers `409` while project membership is unavailable. Add members (a project admin first) before restricting. The card lists the project's data sources that another open project also links (they stay reachable through it) and every other project that shares a data source with it.
- **`Beacon:Authorization:RequireProjectMembership: true`** restricts every project, whatever its own setting, and makes data sources no project links Admin-only. It needs project memberships (Beacon user management or a host [membership source](#host-seams)); the host fails at startup otherwise.
- **Archived projects** grant nothing. No route archives a project.

### Project roles

A member holds one role per project. A global Viewer is a Viewer in every project, whatever their membership; Editors and Admins act with their membership role (and in an open project with the higher of that and their global role).

| Action | Viewer | Editor (restricted) | Editor (open) | Project admin | Admin |
|---|---|---|---|---|---|
| **Read**: resources, SQL, metadata, stored results, documentation | ✓ | ✓ | ✓ | ✓ | ✓ |
| **Run**: execute saved SQL, evaluate data contracts | ✓ | ✓ | ✓ | ✓ | ✓ |
| **Query**: ad-hoc and draft SQL, LLM work (playground, AI actors) | – | ✓ | ✓ | ✓ | ✓ |
| **Write**: create and change resources | – | ✓ | ✓ | ✓ | ✓ |
| **Manage**: delete, archive, take over a lock, force a metadata refresh | – | as recorded owner | ✓ | ✓ | ✓ |
| **ManageMembers**: manage members, link data sources | – | – | – | ✓ | ✓ |
| **Curate**: review learned patterns, edit or regenerate documentation, apply or reject documentation patches, verify relationships and delete verified ones | – | – | – | ✓ | ✓ |
| Restrict the project | – | – | – | – | ✓ (also UserAdmin) |
| Open a restricted project | – | – | – | – | ✓ |

The recorded owner is a data contract's or subscription's owner, an AI actor's creator and a query's lock holder. Owner rules on tasks, contracts and AI actors apply on top: both the project action and the owner rule must pass. A UserAdmin also manages the members of every project and restricts any project without being its member; a DataSourceAdmin links data sources no open project holds (see [Functional permissions](#functional-permissions)).

**ManageMembers and Curate are membership-only.** An open project never grants them, and they are never granted to an API key (Read or Execute scope), an MCP caller (user or system) or the owner of scheduled work. Curation that records who decided (reviewing a learned pattern, verifying a relationship) also needs a signed-in Beacon user (`403` otherwise). A data source's shared grounding (relationships, documentation patches to descriptions) needs Curate in **every** live project linking the source, or the DataSourceAdmin permission; a relationship someone without Curate creates or relabels is stored unverified until a curator verifies it. Golden examples and the glossary stay Admin-only. See [Knowledge base → Who curates grounding](/features/knowledge-base/#who-curates-grounding).

### Data sources follow their projects

- A data source is reachable only through a project the caller may use. A resource that reads several data sources is reachable only through **one** project that links all of them. Resources addressed by their data sources alone (an ad-hoc preview, a metadata read, a data contract) have no home project: any such project serves.
- **Unlinked data sources.** A data source no project links is reachable as before only if it existed before project access was introduced (`OpenWhenUnlinked`). A new data source is Admin-only until it is linked: link it when you create it (the **Projects** field of the **New data source** dialog, `projectIds` on `POST /beacon/api/data-sources`). Removing a source's last link makes it Admin-only again.
- **Shared data sources.** A source linked to an open project is reachable through that project by everyone with a role, even when a restricted project links it too. Writes to a source's shared state (relationships, description edits, a forced metadata refresh) need Write in **every** live project that links it (or DataSourceAdmin, or Admin), because that state grounds every project using the source.
- **Linking.** A project admin (or an Admin) links data sources into the project with **Link data sources** (`POST /beacon/api/projects/{id}/data-sources`). Anyone but an Admin may link only sources already linked to a live open project; a host's data source links only into the project the host declares for it, for Admins too. An unreadable source answers `404`, a readable one that may not be linked `403`, and nothing is linked partially.
- **Queries without steps** read no data source, so no project holds them: their creator (the query's owner) and Admins reach them.
- **One database login per boundary.** Project access decides who may run SQL through Beacon, not what the database login can read. Give each data source a login that sees only what the projects linking it should see. Views, synonyms and functions in the source's own database read with that login's grants.

### Database names in SQL

SQL that Beacon runs against a data source may name objects of that source's own database only. A name qualified with a database (`database.schema.table` on SQL Server, Azure Synapse, PostgreSQL and Snowflake, `database.table` on MySQL and SQLite, `catalog.schema.table` on Databricks, `project.dataset.table` on BigQuery) must carry the database, catalog or project the data source's connection is configured for; anything else is refused before the SQL runs. Unqualified and schema-qualified names are always allowed. The check applies whether or not read-only enforcement is on.

- **The configured database** comes from the connection string (SQL Server and Synapse `Initial Catalog`/`Database`, PostgreSQL `Database`, MySQL `Database`, Snowflake `db`; SQLite is `main`) or the connector settings (Databricks `Catalog`, BigQuery `ProjectId`). A data source without one accepts no database qualifier at all: set it in the connection, or write `schema.table`. When the settings cannot be read, no qualifier is accepted either.
- **Comparison** follows the engine: PostgreSQL folds unquoted names to lower case and Snowflake to upper case; MySQL compares exactly; SQL Server, SQLite, Databricks and BigQuery ignore case.
- **System databases are other databases** (MySQL `information_schema`, SQL Server `master`, Snowflake `SNOWFLAKE`, Databricks `system`). Schema-level catalogs inside the source's own database (`information_schema.tables` on PostgreSQL, `sys.tables` on SQL Server) stay allowed. On BigQuery write `project.dataset.INFORMATION_SCHEMA.TABLES`.
- **Also refused**, with read-only enforcement on or off: names with more parts than the full form (SQL Server linked-server names); names resolved only at run time (`IDENTIFIER(...)`, Snowflake `TABLE('...')`, `GET_DDL`, `OBJECT_DEFINITION`); functions and procedures that reach other servers, files or storage (`dblink…`, `OPENROWSET`, `OPENQUERY`, `EXTERNAL_QUERY`, `read_files`, PostgreSQL `COPY … PROGRAM` and server-file `COPY`, Databricks network and stream functions and file paths); database, role and user switches (`USE`, `SET ROLE`, `EXECUTE AS`, `set_config`, SQLite `ATTACH`); SQL run from a string, variable or session (`EXEC @sql`, `sp_executesql`, `EXECUTE IMMEDIATE`, `PREPARE`/`EXECUTE`, PostgreSQL `DO` and string-bodied functions); and SQL Beacon cannot parse.
- **Objects in the source's own database** (views, synonyms, foreign tables, procedures) are allowed by name and read with the login's grants. Grant the login only what the data source should expose.
- **Where:** every statement Beacon runs on a data source passes the same execution gate: the MCP tools, saving a query or step (also by an AI actor), previews, subscription and test-delivery runs, AI actor checks, data-migration reads, Custom SQL rules, semantic search, documentation enum discovery and value-grounding probes. The in-memory join of a multi-step query reaches no database and is not checked. `IDataSourceProvider.Execute*` called directly by a host, and the metadata extractors (fixed catalog SQL), are not checked.

See [Security model](/features/security-model/) for the full execution gate.

### Guardrails of shared data sources

A data source that several live projects link is held to the strictest guardrails among them, whichever project's caller queries it: read-only enforcement and PII detection are on when any project turns them on; value grounding and sample-value collection are off when any turns them off; every project's custom PII patterns apply; and the row limit and the value-grounding probe budget are the lowest any of them sets. The statement timeout, result size and plan cost settings are resolved the same way (lowest wins), recorded with the settings and shown on the MCP settings page. A source only one project links keeps that project's settings; archived projects count for nothing.

Read-only enforcement, PII detection and the row limit apply to the MCP tools that run SQL (`query`, `dry_run`, `ask`, saved-query tools, and so the playground); the `query` and `ask` tools use a fixed 30-second timeout. The grounding settings apply to grounding (`ask`, `get_query_context`) and to sampling values during a metadata refresh, which collects only while collection is on for **every** live project linking the source. Stored sample values pass the PII screen again whenever they are shown. For an Admin, the project detail lists the effective value and the deciding project per shared source (`sharedSourceGuardrails`).

### Who the caller is

| Caller | Project access |
|---|---|
| Admin (enabled user with the Admin role, or a super admin) | every live project, every action |
| Signed-in Beacon user | open projects at their global role; restricted projects through membership |
| Disabled or archived user | nothing |
| Session without a stored user while user management is on (a host principal, a bearer token not bound to a user) | nothing |
| Session on a host without Beacon user management | open projects at the global role; the `Admin` role claim counts; restricted projects are Admin-only unless the host supplies a [membership source](#host-seams) |
| API key | its owner's access within its project list, capped by the lower of the key's scope and the owner's global role; never the Admin bypass, never ManageMembers or Curate. A key without a list (created before lists were required) has its owner's access |
| Mapped Entra system caller on MCP | its configured projects, for reading, running saved SQL and ad-hoc queries |
| Mapped Entra user caller on MCP | its configured projects within its Beacon user's access; without a Beacon user, its configured open projects |
| Browser session on `/beacon/mcp` | nothing (browsers use the MCP playground) |
| Scheduled work done for its owner | its owner's access at that run (see [Scheduled work runs as its owner](#scheduled-work-runs-as-its-owner)) |
| Scheduled work without an owner (created before owners were recorded) | openly reachable data sources only; nothing with `RequireProjectMembership` |
| Other work without a request, in a scope marked as system work | the system, unrestricted |
| Work without a request, in an unmarked scope | nothing (fail closed, a warning is logged) |

See [API keys](/features/api-keys/#project-restrictions), [MCP server](/features/mcp-server/) and [Entra ID callers](/features/mcp-entra-callers/) for their own rules.

### How it is enforced: 404 versus 403

- **Every request declares its check.** A request that addresses a resource by id implements `IProjectAuthorizedRequest` and names the resource and action; a MediatR pipeline behaviour resolves the resource's project (or data sources) and refuses before the handler runs. A list request implements `IProjectFilteredRequest` and filters in SQL, so totals and pages never count what the caller cannot see. A request with nothing project-scoped is marked `[ProjectScopeExempt("reason")]`. Tests fail when a request's id property is neither checked nor exempt.
- **404 or 403.** A resource that is missing, or that the caller may not read, answers `404`. The caller cannot tell the two apart. A resource the caller may read but whose action is not granted answers `403`. Refusals throw `BeaconNotFoundException` and `BeaconForbiddenException`; on `/beacon/api` they become RFC 7807 problem responses without the message, in every host. `BeaconConflictException` and `BeaconRateLimitedException` map to `409` and `429`. Any other exception, including `UnauthorizedAccessException`, is left to the host.
- **In-process requests are checked too.** The behaviour runs on every `IMediator.Send` made while an HTTP request is served, so a host endpoint that sends Beacon requests is checked against that request's caller.
- **Logged.** A `403` logs `AccessRefused` (7108), a hidden resource `AccessHidden` (7109), with ids, the reason, the principal kind and the caller's user id. The events never carry names or values.
- **The UI** hides what a caller may not do (the **Members** tab, **Link data sources**, the **Access** card) and shows a resource it may not read as not found.
- **MCP.** Tool calls over `/beacon/mcp` and in the playground take the caller's projects from the evaluator on every call: Read for the catalog tools, Query for `ask`, `query`, `dry_run` and `feedback`. Access that cannot be determined answers a retry message and fails `tools/list`, never a shorter list.

### Members

A project admin, a UserAdmin or an Admin manages members on the project page's **Members** tab. Search finds people by **display name** only (case-insensitive, at least two characters, up to 20 matches, rate-limited); people are shown by display name or as "User {id}", never by user name, e-mail address or external id. A disabled or archived member stays listed as inactive and grants nothing.

A project keeps an **effective project admin**: an enabled, non-archived member with the project admin role whose global role is Editor or above. Removing or demoting the last one is refused (`400`) unless an Admin does it. The check and the change run in one serializable transaction; a collision answers `409`.

| Route | Purpose |
|---|---|
| `GET /beacon/api/projects/{id}/members` | members, paged |
| `GET /beacon/api/projects/{id}/member-candidates?search=` | people to add |
| `PUT /beacon/api/projects/{id}/members/{userId}` | add a member or change their role (`{ "role": 1 \| 2 \| 3 }`: Viewer, Editor, ProjectAdmin) |
| `DELETE /beacon/api/projects/{id}/members/{userId}` | remove a member |
| `PUT /beacon/api/projects/{id}/access` | restrict (Admin, UserAdmin) or open (Admin) |
| `POST /beacon/api/projects/{id}/data-sources` | link data sources (`{ "dataSourceIds": [...] }`) |
| `DELETE /beacon/api/projects/{id}/data-sources/{dataSourceId}` | unlink a data source (Admin) |

When the host manages membership, the membership routes answer `409` ("Project membership is managed by the host."); when membership is unavailable, `409` ("Project membership is not available in this deployment."). The Members tab is hidden in both cases.

### Unlinking data sources

An Admin removes a data source's link to a project with `DELETE /beacon/api/projects/{id}/data-sources/{dataSourceId}`, or from the **Access** card of a project that shares the source. Members lose the source through that project at once; other projects that link it keep it. When the removed link was the last, the source becomes Admin-only. A data source never becomes unlinked and open. A host's data source cannot be unlinked (`409`). Each unlink is logged as `DataSourceUnlinked` (7110).

### Scheduled work runs as its owner

A subscription's scheduled run, a data contract's scheduled evaluation and an AI actor's background think cycle run as the owner they recorded (the subscription's or contract's owner, the actor's creator) and are re-authorized at **every** run, right before any SQL or LLM call:

| Work | The owner needs | When refused |
|---|---|---|
| Subscription run | Run in one project that holds every data source of the query's steps | the run is recorded as **Skipped** with "Skipped: owner not authorized."; no SQL, no delivery; flagged `ownerNotAuthorized` |
| Data contract evaluation | Run in a project that links the contract's data source | nothing is evaluated or alerted; the contract records when skipping started and is flagged until an evaluation runs again |
| AI actor background cycle | Query in a project that links the actor's data source | the cycle is recorded with phase **Skipped** |

A refused run is not a failure and is never run as the system; it is logged as `ScheduledWorkSkipped` (7111). Work without an owner runs only while every data source it reads is openly reachable, and never with `RequireProjectMembership`. On a host without Beacon user management, work whose owner has no Beacon user runs in the system scope and Beacon logs a warning naming the owner; turn on Beacon user management to have each run checked against its owner. Work a request asks for runs as the request's caller and answers `403` when the caller may not. This covers running a subscription (`POST /beacon/api/subscriptions/{id}/execute`), **Evaluate now** on a contract, and a think cycle (`POST /beacon/api/ai-actors/{id}/think`).

An **Admin or a UserAdmin** changes the owner with `PUT /beacon/api/subscriptions/{id}/owner`, `PUT /beacon/api/data-quality/contracts/{id}/owner` or `PUT /beacon/api/ai-actors/{id}/owner` (`{ "userId": 12 }`); a UserAdmin never makes themselves the owner. The new owner must be an enabled user who may do the work (Run for a subscription or contract, Query for an AI actor), or the change answers `409`.

## Functional permissions

The Admin role's duties are split into six functional permissions. An Admin holds all six; a user who should do one duty, and not the others, gets that permission's role instead.

| Permission | The duty |
|---|---|
| **Approver** | Approves and rejects query changes and governed changes (migration jobs, Custom SQL rules, recipient destinations), never their own; reads every project's approval requests. See [Approvals](/features/approvals/) |
| **Auditor** | Reads the MCP audit, the MCP and project settings, the admin settings (secrets masked) and every user's API keys |
| **SecurityAdmin** | Changes the MCP settings and the admin settings except the LLM connection and base URL; manages recipients; publishes saved queries as MCP tools; sets repository access tokens |
| **UserAdmin** | Manages users (external users only) and their roles and sessions, revokes any user's API keys, changes the owner of scheduled work, manages the members of any project, restricts projects |
| **DataSourceAdmin** | Creates, tests and deletes data sources; links a data source no open project holds into a project it administers, when it can read the source; changes shared data-source state; forces a metadata refresh without the cooldown |
| **MigrationOperator** | Runs [data migration](/features/data-migration/) for the jobs whose data sources it may query and write (mapped only while `Beacon:Migrations:Enabled`) |

**Admin only:** the glossary and eval/golden curation, the LLM provider, endpoint, region, authentication mode and credentials, the base URL, turning read-only enforcement or PII detection off (only where the deployment unlocks them, see [MCP safety locks](#mcp-safety-locks)), opening a restricted project, unlinking a data source, marking a migration write target, creating data contracts with Custom SQL rules, and the sample host's Warp dashboard.

### Route and permission matrix

Paths are under `/beacon/api`. "Requires" means the route's policy is the permission (an Admin passes too); "Also admits" means the route is open to everyone with read access and additionally to the permission's holder without a Viewer or Editor role.

| Route | Requires | Also admits |
|---|---|---|
| `POST /approvals/{id}/approve`, `/approvals/{id}/reject` | Approver | |
| `POST /approvals/changes/{id}/approve`, `/approvals/changes/{id}/reject` | Approver | |
| `GET /approvals/pending`, `/approvals/{id}`, `/approvals/changes`, `/approvals/changes/{id}` | | Approver |
| `GET /mcp/audit` | Auditor | |
| `GET /mcp/settings`, `/mcp/settings/locks`, `/mcp/projects/{projectId}/settings` | Auditor or SecurityAdmin | |
| `GET /admin-settings` | Auditor or SecurityAdmin | |
| `GET /api-keys/admin` | Auditor or UserAdmin | |
| `PUT /mcp/settings`, `PUT /mcp/projects/{projectId}/settings` | SecurityAdmin | |
| `PUT /admin-settings`, `POST /admin-settings/test-llm` | SecurityAdmin (LLM connection and base URL: Admin, checked by the handler) | |
| `POST /recipients`, `PUT /recipients/{id}`, `DELETE /recipients/{id}` | SecurityAdmin | |
| `PUT /queries/{id}/mcp-tool` | SecurityAdmin | |
| `PUT /projects/repositories/{id}/token` | SecurityAdmin | |
| `/users/*` (list, roles, create, update, toggle, `PUT /users/{id}/roles`, sign-out-everywhere) | UserAdmin | |
| `DELETE /api-keys/admin/{id}` | UserAdmin | |
| `PUT /subscriptions/{id}/owner`, `/data-quality/contracts/{id}/owner`, `/ai-actors/{id}/owner` | UserAdmin | |
| `PUT /projects/{id}/access` | UserAdmin (opening: Admin, checked by the handler) | |
| `GET /projects/{projectId}/members`, `/projects/{id}/member-candidates`, `PUT`/`DELETE /projects/{id}/members/{userId}` | | UserAdmin |
| `POST /data-sources`, `POST /data-sources/test-connection`, `DELETE /data-sources/{id}` | DataSourceAdmin | |
| `/migrations/*` | MigrationOperator | |
| `GET /home/migration-summary` | | MigrationOperator |
| `/glossary/*`, `/eval/*`, `PUT /data-sources/{id}/migration-writes`, `DELETE /projects/{id}/data-sources/{dataSourceId}` | Admin | |

The complete route list is on [Routes & endpoints](/reference/routes/).

### Rules

- **Functional roles grant no project data.** A functional role is a level-0 role with neither read nor write permission, so a user holding only functional roles reads no project, data source or resource and gets `403` elsewhere. Give such a user a Viewer or Editor role as well to let them use projects. Two duty-specific exceptions: an Approver reads every project's approval requests with their SQL; an Auditor reads every project's MCP audit rows.
- **Permissions are read live.** Each route carries the policy `BeaconApi.{Permission}`, which admits the Admin role or the permission's role among the session's claims; the permission filter then admits the caller only if they hold the permission **now** (`IBeaconPermissionAccessor`). A user whose role was removed gets `403` although their session still carries the claim. The `BeaconApi.` policy-name prefix is reserved for Beacon.
- **API keys hold none.** A key carries no role and no functional permission, whatever its owner holds. A key refused on such a route is told: *"API keys cannot use administrative routes; sign in as a user with the X permission."*
- **A UserAdmin cannot raise anyone, itself included.** A UserAdmin who is not an Admin never assigns or removes the Admin role or a functional role, never changes their own account, roles or project membership, and never touches a **protected user**: an Admin, a super admin, or a holder of any level-0 system role (other UserAdmins included). A UserAdmin cannot create an **internal** (password) user nor change a user's user name or e-mail address (`403`), so it cannot make an account it can sign in to. No route sets another user's password. A UserAdmin restricts projects but does not open one, and never makes themselves an owner of scheduled work.
- **What a UserAdmin can do, and how to watch it.** A UserAdmin creates external users, gives them Viewer or Editor, and adds unprotected users to any project. The identity provider and its [admission rules](/features/user-management/#single-sign-on-admission) bound what an external user reaches. To review these changes, alert on events **7114** (`UserCreated`), **7101** (`MemberAdded`) and **7113** (`UserRolesChanged`) whose actor holds UserAdmin.
- **A MigrationOperator has no bypass.** Creating, running and deleting a job need Query on the declared source and on every step's data source and Write on the destination, and the destination may not be more widely reachable than its sources. See [Data migration](/features/data-migration/).
- **A DataSourceAdmin links without gaining access.** Linking gives no membership of other projects; the DataSourceAdmin still needs the project admin role in the target project. A source it cannot read is linked only by someone who can (in practice an Admin).
- **A SecurityAdmin tightens, an Admin loosens.** A SecurityAdmin turns read-only enforcement and PII detection on, globally and per project; turning either off is an Admin's (`403`), and nobody's while the deployment locks them (`409`). Changing where a recipient delivers is refused (`403`) when the recipient is attached to work the SecurityAdmin cannot read.
- **The authorization provider can veto.** A host may refuse a permission with `IBeaconAuthorizationProvider.AllowsFunctionalPermissionAsync` (a default member that returns `true`).

`GET /beacon/api/auth/permissions` returns the caller's `canRead`, `canWrite`, `isAdmin` and `permissions` (the names held now; an Admin's lists all six). The shell uses it to show the pages each permission opens.

### Holding a permission

- **Through a Beacon role.** Each permission is granted by one level-0 system role, named after it by default; `Beacon:Authorization:Roles:{Permission}` maps it to another name (see [Configuration](/getting-started/configuration/#functional-permissions-role-names-identity-provider-app-roles-separation-of-duties)). Beacon creates the roles when roles are listed for assignment (`GET /beacon/api/users/roles`), at first-run setup and at SSO provisioning. A role someone created earlier under one of these names is left alone and grants nothing (seeding logs its id). Assign roles on the **Users** page or with `PUT /beacon/api/users/{id}/roles` (`{ "roleIds": [...] }`).
- **Through an identity-provider app role** (opt-in, `Beacon:Authorization:IdpRoles`). At single sign-on, app roles listed in `IdpRoles:Map` add their permission to the session's claims, issued by Beacon's sign-in. Nothing is stored; the permission counts for `IdpRoles:ClaimLifetimeMinutes` (default 60) after that sign-in, does not show on the Users page, and never reaches an API key. Only the six permissions can be mapped, never Admin, Editor or Viewer.
- **On a host without Beacon user management**, the session's `Admin` role claim grants every permission. Other role claims named like a permission's role grant it only with `Beacon:Authorization:Roles:HonourHostRoleClaims: true` (default `false`).
- **Default roles are never privileged.** `Beacon:Authentication:Oidc:DefaultRoleName` and `Beacon:Mcp:Callers:Users:DefaultRoleName` may not name the Admin role or a functional role; startup fails otherwise (for the MCP setting, while user callers are enabled with `AutoProvision`).

### Separation of duties

With `Beacon:Authorization:SeparationOfDuties:Enabled: true` (off by default), assignments that would give one user both permissions of a conflicting pair are refused with `400`, also when an Admin assigns them:

- Approver + SecurityAdmin
- Auditor + SecurityAdmin
- Auditor + UserAdmin
- UserAdmin + Approver
- UserAdmin + MigrationOperator

With it on, also: an `IdpRoles:Map` that maps onto both permissions of a pair fails startup; at sign-in, a mapped permission that would conflict with the user's stored functional roles is not added; a caller who holds both permissions of a pair anyway (roles assigned before the switch) holds **neither**, with a warning at most once an hour; and the startup log counts the active users holding a conflicting pair. Admins hold every permission and are not separated by these pairs; with the approval workflow on, an Admin still cannot approve a change they requested.

### Sessions and the realtime hub

With user management on, a `Beacon.Auth` session's role claims are replaced by the stored roles at every revalidation (`Beacon:Authentication:Session:RevalidateMinutes`, default 5), and a role change renews the user's security stamp, so their sessions end. See [User management → Sign-in sessions](/features/user-management/#sign-in-sessions).

The realtime hub (`/beacon/api/hub`) admits a signed-in caller with read permission, or one holding **Approver** or **MigrationOperator** (their pages refresh from hub events). Other functional-only users get `403` at negotiation and `GET /beacon/api/auth/me` reports `realtimeEnabled: false`. API keys get no hub connection.

## MCP safety locks

Read-only enforcement and PII detection are MCP settings, globally and per project, but by default the deployment pins both on: `Beacon:Mcp:ForceReadOnly` and `Beacon:Mcp:ForcePiiDetection` default to `true`. Two more locks are off by default: `ForceNoContentRetention` (questions, SQL and free-text errors never persisted) and `ForbidRowDataToLlm` (no value read from a data source reaches the LLM provider). While a lock is on, nobody can change the setting it pins, an Admin included (`409`, `/errors/setting-locked`); `GET /beacon/api/mcp/settings/locks` lists the pinned settings, and the MCP settings page shows them disabled. Stored values a lock overrides are kept, have no effect, and are listed in one startup warning. Only host configuration lifts a lock; see [Configuration → Deployment locks](/getting-started/configuration/#deployment-locks).

## Host seams

All defaults are registered with `TryAdd`: register your own implementation before `AddBeaconServices`, or replace the registration.

| Seam | What it decides |
|---|---|
| `IBeaconAuthorizationProvider` | global read and write permission (`HasReadPermissionAsync`, `HasWritePermissionAsync`), and a veto on functional permissions (`AllowsFunctionalPermissionAsync`) |
| `IProjectMembershipSource` | where memberships come from (for example identity-provider groups). `IsAvailable` is read once at startup; `GetMembershipsAsync` returns the caller's role per project. A `null` result or an exception denies every restricted project; unknown roles are dropped. With a custom source Beacon's membership routes answer `409` |
| `IRunAsCeilingSource` | the global role that caps an actor who is not the request's caller (default: the user's stored roles) |
| `ISystemWorkScope` | marks a DI scope as Beacon's own work. A host that runs Beacon's jobs on its own job host **must** call `MarkAsSystemWork()` on each job's scope; an unmarked scope without a request gets no project access |
| `IRunAsScope` | runs a scope as the owner of scheduled work; set by Beacon's job entry points, not by hosts |
| `IExecutionAccessCheck` | the execute-time re-check of the scope's caller on a set of data sources, right before SQL or LLM work |
| `IBeaconPermissionAccessor` | the caller's `BeaconPermissionSet` (`IsAdmin`, `Permissions`, `Holds(permission)`), read from stored roles once per request |
| `IProjectAccessEvaluator` | the whole project decision, returning a `ProjectAccessScope` (`IsUnrestricted`, `For(action)`, `RoleIn(projectId)`, `Allows(projectId, action)`) |
| `IBeaconActorAccessor` | the caller as a resource owner (`BeaconActor`: `UserId`, `IsAdmin`); an API key is its owner |

### Custom authorization provider

```csharp
using Beacon.Core.Authorization;

public sealed class MyAuthorizationProvider(IBeaconUserContext userContext, IMyPermissionService permissions)
    : IBeaconAuthorizationProvider
{
    public Task<bool> HasReadPermissionAsync(CancellationToken cancellationToken = default)
        => permissions.HasPermissionAsync(userContext.UserId, "beacon.read");

    public Task<bool> HasWritePermissionAsync(CancellationToken cancellationToken = default)
        => permissions.HasPermissionAsync(userContext.UserId, "beacon.write");
}

// in AddBeaconServices(..., options => { ... })
options.Authorization.Enabled = true;
options.AddAuthorizationProvider<MyAuthorizationProvider>();
```

The provider is the global ceiling; which projects and resources a caller reaches is decided by project access. The resource-level members (`AuthorizeAsync`, `AuthorizeNewResourceAsync`, `GetAccessibleResourceIdsAsync`) and `AuthorizationOptions.EnableResourceLevelAuthorization` are `[Obsolete]`, never called, and removed in 5.0.

## Configuration

Code-only, in the `AddBeaconServices` lambda:

| Option | Default | What it does |
|---|---|---|
| `Authorization.Enabled` | `false` | Enforces the global role on `/beacon/api`. With user management on, stored roles cap callers either way |
| `Authorization.ProviderType` (`AddAuthorizationProvider<T>()`) | `DefaultAuthorizationProvider` (allows all); `DatabaseAuthorizationProvider` with user management | The global-permission provider |

Bound from `Beacon:Authorization` and validated at startup: `RequireProjectMembership`, `Roles:*`, `Roles:HonourHostRoleClaims`, `IdpRoles:*` and `SeparationOfDuties:Enabled`. See [Configuration → Authorization](/getting-started/configuration/#authorization).

## Audit and security events

Beacon keeps two audit tables, `McpAuditLog` (every MCP tool call, read by Auditors) and `AppSettingHistory` (admin settings, MCP settings and migration-write-target changes), and writes security-relevant actions as structured log events 7101–7123 with identifiers only. The log events are not stored by Beacon: route them through your log pipeline. See [MCP observability and audit](/features/mcp-observability-and-audit/) and [Security model](/features/security-model/).

## Troubleshooting

| Symptom | Check |
|---|---|
| Everyone can do everything | `options.Authorization.Enabled = true`, or user management on; `app.UseAuthorization()` in the pipeline |
| `403` on every request | the provider returns `true` for authenticated users; the claims transformation runs and adds `ClaimTypes.Role` |
| A user sees "not found" for a project that exists | the project is restricted and the user is not a member; `404` is the answer for anything the caller may not read |
| A user with a functional role sees no projects | functional roles grant no project data; add a Viewer or Editor role |
| An Admin is refused after a role change | permissions and the Admin bypass follow the stored roles now, not the session's claims |
| Scheduled runs show "Skipped: owner not authorized." | the owner lost access; restore it or have an Admin or UserAdmin assign another owner |
| A background job reaches nothing | the job's DI scope was not marked with `ISystemWorkScope.MarkAsSystemWork()` |

## Security practices

1. Enforce on the server; client-side checks are for display only.
2. Use HTTPS and keep the session cookie's flags as Beacon sets them.
3. Keep roles in Beacon's user store or your identity provider, not hard-coded in a claims transformation.
4. Behind a reverse proxy, configure forwarded headers so the per-IP login and setup throttles see the client, not the proxy.
5. Alert on `AccessRefused` (7108) spikes and on the UserAdmin events above.
6. Keep `RevalidateMinutes` short and `AbsoluteLifetimeHours` to a working day; use **Sign out everywhere** when an account may be compromised.
7. With single sign-on, turn local password sign-in off and keep a break-glass account ([Local password sign-in](/features/user-management/#local-password-sign-in)).
8. Restrict sensitive projects (new ones already are), give each data source a login that sees only what its projects should, and configure its database in the connection.
9. Turn on separation of duties where one person must not hold both sides of a pair.

## Upgrading from earlier versions

### Upgrading from 4.5: project access

Projects became the authorization boundary on the REST API, the UI, the MCP playground, MCP tool calls and scheduled work.

- **Schema.** Apply the `AddProjectAuthorization` and `AddContractOwnerNotAuthorized` migrations (both providers) as you apply the others; Beacon does not migrate its schema at startup. Existing projects stay open and existing data sources stay reachable (`open_when_unlinked = true`).
- **New behaviour without configuration.** New projects are restricted with the creator as project admin; new data sources are Admin-only until linked; sessions without a stored user reach nothing while user management is on; a refusal answers `404`/`403` through Beacon's own exception mapping.
- **API keys.** A key's project list applies on every REST route, not only MCP; new keys must name at least one project. Keys **without** a list now reach their owner's projects on MCP (before: none). Re-issue them with a list.
- **MCP.** `ask`, `query`, `dry_run` and `feedback` need Query (Editor) in the project; mapped Entra user callers need membership of restricted projects.
- **Scheduled work runs as its owner** and is skipped when the owner may no longer do it. Assign owners (`PUT …/owner`, Admin or UserAdmin) to ownerless subscriptions, contracts and actors before restricting projects.
- **Background jobs on a host's own job runner** must mark each job scope with `ISystemWorkScope.MarkAsSystemWork()`.
- **Every query execution goes through the execution gate**, with `Beacon:Query:MaxExecutionRows` and `StatementTimeoutSeconds`; check stored queries and subscriptions after upgrading (comments no longer swallow clauses, delivered rows are PII-masked, a statement's own bound above the cap is lowered to it, SQL naming another database is refused).
- **MCP read-only enforcement and PII detection are locked on** by default; **data migration is off** unless `Beacon:Migrations:Enabled` is `true`.
- **Recommended steps for a regulated deployment:** give each data source a login per boundary and configure its database; add members and restrict sensitive projects; re-issue integration keys with project lists; review shared data sources on each project's **Access** card; then set `Beacon:Authorization:RequireProjectMembership: true`.
- **Public API.** New project-access types in `Beacon.Core.Authorization` (`IProjectAccessEvaluator`, `ProjectAction`, `ProjectRole`, `IProjectMembershipSource`, `ISystemWorkScope`, `IRunAsScope`, `IExecutionAccessCheck`, …); several positional records gained members and need a recompile.

### Upgrading: functional permissions

Existing Admins are unchanged: they hold every permission, and the new roles are optional.

- The six roles are created as level-0 system roles when roles are next listed or seeded; a role of yours with one of those names grants nothing until renamed (or map the permission to another name).
- The listed routes carry `BeaconApi.{Permission}` policies and re-check the permission live; a demoted Admin's session is refused on them.
- On a host without Beacon user management, role claims named like a permission no longer grant it unless `Roles:HonourHostRoleClaims` is `true`.
- `DefaultRoleName` (OIDC and MCP callers) may no longer name the Admin role or a functional role.
- UserAdmins create external users only, and no longer open restricted projects or touch protected users; SecurityAdmins no longer turn read-only enforcement or PII detection off or change the base URL.

### Upgrading: sign-in sessions

- Every session is checked against the user store at its next request, then every `RevalidateMinutes`; sessions end `AbsoluteLifetimeHours` (default 12) after sign-in, whatever "remember me" asked.
- The `AddUserSecurityStamp` migration adds `users.security_stamp` and `must_change_password`. Password changes, disabling, role changes and **sign out everywhere** end the user's other sessions.
- `AddBeaconCookieAuthentication` no longer configures Data Protection: call `AddBeaconDataProtection(configuration, environment)` or configure your own; outside Development set `Beacon:DataProtection:ApplicationName` and encrypt the key ring. Everyone signs in again once.
- `Beacon:EncryptionKey` is checked in every environment; list a replaced key in `Beacon:PreviousEncryptionKeys`.
- See [User management → Upgrading from 4.5](/features/user-management/#upgrading-from-45) for sign-in changes (local login, break-glass, forced password change, `RequireMfa`).

### Upgrading: data minimisation

- Host data sources are masked by output position; database errors are redacted by class before they reach prompts, signals, callers or the audit; sample values and value grounding are off for a new installation.
- Banking identifiers (`iban`, `oib`, `jmbg`, account numbers) match the built-in PII pattern.
- Every LLM request carrying row data logs `RowDataSentToLlm` (7123); `Beacon:Mcp:ForbidRowDataToLlm` locks row data out of prompts.
- The `DefaultSampleValueCollectionOff` migration changes only a column default.

### Upgrading: change governance

The approval workflow became a four-eyes control bound to reviewed content, and was extended to migration jobs, Custom SQL rules and recipient destinations. It stays opt-in. Pending requests made before the upgrade can only be rejected and resubmitted. Apply the `AddQueryChangeIntegrity` and `AddChangeApprovalRequests` migrations. See [Approvals](/features/approvals/).

### Upgrading: grounding integrity

Feedback no longer creates golden examples (an Admin promotes candidates), curation needs the project admin role, every curation binds to the content reviewed, and grounding SQL is checked before it is stored. Apply the `AddGroundingAttribution` and `AddGroundingProvenance` migrations, review existing golden cases that record no promoting user, and add a project admin to each project whose grounding someone other than an Admin should curate. See [Knowledge base](/features/knowledge-base/#who-curates-grounding).

## See also

- [User management](/features/user-management/): accounts, sign-in, sessions, functional roles on the Users page
- [API keys](/features/api-keys/): scopes, project lists, expiry
- [Approvals](/features/approvals/): the four-eyes workflow
- [Security model](/features/security-model/): the controls end to end
- [Configuration](/getting-started/configuration/): every key
