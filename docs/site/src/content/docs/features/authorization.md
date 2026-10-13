---
title: Authorization
description: Pluggable authorization for Beacon with global roles, project membership and data-source access, database-backed roles, custom providers and host seams.
---

Beacon authorizes in two layers: a caller's **global permission** (read, write) comes from a pluggable `IBeaconAuthorizationProvider`, and its **project access** — which projects, and through them which data sources, it may use — comes from one evaluator, `IProjectAccessEvaluator`. See [Project access](#project-access).

## Overview

The authorization system consists of:

- **IBeaconUserContext** - Provides access to current user information
- **IBeaconAuthorizationProvider** - The caller's global read and write permission
- **IProjectAccessEvaluator** - The caller's project access per action (membership, open projects, API-key project lists, Admins)
- **IBeaconAuthenticationProvider** - Pluggable authentication (the sample uses `DatabaseAuthenticationProvider`)
- **Built-in Providers** - Ready-to-use implementations (Default, Database-Backed)
- **Custom Providers** - Plug in your own authorization logic

### Key Features

- Opt-in by default — authorization is disabled unless explicitly enabled
- Pluggable architecture — integrate with any authentication system
- Cookie-based sessions — a login form is served at the React route `/login`; optional OIDC/SSO and JWT bearer (for MCP) are also supported
- Two layers — global permissions, and project access: project membership, with data sources following their projects
- Backward compatible — existing installations work unchanged
- Framework agnostic — works with ASP.NET Core Identity, OAuth, custom auth, etc.
- Database-backed roles — built-in user management with Admin, Editor, Viewer roles
- Functional permissions — the Admin role's duties (approving, auditing, security settings, user, data-source and migration administration) can be granted separately, without project data

:::note
For full user management (creating users, assigning roles, login form), see the [User Management Guide](/features/user-management/).
:::

## Quick Start

### 1. Enable Authorization

Update your `Program.cs` to enable authorization:

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options =>
{
    options.AddBeaconScheduler<BeaconScheduler>();
    options.BaseUrl = "https://localhost:7187";

    // Enable authorization (with Beacon user management, DatabaseAuthorizationProvider is used instead)
    options.Authorization.Enabled = true;
    options.AddAuthorizationProvider<MyAuthorizationProvider>(); // see Custom Authorization Provider
})
.UsePostgreSql(connectionString, "beacon");

// Serves the React SPA (Beacon.UI Razor Class Library) at the root URL "/"
builder.Services.AddBeaconUI();

var app = builder.Build();

// Authentication + authorization middleware
app.UseAuthentication();
app.UseAuthorization();

// Map the React shell + Beacon REST API
app.UseBeaconUI();
```

### 2. Add Role Claims

A host that signs users in itself supplies role claims. Add a claims transformer:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Beacon.Core.Authorization;

public class MyClaimsTransformation : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var identity = (ClaimsIdentity)principal.Identity!;

        // Add Beacon role claim, and the standard role claim the Admin checks read
        identity.AddClaim(new Claim(BeaconClaims.Role, "Admin"));
        identity.AddClaim(new Claim(ClaimTypes.Role, "Admin"));
        identity.AddClaim(new Claim(BeaconClaims.UserId, principal.Identity.Name));
        identity.AddClaim(new Claim(BeaconClaims.UserName, principal.Identity.Name));

        return Task.FromResult(principal);
    }
}

// Register it
builder.Services.AddScoped<IClaimsTransformation, MyClaimsTransformation>();
```

### Admin role claim

Admin-only routes (the `BeaconApiAdmin` policy; routes of a [functional permission](#functional-permissions) admit the Admin role too) and the resource rules that let an Admin act on what another user owns (working someone else's alert task, changing another user's data contract or AI actor, setting a repository access token) read the standard `ClaimTypes.Role` claim with the value `Admin`. A host that signs users in itself must emit that claim for its administrators; `beacon:role` alone is not enough. Beacon's own login and OIDC sign-in emit it from the user's roles. When Beacon user management stores the caller, the user must also be enabled and not archived; an API key never carries a role, so it is never an Admin. For [project access](#project-access), a stored user is an Admin by the roles stored now, not by the claim the session carries, so a demoted Admin loses the Admin bypass on the next request; the claim counts only on a host without Beacon user management, for a caller without a stored user. While user management is on, a session without a stored user reaches no project at all.

### 3. Test Authorization

Start your application and verify:

- Username appears in the React UI (not hardcoded)
- Unauthorized operations return 403 Forbidden
- Different roles have different permissions

## User Context

### Accessing Current User

Inject `IBeaconUserContext` anywhere in your application:

```csharp
public class MyService
{
    private readonly IBeaconUserContext _userContext;

    public MyService(IBeaconUserContext userContext)
    {
        _userContext = userContext;
    }

    public void DoSomething()
    {
        var userId = _userContext.UserId;
        var userName = _userContext.UserName;
        var email = _userContext.Email;
        var isAuthenticated = _userContext.IsAuthenticated;

        if (_userContext.HasClaim(BeaconClaims.Role, "Admin"))
        {
            // Admin-only logic
        }
    }
}
```

### Standard Claims

Use these standard claim types for consistency:

```csharp
BeaconClaims.UserId       // "beacon:user_id"
BeaconClaims.UserName     // "beacon:user_name"
BeaconClaims.Role         // "beacon:role"
BeaconClaims.Permission   // "beacon:permission"
```

## Built-in Authorization Providers

### DefaultAuthorizationProvider

Allows all operations (backward compatible default).

**Use case:** When you don't need authorization or handle it elsewhere.

```csharp
// Authorization disabled (default behavior)
builder.Services.AddBeaconServices(builder.Configuration, options =>
{
    // Authorization.Enabled = false by default
    options.AddBeaconScheduler<BeaconScheduler>();
})
.UsePostgreSql(connectionString, "beacon");
```

### DatabaseAuthorizationProvider

Database-backed authorization that reads roles from Beacon's user management tables. This is the recommended provider when using the built-in [User Management](/features/user-management/) system.

| Role | Level | Read | Create/Edit/Execute | Delete/Archive |
|------|-------|------|---------------------|----------------|
| **Admin** | 3 | Yes | Yes | Yes |
| **Editor** | 2 | Yes | Yes | No |
| **Viewer** | 1 | Yes | No | No |
| Functional roles (Approver, Auditor, …) | 0 | No | No | No |

A functional role grants its [functional permission](#functional-permissions) and no read or write permission.

**Use case:** When using Beacon's built-in user management with login form and role assignment.

```csharp
options.Authorization.Enabled = true;

// Enable user management + database auth
options.Authentication.EnableLoginForm = true;
options.AddAuthenticationProvider<DatabaseAuthenticationProvider>();

options.UserManagement = new UserManagementOptions
{
    Enabled = true,
    AllowInternalUsers = true
};
```

No claims transformer needed — roles are loaded directly from the database.

:::note
`DatabaseAuthorizationProvider` is automatically registered when user management is enabled. It also supports `IsSuperAdmin` to bypass all checks.
:::

## Custom Authorization Provider

Implement `IBeaconAuthorizationProvider` to create custom authorization logic:

```csharp
using Beacon.Core.Authorization;

public class MyAuthorizationProvider : IBeaconAuthorizationProvider
{
    private readonly IBeaconUserContext _userContext;
    private readonly IMyPermissionService _permissionService;

    public MyAuthorizationProvider(
        IBeaconUserContext userContext,
        IMyPermissionService permissionService)
    {
        _userContext = userContext;
        _permissionService = permissionService;
    }

    // Global permissions (required)
    public async Task<bool> HasReadPermissionAsync(
        CancellationToken cancellationToken = default)
    {
        return await _permissionService.HasPermissionAsync(
            _userContext.UserId,
            "beacon.read");
    }

    public async Task<bool> HasWritePermissionAsync(
        CancellationToken cancellationToken = default)
    {
        return await _permissionService.HasPermissionAsync(
            _userContext.UserId,
            "beacon.write");
    }
}
```

The resource-level members (`AuthorizeAsync`, `AuthorizeNewResourceAsync`, `GetAccessibleResourceIdsAsync`) are obsolete: they have default bodies, Beacon never calls them, and they are removed in 5.0. Which projects and resources a caller reaches is decided by [project access](#project-access); a provider's read and write permission caps it.

Register your provider:

```csharp
options.Authorization.Enabled = true;
options.AddAuthorizationProvider<MyAuthorizationProvider>();
```

## Integration Examples

### ASP.NET Core Identity

```csharp
// Add Identity
builder.Services.AddIdentity<ApplicationUser, IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>();

// Add Beacon with authorization
builder.Services.AddBeaconServices(builder.Configuration, options =>
{
    options.Authorization.Enabled = true;
    options.AddAuthorizationProvider<MyAuthorizationProvider>();
})
.UsePostgreSql(connectionString, "beacon");

// Claims transformer
public class IdentityToBeaconClaimsTransformer : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var identity = (ClaimsIdentity)principal.Identity!;

        // Map Identity roles to Beacon roles
        if (principal.IsInRole("Administrator"))
            identity.AddClaim(new Claim(BeaconClaims.Role, "Admin"));
        else if (principal.IsInRole("PowerUser"))
            identity.AddClaim(new Claim(BeaconClaims.Role, "Editor"));
        else
            identity.AddClaim(new Claim(BeaconClaims.Role, "Viewer"));

        // Add user claims
        identity.AddClaim(new Claim(BeaconClaims.UserId,
            principal.FindFirstValue(ClaimTypes.NameIdentifier)));
        identity.AddClaim(new Claim(BeaconClaims.UserName,
            principal.Identity.Name));

        return Task.FromResult(principal);
    }
}

builder.Services.AddScoped<IClaimsTransformation, IdentityToBeaconClaimsTransformer>();
```

### OAuth 2.0 / OpenID Connect

```csharp
// Add authentication
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
})
.AddCookie()
.AddOpenIdConnect(options =>
{
    options.Authority = "https://your-identity-provider.com";
    options.ClientId = "your-client-id";
    options.ClientSecret = "your-client-secret";
    options.ResponseType = "code";
    options.SaveTokens = true;
});

// Add Beacon with authorization
builder.Services.AddBeaconServices(builder.Configuration, options =>
{
    options.Authorization.Enabled = true;
    options.AddAuthorizationProvider<MyAuthorizationProvider>();
})
.UsePostgreSql(connectionString, "beacon");

// Claims transformer
public class OAuthToBeaconClaimsTransformer : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var identity = (ClaimsIdentity)principal.Identity!;

        // Map OAuth roles to Beacon roles
        var role = principal.FindFirstValue("role") switch
        {
            "admin" => "Admin",
            "user" => "Editor",
            _ => "Viewer"
        };

        identity.AddClaim(new Claim(BeaconClaims.Role, role));
        identity.AddClaim(new Claim(BeaconClaims.UserId,
            principal.FindFirstValue("sub")));
        identity.AddClaim(new Claim(BeaconClaims.UserName,
            principal.FindFirstValue("name")));

        return Task.FromResult(principal);
    }
}
```

### External Authorization Service

```csharp
// Your external auth service
public interface IExternalAuthService
{
    Task<bool> CheckPermissionAsync(string userId, string permission);
    Task<string[]> GetUserRolesAsync(string userId);
}

// Custom authorization provider
public class ExternalAuthProvider : IBeaconAuthorizationProvider
{
    private readonly IBeaconUserContext _userContext;
    private readonly IExternalAuthService _externalAuth;

    public ExternalAuthProvider(
        IBeaconUserContext userContext,
        IExternalAuthService externalAuth)
    {
        _userContext = userContext;
        _externalAuth = externalAuth;
    }

    public async Task<bool> HasReadPermissionAsync(
        CancellationToken cancellationToken = default)
    {
        return await _externalAuth.CheckPermissionAsync(
            _userContext.UserId,
            "beacon.read");
    }

    public async Task<bool> HasWritePermissionAsync(
        CancellationToken cancellationToken = default)
    {
        return await _externalAuth.CheckPermissionAsync(
            _userContext.UserId,
            "beacon.write");
    }

    // Implement other methods...
}

// Register it
builder.Services.AddScoped<IExternalAuthService, YourExternalAuthService>();
options.AddAuthorizationProvider<ExternalAuthProvider>();
```

## Functional permissions

The Admin role's duties are split into six **functional permissions**. An Admin holds every one of them, so nothing changes for Admins; a user who should do one duty, and not the others, gets the role of that permission instead.

| Permission | What it opens |
|---|---|
| **Approver** | Approve and reject query changes (`POST /approvals/{id}/approve`, `/reject`) — never their own (see [Queries → Approval workflow](/features/queries/#approval-workflow)); with the approval workflow on, approve the plans of AI actors they did not create; read the approval requests of **every** project (`GET /approvals/pending`, `GET /approvals/{id}`) |
| **Auditor** | Read the MCP audit (`GET /mcp/audit`), the MCP settings and project settings (`GET /mcp/settings`, `GET /mcp/projects/{projectId}/settings`), the admin settings with secrets masked (`GET /admin-settings`) and every user's API keys (`GET /api-keys/admin`) |
| **SecurityAdmin** | Read and change the MCP settings and project settings; read and change the admin settings **except the LLM provider, endpoint, region, authentication mode and credentials** ([Admin Settings](/features/admin-settings/#who-may-do-what)); test the stored LLM connection; create, change and delete recipients; publish a saved query it can read as an MCP tool (`PUT /queries/{id}/mcp-tool`) — with the approval workflow on, not when it requested or approved the version the tool runs; set a repository access token (`PUT /projects/repositories/{id}/token`, and on project creation) |
| **UserAdmin** | Manage users (`/users/*`, including `PUT /users/{id}/roles`; creating an internal user and changing what a user signs in with stay an Admin's), list and revoke any user's API keys, change the owner of a subscription, data contract or AI actor, manage the members of **any** project, restrict a project (`PUT /projects/{id}/access`) |
| **DataSourceAdmin** | Create, test and delete data sources; link a data source no open project holds (restricted-only or unlinked) into a project it is the admin of, when it can read the source; change a shared data source's state with Write only where it acts; force a metadata refresh without the cooldown |
| **MigrationOperator** | Data migration (`/migrations/*`, mapped only while the host runs migrations), for the jobs whose data sources it reads, and the Home migration totals of those jobs |

**Admin only:** glossary and eval curation (checked against live roles), the LLM provider, endpoint, region, authentication mode and credentials, the base URL, turning read-only enforcement or PII detection off (globally or for a project — only where the deployment unlocks them, see [MCP safety locks](#mcp-safety-locks)), opening a restricted project, unlinking a data source from a project, data contracts with Custom SQL rules, and the Warp dashboard of the sample host.

### Rules

- **Functional roles grant no project data.** Project access is decided as before: the Admin bypass stays an Admin's, and a functional role carries no global role (it is a level-0 role), so a user holding only functional roles reads no project, data source or resource and gets `403` on every other route. Give such a user a Viewer or Editor role as well to let them use projects as usual. Two duty-specific exceptions: an **Approver** reads every project's approval requests, with their SQL; an **Auditor** reads every project's MCP audit rows, with the SQL and question text the retention settings keep.
- **A UserAdmin cannot raise anyone, itself included.** A UserAdmin who is not an Admin never assigns or removes the Admin role or a functional role, never changes their own account, roles or project membership, and never changes the account, the project memberships or the API keys of a **protected user**: an Admin, a super admin, or a holder of a functional role (every level-0 system role, whatever it is named now, and any role named as a functional role) — other UserAdmins included, so two UserAdmins cannot add each other to a project (`403`). A UserAdmin restricts projects but does not open a restricted one, and never makes themselves the owner of a subscription, data contract or AI actor (`403`). The membership rules still apply: the last effective project admin is kept unless an Admin removes them. Every user created is logged (event 7114 `UserCreated`, ids only), every role change (event 7113 `UserRolesChanged`, ids only), and every membership change as before (7101–7103).
- **A UserAdmin controls no one's sign-in.** Otherwise a UserAdmin could create an account with a password of its choosing, make it a member of a restricted project and sign in as it. So a UserAdmin who is not an Admin cannot create an **internal** (password) user (`POST /users/internal`, `403`, nobody is created), nor change a user's **user name or e-mail address** (`PUT /users/{id}` with values other than the stored ones, `403`), since an internal user signs in with either. No route sets another user's password, or changes a user's external id, identity provider or user type after the user is created; a user changes their own password only with the current one. An Admin does all of these.
- **What a UserAdmin can still do (accepted).** A UserAdmin creates **external** users and gives them the Viewer or Editor role, and adds unprotected users to any project. An external user signs in through the identity provider as the subject (`ExternalId`) it was created with, so the identity provider and its [admission rules](/features/user-management/#single-sign-on-admission) bound what this reaches: a UserAdmin who also controls a second account at the identity provider can pre-register it and make it a member of a restricted project. Beacon records no creator per user, so it applies no four-eyes rule to that; it is the duty, not a loophole, and it should be watched: alert on events **7114** (`UserCreated`), **7101** (`MemberAdded`) and **7113** (`UserRolesChanged`) whose actor holds UserAdmin, and review them. A permission an identity provider maps is not stored, so a user holding UserAdmin (or another permission) only through an app role is not a protected user.
- **A MigrationOperator goes through project access, with no bypass.** A job reads its declared source and the data source of **every step** its query text runs (read the way a run reads it). Creating a job, every run of it and deleting it need **Query** on each of those data sources (a step is SQL of its own) and Write on the destination (`404` unreadable, `403` refused), and the destination may not be more widely reachable than any of them: every live project linking the destination must link each source too, and an openly reachable destination needs openly reachable sources (`403`). A source that is also the destination widens nothing, but its steps are checked like any other. The job list, a job and its run history need Read on the declared source and the destination; a failed run's error text is shown only to a caller who reads every data source the job reads. Running the steps checks Query on each step's data source once more. See [Data Migration](/features/data-migration/#managing-migration-jobs).
- **A DataSourceAdmin links without gaining access.** Linking a data source that only restricted projects hold (or no project) into a project the DataSourceAdmin is the admin of gives them no membership of those projects; they still need that project's admin role to link at all. A data source the DataSourceAdmin cannot read is linked only into a restricted project they are not a member of, since the link would otherwise let them read it: in practice an Admin links it. `POST /projects` with such a data source is refused (`403`): the creator becomes the new project's member.
- **A SecurityAdmin redirects only what it can read, and tightens.** Changing where a recipient delivers (type, destination or headers) is refused (`403`) when the recipient is attached to a subscription or data contract the SecurityAdmin cannot read; only an Admin changes or tests another LLM endpoint, region, provider, authentication mode or credential, or changes the base URL. A SecurityAdmin turns read-only enforcement and PII detection on, globally and per project; turning either off, pinning a project's override off, or clearing a project's override that is on while the global value is off is an Admin's (`403`) — and nobody's while the deployment locks them (`409`, see [MCP safety locks](#mcp-safety-locks)).
- **Permissions are read live.** Each route of a functional permission carries the policy `BeaconApi.{Permission}` (`BeaconPermissions.ApproverPolicy`, …; the settings reads `BeaconApi.Auditor|SecurityAdmin`, the key list `BeaconApi.Auditor|UserAdmin`), which admits the Admin role or the permission's role among the session's role claims (role names compared ignoring case). The permission filter then admits the caller only if they hold the permission **now**: a user whose role was removed is refused with `403` although their session still carries the claim. The glossary and eval routes keep `BeaconApiAdmin` and, the same way, admit only an Admin by live roles. A few routes a permission also opens (approval reads, a project's members and member candidates, the Home migration summary) admit its holder beside everyone with read or write permission.
- **The authorization provider is still asked.** On a permission's routes an Admin needs read or write permission from `IBeaconAuthorizationProvider`, as on every other route. A holder who is not an Admin is admitted without a Viewer or Editor role, unless the provider vetoes the permission: `AllowsFunctionalPermissionAsync(permission, cancellationToken)` (a default interface member that returns `true`) is asked instead of `HasReadPermissionAsync`/`HasWritePermissionAsync`. On a route the permission is the requirement of, a veto is `403`; on a route it only also opens, the caller needs read or write permission as usual.

### Holding a permission

- **Through a Beacon role.** Each permission is granted by one role, by default named after it (`Approver`, `Auditor`, `SecurityAdmin`, `UserAdmin`, `DataSourceAdmin`, `MigrationOperator`); `Beacon:Authorization:Roles:{Permission}` maps it to another name (see [Configuration](/getting-started/configuration/#functional-permissions-role-names-identity-provider-app-roles-separation-of-duties)). Only a **functional role** grants a permission: a system role at level 0 with the mapped name (compared ignoring case). Beacon creates the roles at level 0 when roles are listed for assignment (`GET /beacon/api/users/roles`, which returns each role's `permission`), at first-run setup and when single sign-on provisions a user; a role someone created earlier under one of these names is left as it is and grants nothing, and seeding logs a warning with its id (rename it, or map the permission to another name). Assign them on the Users page or with `PUT /beacon/api/users/{id}/roles` (`{ "roleIds": [...] }`), see [User Management](/features/user-management/#functional-roles).
- **Through an identity-provider app role** (opt-in, `Beacon:Authorization:IdpRoles`). At single sign-on, app roles listed in `IdpRoles:Map` add their permission to the session's claims, issued by Beacon's sign-in and stamped with its time. Nothing is stored, and the permission is honoured for `IdpRoles:ClaimLifetimeMinutes` (default 60) after that sign-in, although the session cookie lives longer: an app role the identity provider withdraws stops counting at the latest then, and signing in again renews it. Dropped permissions are logged once per sign-in. Such claims from any other issuer grant nothing. The permission does not show on the Users page.
- **Never through an API key.** A key carries no role and no functional permission, whatever its owner holds. A key refused by a route a permission is required on is told so: *"API keys cannot use administrative routes; sign in as a user with the X permission."* Automate administrative work as a signed-in user.
- **On a host without Beacon user management**, the session's `Admin` role claim grants every permission. Its other role claims (`ClaimTypes.Role` with a permission's role name) grant that permission only with `Beacon:Authorization:Roles:HonourHostRoleClaims: true` (default `false`); the route policies follow the same setting. The startup log says which applies.

`GET /beacon/api/auth/permissions` returns the caller's permissions now, besides `canRead` and `canWrite`: `isAdmin` and `permissions` (the names it holds; an Admin's lists all six). The shell uses it to show the pages each permission opens.

### Sessions and the realtime hub

- **A session's roles follow the user store.** With user management on, a `Beacon.Auth` session is checked against the user store at most every `Beacon:Authentication:Session:RevalidateMinutes` (default 5): a session of a missing, disabled or archived user, or of a user whose security stamp changed, is signed out, and the session's role claims are replaced by the stored roles (plus those an identity provider mapped at sign-in, while that mapping is on). A role change also changes the user's security stamp, so the user's sessions end and they sign in with the new roles. Every session ends `Beacon:Authentication:Session:AbsoluteLifetimeHours` (default 12) after its sign-in. See [User Management → Sign-in sessions](/features/user-management/#sign-in-sessions).
- **The realtime hub** (`/beacon/api/hub`) admits a signed-in caller with read permission (Viewer or above, or an Admin), or one holding a permission whose pages refresh from hub events: **Approver** (approval updates) and **MigrationOperator** (the status of the migration jobs they run). A user holding only other functional permissions (Auditor, SecurityAdmin, UserAdmin, DataSourceAdmin) gets `403` at the hub's negotiate request, and `GET /beacon/api/auth/me` reports `realtimeEnabled: false` to them, so the shell does not try. A hub connection is checked against the user store when it is opened and closed when the session's next check would be due (`Beacon:Authentication:Session:RevalidateMinutes`, at least one minute; at the latest at the absolute lifetime), so a demoted or signed-out user loses it then; the shell reconnects through a new check. API keys get no hub connection. A host with neither authorization nor user management on admits every signed-in caller, as before; a host without Beacon's configuration admits nobody.

### Separation of duties

With `Beacon:Authorization:SeparationOfDuties:Enabled: true` (off by default), assigning roles that would give one user both permissions of a conflicting pair is refused with `400`, also when an Admin assigns them. The pairs: **Approver + SecurityAdmin**, **Auditor + SecurityAdmin**, **Auditor + UserAdmin**, **UserAdmin + Approver**, **UserAdmin + MigrationOperator**. With it on, also:

- an `IdpRoles:Map` that maps app roles onto both permissions of a pair fails startup;
- at single sign-on, a mapped permission that would conflict with the user's stored functional roles is not added (logged as a warning, ids and permission names only);
- whatever way a caller came to hold both permissions of a pair (roles assigned before the switch, a mapped permission), it holds **neither** of them, and a warning is logged at most once an hour per caller;
- the startup log says that separation of duties is on and, best effort, how many active users already hold a conflicting pair through their stored roles.

Admins hold every permission by construction and are not separated by these pairs; with the approval workflow on, it still separates them from their own query changes (an Admin does not approve a change they requested or shaped, nor publish as an MCP tool a version they requested or approved — see [Upgrading: change governance](#upgrading-change-governance)).

### Host seams for permissions

- **`IBeaconPermissionAccessor`** (scoped, registered with `TryAdd`) — the caller's `BeaconPermissionSet` (`IsAdmin`, `Permissions`, `Holds(permission)`). The default reads a stored user's roles in the user store, once per request and only when a permission is asked for.
- **`IBeaconAuthorizationProvider.AllowsFunctionalPermissionAsync`** — a host's veto on a functional permission it does not want used (default: allowed).
- **`BeaconPermissions`** — the permission names, their policy names (`Policies`) and the conflicting pairs; **`RoleMappingOptions`** — the role names, identity-provider mapping and separation of duties, validated at startup.
- **Policy names.** The `BeaconApi.` prefix is Beacon's: do not name a host authorization policy with it.

## Project access

Projects are the authorization boundary. A caller reaches a project's queries, subscriptions, stored runs, tasks, data contracts, AI actors, documentation and data-source metadata only through a project it may use, and a data source only through a project that links it. One evaluator, `IProjectAccessEvaluator`, decides this for every caller once per request; the REST API, the UI, the MCP playground and MCP tool calls consult it.

### Open and restricted projects

- **Open** (every project that existed before this version, and a new project whose creator cannot be its member — see below): everyone with a Beacon role uses the project at their global role — a Viewer reads and runs saved SQL, an Editor also writes and works ad hoc.
- **Restricted**: only the project's members and Admins use it; to anyone else it does not exist (`404`). An Admin or a UserAdmin restricts a project with the **Access** card on the project page (`PUT /beacon/api/projects/{id}/access` with `{ "requiresMembership": true }`); opening a restricted one again is an Admin's (`403` for anyone else). Add members — a project admin first — before restricting. The card lists the project's data sources another open project also links, which stay reachable through that project, and every other project, open or restricted, that links any of its data sources, with that project's admins (see [Unlinking](#unlinking-data-sources)); the `PUT` answer carries the same list as `sharedSourceProjects`. Restricting answers `409` while project membership is unavailable; an Admin can always open a project.
- **`Beacon:Authorization:RequireProjectMembership: true`** restricts every project, whatever its own setting, and makes data sources no project links Admin-only. It needs project memberships (Beacon user management, or a host [membership source](#host-seams)); the host fails at startup otherwise.
- **New projects are restricted.** When the creator is an active Beacon user and Beacon manages project membership, the project is created restricted with the creator as its project admin (an Admin who creates a project becomes its project admin too); the creator adds members, and an Admin opens it if everyone should reach it. A creator without an active Beacon user (a host principal, a host without Beacon user management), and any creator while the host manages membership or membership is unavailable, gets an open project — nobody could be a member of a restricted one — and Beacon logs `ProjectCreatedOpen` (7112, Information, ids only). The `POST /beacon/api/projects` answer carries `requiresMembership`.
- **Archived projects** grant nothing. No route archives a project today.

### Project roles

A member holds one role in a project. The **effective role** is the lower of the membership role and the caller's global role (a global Viewer is a Viewer in every project); in an open project, the open grant applies as well and the higher of the two wins.

| Action | Viewer | Editor (restricted project) | Editor (open project) | Project admin | Admin |
|---|---|---|---|---|---|
| Read: see resources, SQL, metadata, stored results, documentation | ✓ | ✓ | ✓ | ✓ | ✓ |
| Run: execute saved SQL, evaluate data contracts | ✓ | ✓ | ✓ | ✓ | ✓ |
| Query: ad-hoc and draft SQL, LLM work (playground, AI actors) | – | ✓ | ✓ | ✓ | ✓ |
| Write: create and change resources | – | ✓ | ✓ | ✓ | ✓ |
| Manage: delete, archive, take over a lock, force a metadata refresh | – | only as the recorded owner | ✓ | ✓ | ✓ |
| Manage members and link data sources | – | – | – | ✓ | ✓ |
| Restrict or open the project | – | – | – | – | ✓ |

A UserAdmin also manages the members of every project (never a protected user's), and restricts any project, without being its member (see [Functional permissions](#functional-permissions)); a DataSourceAdmin links data sources no open project holds, when it can read them.

The recorded owner is a data contract's or subscription's owner, an AI actor's creator, and a query's lock holder. The ownership rules on tasks, contracts and AI actors still apply on top: both the project action and the owner rule must pass.

### Data sources follow their projects

- A data source is reachable only through a project the caller may use. A resource that reads several data sources is reachable only through **one** project that links all of them. Resources addressed by their data sources alone (an ad-hoc preview, a metadata read, a data contract) have no home project: any such project serves.
- **Unlinked data sources.** A data source no project links stays reachable as before only if it existed before this version (`OpenWhenUnlinked`, true for those rows). A new data source is Admin-only until it is linked — link it on create (the **Projects** field of the create dialog, `projectIds` on `POST /beacon/api/data-sources`).
- **Shared data sources.** A source linked to an open project is reachable through that project by everyone with a role, even when a restricted project links it too. Writes to a source's shared state — relationships, metadata description edits, a forced metadata refresh, documentation patches that write descriptions — need Write in **every** live project that links it, or an Admin, because that state grounds every project that uses the source.
- **Linking.** A project admin (or an Admin) links data sources into the project from its page (**Link data sources**, `POST /beacon/api/projects/{id}/data-sources`). Anyone but an Admin may link only sources already linked to a live open project; a host's data source links only into the project the host declares for it (the project with the same host key), for Admins too. An unreadable or missing source answers `404`, a readable one that may not be linked `403`, and nothing is linked partially. The same rules apply to the data sources named when a project is created (`dataSourceIds` and `repositoryUrls` may be omitted or `null`: none). Each link records who created it; links the host creates, and links that existed before this version, record no one.
- **Queries without steps** read no data source, so no project holds them: their creator (recorded as the query's owner) reads and changes them, and Admins; once the query has steps, the project rule above applies to it and to the work derived from it. A step-less query created before owners were recorded has no owner: it is reachable while every project is open and every data source openly reachable (and the caller carries no project list), and Admin-only otherwise.
- **One database login per boundary.** Project access decides who may run SQL through Beacon, not what the database login may read. Give each data source a login that sees only what the projects linking it should see: a login that can read several boundaries defeats project separation, because a view, a synonym or a function in the source's own database can still read whatever the login can. Beacon also refuses SQL that names another database (see [Database names in SQL](#database-names-in-sql)), which only holds when the data source's database is configured.

### Database names in SQL

SQL that Beacon runs against a data source may name objects of that source's own database only. A name qualified with a database — SQL Server, Azure Synapse, PostgreSQL and Snowflake `database.schema.table`, MySQL and SQLite `database.table`, Databricks `catalog.schema.table`, BigQuery `project.dataset.table` — must carry the database, catalog or project the data source's connection is configured for; anything else is refused before the SQL runs. Unqualified and schema-qualified names are always allowed. The check applies whether or not read-only enforcement is on.

- **Where the configured database comes from:** the connection string (SQL Server and Azure Synapse `Initial Catalog` or `Database`, PostgreSQL `Database` or `DB`, MySQL `Database` or `Initial Catalog`, Snowflake `db`; a SQLite file is always `main`) or the connector settings (Databricks `Catalog`, BigQuery `ProjectId`). A data source without one — a login's default database, a workspace's default catalog — accepts no database qualifier at all: set it in the connection, or write `schema.table`. When the connection settings cannot be read or parsed, no qualifier is accepted either, and the refusal says that the data source's database could not be determined (a warning names the data source by id).
- **Comparison** follows the engine: PostgreSQL folds an unquoted name to lower case and Snowflake to upper case before an exact comparison; MySQL compares database names exactly (they are case-sensitive on most servers, so write them as configured); SQL Server, SQLite, Databricks and BigQuery compare without regard to case. A SQL Server database with a case-sensitive collation is rare; there `Sales` and `SALES` would still be read as the same database.
- **System databases are other databases:** MySQL `information_schema.tables` or `mysql.user`, SQL Server `master.sys.databases`, Snowflake `SNOWFLAKE.ACCOUNT_USAGE.…` and Databricks `system.…` are refused like any other database. Schema-level catalogs inside the source's database (`information_schema.tables` on PostgreSQL, `INFORMATION_SCHEMA.TABLES` or `sys.tables` on SQL Server) stay allowed.
- **BigQuery `INFORMATION_SCHEMA`:** in `[project.](dataset|region-xx).INFORMATION_SCHEMA.view` the first part is the project; a single part before `INFORMATION_SCHEMA` is read as a project too, unless it is a `region-xx` qualifier, so `dataset.INFORMATION_SCHEMA.TABLES` has to be written `project.dataset.INFORMATION_SCHEMA.TABLES`.
- **Also refused:** names with more parts than the full form (SQL Server four-part linked-server names among them); Snowflake and Databricks `IDENTIFIER(...)` and Snowflake `TABLE('...')` (they name an object only when the statement runs); Snowflake `GET_DDL` and SQL Server `OBJECT_DEFINITION`, which name their object in a string (`OBJECT_ID` alone is allowed); a SQL Server full-text or semantic rowset function (`CONTAINSTABLE`, `FREETEXTTABLE`, `SEMANTICKEYPHRASETABLE`, `SEMANTICSIMILARITYTABLE`, `SEMANTICSIMILARITYDETAILSTABLE`) whose table, its first argument, is in another database or not written as a name; functions and procedures that reach other servers, files or storage (`dblink…`, `lo_…`, `xp_…`, `OPENROWSET`, `OPENQUERY`, `OPENDATASOURCE`, `OPENXML`, `EXTERNAL_QUERY`, `read_files`, `cloud_files`; on Databricks also `http_request`, `read_kafka`, `read_kinesis`, `read_pubsub`, `read_pulsar`, `reflect`, `try_reflect` and `java_method`), PostgreSQL `COPY … TO/FROM PROGRAM` and `COPY` to or from a server file, PostgreSQL extension functions that run SQL text (`pg_background_…`, pgRouting's `pgr_…`), and Databricks file and storage paths (`` json.`/mnt/…` ``, `` `s3://…` ``); and SQL Beacon cannot parse. All of these stay refused with read-only enforcement off. The in-memory join of a multi-step query reaches no database and is not checked; a SQLite data source is.
- **Database switches, role switches and dynamic SQL are refused**, with read-only enforcement on or off. SQL runs in its data source's own context, so `USE` in every form (SQL Server, Azure Synapse and MySQL `USE db`, Snowflake `USE [DATABASE|SCHEMA|WAREHOUSE] …`, Databricks `USE [CATALOG|SCHEMA|DATABASE] …`), role and user switches (`SET ROLE`; PostgreSQL `SET role`, `SET session_authorization` and `set_config`; SQL Server `EXECUTE AS` and `sp_setapprole`), Databricks settings that name a catalog (`SET spark.sql.defaultCatalog = …`) and SQLite `ATTACH` are refused. So is SQL run from a string, a variable or the session, whose names no check can read: SQL Server `EXEC` of a variable or a string, `sp_executesql`, `sp_sqlexec` and the other procedures that run SQL text; `EXECUTE IMMEDIATE`; `PREPARE` and `EXECUTE` of a prepared statement; Snowflake `EXECUTE TASK`; PostgreSQL `DO` blocks, a function created from a string body, and `query_to_xml`, `crosstab` and `ts_stat`, which run the query text they are given (`connectby` names its table in a string). Forms Beacon's parser cannot read — `EXEC('…')`, `DO`, Snowflake `USE ROLE` and `ALTER SESSION`, Databricks `SET CATALOG`, BigQuery `SET @@dataset_project_id`, MySQL `PREPARE … FROM` — are refused as SQL it cannot parse. A SQL Server `EXEC` of a stored procedure by name, `CALL`, and settings that change neither the database nor the role (`SET search_path`, `SET @x = …`, `SET statement_timeout`) stay allowed when read-only enforcement is off.
- **What a name rule cannot see:** a view, synonym, foreign table, linked table, share or stored procedure in the source's own database that reads another database. The data source login's grants remain the boundary: give each data source a login that can read only what its projects should (see [One database login per boundary](#data-sources-follow-their-projects)).
- **Where:** every statement Beacon runs on a data source goes through the same execution gate, so the check applies on the MCP tools (`ask`, `query`, `dry_run`, saved-query tools, the playground), when a query or a step is saved (also by an AI actor), on every preview (stored, draft and paged), saved-query, subscription and test-delivery run, AI actor check and data-migration read, on data contracts' Custom SQL rules (on save and on every evaluation) and generated rules, the AI semantic search, documentation enum discovery, value-grounding probes and the provider dry-run. A host data source already accepts only its exposed, schema-qualified tables. The check is not applied by `IDataSourceProvider.Execute*` called directly by a host, nor by the metadata extractors and column value samplers, whose SQL is fixed catalog SQL or built only from validated, quoted catalog identifiers.

### Guardrails of shared data sources

A data source several live projects link is held to the strictest guardrails among those projects, whichever project's caller queries it: read-only enforcement and PII detection are on when any of them turns them on; value grounding and sample-value collection are off when any of them turns them off; the custom PII patterns of all of them apply (to query results and to the grounding PII screens); and the row limit, statement timeout, maximum result size, maximum plan cost and value-grounding probe budget are the lowest any of them sets. A data source only one project links keeps that project's settings, and archived projects count for nothing. Beacon computes this itself from each project's effective settings (`IMcpSettingsProvider.GetEffectiveSettingsAsync(projectId)`), so a host's own settings provider takes part unchanged.

They apply to the MCP tools that run SQL — `query`, `dry_run`, `ask` (on every data source it routes to, cross-source included) and the saved-query tools — and so to the MCP playground; to grounding (`ask`, `get_query_context`: live value probes, their budget, the stored sample values shown in the context and the PII screen of probed values); and to sampling a data source's values when its metadata is refreshed, which no project asks for: sample values are collected while collection is on in the effective settings of every live project that links the source (each project's override, else the global value), the global value deciding alone only for a source no live project links. Stored sample values pass the PII screen again whenever they are shown — in grounding and, to an Admin, in the data source's metadata (`GET /beacon/api/data-sources/{id}/metadata`) — with the patterns that apply then, so values stored before a stricter project linked the source, or before a pattern was added, are withheld: a column whose name matches shows none, a matching value is dropped and the rest stay. As when values are sampled, the built-in patterns apply whatever the PII-detection setting, and no stored values are shown while sample-value collection is off. For an Admin, the project detail lists them per shared data source (`sharedSourceGuardrails`: each setting's effective value and the project that sets it), next to the projects that share the source.

### Who the caller is

| Caller | Project access |
|---|---|
| Admin (enabled user with the Admin role, or a super admin) | every live project, every action |
| Signed-in Beacon user | open projects at their global role; restricted projects through membership |
| Disabled or archived user | nothing |
| Session with no stored user while user management is on (a host principal, a bearer token not bound to a user) | nothing: with user management on, every caller that may use Beacon has a stored user |
| Session on a host without Beacon user management | open projects at the global role; the `Admin` role claim counts; a restricted project is Admin-only unless the host supplies a [membership source](#host-seams) |
| API key with a project list | its owner's access, within the list, capped by the lower of the key's scope and the owner's global role (`Read` keys read and run only; an owner who may neither read nor write gives the key nothing); never the Admin bypass |
| API key without a project list (created before lists were required) | its owner's access, over REST and on MCP, capped the same way |
| Mapped Entra system caller on MCP | its configured projects, restricted or not, for reading, running saved SQL and ad-hoc queries |
| Mapped Entra user caller on MCP | its configured projects within its Beacon user's access (a restricted project only as a member); without a Beacon user, its configured open projects |
| Browser session on `/beacon/mcp` | nothing: browsers use the MCP playground |
| Scheduled work done for its owner: a subscription's scheduled run, a data contract's scheduled evaluation, an AI actor's background think cycle | its owner's access as it is at that run (see [scheduled work](#scheduled-work-runs-as-its-owner)); a disabled or archived owner gets nothing |
| Scheduled work without an owner (created before owners were recorded) | the openly reachable data sources only; nothing with `RequireProjectMembership` |
| Scheduled work whose owner has no Beacon user, on a host without Beacon user management | the system, as before, without a re-check (a warning, at most once an hour per owner) |
| Other work with no HTTP request in a scope marked as system work (maintenance jobs, startup) | the system, unrestricted, keeping per-project data scoping (see [system work](#host-seams)) |
| Work with no HTTP request in an unmarked scope | nothing |

Keys and MCP callers never manage members. See [API Keys](/features/api-keys/#project-restrictions), the [MCP Server](/features/mcp-server/#overview) and [Entra ID callers on MCP](/features/mcp-entra-callers/#what-a-caller-reaches) for their rules.

The global role that caps a signed-in caller comes from the authorization provider: write permission makes it an Editor, read permission a Viewer. With Beacon user management on, the stored roles cap it even when `Beacon:Authorization:Enabled` is `false`; only with both off is every signed-in caller an Editor.

### How it is enforced

- **Every request declares its check.** A request that addresses a resource by id implements `IProjectAuthorizedRequest` and names the resource and the action; a MediatR pipeline behaviour resolves the resource's project (or data sources) and refuses before the handler runs — `404` when the resource is missing or unreadable, `403` when it is readable but the action is not allowed. A list request implements `IProjectFilteredRequest` and filters in SQL, so totals and pages never count what the caller cannot see. A request with nothing project-scoped is marked `[ProjectScopeExempt("reason")]`. A test fails when a request property ending in `Id` or `Ids` is neither checked nor exempt.
- **Requests sent in-process are checked too.** The behaviour runs on every `IMediator.Send` made while an HTTP request is served, so a host endpoint that sends Beacon requests is checked against that request's caller (`404`/`403`), not trusted.
- **Problem details.** Refusals throw `BeaconForbiddenException` (`403`) and `BeaconNotFoundException` (`404`). On `/beacon/api` these become RFC 7807 responses without their message (a caller cannot tell a resource it may not see from a missing one), and `BeaconConflictException` and `BeaconRateLimitedException` become `409` and `429` with theirs — in every host, without the sample's middleware. Only these Beacon exceptions are mapped; any other exception, including `UnauthorizedAccessException`, is left to the host's exception handling. Each mapping is logged with the exception type, the route name and the route's ids.
- **Security log events.** Refusals are logged as `AccessRefused` (7108, a `403`) and `AccessHidden` (7109, a `404` for a resource that is missing or unreadable), and scheduled work skipped because its owner may not run it as `ScheduledWorkSkipped` (7111, a warning), with the resource and its id, the reason (`not-readable`, `missing` or `not-granted`), the principal kind, the API key's id and the caller's Beacon user id (its external id when it has no stored user) — identifiers only; a missing resource logs at Information, the rest at Warning. Membership, access-mode, link and repository changes have their own events (7101–7107, 7110; see [Upgrading](#upgrading-from-45-project-access)), a project created open because its creator could not be its member `ProjectCreatedOpen` (7112, Information), a change to a user's roles `UserRolesChanged` (7113, Information: the user, the role ids added and removed, and who changed them), a user created `UserCreated` (7114, Information: the new user, whether it is an internal user, and who created it), a data source marked or unmarked as a migration write target `MigrationWritesChanged` (7115, Warning: the data source id, the value before and after, and who changed it), and a SQL Server read that ended the rolled-back transaction it ran in `ReadOnlyTransactionEnded` (7116, Warning: the data source id).
- **The UI** hides what a caller may not do: the **Members** tab (shown to project admins and Admins while Beacon manages membership), **Link data sources**, and the **Access** card (Admins), which says whether members are managed by the host or membership is unavailable. A resource the caller may not read shows the not-found state.
- **The MCP playground** checks the caller's project access for every tool call (Read for the catalog tools, Query for `ask`, `query`, `dry_run` and `feedback`) and refuses a call without a signed-in request.
- **MCP tool calls** over `/beacon/mcp` take the projects the caller may read from the evaluator on every call, so a revoked membership or a disabled user applies to the next call of the same session. Each built-in tool needs its action in the project it resolves, as in the playground: Read for the catalog tools, Query for `ask`, `query`, `dry_run` and `feedback` (a Viewer is refused them, audited). Saved-query tools are listed only where the caller may run saved SQL and re-checked right before their SQL runs; host endpoint tools need Read in the host's project. A call without an HTTP request, or without an authenticated caller, reaches nothing. Audit rows and signals record the caller's Beacon user id.
- **Real-time events** on the SignalR hub may still carry resource ids to a member whose access was just revoked; the resources themselves are refused.

### Members

A project admin, a UserAdmin or an Admin manages members on the project page's **Members** tab (a UserAdmin never their own membership): search people by display name (case-insensitive, at least two characters; up to 20 matches; rate-limited), add them with a role, change a role, remove a member. People are shown by display name, or as "User {id}" without one — never by user name, e-mail address or external id — and people without a display name are not found by the search. A disabled or archived user stays listed as inactive and grants nothing.

A project keeps an **effective project admin**: a member with the project admin role who is enabled, not archived, and holds a global role of Editor or above (a project admin capped at Viewer manages nothing). Removing or demoting the last one is refused (`400`) unless an Admin does it. The check and the change run in one serializable transaction, so two concurrent changes cannot both pass; a change that collides with another answers `409` (reload and retry). The routes:

| Route | Purpose |
|---|---|
| `GET /beacon/api/projects/{id}/members` | members, paged |
| `GET /beacon/api/projects/{id}/member-candidates?search=` | people to add |
| `PUT /beacon/api/projects/{id}/members/{userId}` | add a member or change their role (`{ "role": 1 \| 2 \| 3 }`: Viewer, Editor, ProjectAdmin) |
| `DELETE /beacon/api/projects/{id}/members/{userId}` | remove a member |
| `PUT /beacon/api/projects/{id}/access` | restrict (Admin, UserAdmin) or open (Admin) |
| `POST /beacon/api/projects/{id}/data-sources` | link data sources (`{ "dataSourceIds": [...] }`) |
| `DELETE /beacon/api/projects/{id}/data-sources/{dataSourceId}` | unlink a data source (Admin) |

When the host manages membership, the membership routes answer `409` ("Project membership is managed by the host."); when membership is unavailable, they answer `409` ("Project membership is not available in this deployment."). The Members tab is hidden in both cases. Restricting a project answers `409` only while membership is unavailable: with a host membership source, an Admin may restrict a project and the host's memberships decide who reaches it. Linking data sources needs the project-admin role however membership is managed.

Project lists carry `requiresMembership`, `myRole` and `memberCount`; `GET /beacon/api/projects?forApiKey=true` lists only the projects a new API key of the caller may name (those it reads without the Admin bypass). Project details also carry `canManageMembers`, `openlyReachableDataSourceIds`, `membershipManagement` (`1` Beacon, `2` the host, `3` unavailable) and, for an Admin, `sharedSourceProjects` and `sharedSourceGuardrails` (see [Guardrails of shared data sources](#guardrails-of-shared-data-sources)).

### Unlinking data sources

An Admin removes a data source's link to a project with `DELETE /beacon/api/projects/{id}/data-sources/{dataSourceId}`, or from the **Access** card of a project that shares the source: the card lists every other live project, open or restricted, that links any of the project's data sources, with the shared sources and that project's active admins, and unlinks a shared source from one of them after confirming.

- Members of the project lose the data source through it at once; other projects that link it keep it.
- When the removed link was the data source's last, the source becomes Admin-only until it is linked again: a data source never becomes unlinked and open.
- A host's data source cannot be unlinked (`409`; the host links it). A link that does not exist answers `404`, and a concurrent change to the source's links `409` (retry).
- Each unlink is logged as `DataSourceUnlinked` (7110), with whether it left the source unlinked.

### Scheduled work runs as its owner

A subscription's scheduled run, a data contract's scheduled evaluation and an AI actor's background think cycle run as the owner they recorded — the subscription's or contract's owner, the actor's creator — and are re-authorized at **every** run, right before any SQL or LLM call, against the owner as they are then:

| Work | The owner needs | When refused |
|---|---|---|
| Subscription run | Run in one project that holds every data source of the query's steps | the run is recorded with status **Skipped** (`8`) and "Skipped: owner not authorized."; no SQL, no delivery, no task, no AI actor trigger; the Control Tower and the subscription's detail flag it (`ownerNotAuthorized`) |
| Data contract evaluation | Run in a project that links the contract's data source | nothing is evaluated, scored or alerted; a warning is logged, and the contract records when its evaluations started being skipped: the contract list and detail, the data-quality overview (per data source) and the Control Tower statistics flag it (`ownerNotAuthorized`, `ownerNotAuthorizedContracts`) until an evaluation runs again |
| AI actor background cycle | Query in a project that links the actor's data source | the cycle is recorded with phase **Skipped** (`7`); the actor keeps its status |

A refused run is not a failure: the job does not fail and nothing alerts as an error, but the run is skipped until the owner may run it again or an Admin assigns another owner. Work **without an owner** runs only while every data source it reads is openly reachable, and is always skipped with `RequireProjectMembership`. On a host without Beacon user management, an owner with no Beacon user runs as the system: there is no user to re-check, so the re-check at every run does not apply there, and a warning names the owner (at most once an hour per owner). Work a request asks for — **Execute Now** on a subscription, **Evaluate now** on a contract, **Think** on an actor — runs as the request's caller instead and answers `403` when the caller may not.

An Admin changes the owner with `PUT /beacon/api/subscriptions/{id}/owner`, `PUT /beacon/api/data-quality/contracts/{id}/owner` or `PUT /beacon/api/ai-actors/{id}/owner` (`{ "userId": 12 }`). The new owner must be an existing, enabled user who may do the work — Run for a subscription or contract, Query for an AI actor — or the change answers `409` with a message saying so.

### Host seams

All defaults are registered with `TryAdd`: register your own implementation before `AddBeaconServices`, or replace the registration.

- **`IProjectMembershipSource`** — where memberships come from, for example IdP groups. `IsAvailable` is read once at startup (the default reports whether Beacon user management is on); `GetMembershipsAsync` returns the caller's role per project id. A `null` result or an exception denies every restricted project for that request, never opens one, and a membership whose role is not a defined `ProjectRole` value is dropped (a warning logs how many). With a custom source, Beacon's membership routes answer `409` and the UI hides the Members tab.
- **`IRunAsCeilingSource`** — the global role that caps an actor who is not the request's signed-in caller (the default reads the user's stored roles).
- **`ISystemWorkScope`** — marks a DI scope as work Beacon does for itself (a background job, a startup task). A host that runs Beacon's jobs on its own job host **must** call `MarkAsSystemWork()` on each job's scope before the job runs; the sample's Warp host and Beacon's own startup scopes do this. Work without an HTTP request in an unmarked scope gets no project access (fail closed): every project check refuses it, and a warning is logged (at most once a minute). Marking has no effect on a request's own caller, nor on scheduled work, which runs as its owner.
- **`IRunAsScope`** — runs a DI scope as the owner of scheduled work. Beacon's job entry points (`IJobService.ExecuteQuery`, `IJobService.EvaluateDataContract`, the AI actor's background cycle) set it themselves when there is no request; it is set once, never inside a request, and before anything in the scope resolves its caller (each of these throws `InvalidOperationException`). Hosts do not set it.
- **`IExecutionAccessCheck`** — the execute-time check: whether the scope's caller (the request's caller, or the owner the scope runs as) may still take an action on a set of data sources, right before work runs on them; refusals are logged.
- **`IProjectAccessEvaluator`** — the whole decision, returning a `ProjectAccessScope` (`IsUnrestricted`, `For(action)`, `RoleIn(projectId)`, `Allows(projectId, action)`).

`IBeaconAuthorizationProvider` stays the global ceiling: `HasReadPermissionAsync` and `HasWritePermissionAsync` are still called. Its resource-level members and `AuthorizationOptions.EnableResourceLevelAuthorization` are `[Obsolete]`, never called, and removed in 5.0; `ResourceType` and `PermissionAction` remain only for them.

## Advanced Scenarios

### The caller as a resource owner

Alert tasks (assignee, resolver), data contracts (owner) and AI actors (creator) store the caller's `Users.ExternalId`. Handlers get the caller from `IBeaconActorAccessor` (scoped, registered with `TryAddScoped`, so a host can replace it), which returns a `BeaconActor`: its `UserId` and whether it `IsAdmin` (see [Admin role claim](#admin-role-claim)). An API key is its owner, found by the key's owner id; any other session is the user its `NameIdentifier` names. A session with no stored user (a host without Beacon user management) keeps the `NameIdentifier` it presents; a disabled or archived user, an API key whose owner is not found, and an anonymous request have no `UserId` and own nothing. These rules sit beside the authorization provider: a custom `IBeaconAuthorizationProvider` decides read and write permission, not who owns a task, contract or actor.

### Permission Caching

Cache permissions for better performance:

```csharp
public class CachedAuthorizationProvider : IBeaconAuthorizationProvider
{
    private readonly IBeaconUserContext _userContext;
    private readonly IMemoryCache _cache;
    private readonly IActualAuthProvider _actualProvider;

    public async Task<bool> HasWritePermissionAsync(
        CancellationToken cancellationToken = default)
    {
        var cacheKey = $"write_perm_{_userContext.UserId}";

        return await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            return await _actualProvider.HasWritePermissionAsync(cancellationToken);
        });
    }
}
```

## MCP safety locks

Read-only enforcement and PII detection are MCP settings, globally and per project, but by default the deployment pins
both on: `Beacon:Mcp:ForceReadOnly` and `Beacon:Mcp:ForcePiiDetection` are `true` unless the host's configuration sets
them to `false`. While a lock is on, no one can turn the setting off or override it for a project — an Admin included
(`409`, `/errors/setting-locked`); the global settings page shows the switch disabled with a note, and
`GET /beacon/api/mcp/settings/locks` lists the pinned settings. With `ForceReadOnly` on, the SQL execution gate judges
every statement read-only whatever the settings it is handed, including the `ask`, `query` and `dry_run` tools and the
in-memory join of a cross-source query. Only after the host turns a lock off do the role rules above apply (an Admin
turns it off; a SecurityAdmin only on), and the host logs a warning at startup for each lock that is off.

`Beacon:Mcp:ForbidRowDataToLlm` (off by default) is a lock of the same kind: it pins sample-value collection, value
grounding and the eval judge off, so no value read from a data source reaches the LLM provider (see
[What reaches the LLM provider](/features/mcp-server/#what-reaches-the-llm-provider)).

A stored value a lock overrides (read-only or PII detection switched off globally or for a project before the lock,
content retention on under `ForceNoContentRetention`, or sample values, value grounding or the eval judge on under
`ForbidRowDataToLlm`) is kept but has no effect. After startup the host logs one warning
that lists those rows by id — the global settings row and the project ids — and the locks concerned; and a statement
refused only because the lock turned read-only enforcement on over such a stored value says so, naming
`Beacon:Mcp:ForceReadOnly`.

Every change to the MCP settings is recorded in the settings history shown on the Admin Settings page (for an Auditor or
a SecurityAdmin): one row per changed setting — `Mcp.{Setting}` globally, `Mcp.Project.{projectId}.{Setting}` for a
project — with the value before and after, the time and the caller's Beacon user id. Changes are found on the full
values; a long text value (a prompt, a tool description) is stored cut to 2,000 characters, so an edit past that length
is recorded even when the stored copies read the same. See
[Deployment locks and ceilings](/features/mcp-server/#deployment-locks-and-ceilings).

## Audit Trail (Future)

Optional audit fields are available on entities for future audit trail support:

```csharp
public abstract class AuditableBaseEntity : BaseEntity
{
    public string? CreatedByUserId { get; set; }
    public string? CreatedByUserName { get; set; }
    public DateTime? ModifiedTime { get; set; }
    public string? ModifiedByUserId { get; set; }
    public string? ModifiedByUserName { get; set; }
}
```

**Note:** These fields are currently nullable and not automatically populated. A future release will include an EF Core interceptor to populate these fields automatically when authorization is enabled.

## Configuration Options

### Authorization Options

```csharp
public class AuthorizationOptions
{
    /// <summary>
    /// Enable authorization checks. Default: false
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Authorization provider type. If null, uses DefaultAuthorizationProvider.
    /// </summary>
    public Type? ProviderType { get; set; }

    /// <summary>
    /// Never read; removed in 5.0. Project and resource access is decided by IProjectAccessEvaluator.
    /// </summary>
    [Obsolete]
    public bool EnableResourceLevelAuthorization { get; set; } = false;
}
```

Project access has its own options, bound from `Beacon:Authorization`:

```json
{
  "Beacon": {
    "Authorization": {
      "RequireProjectMembership": false
    }
  }
}
```

`RequireProjectMembership: true` restricts every project and makes data sources no project links Admin-only (see [Project access](#open-and-restricted-projects)). It is validated at startup: without Beacon user management and without a host `IProjectMembershipSource`, the host does not start.

The same section holds the [functional permissions](#functional-permissions) settings (`Roles`, `IdpRoles`, `SeparationOfDuties`), bound to `RoleMappingOptions`; see [Configuration](/getting-started/configuration/#functional-permissions-role-names-identity-provider-app-roles-separation-of-duties).

### Example Configuration

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options =>
{
    options.AddBeaconScheduler<BeaconScheduler>();
    options.BaseUrl = "https://localhost:7187";

    // Authorization configuration
    options.Authorization.Enabled = true;
    options.AddAuthorizationProvider<MyAuthorizationProvider>();
})
.UsePostgreSql(connectionString, "beacon");
```

## Troubleshooting

### Authorization Not Working

**Problem:** Users can access resources they shouldn't.

**Solution:**

1. Verify authorization is enabled:
   ```csharp
   options.Authorization.Enabled = true;
   ```

2. Verify `UseAuthorization()` is called:
   ```csharp
   app.UseAuthentication();
   app.UseAuthorization(); // ← This must be present
   app.UseBeaconUI();      // serves the React SPA at "/"
   ```

3. Check claims are added correctly:
   ```csharp
   // Add logging to your claims transformer
   _logger.LogInformation("User {User} assigned role {Role}",
       principal.Identity.Name, role);
   ```

### Username Not Displayed

**Problem:** The React UI shows "Guest" or nothing.

**Solution:**

1. Verify `IBeaconUserContext` is registered (automatic with `AddBeaconUI()`)
2. Check that the claims transformer adds `BeaconClaims.UserName`
3. Verify the user is authenticated

### 403 Forbidden on All Requests

**Problem:** All requests return 403 Forbidden.

**Solution:**

1. Check the authorization provider is returning `true` for authenticated users
2. Add logging to the authorization provider:
   ```csharp
   _logger.LogWarning("Authorization denied for {User}: {Reason}",
       _userContext.UserName, result.FailureReason);
   ```
3. Verify the claims transformer is executed

### Provider Not Called

**Problem:** Authorization provider methods never execute.

**Solution:**

1. Verify the provider is registered:
   ```csharp
   options.AddAuthorizationProvider<MyAuthorizationProvider>();
   ```
2. Check authorization is enabled
3. Ensure `UseAuthorization()` middleware is added

## Security Best Practices

1. **Always validate on the server** - Client-side checks are for UX only
2. **Use HTTPS in production** - Protect credentials and session cookies
3. **Store role assignments in the database** - Don't hardcode in the claims transformer
4. **Implement rate limiting** - Prevent brute force attacks
5. **Audit authorization failures** - Log all 403 responses with context
6. **Use strong session management** - Keep `Beacon:Authentication:Session:RevalidateMinutes` short and `AbsoluteLifetimeHours` to a working day; use **Sign out everywhere** when an account may be compromised; protect the Data Protection key ring with a key only that environment holds, and give a restored copy of the Beacon database new security stamps and its own key ring ([Configuration → Data Protection](/getting-started/configuration/#data-protection-the-keys-behind-the-session-cookie))
7. **Sign in through the identity provider** - With single sign-on, turn local password sign-in off (keep a break-glass account) and consider `RequireMfa` ([User Management → Local password sign-in](/features/user-management/#local-password-sign-in))
8. **Validate resource ownership** - Don't rely on resource IDs alone
9. **Restrict sensitive projects** - New projects are restricted; for existing ones, add members (a project admin first), then restrict the project. Give each data source a database login that sees only what its projects should see, and configure its database (catalog, project) in the connection
10. **Principle of least privilege** - Default to the most restrictive permissions

## Upgrading from 4.5: project access

This version makes projects the authorization boundary on the REST API, the UI, the MCP playground, MCP tool calls over `/beacon/mcp` and scheduled work, which runs as its owner (see [Project access](#project-access)).

**Schema.** The `AddProjectAuthorization` migration (PostgreSQL `20261011120000`, SQL Server `20261011120001`) adds the `project_members` table and seven columns: `projects.requires_membership` (default `false`, so every existing project stays open), `data_sources.open_when_unlinked` (default `true` for existing rows; new data sources are stored with `false`), `subscriptions.owner_user_id`, `queries.owner_user_id`, `query_versions.data_source_ids`, `project_data_sources.linked_by_user_id` and `query_step_change_history.data_source_id` (all null for existing rows). Beacon does not migrate its schema at startup: apply the migration as you apply the others. Rolling it back drops the table and the seven columns, and with them memberships, subscription and query owners, who linked each data source, and the data sources recorded on query versions and step history. The `AddContractOwnerNotAuthorized` migration (PostgreSQL `20261011130000`, SQL Server `20261011130001`) adds `data_contracts.owner_not_authorized_since` (null for existing rows): when a data contract's scheduled evaluations started being skipped because its owner may no longer run them. Rolling it back drops the column.

**What changes without any configuration:**

- Existing projects and existing data sources stay open, so interactive users keep reaching what they reach today — except where the next points say otherwise.
- **Stored runs made before runs recorded their data sources** are shown by their subscription's current query: to interactive users and to API keys without a project list while that query is readable, also with `RequireProjectMembership` on. Callers confined to a project list do not see them, and a run whose subscription or query is archived or missing, or whose query reads no data source, is Admin-only.
- **Queries without steps** are reachable only by their creator and Admins, once any project is restricted or archived or any data source is not openly reachable. Queries created before this version have no recorded owner, so such a query without steps is then Admin-only; add a step (as an Admin) to give it a project.
- **API keys** with a project list are confined to those projects on every REST route, not only on MCP. A key never exceeds its owner: it is capped by the lower of its scope and its owner's global role, and a key whose owner may neither read nor write reaches nothing. New keys must name at least one project (`400` otherwise; `404` for a project the creator cannot read without the Admin bypass). A key created before lists were required acts with its owner's access, over REST and on MCP.
- **MCP tool calls use the same project access**, decided on every call. **API keys without a project list now reach their owner's projects on MCP** — before, they reached no project there — which widens what such keys can do over MCP; re-issue them with a project list. **Mapped Entra user callers need membership on restricted projects** they are configured for, reach nothing while their user is disabled or archived, and without a Beacon user reach only their configured open projects; system callers keep their configured projects. Saved-query tools are listed only where the caller may run saved SQL, and each run is re-checked before its SQL; host endpoint tools need Read in the host's project. A call without an authenticated caller — also over a transport without an HTTP request, such as stdio — is refused and reaches no project. MCP error messages no longer mention API keys. Audit rows and signals record the caller's Beacon user id, so a signed-in session can rate its own signals. An eval run without a project is refused. See the [MCP Server](/features/mcp-server/#upgrading-from-45-project-access) and [Entra ID callers](/features/mcp-entra-callers/#upgrading-from-45-project-access).
- **Sessions without a stored user** reach nothing while Beacon user management is on (a host principal, a bearer token not bound to a user); the `Admin` role claim counts for them only on hosts without user management. `Beacon:Authorization:Enabled: false` with user management on still caps every signed-in user by their stored roles.
- **New data sources** are Admin-only until a project links them; link them on create.
- **Linking**: anyone but an Admin links only data sources already in a live open project; a host's data source links only into the project the host declares for it (same host key), for Admins too. An Admin can now unlink a data source (`DELETE /beacon/api/projects/{id}/data-sources/{dataSourceId}`); removing a source's last link makes it Admin-only.
- **Host data sources moved by the host sync**: when the project a host declares for a data source changes, the source moves to it. If a live restricted project held the source, the host project becomes restricted and takes that project's members; when that is not possible — the host manages membership, several live restricted projects held it, or the host project has members the restricted one does not — the source stays where it is and an error is logged.
- Writes to a shared data source's relationships, descriptions and metadata refresh need Write in every project that links it, or an Admin. Relationship routes act only on the route's data source: a relationship id of another data source answers `404`.
- A resource the caller may not read answers `404` like a missing one, and a refused action `403`; Beacon maps these itself instead of answering `500` in hosts without the sample middleware. Refusals now throw `BeaconForbiddenException`, not `UnauthorizedAccessException`, and the API maps only Beacon's exceptions: a host that mapped `UnauthorizedAccessException` keeps doing so for its own code.
- **Beacon requests a host sends in-process** with `IMediator.Send` during an HTTP request are authorized against that request's caller and may now answer `404` or `403`.
- **Project membership** routes: a project keeps an effective project admin (enabled, not archived, global role Editor or above); concurrent membership changes answer `409`. Member candidates match display names only, and people without a display name are shown as "User {id}".
- **Query versions** are read by the data sources each version recorded, in the version list as on a version's own page: after a query moves to other data sources, a caller who cannot read the sources an earlier version recorded does not see that version, and the query's MCP tool card does not name an approved version the caller cannot read.
- **Feedback verdicts** on a query signal (the `feedback` tool and `POST /beacon/api/eval/feedback`) are recorded only by the signal's own caller, a project admin of its project, or an Admin (`403` otherwise).
- **Counts**: a data source's query count includes only the queries the caller may read; migration job counts on data sources and the Home migration summary are an Admin's (zero for anyone else); learning statistics leave out signals without a project for anyone but an Admin.
- Subscriptions record their owner (the signed-in creator; an AI actor's creator for the subscriptions it creates), shown on the subscription page by display name. Data contracts, AI actors and tasks name their owner the same way: their REST shapes return `hasOwner` and `ownerDisplayName` instead of the owner's external id (and, for tasks, user name). While Beacon user management is on, a caller without a Beacon user cannot create subscriptions, data contracts or AI actors (`403`).
- **Background work**: a host that runs Beacon's jobs on its own job host **must** mark each job's DI scope as system work (`ISystemWorkScope.MarkAsSystemWork()`) before the job runs. Work without a request in an unmarked scope now gets **no project access**: every project check refuses it (a missing resource), and a warning is logged at most once a minute. The sample's Warp host marks every job through a pipeline behaviour, and Beacon marks its own startup work (host sync, the first-run setup announcement).
- **Scheduled work stops when its owner may no longer do it.** Subscription runs, data contract evaluations and AI actors' background cycles run as their owner and are re-checked at every run (see [Scheduled work runs as its owner](#scheduled-work-runs-as-its-owner)): when the owner is disabled or archived, loses a restricted project's membership or loses the role, the work is skipped — a run recorded as `Skipped` (`NotificationStatus` `8`), an evaluation not made (event 7111), a cycle recorded as `Skipped` (`AiActorExecutionPhase` `7`) — instead of running as the system. **Ownerless** subscriptions, contracts and actors (created before owners were recorded) run only on openly reachable data, and never with `RequireProjectMembership`: assign them owners (`PUT …/owner`, Admin) before restricting projects. A new owner must be able to do the work (`409` otherwise), also on the existing contract and AI actor owner routes. Execute Now, Evaluate now and Think run as the caller and answer `403` when the caller may not.
- **Hosts without Beacon user management:** an owner with no Beacon user (a host principal) cannot be re-checked, so for such owners the re-check at every run is a no-op and the work keeps running as the system, as before; a warning names the owner, at most once an hour per owner. To have scheduled work re-checked, run Beacon user management so owners are stored users.
- **MCP project access that cannot be determined** (the database or a host membership source fails while Beacon reads the caller's access) no longer looks like "no projects": a tool call answers "Project access could not be determined; retry the call." (audited), and `tools/list` fails with that message instead of returning a shorter list.
- **MCP SQL tools need Query.** `ask`, `query`, `dry_run` and `feedback` over `/beacon/mcp` need an Editor role in the project, as in the playground; a Viewer reads the catalog only. Mapped Entra users provisioned with the default `Viewer` role are affected (see [Entra ID callers](/features/mcp-entra-callers/#upgrading-from-45-project-access)).
- **Host membership sources**: memberships whose role is not a defined `ProjectRole` value are dropped (a warning logs how many).
- `PUT /beacon/api/projects/{id}/access` answers `409` only when restricting while project membership is unavailable; with a host membership source, an Admin may restrict a project.
- Archived projects grant nothing (no route archives projects today).
- **New projects are restricted.** A project created by an active Beacon user, while Beacon manages project membership, is restricted with its creator as project admin — Admins included; only its members and Admins reach it until an Admin opens it. Other creators get an open project (logged as `ProjectCreatedOpen`, 7112). Projects that already exist keep their mode. Integrations that create a project and expect everyone to reach it must open it (`PUT /beacon/api/projects/{id}/access`, Admin) or add members.
- **Every query execution goes through the MCP execution gate.** Previews, subscription runs and test deliveries, AI actor checks and data-migration reads now pass the regex guardrail as well as the read-only parser (SQL that does not start with `SELECT`, `WITH` or `EXPLAIN`, or that holds a write keyword, is refused; comments are prose — a leading `-- note` or a `/* … */` remark is removed before this check, as the source's dialect reads it, while MySQL executable comments and optimizer hints stay and are judged), run through the provider's read-only path (a `READ ONLY` transaction on PostgreSQL and MySQL, a transaction that is always rolled back on SQL Server), return at most `Beacon:Query:MaxExecutionRows` rows per statement (default 100,000; the cap is written into the SQL, on PostgreSQL, MySQL, SQL Server, Azure Synapse and Snowflake the read also stops one row past it, and a larger result fails the run instead of being cut short, with a message that names the setting) and stop after `Beacon:Query:StatementTimeoutSeconds` (new, default 120, 1–3600, validated at startup) or a subscription's shorter timeout. Subscription results are PII-masked before they reach recipients and stored results, following the linked projects' PII settings, and so are a Custom SQL rule's `actual_value` and `message`. Saving a query, a step or an AI actor's query runs the full gate (SQL the parser cannot read is no longer saved) and, where Beacon holds the source's metadata, refuses a column a known table does not have. T-SQL `TABLOCK`, `HOLDLOCK` and `SERIALIZABLE` hints and more side-effecting functions (`gin_clean_pending_list`, `brin_summarize_…`, MySQL keyring, audit-log and locking-service functions) are refused. Metadata refreshes leave out tables and columns whose names carry quote, bracket, semicolon or control characters, and PostgreSQL's per-session temporary schemas. See [How a query runs](/features/queries/#how-a-query-runs).
- **Check stored queries and subscriptions after upgrading.** The gate changes what some stored SQL returns, or whether it runs:
  - **Line comments no longer swallow clauses.** A stored statement used to be flattened onto one line with its `--` comments in place, so a comment ended the statement there and dropped what followed it (a `WHERE` on the next line, for example). Comments are now removed first and the whole statement runs: such a subscription returns fewer or different rows, and its *notify on change*, minimum-row rule or anomaly detection may fire once on the new count.
  - **Delivered rows are PII-masked.** Subscription results reaching recipients, attachments and stored results, and a Custom SQL rule's `actual_value` and `message`, are masked under the linked projects' strictest PII settings.
  - **A statement's own bound above the cap is lowered to it.** An outer `LIMIT`, `TOP` or `FETCH` above `Beacon:Query:MaxExecutionRows` (or the MCP row limit on the MCP tools) is lowered to the cap, so the statement fetches at most the cap; `LIMIT ALL`/`NULL`, an expression, `TOP … PERCENT` and `WITH TIES` are wrapped in a capped derived table, and a SQL Server statement with a `WITH` clause whose bound cannot be lowered is refused (`uncappable_bound`). A SQL Server set operation (`UNION`, `EXCEPT`, `INTERSECT`) without an `ORDER BY` is capped as a derived table (`SELECT TOP n * FROM (…) AS beacon_rows`).
  - **Timeouts.** Every statement stops at `Beacon:Query:StatementTimeoutSeconds` (default 120). Saving a subscription whose timeout is above it is refused; a stored subscription with a longer timeout runs with the deployment's limit.
  - **SQL the gate now refuses** fails at its next run with the gate's reason (and is refused on save): control characters other than tab, line feed and carriage return, and Unicode noncharacters (U+FFFE, U+FFFF and the like); on PostgreSQL and SQL Server, comment markers that share a character (`/*/`, `*/*`); a top-level `END` that closes no `CASE`; on SQL Server and Azure Synapse, a reserved keyword used as an unquoted column alias (`SELECT 1 COMMIT`); write keywords, multiple statements and names in other databases, as before on MCP. Known cases that need a change: a string literal containing `/*/` or `*/*` on PostgreSQL or SQL Server (build it with concatenation, `'/' || '*/'`); an unquoted MySQL column named `end` (quote it, `` `end` ``); and, where a statement is wrapped in a derived table on MySQL, SQL Server or Synapse, result columns without a name or with the same name twice (alias them).
  - **Every engine needs a read-only login.** Database-level read-only is defence in depth: PostgreSQL sends exactly one statement per read, and SQL Server checks after each read that its rolled-back transaction is still open (a savepoint and `@@TRANCOUNT`, two extra round trips per statement).
  - **Visible refusals.** A migration run the migration service refuses because its destination is not a migration write target, or because it reads one (an in-process caller of `IMigrationService`, or a flag changed after the request was checked), is recorded in the job's history as failed with the reason; the run route refuses such a run with `400` before anything is recorded, and refusals while migrations are off, and runs of a missing or disabled job, are answered to the caller only. A metadata refresh returns `skippedNameCount` and logs the table and column names it left out; a left-out table loses its stored and curated descriptions (the applied documentation patch stays in the patch history). Sample-value collection and documentation enum discovery log each table they skip with the reason and a per-refresh count. Stored MCP settings that a deployment lock overrides are listed by id in one startup warning, and a refusal that the read-only lock causes names `Beacon:Mcp:ForceReadOnly`.
- **MCP read-only enforcement and PII detection are locked on by default** (`Beacon:Mcp:ForceReadOnly`, `Beacon:Mcp:ForcePiiDetection`). A deployment that had either switched off, globally or for a project, runs with it on after the upgrade, and a request to switch it off answers `409`, an Admin's included; the stored value is kept and applies again only if the host sets the lock to `false`. Every MCP settings change is now recorded in the settings history. See [MCP safety locks](#mcp-safety-locks).
- **Data sources without database-level read-only enforcement are marked.** SQL Server, Azure Synapse, Snowflake, BigQuery and Databricks sources carry a "Read-only login required" badge, and the host logs their ids at startup and when one is saved: connect them with a login that can only read. PostgreSQL and MySQL sources read "Enforced by the engine — still use a read-only login": the engine's read-only session is defence in depth, and every data source Beacon reads needs a login that can only read. On PostgreSQL the statement is sent as a batch of exactly one command, so a second statement in it is refused by the driver and the server; on SQL Server a statement that ends the rolled-back transaction it runs in fails the run, its result is discarded, and the security log records `ReadOnlyTransactionEnded` (7116, the data source id). See [Read-only guarantees per engine](/features/data-sources/#read-only-guarantees-per-engine).
- **Data migration is off unless the host turns it on, and writes only to migration write targets.** With `Beacon:Migrations:Enabled` unset or `false` (the default) the `/beacon/api/migrations/*` endpoints answer `404`, the shell hides the feature, and no job is created or run by anyone. A host that uses migrations sets it to `true`, and an Admin marks each destination as a **migration write target** (`PUT /beacon/api/data-sources/{id}/migration-writes`, audited in the settings history and the security log); Beacon never reads a write target, so only its login holds write grants, and a job may not read one. A migration's read has its own row budget, `Beacon:Migrations:MaxRows` (default 100,000), in place of `Beacon:Query:MaxExecutionRows`. The `AddDataSourceMigrationWrites` migration (PostgreSQL `20261011140000`, SQL Server `20261011140001`) adds `data_sources.allow_migration_writes` (`false` for existing rows); rolling it back drops the column. See [Turning data migration on](/features/data-migration/#turning-data-migration-on).
- **Database names in SQL must be the data source's own.** SQL that names another database, catalog or project than the one its data source's connection is configured for is refused — on MCP, when a query, step or Custom SQL rule is saved, and when saved queries, subscriptions and data contracts run. A data source without a configured database accepts no database qualifier: queries that relied on the login's default database with `database.schema.table` names must drop the qualifier or configure the database. Unparseable SQL is now refused by the MCP gate even with read-only enforcement off. See [Database names in SQL](#database-names-in-sql).
- **Shared data sources take the strictest guardrails** of the live projects that link them, on the MCP SQL tools and in grounding: a project with loose settings that shares a source with a stricter project now gets the stricter row limit, PII detection, read-only enforcement, value grounding and sample-value settings on that source. See [Guardrails of shared data sources](#guardrails-of-shared-data-sources).
- **`get_query_context` probes live values only for callers who may query.** The tool needs Read; a caller with Read but not Query in the project (a Viewer) gets value grounding from the stored sample values only, with no probe against the live data.
- **Database names on MySQL are compared exactly**, and a BigQuery name before `INFORMATION_SCHEMA` is a project unless it is a `region-xx` qualifier; boundary functions, Databricks paths and SQL Server four-part names are refused with read-only enforcement off too. See [Database names in SQL](#database-names-in-sql).
- **Grounding goes through the caller's project.** Every AI grounding call that names a data source reads it through the caller's authorized project only: a data source the project does not link is not searched, described or catalogued (its schema catalog is empty, so SQL against it fails the schema check), value grounding probes nothing in it, and a table's documentation in an LLM context comes from that project only. Repository scans name their project, and a dbt manifest is imported only into a project that links its data source. See [Project isolation](/features/knowledge-base/#project-isolation).
- **Forced metadata refresh**: at most once per data source per `Beacon:Metadata:ForcedRefreshCooldownMinutes` (new, default `15`, `0` off, range 0–1440, validated at startup) — `429` inside the cooldown or while a refresh of the source runs. The cooldown counts from the last successful refresh, belongs to the data source across every project linking it, starts over when the data source is changed, and is kept per Beacon process (each instance of a multi-instance host keeps its own). A refresh keeps the table and column descriptions set by applying documentation patches instead of wiping them. Archiving a data source, or changing its connection, evicts its cached metadata and relationship graph; an archived data source's metadata is not served (`404`). See [Data Sources](/features/data-sources/#refreshing-metadata).
- **Dashboards**: sharing a dashboard (`isShared`) or making it the default (`isDefault`) needs the dashboard's Admin level (its owner or an Admin grantee); an Edit grantee who changes either gets `403` and may still change the content.
- **Data contracts stay on their data source**: the contract's table and every table and column its rules name must be in the data source's stored metadata (`400` on save otherwise); a name that differs from the metadata only in case is stored, and run, as the metadata spells it, and one that matches several names differing only in case is refused. An evaluation records a stored rule that names anything else as failed without running it. Refresh a data source's metadata before adding contracts on new tables. See [Data Quality](/features/data-quality/#rule-types).
- **Eval runs** without a project answer `400` when started (before, a run was created and then failed).
- **The project brief** (`get_context` with `format: agents_md`) lists saved-query tools only where the caller may run stored SQL, as the MCP tool list does.
- **Scheduled contract evaluations that are skipped are visible**: data contracts return `ownerNotAuthorized`, the data-quality overview `ownerNotAuthorizedContracts` per data source and the Control Tower statistics `ownerNotAuthorizedContracts`; the Home activity feed lists skipped subscription runs (warn tone).
- **People are named by label on more shapes**: query versions and dashboards (`createdByUserName`, in lists and details) and tasks (`resolvedByUserName`, `assigneeUserName`) name people by display name or "User {id}", never by user name; a person without a Beacon user is not named (`null`). Query versions and dashboard details no longer return `createdByUserId`, and task details return `hasAssignee` instead of `assigneeUserId`.

**Admin claim.** For project access, a caller stored by Beacon user management is an Admin by its current roles; the session's `Admin` claim counts only for a caller without a stored user on a host without user management (see [Admin role claim](#admin-role-claim)). The `BeaconApiAdmin` policy on Admin-only routes still reads the session's claims.

**Public API.**

- New in `Beacon.Core.Authorization`: `IProjectAccessEvaluator`, `ProjectAccessScope`, `ProjectSet`, `ProjectRole`, `ProjectAction`, `ProjectMembershipManagement`, `IProjectMembershipSource`, `IRunAsCeilingSource`, `ISystemWorkScope`, `IProjectAuthorizedRequest`, `IProjectFilteredRequest`, `ProjectCheck`, `ProjectResource`, `ProjectResourceKind`, `ProjectScopeExemptAttribute`, `ScheduledWorkOwner`, `AccessRefusal`; `BeaconActor.LogId`, `IRunAsScope`, `IExecutionAccessCheck`, `AccessRefusal.Skipped`. In `Beacon.Core.Authorization.ProjectAccess`, `ProjectAccessQueryableExtensions` and `ProjectAccessEvents`: security log event ids 7101 `MemberAdded`, 7102 `MemberRoleChanged`, 7103 `MemberRemoved`, 7104 `AccessModeChanged`, 7105 `DataSourcesLinked`, 7106 `RepositoryScanRequested`, 7107 `RepositoryTokenChanged`, 7108 `AccessRefused`, 7109 `AccessHidden`, 7110 `DataSourceUnlinked`, 7111 `ScheduledWorkSkipped` (identifiers only). In `Beacon.Core.Configuration`, `ProjectAuthorizationOptions`; in `Beacon.Core.Models`, `BeaconForbiddenException`, `BeaconNotFoundException`, `BeaconConflictException` and `BeaconRateLimitedException`. All defaults are registered with `TryAdd`.
- `IBeaconActorAccessor.ResolveOwnerAsync` is a new member; hosts are not expected to implement the accessor. `StoredRunScope` gains `From(ProjectSet)` and `IncludesOpen`.
- Database names and shared-source guardrails: `SqlCrossDatabaseRule`, `DataSourceConfiguredDatabase` and `IConfiguredDatabaseResolver` (registered with `TryAdd`) in `Beacon.Core.Services.Validation`; `SqlGateRequest.ConfiguredDatabase` (null allows no database qualifier) and `SqlGateCodes.CrossDatabase` (`cross_database`, reported as a blocking schema verdict); `SmartSchemaContext.ConfiguredDatabase`; `DataSourceConfiguredDatabase.Undetermined` (connection settings that cannot be read) and `SqlGateRequest.InMemoryJoin` / `SqlGateRequest.ForInMemoryJoin` (the in-memory join's statement only). `IMcpSettingsProvider` is unchanged; the new `ISharedSourceSettings` (registered with `TryAdd`) computes the shared-source guardrails from it, and `McpSettingsData.Clone()` is public. `ProjectAccessEvents.ProjectCreatedOpen` (7112). `CreateProjectResult` gains `RequiresMembership` and `ProjectDetailEntry` gains `SharedSourceGuardrails` (positional records, binary-incompatible), with the new `SharedSourceGuardrailsEntry` and `SharedSourceGuardrail`.
- Scheduled work: `NotificationStatus.Skipped` (`8`), `AiActorExecutionPhase.Skipped` (`7`), `NotificationFailureReasons.OwnerNotAuthorized` and `AiActorService.OwnerNotAuthorizedMessage` are new; `BeaconForbiddenException` is no longer sealed. `AiActorService`'s constructor takes an `IRunAsScope` and an `IExecutionAccessCheck`. `SetSubscriptionOwnerCommand` and `PUT /beacon/api/subscriptions/{id}/owner` are new.
- `IBeaconAuthorizationProvider.AuthorizeAsync`, `AuthorizeNewResourceAsync` and `GetAccessibleResourceIdsAsync`, and `AuthorizationOptions.EnableResourceLevelAuthorization`, are `[Obsolete]` with default bodies and never called; they are removed in 5.0. A custom provider recompiles unchanged; code that calls these members gets `CS0618` warnings, which fail the build where warnings are treated as errors.
- Signature changes on `Beacon.AI`'s `IKnowledgeGraphService`: every member takes the caller's authorized `projectId`. New: `GetTableKnowledgeAsync(dataSourceId, projectId, …)`, `GetLineageAsync(dataSourceId, projectId, …)`, `GetDataSourceKnowledgeAsync(dataSourceId, projectId, ct)`, `SearchAsync(query, projectId, dataSourceId?, maxResults, ct)`, `GetContextForLlmAsync(dataSourceId, projectId, schemaName?, tableName?, ct)`, `GetSchemaCatalogAsync(dataSourceId, projectId, ct)`, `GetTablesContextAsync(dataSourceId, projectId, tableNames, ct)` and `GetSmartContextForAskAsync(dataSourceId, projectId, question, liveValueProbes, ct)` (`liveValueProbes` is true only for a caller allowed to query the project). Documentation and code references come from that project only; a data source the project does not link is not found (`InvalidOperationException`; an empty catalog from `GetSchemaCatalogAsync`, no results from `SearchAsync`), on every member that names one. Positional callers of `SearchAsync` must be updated: `projectId` comes before the optional `dataSourceId`.
- `IValueGroundingService.BuildValueMatchesBlockAsync(dataSourceId, projectId, question, tables, settings, ct)` and `ISemanticSearchService.AskAsync(dataSourceId, projectId, question, execute, ct)` take the caller's project; `AskAsync` refuses a caller without Query in it (`BeaconNotFoundException` for a project it may not read, `BeaconForbiddenException` otherwise) before any LLM call. `IGitHubScannerService.ScanRepositoryAsync(projectId, repositoryId, ct)` and `GetScanProgressAsync(projectId, repositoryId, ct)` find only that project's repository; `IDbtIntegrationService.ImportModelsAsync` refuses a data source the project does not link.
- `DatabaseMetadataService`'s constructor takes an `ISchemaGraphService` and an `ISharedSourceSettings` (in place of `IMcpSettingsProvider`), and its "not found" errors are `BeaconNotFoundException` (still a `BeaconException`). New: `Beacon.Core.Configuration.MetadataRefreshOptions` (`Beacon:Metadata`). `IMcpEvalService.StartRunAsync` refuses a null project (`InvalidOperationException`).
- People labels: `Beacon.Core.Helpers.UserLabel` is public — `UserLabel.OfUser`, the label as an expression EF Core translates on PostgreSQL and SQL Server, and `UserLabel.Of(userId, displayName)` for a user already read; a missing or blank display name is labelled "User {id}" everywhere. `BeaconActor.BeaconUserId` (the caller's `Users.Id`) has a public getter. `ISemanticSearchService.AskAsync` rethrows Beacon and business-rule refusals (`BeaconException`, `InvalidOperationException`) and answers any other failure with a generic message, never the exception's text. Data contracts: `DataContract.OwnerNotAuthorizedSince`, `DataContractData.OwnerNotAuthorized`, `DataQualityOverviewData.OwnerNotAuthorizedContracts` and `ControlTowerStatistics.OwnerNotAuthorizedContracts` are new.
- Shapes without raw user ids: `QueryVersionSummary`, `QueryVersionDetail` and `DashboardDetailsData` lose `CreatedByUserId`; `TaskDetailResult` (positional, binary-incompatible) has `HasAssignee` in place of `AssigneeUserId`.
- `IQueryApprovalService.GetPendingApprovalsAsync(request, queryId, readable, ct)` takes the `ProjectSet` of the projects the caller reads, and counts only the approvals in them.
- `IQueryVersionService.GetVersionsAsync(queryId, request, readable, ct)` takes the `ProjectSet` of the projects the caller reads, and lists and counts only the versions it reads by the data sources each one recorded.
- `VerifySchemaRelationshipCommand(DataSourceId, Id, IsVerified)`, `DeleteSchemaRelationshipCommand(DataSourceId, Id)` and `UpdateSchemaRelationshipCommand.DataSourceId` name the relationship's data source. `GetQueriesRequest`, `GetTasksRequest` and `GetControlTowerDataRequest` require `Readable` (the projects to list from).
- Positional records changed shape, so code that constructs or deconstructs them recompiles (binary-incompatible): `ProjectDataSourceEntry` (a leading `Id`), `ProjectSummaryEntry` (+ `RequiresMembership`, `MyRole`, `MemberCount`), `ProjectDetailEntry` (+ `RequiresMembership`, `MyRole`, `CanManageMembers`, `OpenlyReachableDataSourceIds`, `SharedSourceProjects`, `MembershipManagement`), `CreateDataSourceCommand` (+ an optional `ProjectIds`), `SubscriptionDetail` (+ `HasOwner`, `OwnerDisplayName`, `OwnerNotAuthorized`; the owner's id is not returned) and `TaskDetailResult` (`OwnerUserId`, `OwnerUserName` → `HasOwner`, `OwnerDisplayName`). `DataContractData` (`OwnerUserId` → `HasOwner`, `OwnerDisplayName`), `TaskDetailsData` (likewise) and `GetAiActorDetailsResult` (`CreatedByUserId` → `HasOwner`, `OwnerDisplayName`) changed shape too; `ControlTowerSubscriptionHealthData` and `SubscriptionDetailsData` gain `OwnerNotAuthorized`. `CreateProjectCommand.DataSourceIds` and `RepositoryUrls` are nullable (`null` means none).
- Additive: data-source entries carry `openWhenUnlinked`, `projects` and `otherProjectCount`; `GetProjectsQuery.ForApiKey`; `SetProjectAccessModeResult.SharedSourceProjects`; `SharedSourceProjectEntry`; `UnlinkProjectDataSourceCommand` and its result; new membership and link commands and routes (see [Members](#members)).
- SQL execution gate and data migration:
  - Removed: `IDataSourceService.GetDecryptedConnectionData` (no remaining callers; a host that called it reads the connection through `IDataSourceConnectionResolver`). `IMigrationService.UpdateMigrationJob` (no route or handler called it, and it did not re-check the destination; a job is changed by deleting and re-creating it). The unused `QueryExecutionOrchestrator` and its public result type `StepExecutionResult` (every query step runs through `IQueryService`).
  - Changed: `IColumnValueSampler.EnrichWithSampleValuesAsync(dataSource, tables, customPiiPatterns, ct)` takes the `DataSource` in place of an engine type and a connection string, and runs through the execution gate. `AiActorService`'s constructor takes an `IStepSqlSaveCheck`. `IDataSourceConnectionResolver.GetConnectionString` throws `InvalidOperationException` (`MigrationWriteTargets.ReadRefusedMessage`) for a migration write target, and so does the metadata scan. `IQueryService.ExecuteQueryAdvanced` keeps its signature and gains an overload `ExecuteQueryAdvanced(queryId, finalQuery, parameters, maxRows, ct)`, and `IQueryExecutionPreviewService.ExecuteTemporaryQueryPreview` an overload `(queryData, parameters, maxRows, ct)`: the row limit of a migration's read, in place of `Beacon:Query:MaxExecutionRows` (implementers of these interfaces add the overloads). `AdHocQueryResult.RowCount` counts the rows shown (at most 100) and `AdHocQueryResult.Truncated` says the statement had more. `McpDeploymentOptions.ForceReadOnly` defaults to `true`. `SetDataSourceMigrationWritesCommand` gains `Force` (default `false`): marking a source that live queries, subscriptions or data contracts read answers `409` with their counts (`MigrationWriteTargets.InUseMessage`) unless it is set. The migration list, detail, delete and runs handlers refuse with `409` while `Beacon:Migrations:Enabled` is off, as create and run do. Beacon's own query, runner, provider, subscription and migration services take `IOptions<QueryExecutionOptions>` / `IOptions<MigrationOptions>` as required constructor parameters (registered by `AddBeaconServices`), never a built-in default in place of the host's configuration.
  - New: in `Beacon.Core.Configuration`, `QueryExecutionOptions` (`Beacon:Query`: `MaxExecutionRows`, `StatementTimeoutSeconds`) and `MigrationOptions` (`Beacon:Migrations`: `Enabled`, `MaxRows`), both validated at startup, and `McpDeploymentOptions.ForcePiiDetection`. `Beacon.Core.Exceptions.QueryExecutionFailedException`; `Beacon.Core.Services.MigrationWriteTargets` and `IStepSqlSaveCheck` (registered with `TryAdd`); `DataSource.AllowMigrationWrites`; `SetDataSourceMigrationWritesCommand` and `GetMcpDeploymentLocksQuery` with their results; `GetDataSourcesQuery.MigrationTargets`, `DataSourceEntry.AllowMigrationWrites` and `DataSourceEntry.DatabaseReadOnlyEnforced`; `DatabaseMetadataSnapshot.SkippedNameCount`. In `Beacon.Core.Services.Validation`: `SqlRowLimitOutcome.OwnBoundCapped`, `SqlRowLimitResult.RefusalMessage` and `RefusalCode`, `SqlGateCodes.DisallowedCharacter` (`disallowed_character`), `OwnBoundCapped` (`own_bound_capped`) and `UncappableBound` (`uncappable_bound`). `QueryGuardrailOptions.Dialect`. `SqlIdentifierGuard.Quote`, `HasUnsafeCharacters`, `ForLog` and `MaxLoggedLength`. In `Beacon.Core.Authorization.ProjectAccess`, `ProjectAccessEvents.MigrationWritesChanged` (7115) and `ReadOnlyTransactionEnded` (7116).
  - Routes: `PUT /beacon/api/data-sources/{id}/migration-writes` (Admin), `GET /beacon/api/mcp/settings/locks`; `/beacon/api/auth/permissions` returns `migrationsEnabled`; the refresh-metadata and metadata responses carry `skippedNameCount`; the migration routes are mapped only while `Beacon:Migrations:Enabled` is `true`.

**Recommended steps for a regulated deployment:** upgrade; give each data source a database login per boundary and configure its database (catalog, project) in the connection; add members (a project admin first) and restrict sensitive projects, checking the list of data sources that stay reachable through open projects; re-issue integration keys with explicit project lists; check the other projects each sensitive project shares data sources with (the **Access** card), and unlink what should not be shared; finally set `Beacon:Authorization:RequireProjectMembership: true`, which restricts every project and makes unlinked data sources Admin-only.

## Upgrading: functional permissions

This version splits the Admin role into [functional permissions](#functional-permissions). **Existing Admins are unchanged**: they hold every permission, and the new roles are optional — nothing has to be configured or assigned.

**What changes without any configuration:**

- **New roles appear.** `Approver`, `Auditor`, `SecurityAdmin`, `UserAdmin`, `DataSourceAdmin` and `MigrationOperator` are created as level-0 system roles the next time roles are listed for assignment or seeded (names compared ignoring case, each role in its own save). A role that already exists under one of these names but is not a level-0 system role is left as it is and grants **nothing** (a warning names its id): rename it, or map the permission to another name with `Beacon:Authorization:Roles`. No schema change: the roles are rows in the existing `roles` table.
- **Route policies.** The routes listed in [Functional permissions](#functional-permissions) carry `BeaconApi.{Permission}` policies instead of `BeaconApiAdmin`; both admit the `Admin` role claim, and the permission filter additionally requires the permission (Admin included) to be held now — a demoted Admin's session that still carries the `Admin` claim is refused on these routes. Glossary, eval, unlinking and the Warp dashboard keep `BeaconApiAdmin`; glossary and eval now also admit only an Admin by live roles. The `BeaconApi.` policy-name prefix is reserved for Beacon.
- **Hosts without Beacon user management.** A session's role claims named like a permission's role no longer grant it by themselves: set `Beacon:Authorization:Roles:HonourHostRoleClaims: true` to keep that. The `Admin` claim still grants every permission.
- **Custom authorization providers** are asked on a permission's routes again: an Admin needs read or write permission there, and a host may veto a permission with `AllowsFunctionalPermissionAsync` (default: allowed). No code change is needed.
- **Default roles.** `Beacon:Authentication:Oidc:DefaultRoleName` and `Beacon:Mcp:Callers:Users:DefaultRoleName` may no longer name the Admin role or a functional role: startup fails, since every provisioned user would administer Beacon.
- **UserAdmins** no longer open restricted projects (an Admin does), change the memberships or revoke the API keys of protected users, or make themselves the owner of scheduled work. They create external users only, and change no user's user name or e-mail address (`403`); an Admin creates internal users and changes those.
- **SecurityAdmins** no longer turn read-only enforcement or PII detection off, or change the base URL (`403`); an Admin does.
- **MigrationOperators** need Query (not Run) on the declared source and on every step's data source, also to delete a job; a failed run's error text shows only to callers who read every data source the job reads. The Home migration totals count only the jobs their list shows.
- **LLM connection.** Only an Admin changes or tests another LLM provider, endpoint, region, Bedrock authentication mode or credential; a SecurityAdmin's admin-settings save that changes any of them answers `403`.
- **Forced metadata refresh.** Admins (and DataSourceAdmins) are no longer held to `Beacon:Metadata:ForcedRefreshCooldownMinutes`; a refresh of a data source that is running still refuses a second one (`429`).
- **Recipients.** Admins and SecurityAdmins see recipients' masked destinations; a SecurityAdmin redirecting a recipient attached to work it cannot read gets `403`.
- **The Home migration summary** shows the totals to MigrationOperators as well as Admins.
- **`GET /beacon/api/auth/permissions`** returns `isAdmin` and `permissions` besides `canRead` and `canWrite`.

**Public API.**

- New in `Beacon.Core.Authorization`: `BeaconPermissions` (names, policy names, `Policies`, `ConflictingPairs`, `Find`, `FirstConflict`, `WithoutConflicts`), `BeaconPermissionSet`, `IBeaconPermissionAccessor` (registered with `TryAdd`), `BeaconClaims.IdpPermission`, `BeaconClaims.IdpPermissionsIssuedAt` and `BeaconClaims.Issuer`, `ProjectCheck.GrantedBy` and `ProjectCheck.OrPermission`. In `Beacon.Core.Authorization.ProjectAccess`, `ProjectAccessEvents.UserRolesChanged` (7113) and `ProjectAccessEvents.UserCreated` (7114). In `Beacon.Core.Configuration`, `RoleMappingOptions` with `FunctionalRoleNames` (incl. `HonourHostRoleClaims`), `IdpRoleOptions` (incl. `ClaimLifetimeMinutes`) and `SeparationOfDutiesOptions` (bound from `Beacon:Authorization`, validated at startup), `PermissionOfStoredRole` and `IsPrivilegedRoleName`; `PermissionOfRole` compares names ignoring case. `RoleService.RoleLevels.Functional` (`0`).
- New route and request: `PUT /beacon/api/users/{id}/roles` (`SetUserRolesCommand(int UserId, int[] RoleIds)`). `RoleEntry` gains `Permission` (an optional last positional parameter; set for functional roles only). The `ProjectScopeExempt` policy of the requests behind the moved routes names the permission policy.
- `IBeaconAuthorizationProvider` gains `AllowsFunctionalPermissionAsync` as a default interface member (no change for implementers). `IUserManagementService` and the evaluator are unchanged.

## Upgrading: sign-in sessions

- **Existing sessions are re-checked.** With user management on, every session is checked against the user store at its next request after the upgrade, then every `Beacon:Authentication:Session:RevalidateMinutes` (default 5): a session of a missing, disabled or archived user ends, and the session's role claims become the stored roles. A session without a stored user ends while user management is on.
- **Absolute lifetime.** A session ends `Beacon:Authentication:Session:AbsoluteLifetimeHours` (default 12) after its sign-in, whatever sliding expiration or "remember me" would allow; its cookie, renewed or not, expires no later (a cookie scheme without sliding expiration keeps its expiry when a check renews it). A session signed in before the upgrade counts from its last renewal. Users signed in longer than that sign in again.
- **Security stamp.** The migration `AddUserSecurityStamp` (both providers) adds the nullable `users.security_stamp` column; existing users and sessions carry none, which match, until the stamp first changes. Changing a password, disabling, re-enabling or archiving a user, changing their roles, user name or e-mail address, and **sign out everywhere** (`POST /beacon/api/users/{id}/sign-out-everywhere`, `POST /beacon/api/auth/logout?everywhere=true`) end the user's other sessions.
- **Realtime hub.** Callers without read permission get no hub connection unless they hold Approver or MigrationOperator; `/auth/me` reports `realtimeEnabled` per caller.
- **API keys and owner inactivity** (opt-in): `Beacon:ApiKeys:MaxOwnerInactivityDays` refuses the keys of an owner who has not signed in for that long. See [API Keys → When the owner leaves](/features/api-keys/#when-the-owner-leaves).
- **Public API.** New: `SessionLifetimeOptions` (`Beacon:Authentication:Session`), `ApiKeyOptions.MaxOwnerInactivityDays`, `ApiKeyOwnerInactiveException` (thrown by `IApiKeyService.ValidateApiKeyAsync` for an inactive owner's key), `BeaconUser.SecurityStamp`, `SignUserOutEverywhereCommand`, `ProjectAccessEvents.UserSessionsRevoked` (7117) and `ProjectAccessEvents.SessionEnded` (7118). `POST /beacon/api/auth/logout` takes an optional `everywhere` query parameter.
- **Data Protection.** `AddBeaconCookieAuthentication` no longer configures Data Protection (no application name, no key storage): call `AddBeaconDataProtection(configuration, environment)` for Beacon's database-backed key ring, or configure your own. Outside Development, `Beacon:DataProtection:ApplicationName` is required for Beacon's setup, and a key ring persisted without `ProtectKeysWith*` stops the host. Every session signs in again once after the upgrade. After restoring a Beacon database into another environment, give every user a new security stamp and the copy its own key ring — see [Configuration → Data Protection](/getting-started/configuration/#data-protection-the-keys-behind-the-session-cookie).
- **Encryption key.** `Beacon:EncryptionKey` must be base64 of at least 32 bytes, not a published value and not a `${...}` placeholder, in every environment; `Beacon:PreviousEncryptionKeys` keeps older values readable. See [Configuration → Encryption Key](/getting-started/configuration/#encryption-key-configuration-required).
- **Sign-in.** `Beacon:UserManagement:LocalLogin` (on by default; off unmaps `POST /beacon/api/auth/login` except for break-glass accounts), a forced password change for passwords an Admin sets (`users.must_change_password`), `Beacon:Authentication:Oidc:RequireMfa` (off by default) and role provenance (`AssignedByUserId` on every grant by a person). See [User Management → Upgrading from 4.5](/features/user-management/#upgrading-from-45).
- **Keys written before encryption.** At the first start outside Development with key encryption, every Data Protection key still stored without encryption is revoked and a new, encrypted key is created (`ProjectAccessEvents.DataProtectionKeyRing`, 7121): everyone signs in again once. A host with no key repository gets a startup Warning; `Beacon:DataProtection:AllowUnencryptedKeys` acknowledges an unencrypted ring instead of failing.
- **Retired encryption keys.** `IStoredValueReEncryptionService` moves stored values off `Beacon:PreviousEncryptionKeys` (the sample host enqueues `ReEncryptStoredValuesJob` at every start while a previous key is configured; `ProjectAccessEvents.StoredValuesReEncrypted`, 7122). `Beacon:Mcp:CallerHashKey` keeps MCP caller hashes stable across a key change.
- **Stricter.** An encryption key that decodes to printable text or fewer than 16 distinct byte values is refused. Break-glass names match the stored user name exactly. While local login is off, every password sign-in, whatever provider authenticated it, needs a stored internal break-glass user. Realtime connections close at the session's next check. `IBeaconAuthenticationProvider.ValidateSessionAsync` is marked obsolete (Beacon never called it).
- **More public API.** New: `BeaconDataProtectionExtensions.AddBeaconDataProtection` (`Beacon.Api.Authentication`), `LocalLoginOptions` and `UserManagementOptions.LocalLogin`, `OidcAuthenticationOptions.RequireMfa` and `AcceptedAcrValues`, `AuthenticationResult.PasswordChangeRequired` and `PasswordChangeNeeded`, `IUserManagementService.CompleteRequiredPasswordChangeAsync`, `BeaconUser.MustChangePassword`, `CreateInternalUserRequest.AssignedByUserId` and `CreateExternalUserRequest.AssignedByUserId`, `ProjectAccessEvents.BreakGlassSignIn` (7119) and `ProjectAccessEvents.PasswordSignInRefused` (7120), `BeaconClaims.SessionStamp`, `StoredValueReEncryptionResult`, `ServiceConfiguration.PreviousEncryptionKeysKey` and `HasPreviousEncryptionKeys`, `McpCallerSubjectHasher.CallerHashKeyKey` and `KeySource`, and the `BeaconDataProtectionExtensions` constants `ApplicationNameKey`, `DevelopmentApplicationName` and `AllowUnencryptedKeysKey`. `GET /beacon/api/auth/sso` returns `localLoginEnabled` and `/auth/me` `signOutEverywhereEnabled`.
- **Changed signatures.** `IUserManagementService.CompleteRequiredPasswordChangeAsync` is a default interface member: an existing implementation recompiles unchanged and, without an override, answers the generic sign-in failure (a user it flags cannot complete the change through the login form). `HybridAuthenticationProvider`'s constructor gains an optional third parameter, `BeaconConfiguration? configuration` (the old two-parameter constructor is gone: code compiled against it recompiles unchanged); without the configuration the provider does not fall back to its external JWT login, since it cannot tell whether local login is on. `JwtExternalApiAuthenticationProvider` gains a constructor that takes `BeaconConfiguration` (while local login is off it refuses every login); the existing one is kept. `LoginRequest` (a positional record) gains an optional last parameter, `NewPassword`: code that constructs or deconstructs it recompiles (binary-incompatible). `LoginResponse` gains `PasswordChangeRequired`, and the login operation now declares `LoginResponse` for `200`, `400` and `401` in the OpenAPI document (the typed client's `login()` returns it, and throws with it on `400`/`401`); the operation is documented only where it is mapped (local login on, or a break-glass account listed).

## Upgrading: data minimisation

- **Host data sources are masked by position.** The columns a host policy masks are masked by their position in the
  result, before the rows are keyed by column name, on every path that reads a host data source (a caller's alias, a
  long or non-ASCII alias, or a repeated name no longer decides what is masked). A result column the database names
  differently than the policy derived is masked too, and the MCP `query` tool says how many; a result whose columns do
  not line up with the columns the policy checked is discarded and the request fails. On PostgreSQL host data sources
  two identifiers PostgreSQL would shorten to the same 63-byte name, and unquoted identifiers with a non-ASCII letter
  that changes case, are refused. See [Host DbContext](/features/host-dbcontext/).
- **Failure logs name the failure, not its message.** Connectors, previews, the in-memory join, migrations,
  grounding, the ask and cross-source pipelines, evals and the data-contract and AI-actor jobs log a failed query by
  exception type, error code (SQLSTATE, SQL Server error number, SQLite result code, HTTP status, Google, AWS and
  Databricks error codes), the types of the exceptions it wraps and the method that threw it —
  `PostgresException (error code 22P02, inner SocketException, at NpgsqlConnector.ReadMessage)` — where they logged
  separate type and code fields, or the message, before; the API connector logs the data source of a call, not its
  path. A refusal Beacon raises itself (the execution gate) keeps its message.
- **Database errors are redacted by class** before they reach an LLM prompt, a learning signal or the stored ad-hoc
  query history (always), and the caller and the audit row (while PII detection is on): an error that can quote row
  values becomes `Database error <code> (<class>): <generic description>`. See
  [MCP Server → Database errors](/features/mcp-server/#database-errors).
- **Sample values and value grounding are off for a new installation**, and a project turns them on for itself:
  a metadata refresh collects while every live project that links the data source has collection on (its override,
  else the global value), so a project that turns it on collects even while the global value is off. Stored sample
  values are returned only to an Admin by the REST metadata endpoint; `get_query_context` returns them to the project's
  callers while sampling is on. The refresh reads only the columns it may sample; secret-like (now also `hmac` and key
  material named with `hmac`, `aes`, `rsa`, `pgp`, `gpg` or `ssh`), PII (banking identifiers included), person-name,
  Croatian personal-data and financial-amount columns are never sampled, probed or listed, while surrogate keys such as
  `ProductKey` are sampled again; archiving a data source or marking it as a migration write target clears its samples,
  archived tables included. See [Sample values](/features/data-sources/#sample-values) and
  [What reaches the LLM provider](/features/mcp-server/#what-reaches-the-llm-provider).
- **Banking identifiers are PII.** Column names `iban`, `oib` and `jmbg` (as a whole name segment: `customer_iban`,
  `ClientOib`, `IBANNumber` — not `Libanon`) and account numbers (`account_no`, `account_number`, `acct_num`) match the
  built-in PII pattern, so query results mask them on every path while PII detection is on; values with valid IBAN, OIB
  or JMBG check digits count as PII-shaped, and data-quality custom SQL masks a text value that looks like PII whatever
  its column is called.
- **Row data to the LLM provider is logged and can be locked out.** Every prompt carrying row data is logged (event
  `7123`, as it is handed to the provider; a provider failure adds a Warning), and `Beacon:Mcp:ForbidRowDataToLlm` pins
  sample values, value grounding and the eval judge off and keeps value-listing documentation sections out of prompts;
  under it, an instruction on such a section answers `400`. New startup records: a Warning naming the projects whose
  stored documentation still has value-listing sections (under the lock), and an Information record of the row-data
  settings in effect while no global MCP settings row is stored.
- **Settings.** A save under a deployment lock keeps the stored value of every field the lock pins — now for a project's
  overrides too (before, a project save cleared them), so lifting the lock restores the project's configuration. A
  `PUT /beacon/api/mcp/settings` body that omits `enableSampleValueCollection` or `enableValueGrounding` stores `false`
  for it (the request shape is unchanged; the meaning of an omitted field is). The MCP settings page shows both switches,
  globally and per project, and sends back every setting it loaded.
- **Migration.** `DefaultSampleValueCollectionOff` (both providers) changes only the default of
  `mcp_settings.enable_sample_value_collection` to `false`; no row is rewritten.
- **Public API.** Removed (breaking for implementers and callers): `IHostDataSourceGuard.Mask` — host results are masked
  by position inside Beacon. New member on an interface (breaking for implementers):
  `IProjectDocumentationService.ExportLatestToMarkdownAsync(int projectId, IReadOnlyCollection<ProjectDocSectionType> leaveOut, CancellationToken)`;
  `ILlmRowDataEgress` gains `RecordReturnedToMcpClientAsync`. New: `HostPolicyResult.OutputColumns`,
  `HostPolicyResult.Engine` and `HostPolicyResult.MaskedOutputPositions`, `HostOutputColumn`, `HostResultMetadata`
  (`RenamedMaskedColumns`, a `ProviderQueryResult.Metadata` key);
  `Beacon.Core.Helpers.FailureCodes` (`ErrorCodeOf`, `Describe`, `InnerTypesOf`, `ThrowingMethodOf`);
  `Beacon.Core.Exceptions.DataSourceServiceException` (`ErrorCode`); `McpDeploymentOptions.ForbidRowDataToLlm`;
  `SecretLikeColumnNames.IsSecretLikeForValues`; in `Beacon.Core.Services.Security`, `CredentialValueScreen`,
  `BankingIdentifiers`, `ColumnValueExclusions` and `DatabaseErrorRedactor` (`Placeholder`, `UnscreenedMessage`,
  `Redact`, `ForCaller`, `ClassOnly`, `KnownNames`); `Beacon.Core.Data.Enums.ProjectDocSectionTypes` (`ListingValues`,
  `ListsValues`); `BeaconTelemetry.ErrorRedactionFallbacks` (`beacon.error_redaction.fallbacks`);
  `ProjectAccessEvents.RowDataSentToLlm` (7123); in `Beacon.AI.Services.LlmProviders`, `ILlmRowDataEgress` (registered
  with `TryAdd`), `LlmRowDataScope` (with an init-only `CallerId`) and `LlmRowDataCategories` (`sample-values`,
  `value-matches`, `column-values`, `result-rows`, `database-errors`, `stored-documentation`). New init-only properties:
  `SmartSchemaContext.RowDataCategories`, `AskSqlPipelineOptions.CallerId`, `FailureCluster.ProjectId`,
  `DocChunkHit.SectionType`. Changed defaults: `McpSettingsData` and the `McpSettings` entity default
  `EnableSampleValueCollection` and `EnableValueGrounding` to `false`. Changed behaviour:
  `ISharedSourceSettings.GetDataSourceSettingsAsync` takes sample-value collection from the linking projects' effective
  settings; `IQueryGuardrailService.IsPiiColumn` and `PiiValueScreen.ContainsPiiValue` match more. The REST and OpenAPI
  shapes are unchanged; `GET /beacon/api/data-sources/{id}/metadata` and the refresh return `sampleValues` to an Admin
  only.

## Upgrading: change governance

This version makes the query change approval workflow a working four-eyes control and applies it to every path that
changes what a query runs. It stays **opt-in** (see [Configuration → Approval Workflow](/getting-started/configuration/#approval-workflow));
the rules are in [Queries → Approval workflow](/features/queries/#approval-workflow).

**Without any configuration:**

- **Startup note.** With the workflow off, startup logs a Warning outside Development (Information in Development).
- **Every executable change is versioned.** A save that changes a step's order, name, data source or parameters, removes
  or adds a step, or changes the final query writes a new active version, as a SQL change always did. Step orders must run
  1, 2, 3, … without gaps or repeats: a save that changes what runs, a restore or an approval of other orders is refused
  (`409`), so renumber such a query's steps (save them as 1, 2, 3, …) before its next content change, and do not restore
  or approve a version recorded with such orders. The baseline that records the SQL an edit replaces is not written twice
  when a version that was live already holds the same content.
- **Restore** puts back only a version that was live: restoring a pending, rejected or superseded version answers `409`.
  The restored steps pass the save check again (`409` with the reason otherwise). Restoring a query another user locked
  needs Manage on the query (overriding the lock); the lock holder needs Write, as for an unlocked query.
- **AI actors** validate a refinement and a new query as a draft before anything is stored (the stored query is no longer
  changed to try the SQL). A refinement is saved as a new active version attributed to the actor's creator.
- **The editor sends the version it loaded** (`baseVersionId`); a save made on a version that is no longer active answers
  `409` — reload and make the change again.

**With the workflow on:**

- **Four eyes.** The requester and the reviewer are the signed-in users (recorded by `Users.ExternalId`, named by
  their label), never ids the request carries. Nobody approves their own change, nor one they shaped (an AI plan's
  reviewer and revision requesters) — **Admins included**: an Admin can no longer approve their own change. A host with a
  single person who can approve needs a second Approver or `AllowSelfApproval` (off by default; a startup Warning says
  when it is on, and another when fewer than two users can approve). A submission Beacon cannot name a requester for (an
  API key without an active owner, an AI actor without a creator) is refused.
- **The decision binds to what was reviewed.** The approval detail carries the proposal's content hash and the
  step-by-step diff; `POST /beacon/api/approvals/{id}/approve` must send that hash back (`expectedContentHash`), or it
  answers `409`. A client that approved with a comment only must be updated.
- **Decisions are final.** Approve and reject act only on a pending request (`409` otherwise); an approval is refused as
  stale (`409`) once another version went live after the request was made, and when the proposal no longer holds the
  content it was submitted with; every step passes the save check again. The decision, the query's steps and the version
  statuses commit in one transaction.
- **Pending requests made before this version must be submitted again.** They record neither the version they were made
  on nor their content hash, so they can be rejected, not approved: reject them and save (or restore) the change again.
- **One pending request per query.** A new submission supersedes the earlier pending one (`Superseded`) and notifies its
  requester; pending requests that already exist are left as they are until the next submission for the query. A save
  that only brings unreviewed stored SQL under review, and an AI refinement, never supersede another person's request
  (`409` while one waits).
- **New queries go live only after approval.** A query created with steps, or a new query's first save, is submitted
  as its first version; until it is approved the query has no steps and runs nothing.
- **Restore and AI refinements are changes**: a restore is submitted for approval ("Restore of version N"), and so is an
  AI actor's refinement or new query, with the actor's creator as its requester. Saving a query whose stored SQL is not
  its active version's (never versioned, or changed before this version outside a versioned save) submits that SQL for
  approval.
- **AI actors' plans and subscriptions.** Approving a plan needs the Approver permission (an Admin holds it) and someone
  other than the actor's creator and the plan's revision requesters; the creator still rejects or asks for a revision. A
  plan is decided only from `PendingApproval` (`409` otherwise). An actor subscribes only to a query whose active version
  was approved for exactly its content.
- **Runs execute reviewed SQL.** A subscription run whose query's stored steps are not its active version's content is
  not run and is recorded as *Skipped* ("Skipped: query change not approved. Save the query to submit its SQL for
  approval, or restore a version and approve it."); the subscription detail and Control Tower rows report `notApproved`,
  and a test delivery answers `409`. In strict mode (`ApprovalWorkflow.RequireApprovedVersionToRun`, recommended for
  regulated deployments) a run also needs an approved request for exactly the active version's content: versions approved
  before this version recorded no content hash and do not run until approved again (restore the active version to
  submit it), nor do queries never versioned. The startup Warnings count the queries affected.
- **MCP tools**: while the workflow is on, whoever requested or approved the version a tool runs cannot enable the tool
  (`403`), nor can a caller without a user id; another SecurityAdmin (or Admin) publishes it. `AllowSelfApproval` lifts
  this. The check is made when the tool is enabled.
- **Direct step changes** (`IQueryService.AddQueryStep`, `UpdateQueryStep`, `DeleteQueryStep`) are refused (`409`).

**Migration.** `AddQueryChangeIntegrity` (PostgreSQL `20261013120000`, SQL Server `20261013120001`) adds four nullable
columns: `query_versions.content_hash` (the SHA-256 of a version's executable content), and on
`query_approval_requests` `base_version_id`, `approved_content_hash` and `contributor_user_ids` (the people besides the
requester who shaped a change: a text array on PostgreSQL, JSON text on SQL Server). Existing rows are not rewritten: a
version without a hash is compared by the hash of its snapshot, a request without a base and a hash is reject-only, and
an approval of a version without a hash records it. Rolling it back drops the four columns.

**Public API.**

- Changed (breaking for callers and implementers):
  - `IQueryApprovalService.ApproveAsync(int requestId, string? comment, string? expectedContentHash, CancellationToken)`
    and `RejectAsync(int requestId, string? comment, CancellationToken)` decide as the current caller (the reviewer id and
    name parameters are gone) and return a `QueryApprovalDecision`; approving requires the content hash the reviewer was
    shown.
  - `IQueryVersionService.CreateProposedVersionAsync` is replaced by
    `SubmitForApprovalAsync(int queryId, QueryData proposed, QueryChangeSubmission submission, CancellationToken)`,
    which also files the request and returns a `QueryVersionChange`; `RestoreVersionAsync` returns a `QueryVersionChange`
    (the new version — Active, or PendingApproval when submitted — and the number of requests it superseded) instead of
    the version's number.
  - `RestoreQueryVersionCommand` is an `IRequest<RestoreQueryVersionResult>`:
    `RestoreQueryVersionResult(int VersionNumber, bool SubmittedForApproval, int SupersededRequests)`.
  - `SavedQueryRunnableVersion.WhereHasRunnableVersion` and `ForQuery` take an optional `bool requireApprovedHash`
    (strict mode; see `SavedQueryRunnableVersion.RequiresApprovedHash`) — source compatible, recompile.
  - The `AiActorService` constructor takes `IQueryVersionService` and `BeaconConfiguration`.
  - `IQueryService.CreateQuery` submits a new query's content for approval while the workflow is on.
- Obsolete and ignored: `ApproveQueryChangeCommand.ReviewerUserId` / `ReviewerName`, `RejectQueryChangeCommand.ReviewerUserId` /
  `ReviewerName`, `RestoreQueryVersionCommand.UserId`. Obsolete: `IQueryService.AddQueryStep`, `UpdateQueryStep`,
  `DeleteQueryStep` (to be removed).
- New:
  - `ApprovalWorkflowOptions.AllowSelfApproval` and `RequireApprovedVersionToRun`; `ApprovalStatus.Superseded` (`4`)
    and `QueryVersionStatus.Superseded` (`5`).
  - `QueryVersion.ContentHash`; `QueryApprovalRequest.BaseVersionId`, `ApprovedContentHash` and `ContributorUserIds`.
  - `IQueryVersionService.SubmitNewQueryAsync(Query query, QueryData proposed, QueryChangeSubmission submission, CancellationToken)`.
  - `QueryChangeSubmission(RequesterUserId, RequesterDisplayName, Source, Reason, Summary)` with `ExpectedBaseVersionId`,
    `SupersedesOthers` (default `true`) and `ContributorUserIds`; `QueryVersionChange(Version, SupersededRequests)`;
    `QueryApprovalDecision(RequestId, Status, RequesterUserId, ReviewerUserId)`.
  - `ApprovalRequestDetail.ContentHash`, `IsOwnRequest`, `MayApprove`, `ApproveBlockedReason` and `IsStale`;
    `ApproveQueryChangeCommand.ExpectedContentHash`.
  - `QueryData.BaseVersionId`; `QueryDetailsData.ActiveVersionId`.
  - `SubscriptionDetailsData.NotApproved`, `SubscriptionDetail.NotApproved` (an optional last positional parameter),
    `ControlTowerSubscriptionHealthData.NotApproved`; `NotificationFailureReasons.NotApproved`.
  - `SavedQueryRunnableVersion.RequiresApprovedHash(BeaconConfiguration)`; `BeaconActor.IsRecordedAs`;
    `AiActorService.AiActorChangeSource`, `PlanNotPendingMessage` and `NotApprovedForSubscriptionMessage`.
- REST and OpenAPI:
  - `GET /beacon/api/approvals/{id}` adds `contentHash`, `isOwnRequest`, `mayApprove`, `approveBlockedReason` and
    `isStale`; `POST /beacon/api/approvals/{id}/approve` takes `expectedContentHash` besides `comment` (required to
    approve; ignored by `/reject`).
  - `PUT /beacon/api/queries/{id}` (`QueryData`) takes `baseVersionId`; `GET /beacon/api/queries/{id}` returns
    `activeVersionId`.
  - `POST /beacon/api/query-versions/{id}/restore` returns `submittedForApproval` and `supersededRequests` besides
    `queryId` (which holds the new version's number, as before).
  - The subscription detail and the Control Tower subscription rows return `notApproved`.
  - The `ApprovalStatus` enum gains `4` (Superseded) and `QueryVersionStatus` gains `5` (Superseded).
  - Approve, reject and restore answer `409` for a decided, superseded, stale or pre-upgrade request, a changed proposal
    or other content than reviewed, a step the save check refuses or a never-approved restore source, and `403` for an
    own change, one the caller shaped, or a request without a requester. A save answers `409` for a stale editor, another
    person's pending request, a requester Beacon cannot name or a concurrent write.

## Migration from Previous Versions

:::caution[Upgrading]
The Admin checks on routes and resources read the `ClaimTypes.Role` claim `Admin` (see [Admin role claim](#admin-role-claim)): a host that only emits `beacon:role` must add it. New public types: `BeaconActor` and `IBeaconActorAccessor` (registered by Beacon; replaceable). `IActorUserResolver` now resolves an API key to its owner by the key's owner id.
:::

If you are upgrading from a version without authorization:

1. **No changes required** - Authorization is disabled by default
2. **Opt-in when ready** - Enable authorization when you're ready
3. **No breaking changes** - Existing code continues to work
4. **Gradual adoption** - Start with global permissions, add resource-level later

## See Also

- [User Management](/features/user-management/) - Built-in user management with login form and role assignment
- [Admin Settings](/features/admin-settings/) - Runtime configuration (Admin-only)
- [Configuration Guide](/getting-started/configuration/)
- [Quick Start](/getting-started/quick-start/)
