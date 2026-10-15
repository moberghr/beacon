---
title: User Management
description: Built-in user management with internal password and external JWT/OAuth authentication, role-based access control, and a first-run admin setup.
---

Beacon includes a built-in user management system with support for internal (password-based) users, external (JWT/OAuth) users, and role-based access control.

## Overview

The user management system provides:

- **Internal Users** - Username/password authentication stored in Beacon's database
- **External Users** - JWT/OAuth authentication from your existing identity provider
- **Hybrid Mode** - Support both internal and external users simultaneously
- **Role-Based Access Control** - Admin, Editor, and Viewer roles with level-based permissions, and functional roles that split the Admin role's duties
- **First-Run Setup** - Guided wizard to create the initial super admin on first launch
- **User Administration** - Manage users, assign roles, and enable/disable accounts via the UI

### Key Features

- Opt-in by default — user management is disabled unless explicitly enabled
- Flexible authentication — internal passwords, external JWT, or hybrid
- Three predefined roles — Admin, Editor, Viewer with clear permission boundaries
- Six functional roles — Approver, Auditor, SecurityAdmin, UserAdmin, DataSourceAdmin, MigrationOperator — for one administrative duty each, without project data
- Super admin — bypasses all authorization checks
- Role provenance — every role assignment records when it was made and who made it (see [Who assigned a role](#who-assigned-a-role))
- Local password sign-in can be turned off for single sign-on deployments, with break-glass accounts (see [Local password sign-in](#local-password-sign-in))
- A password an administrator sets is replaced by the user at their first sign-in
- Soft delete — archive users without losing history

## Quick Start

### 1. Enable User Management

Update your `Program.cs`:

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options =>
{
    options.AddBeaconScheduler<BeaconScheduler>();
    options.BaseUrl = "https://your-domain.com";

    // Enable authorization
    options.Authorization.Enabled = true;

    // Enable login form
    options.Authentication.EnableLoginForm = true;
    options.AddAuthenticationProvider<DatabaseAuthenticationProvider>();

    // Enable user management
    options.UserManagement = new UserManagementOptions
    {
        Enabled = true,
        AllowInternalUsers = true,
        MinimumPasswordLength = 8,
        RequirePasswordComplexity = true
    };
})
.UsePostgreSql(connectionString, "beacon");

// Serves the React SPA (Beacon.UI Razor Class Library) at the root URL "/"
builder.Services.AddBeaconUI();

// Add cookie authentication
builder.Services.AddBeaconCookieAuthentication();

// Data Protection keys for the session cookie (or configure your own). Outside Development: set
// Beacon:DataProtection:ApplicationName and chain ProtectKeysWith*, or the host does not start.
builder.Services.AddBeaconDataProtection(builder.Configuration, builder.Environment);

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// Maps the React shell + Beacon REST API; the login form is served at "/login"
app.UseBeaconUI();
```

### 2. First-Run Setup

First-run setup needs user management (`EnableUserManagement()`); `MapSetupEndpoints` without it cannot run setup and says so in the log at startup. On first launch, while no super admin has ever existed in the database (an archived super admin counts), Beacon redirects to a setup wizard:

1. Navigate to the application URL
2. You are redirected to the setup page automatically
3. Enter the **setup token**: the value of `Beacon:UserManagement:SetupToken` (or `UserManagementOptions.SetupToken`), or, when none is configured, the token Beacon prints once to the process console (standard error) when it starts. The generated token is never written to the log; a log Warning only says that it was printed.
4. Create the initial super admin account (username, email, password)
5. System roles (Admin, Editor, Viewer, and the [functional roles](#functional-roles)) are seeded automatically
6. Log in with your new credentials

A configured `SetupToken` must be at least 32 characters, or the host does not start. The token is compared in constant time, and creation is atomic: concurrent attempts create exactly one super admin (a request that loses the race is told setup is complete, or gets `409` and can retry). Each replica generates its own token, so a multi-replica deployment must configure `SetupToken`. Super-admin creation is logged with the new user's id.

The setup endpoints are rate-limited per client address, in a budget separate from the login form's. Behind a reverse proxy, configure forwarded headers (`UseForwardedHeaders` with your proxy as a known proxy) so the limit applies per client rather than to the proxy's address. `GET /beacon/api/setup/roles` is anonymous only while setup is open.

Until the super admin exists, Beacon provisions **no other user**: an SSO sign-in or an MCP caller that would create a user is refused, with a log line that says first-run setup has not been completed. Users that already exist are unaffected.

### 3. Manage Users

After setup, an Admin or a [UserAdmin](#functional-roles) navigates to **Users** in the React UI (`/users`) to:

- Create users: internal (password) users are an Admin's to create, and choose their own password at their first sign-in ([Passwords set by an administrator](#passwords-set-by-an-administrator)); a UserAdmin creates external users
- Assign roles (a UserAdmin assigns neither the Admin role nor a functional role); each assignment records who made it
- Enable/disable accounts (disabling or archiving an account revokes its [API keys](/features/api-keys/#when-the-owner-leaves), and re-enabling it revokes any key still active instead of restoring one)
- View user details and login history

## Roles and Permissions

Beacon includes three predefined system roles, and six [functional roles](#functional-roles) for single administrative duties:

| Role | Level | Read | Create/Edit | Execute | Delete/Archive |
|------|-------|------|-------------|---------|----------------|
| **Admin** | 3 | Yes | Yes | Yes | Yes |
| **Editor** | 2 | Yes | Yes | Yes | No |
| **Viewer** | 1 | Yes | No | No | No |

### Super Admin

Users with the `IsSuperAdmin` flag bypass all authorization checks. The first user created during setup is automatically a super admin.

### Permission Details

- **Viewer (Level 1+)** - Read-only access to all resources
- **Editor (Level 2+)** - Create, edit, and execute queries, subscriptions, data sources
- **Admin (Level 3)** - Full access including delete, archive, user management, and admin settings

Besides the role levels, some resources belong to a person: an alert task is worked by its [assignee](/features/tasks/#assignment-watching-snooze-priority), a [data contract](/features/data-quality/) is changed, evaluated or deleted by its owner and an [AI actor](/features/ai-actors/) by its creator; an Admin may do all of these, and only an Admin or a UserAdmin gives a subscription, a contract or an actor a new owner. Reading the user directory and the role list is an Admin's or a UserAdmin's, the MCP settings an Admin's, an Auditor's or a SecurityAdmin's, the data-migration jobs and runs an Admin's or a MigrationOperator's, and setting a project repository's access token an Admin's or a SecurityAdmin's (see [Functional roles](#functional-roles)). These checks read the `ClaimTypes.Role` claim of an enabled user (see [Authorization](/features/authorization/#admin-role-claim)), and a functional permission must also be held now.

:::caution[Upgrading]
`GET /users`, `GET /users/roles`, `GET /mcp/settings`, `GET /mcp/projects/{projectId}/settings`, `GET /migrations/jobs`, `GET /migrations/jobs/{id}` and `GET /migrations/executions` need the Admin role or the functional permission that opens them (API keys never pass), as do `PUT /projects/repositories/{id}/token` and a project created with an access token. The sidebar hides User Management, MCP Settings, Admin Settings, Approvals and Data Migration from users without the permission that opens them. `GET /home/trends` clamps `days` to 1–90. Routes set a data contract's owner (`PUT /data-quality/contracts/{id}/owner`) and an AI actor's creator (`PUT /ai-actors/{id}/owner`), for an Admin or a UserAdmin. A disabled or archived user's session passes none of these checks, whatever its claims.
:::

### Functional roles

The Admin role's duties can be given one at a time. Each functional role grants one [functional permission](/features/authorization/#functional-permissions) and **no access to project data**: it is a level-0 role, with neither read nor write permission. A user who should also use projects needs a Viewer or Editor role beside it. Admins hold every permission.

| Role | The duty |
|---|---|
| **Approver** | Approves and rejects query changes; reads every project's approval requests |
| **Auditor** | Reads the MCP audit, the MCP and admin settings (secrets masked) and every user's API keys |
| **SecurityAdmin** | Changes the MCP and admin settings (not the LLM connection), recipients, MCP tool publishing and repository tokens |
| **UserAdmin** | Manages users (creates external users only) and their roles and API keys, resource owners, the members of any project, and restricts projects |
| **DataSourceAdmin** | Creates, tests and deletes data sources; links any data source it can read into a project it administers |
| **MigrationOperator** | Runs data migration between data sources it may query and write |

**Assigning them.** The roles are listed with the others (`GET /beacon/api/users/roles`; each entry's `permission` names the permission a functional role grants) and are assigned when a user is created or with `PUT /beacon/api/users/{id}/roles` (`{ "roleIds": [1, 4] }`, the complete set of the user's roles; `204`, `404` for an unknown user, `400` for an unknown role). Their names follow `Beacon:Authorization:Roles` (see [Configuration](/getting-started/configuration/#functional-permissions-role-names-identity-provider-app-roles-separation-of-duties)).

**On the Users page.** The add-user dialog and the edit dialog (open a user) list the roles in two groups: **Global roles** (Admin, Editor, Viewer and any other) and **Functional roles**. Saving the edit dialog sends the account changes and, when the roles changed, the complete set of roles (`PUT /users/{id}/roles`). For a UserAdmin who is not an Admin, the add-user dialog offers external users only, the edit dialog shows the user name and e-mail address read-only (and sends them unchanged), the Admin role and the functional roles are shown disabled, and the dialog opens read-only, with a note saying why, for their own account and for an Admin, a super admin or a holder of a functional role; the enabled/disabled toggle in the list is disabled for the same users. When the server refuses the roles, the dialog stays open and shows its reason beside them: `400` for separation of duties or an unknown role, `403` for a change the caller may not make.

**In the shell.** The sidebar shows each page a permission opens to its holders (an Admin sees them all), and pages show their actions only to those who may use them: approving and rejecting to Approvers, the MCP and admin settings read-only to Auditors and editable by SecurityAdmins (the LLM provider, endpoint, region, authentication mode and credential fields stay disabled, with a note, for anyone but an Admin), every user's API keys to Auditors and UserAdmins (revoking to UserAdmins), creating and deleting data sources to DataSourceAdmins, recipients, MCP tool publishing and repository tokens to SecurityAdmins, owner changes, the project **Access** card and the **Members** tab of any Beacon-managed project to UserAdmins. A user who holds functional roles only gets in, sees the pages those open, and finds on every other page a note that project data needs a Viewer or Editor role.

**What a UserAdmin cannot do.** A UserAdmin who is not an Admin cannot raise anyone, itself included (`403`):

- assign or remove the Admin role or a functional role (also when creating a user);
- create an internal (password) user, or change any user's user name or e-mail address, which an internal user signs in with: so a UserAdmin cannot make an account it can sign in to (`POST /users/internal` creates nobody). No route sets another user's password or changes a user's external id, identity provider or user type after the user is created;
- change their own account or roles — no self-grant, no self-demotion, no disabling themselves — or their own project membership;
- change the account, roles, enabled state, project memberships or API keys of a protected user: an Admin, a super admin or a holder of a functional role (another UserAdmin included, so two UserAdmins cannot add each other to a project). Every level-0 system role counts as a functional role, also one the mapping no longer names;
- open a restricted project (restricting one is theirs), or make themselves the owner of a subscription, data contract or AI actor.

Every user created is logged with ids only (event 7114 `UserCreated`: the new user, whether it is an internal user, who created it), every role change (event 7113 `UserRolesChanged`: the user, the role ids added and removed, who changed them), and every membership change as before.

**What a UserAdmin can do, and how to watch it.** Creating external users and making unprotected users members of any project is the UserAdmin's duty. An external user signs in through the identity provider as the subject (`ExternalId`) it was created with, so the identity provider and the [admission rules](#single-sign-on-admission) bound what this reaches: a UserAdmin who also controls a second account at the identity provider can pre-register it and add it to a restricted project. Beacon records no creator per user and applies no four-eyes rule to that; it is accepted by design and should be watched: alert on events **7114** (`UserCreated`), **7101** (`MemberAdded`) and **7113** (`UserRolesChanged`) whose actor holds UserAdmin, and review them. A permission an identity provider maps is not stored, so a user who holds one only through an app role is not protected.

**Separation of duties.** With `Beacon:Authorization:SeparationOfDuties:Enabled`, roles that would give one user both permissions of a conflicting pair — Approver + SecurityAdmin, Auditor + SecurityAdmin, Auditor + UserAdmin, UserAdmin + Approver, UserAdmin + MigrationOperator — are refused with `400`, whoever assigns them; a user who holds such a pair anyway holds neither permission of it (see [Separation of duties](/features/authorization/#separation-of-duties)).

**From the identity provider.** With `Beacon:Authorization:IdpRoles:Enabled`, app roles mapped in `IdpRoles:Map` add their permission to an SSO session when the user signs in. They are not stored: they count for `IdpRoles:ClaimLifetimeMinutes` (default 60) after the sign-in, do not show on the Users page, and an API key never carries them.

**Default roles.** The role SSO and MCP user provisioning give new users (`DefaultRoleName`) may not be the Admin role or a functional role; startup fails otherwise.


### Project roles and membership

The global role caps what a user may do; [project access](/features/authorization/#project-access) decides where. Every project is **open** until an Admin **restricts** it: an open project is used by everyone with a role, a restricted one only by its members and Admins. A member holds one project role:

| Project role | In the project |
|---|---|
| **Viewer** | reads its resources and runs its saved SQL |
| **Editor** | also works ad hoc (draft SQL, AI) and creates and changes resources; deletes only what they own |
| **Project admin** | also deletes any of its resources, manages its members and links data sources into it |

A member's effective role is the lower of the project role and their global role, so a global Viewer is a Viewer everywhere. Project admins, UserAdmins and Admins manage members on the project page's **Members** tab (a UserAdmin never their own membership, nor a protected user's: the tab does not offer those changes, and each member's `canChange` and each search result's `canAdd` says whether the caller may make them); only an Admin or a UserAdmin restricts a project, and only an Admin opens a restricted one. A disabled or archived user keeps their membership rows, which grant nothing.

- **Searching for people.** The Members tab searches **display names** only (case-insensitive, at least two characters). Users without a display name are not found there, and are shown as "User {id}"; user names, e-mail addresses and external ids are never shown to other users. Set a display name for everyone who should be added to projects.
- **The last project admin.** A project keeps an effective project admin: an enabled, non-archived member with the project admin role whose global role is Editor or above. Removing or demoting the last one is refused unless an Admin does it.
- **Who has a project role.** With user management on, every caller that uses Beacon is a stored user: a session or bearer token without a stored user reaches no project, and the session's `Admin` claim does not make it an Admin. Global roles keep capping project roles when `Beacon:Authorization:Enabled` is `false`.

**Membership managed by the host.** Memberships live in Beacon's database while user management is on. A host can supply its own `IProjectMembershipSource` (for example from IdP groups); Beacon's membership routes then answer `409` ("managed by the host") and the UI hides the Members tab. Without user management and without such a source, membership is unavailable: the membership routes answer `409` ("not available in this deployment"), a restricted project is Admin-only, and a project cannot be restricted. The project page's **Access** card tells an Admin which case applies. See [Host seams](/features/authorization/#host-seams).

## Authentication Providers

Beacon supports multiple authentication strategies through pluggable providers.

### DatabaseAuthenticationProvider

Authenticates users against Beacon's internal user table with hashed passwords.

```csharp
options.Authentication.EnableLoginForm = true;
options.AddAuthenticationProvider<DatabaseAuthenticationProvider>();
```

Best for: standalone deployments without an external identity provider.

### JwtExternalApiAuthenticationProvider

Authenticates users via an external JWT/OAuth identity provider: the login form posts the credentials to your login endpoint and validates the token it returns.

```csharp
builder.Services.AddBeaconJwtAuthentication(jwt =>
{
    jwt.ExternalLoginEndpoint = "https://your-idp.example/api/auth/login";
    jwt.EnableBearerAuthentication = true; // optional: also accept Authorization: Bearer on the REST API
    jwt.Validation.JwksEndpoint = "https://your-idp.example/.well-known/jwks.json"; // or Validation.SigningKey (HS256)
    jwt.Validation.ValidIssuer = "https://your-idp.example";  // required
    jwt.Validation.ValidAudience = "beacon";                   // required
    jwt.ClaimsMapping.UserIdClaim = "sub";
    jwt.AccessTokenClaim = "scope"; // only for a provider that does not set typ: at+jwt (see below)
});
```

The options are configured in code (there is no `Beacon:Authentication:Jwt` configuration section). With `ExternalLoginEndpoint` or `EnableBearerAuthentication` set, the host **does not start** without a signing key or JWKS endpoint, at least one issuer (`ValidIssuer`/`ValidIssuers`) and at least one audience (`ValidAudience`/`ValidAudiences`); `ValidateIssuer`, `ValidateAudience` and `ValidateLifetime` must stay on, and `ClockSkew` may be at most five minutes.

The token the login API returns passes the same screen as a REST bearer token, whether or not SSO is enabled:

- It must be an **access token**, shown by positive evidence. A Microsoft Entra ID token (issuer `login.microsoftonline.com` or `sts.windows.net`) must name the client it was issued to (`azp` or `appid`) and carry `scp` or `roles`. A token from any other issuer must have the JWT header `typ: at+jwt` (RFC 9068), or carry the claim named by `AccessTokenClaim` (for example `scope` or `scp`; not set by default). A token with `nonce`, `at_hash` or `c_hash` is an ID token and is refused. `roles` alone never counts as evidence.
- When the SSO authority issued it, it must pass the [SSO admission rules](#single-sign-on-admission).

`AccessTokenClaim` must name a claim your provider puts in access tokens and never in ID tokens; a claim any ID token can carry (`sub`, `aud`, `nonce`, `azp`, `roles`, …) stops the host at startup.

A token proves identity only. Sign-in (and a REST bearer request) succeeds only for an **existing, enabled, external Beacon user** the token names, and the session carries that user's **Beacon roles** — roles in the token are ignored. This needs user management: without an `IUserManagementService`, every login-form JWT sign-in and every REST bearer token is refused. External users must be pre-registered (or provisioned by SSO). The login form answers every failure — wrong credentials, an unreachable login API, an unknown or disabled user — with the same "Invalid username or password." message, and the log never carries the user name.

Best for: organizations with an existing identity provider (Keycloak, Auth0, Azure AD).

### HybridAuthenticationProvider

Tries internal database authentication first, then falls back to external JWT authentication.

```csharp
options.AddAuthenticationProvider<HybridAuthenticationProvider>();
```

Best for: organizations transitioning from internal to external auth, or supporting both admin and regular users.

## Implementing User Management for Consumers

This section explains how to integrate Beacon's user management into your own application.

### Option 1: Use Built-in User Management (Recommended)

Enable Beacon's built-in user management and let it handle everything:

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options =>
{
    options.AddBeaconScheduler<BeaconScheduler>();

    options.Authorization.Enabled = true;
    options.Authentication.EnableLoginForm = true;
    options.AddAuthenticationProvider<DatabaseAuthenticationProvider>();

    options.UserManagement = new UserManagementOptions
    {
        Enabled = true,
        AllowInternalUsers = true,
        MinimumPasswordLength = 8,
        RequirePasswordComplexity = true
    };
})
.UsePostgreSql(connectionString, "beacon");

builder.Services.AddBeaconUI();
builder.Services.AddBeaconCookieAuthentication();
```

This gives you:

- Login form at the React route `/login`
- First-run setup wizard
- User management UI at `/users`
- Cookie-based sessions (24h default, 30 days with "Remember Me")
- Password hashing with salt

### Option 2: External Identity Provider (JWT/OAuth)

Integrate with your existing identity provider:

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options =>
{
    options.Authorization.Enabled = true;
    options.Authentication.EnableLoginForm = true;
    options.AddAuthenticationProvider<JwtExternalApiAuthenticationProvider>();

    options.UserManagement = new UserManagementOptions
    {
        Enabled = true,
        AllowInternalUsers = false  // External users only
    };
})
.UsePostgreSql(connectionString, "beacon");
```

**Pre-register external users** so they get Beacon roles:

```csharp
// In your user provisioning code
var userService = serviceProvider.GetRequiredService<IUserManagementService>();

await userService.CreateUserAsync(new CreateUserRequest
{
    ExternalId = "jwt-sub-claim-value",  // Maps to JWT 'sub' claim
    UserName = "john.doe",
    Email = "john@example.com",
    DisplayName = "John Doe",
    IsInternalUser = false,
    RoleIds = new[] { editorRoleId }
});
```

When an external user authenticates via JWT (login form or REST bearer token), Beacon looks the user up, in one query, by the token subject together with the token issuer:

- The subject is the configured `ClaimsMapping.UserIdClaim` (`sub` by default), read by its exact name. When a custom `UserIdClaim` is configured and the token lacks it, the token is refused — Beacon never falls back to `sub`.
- A user stored under the token issuer (as SSO sign-in stores users) matches. A user pre-registered **without** an identity provider matches on the subject alone only when exactly **one** issuer is configured; with several issuers, set the user's identity provider to the issuer.
- Internal (password) users and super admins never match a token, whatever their `ExternalId`.
- A disabled or archived match refuses the token, even when another row would match.
- Users created by MCP caller provisioning (keyed on the Entra `oid`) get no REST session from a bearer token.

SSO-provisioned users match a bearer token only when the access token's `iss` and `sub` equal the ID token's. For Entra this means a v2.0 access token issued to the same application as the sign-in (the `sub` claim is pairwise per application). Only their Beacon roles apply.

### Option 3: Custom Authentication Provider

Build your own authentication logic:

```csharp
public class MyAuthenticationProvider : IBeaconAuthenticationProvider
{
    private readonly IMyAuthService _authService;

    public MyAuthenticationProvider(IMyAuthService authService)
    {
        _authService = authService;
    }

    public async Task<AuthenticationResult> AuthenticateAsync(
        string username, string password, CancellationToken ct)
    {
        // Call your auth system (LDAP, Active Directory, API, etc.)
        var result = await _authService.ValidateCredentialsAsync(username, password);

        if (!result.Success)
            return AuthenticationResult.Failed(result.ErrorMessage);

        return AuthenticationResult.Succeeded(new AuthenticatedUser
        {
            UserId = result.User.Id,
            UserName = result.User.Username,
            Email = result.User.Email,
            DisplayName = result.User.FullName,
            Roles = result.User.Roles
        });
    }

    public Task<bool> ValidateSessionAsync(string userId, CancellationToken ct)
        => Task.FromResult(true);

    public Task SignOutAsync(CancellationToken ct)
        => Task.CompletedTask;
}

// Register it
options.AddAuthenticationProvider<MyAuthenticationProvider>();
```

### Option 4: Custom Authorization Only (No User Management)

If you already handle authentication and just need Beacon to check permissions:

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options =>
{
    options.Authorization.Enabled = true;
    options.AddAuthorizationProvider<RoleBasedAuthorizationProvider>();
    // No user management, no login form
})
.UsePostgreSql(connectionString, "beacon");

// Add your own claims transformer
builder.Services.AddScoped<IClaimsTransformation, MyClaimsTransformation>();

// Plug in your own authentication, then map the React shell + REST API
app.UseAuthentication();
app.UseAuthorization();
app.UseBeaconUI();
```

## User Entity Model

The `BeaconUser` entity stores user information:

| Field | Type | Description |
|-------|------|-------------|
| `ExternalId` | string | GUID for internal users, JWT `sub` for external |
| `UserName` | string | Unique username |
| `Email` | string? | Email address |
| `DisplayName` | string? | Friendly display name |
| `IsInternalUser` | bool | True if password stored in Beacon |
| `PasswordHash` | string? | Hashed password (null for external users) |
| `IsSuperAdmin` | bool | Bypass all authorization checks |
| `IsEnabled` | bool | Account enabled/disabled |
| `LastLoginAt` | DateTime? | Last successful sign-in with the user's own identity (password, SSO, an identity-provider token; never an API key) |
| `SecurityStamp` | string? | Changes whenever every session of the user must end (see [Sign-in sessions](#sign-in-sessions)); empty until it first changes |
| `MustChangePassword` | bool | The user must choose a new password before signing in: set when an Admin sets the password, cleared when the user sets their own (see [Passwords set by an administrator](#passwords-set-by-an-administrator)); false for existing users |
| `UserRoles` | list | Assigned roles, each with when it was assigned (`AssignedAt`) and who assigned it (`AssignedByUserId`, see [Who assigned a role](#who-assigned-a-role)) |

## Cookie Authentication Options

Configure session behavior:

```csharp
builder.Services.AddBeaconCookieAuthentication(options =>
{
    options.CookieExpirationHours = 24;       // Normal session duration
    options.RememberMeExpirationDays = 30;    // "Remember Me" duration
});
```

:::note
The `Beacon.Auth` cookie is configured with `HttpOnly = true`, `SameSite = Lax`, and `SecurePolicy = SameAsRequest`.
:::

## Sign-in Sessions

A `Beacon.Auth` session is checked on the server, not only by its cookie:

- **Revalidation.** With user management on, a session is checked against the user store at most every `Beacon:Authentication:Session:RevalidateMinutes` (default `5`; `0` checks every request). The user must still exist, be enabled and not archived, and carry the security stamp the session was signed in with; otherwise the session is signed out (its cookie is deleted, the API answers `401`, and the user signs in again). The session's roles are replaced by the roles the user holds in the store at that check (and, while identity-provider role mapping is on, the roles that sign-in mapped, for `IdpRoles:ClaimLifetimeMinutes` after it). The sign-in itself reads the user once for both the stamp and the roles the session starts with, and a user disabled or archived by then gets no session. A password sign-in also gets no session when the user's security stamp changed while the password was being checked (an Admin reset the password, or the user was signed out everywhere): it answers like a wrong password, and the user signs in again. Changing your own password likewise answers `401` when another change to your sessions lands at the same moment. A session naming no stored user is logged at Warning with a fingerprint of the id it presented. A host without Beacon user management keeps its own principals: only the absolute lifetime applies to them.
- **Security stamp.** Each user has a security stamp that every one of their sessions carries. It changes when their password is changed or reset, when they are disabled, re-enabled or archived, when their roles change, when their user name or e-mail address changes, and on **sign out everywhere**. A changed stamp ends every session of the user at its next revalidation; until then, a request of such a session already acts as nobody for project access (the session carries the stamp it was signed in with). Changing your own password keeps the session you changed it from (it is signed in again with the new stamp) and ends the others.
- **Sign out everywhere.** An Admin, or a UserAdmin for a user they may change, ends every session of a user with **Sign out everywhere** in the user dialog (`POST /beacon/api/users/{id}/sign-out-everywhere`). A user ends all of their own sessions from the account menu (**Sign out everywhere**, `POST /beacon/api/auth/logout?everywhere=true`); the menu offers it only when `/auth/me` reports `signOutEverywhereEnabled` (a signed-in session, user management on). Both go through one command and one log entry. When the sessions cannot be signed out (user management is off, or the caller is an API key or MCP caller) nothing changes and the answer is `409`: the account menu says so and keeps you signed in. A plain sign-out ends the current session only. Neither affects the user's API keys.
- **Absolute lifetime.** A session ends `Beacon:Authentication:Session:AbsoluteLifetimeHours` (default `12`) after its sign-in, however often sliding expiration or "remember me" renews its cookie; the cookie itself never outlives that. A realtime (SignalR) connection is checked against the user store when it is opened and closed when the session's next check would be due (`RevalidateMinutes`, at least one minute), never later than the absolute lifetime; the shell then reconnects through a new check. Realtime requests never renew the session cookie.

```json
{
  "Beacon": {
    "Authentication": {
      "Session": {
        "RevalidateMinutes": 5,
        "AbsoluteLifetimeHours": 12
      }
    }
  }
}
```

Both are validated at startup: `RevalidateMinutes` between 0 and 1440, `AbsoluteLifetimeHours` between 1 and 8760. Every session a check ends is logged with the user's id and the reason (`SessionEnded`; a session past its absolute lifetime usually just presents an expired cookie, which is refused without an entry), every sign-out everywhere with the user's id and who asked (`UserSessionsRevoked`).

A host **without** Beacon user management keeps its own principals: Beacon does not call `IBeaconAuthenticationProvider.ValidateSessionAsync` on its sessions (a provider written before sessions were revalidated may not expect it), so only the absolute lifetime applies. Such a host revalidates in its own cookie events or claims transformation.

The cookie itself is protected by ASP.NET Core Data Protection, which belongs to the host. See [Configuration → Data Protection](/getting-started/configuration/#data-protection-the-keys-behind-the-session-cookie) for the required setup outside Development and for what to do after restoring a Beacon database into another environment (give every user a new security stamp, give the copy its own key ring).

## Local Password Sign-in

Password sign-in through the login form (`POST /beacon/api/auth/login`) is **on** by default, for compatibility. A deployment where people sign in through single sign-on should turn it off, so every sign-in passes the identity provider's multi-factor authentication, conditional access and offboarding, and keep one emergency account:

```json
{
  "Beacon": {
    "UserManagement": {
      "LocalLogin": {
        "Enabled": false,
        "BreakGlassUserNames": [ "superadmin" ]
      }
    }
  }
}
```

With `Enabled: false`:

- `POST /beacon/api/auth/login` is **not mapped** — unless a break-glass account is listed. Then every name gets the same password check (so the answer and its timing tell no break-glass account from another), and whichever provider authenticated the password (Beacon's, the external JWT login, a host's provider, or `IBeaconAuthenticationService.SignInAsync`), only a stored internal user, enabled and not archived, whose own stored user name is on the list signs in. Any other account gets the generic "Invalid username or password.", and the attempt is logged (`PasswordSignInRefused`, 7120, without the name typed).
- `DatabaseAuthenticationProvider` and `HybridAuthenticationProvider` refuse every account but a break-glass one (the hybrid provider does not fall back to its external login endpoint), and `JwtExternalApiAuthenticationProvider` refuses every login without calling its external API.
- The login page shows no password form; it shows only the single sign-on button (or, without single sign-on, a note that password sign-in is off). A break-glass account opens the form at `/login?local=1`.
- A break-glass sign-in is logged at **Warning** with the user's id (`BreakGlassSignIn`, 7119). Treat every one as an incident: the identity provider was bypassed.

`BreakGlassUserNames` holds up to 3 user names, each compared exactly (case included) with the user name the account is stored with; its holder may sign in with that name or the account's e-mail address. A blank entry or a longer list fails startup; so does turning local login off with single sign-on off, no external login page and no break-glass account. A listed name that matches no enabled internal user is warned about at startup by its position in the list. The first-run super admin is a password account, so list it (or another internal Admin) before turning local login off. Give the break-glass account a long random password kept in a vault, and review its use.

## Passwords Set by an Administrator

Whoever sets a password can sign in with it. So when an Admin creates an internal user, or resets a password, the user must replace it at their first sign-in:

- The right user name and password then answer `401` with `passwordChangeRequired: true` (no session); a wrong password answers as before, so only the holder of the right password learns that a change is pending.
- The login page asks for a new password and sends the same sign-in again with `newPassword`. When the password rules accept it (and it differs from the one the Admin set), it becomes the user's password, the requirement is cleared, the user's security stamp changes and they are signed in. A refused new password answers `400` with the reason and `passwordChangeRequired: true`.
- Changing your own password (`POST /beacon/api/user-settings/change-password`) also clears the requirement. The first-run super admin chooses their own password and is not affected, nor are users that existed before this version.

## Who Assigned a Role

Every row of `user_roles` records when the role was assigned (`AssignedAt`) and who assigned it (`AssignedByUserId`, the assigner's external id). It is recorded when an Admin or UserAdmin creates an internal or an external user, changes a user's roles (`PUT /beacon/api/users/{id}/roles`), and by `IUserManagementService.AssignRoleAsync`. It is empty (null) where Beacon grants the role itself, not a person: the configured default role at a user's first single sign-on or MCP sign-in, and the super admin's Admin role at first-run setup (granted through the setup token). Assignments made before this version have none either. Roles mapped from identity-provider app roles (`Beacon:Authorization:IdpRoles`) are never stored, so they have no row; the sign-in that mapped them is logged.

## Database Schema

User management creates these tables in your Beacon schema:

- **`users`** - User accounts (internal and external)
- **`roles`** - System roles (Admin, Editor, Viewer, and the functional roles at level 0)
- **`user_roles`** - Many-to-many join with audit fields (assigned_by_user_id, assigned_at)

These tables are created automatically by the EF Core migration `20260206112218_UserManagement`. The migration `AddUserSecurityStamp` adds the users' security stamp, a nullable column existing users leave empty, and `must_change_password`, a boolean existing users leave `false`.

## API Endpoints

### Authentication

These are part of Beacon's REST minimal-API surface under `/beacon/api/*` (OpenAPI document at `/openapi/v1.json`).

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/beacon/api/auth/login` | POST | Authenticate with username/password. `401` with `passwordChangeRequired: true` when an Admin set the password: send the same sign-in with `newPassword` (`400` with the reason when the new one is refused). Not mapped while [local login](#local-password-sign-in) is off and no break-glass account is listed |
| `/beacon/api/auth/logout` | POST | Clear session cookie; requires the antiforgery token (`X-XSRF-TOKEN` header), `400` without it (there is no `GET` sign-out). `?everywhere=true` also ends every other session of the signed-in user; `409`, with nothing signed out, when that cannot be done |

### Users

For an Admin or a UserAdmin (see [Functional roles](#functional-roles) for what a UserAdmin cannot do).

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/beacon/api/users` | GET | The user directory |
| `/beacon/api/users/roles` | GET | Every role, with the permission each functional role grants |
| `/beacon/api/users/internal` | POST | Create an internal (password) user with roles; an Admin's alone (`403` for a UserAdmin) |
| `/beacon/api/users/external` | POST | Create an external user with roles |
| `/beacon/api/users/{id}` | PUT | Change a user's name, e-mail, display name or enabled state; the user name and e-mail address are an Admin's to change (`403` for a UserAdmin who sends others than the stored ones) |
| `/beacon/api/users/{id}/toggle-enabled` | POST | Enable or disable a user |
| `/beacon/api/users/{id}/roles` | PUT | Replace a user's roles (ends the user's sessions when they change) |
| `/beacon/api/users/{id}/sign-out-everywhere` | POST | End every session of the user (`204`; `404` for an unknown user; `409` when user management is off); their API keys keep working |

### First-Run Setup

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/beacon/api/setup/status` | GET | Check if first-run setup is needed |
| `/beacon/api/setup/superadmin` | POST | Create super admin (first run only; requires `setupToken`, `403` when it is missing or wrong) |
| `/beacon/api/setup/roles` | GET | List available roles |

## Troubleshooting

### Login Page Not Showing

**Problem:** Navigating to the app doesn't show a login form.

**Solution:**

1. Ensure `EnableLoginForm = true` in authentication options
2. Ensure `AddBeaconCookieAuthentication()` is registered
3. Confirm the React app is being served (`AddBeaconUI()` / `UseBeaconUI()`); the login form lives at the React route `/login`

### External Users Can't Log In

**Problem:** JWT users get "unauthorized" errors.

**Solution:**

1. Pre-register the user with their `ExternalId` matching the JWT `sub` claim, and make sure the account is enabled
2. Verify JWT validation settings (issuer, audience, signing key) — both an issuer and an audience are required
3. Check claims mapping matches your JWT token structure
4. Bearer failures are deliberately generic (`invalid_token`); the server logs a reason code (for example `NoBeaconUser`, `DisabledUser`, `IdToken`, `NotAccessToken`, `NotAdmitted`) with the tenant, the issuer and a hash of the subject — at Warning at most once a minute, otherwise at Debug. Login-form JWT refusals log the same reason codes at Information
5. `NotAccessToken` from an identity provider other than Entra: the token has neither the header `typ: at+jwt` nor the configured `AccessTokenClaim` — set `AccessTokenClaim` to a claim the provider puts in access tokens only (for example `scope`)

### Roles Not Applied

**Problem:** Users are authenticated but permissions don't work.

**Solution:**

1. Verify the user has roles assigned in the Users management page
2. Check that `DatabaseAuthorizationProvider` is registered (not just `RoleBasedAuthorizationProvider`)
3. Ensure authorization is enabled: `options.Authorization.Enabled = true`

### First-Run Setup Doesn't Appear

**Problem:** App shows login form instead of setup wizard.

**Solution:** The setup wizard only appears while no super admin has ever existed in the database — an archived (deleted) super admin counts, so archiving it does not reopen setup. Other users never close setup: an installation whose users were all created some other way still shows the setup wizard until a super admin is created with the setup token.

### Signed In, but "No Access Yet"

**Problem:** An SSO user signs in and sees "Your account has no access yet".

**Solution:** SSO provisions new users **without a role** unless `Beacon:Authentication:Oidc:DefaultRoleName` is set. Assign a role on the Users page; the user then selects **Check again** (or returns to the browser tab) to get in.

## Single Sign-On Admission

When OIDC SSO is enabled, Beacon admits only subjects that pass the configured rules — `AllowedTenants` (or an explicit `AllowAnyTenant`), `BlockGuests` (on by default) and the optional `RequiredRoles`/`RequiredGroups` — and provisions nothing for anyone else. A bearer token on the REST API, and a token returned to the login form, that the SSO authority issued passes the same rules before it is bound to a user. A refused sign-in is logged at Warning with the reason, the tenant, the issuer and a hash of the subject — never the subject, an e-mail or other claim values. With `RequireMfa: true` (off by default) an interactive SSO sign-in must also show multi-factor authentication in its ID token — `amr` containing `mfa`, or an `acr` listed in `AcceptedAcrValues` — or it is refused (reason `MfaNotPerformed`) and the login page says that multi-factor authentication is required. With Microsoft Entra ID, request `amr` as an optional ID-token claim in the app registration. `RequireMfa` covers the interactive SSO sign-in only: bearer tokens (REST and `/beacon/mcp`), API keys, the login-form JWT flow and password sign-in (including break-glass accounts) are not checked. See [Configuration → OIDC / SSO](/getting-started/configuration/#optional-oidc--sso) for the options.

## Upgrading from 4.5

Hosts upgrading from 4.5 must act on the following:

- **First-run setup needs a token.** Configure `Beacon:UserManagement:SetupToken` (at least 32 characters, required when several replicas serve the setup page), or take the generated token from the process console at startup — it is no longer written to the log. Automation that calls `POST /beacon/api/setup/superadmin` must send `setupToken`. Validation and state errors now come back as RFC 7807 problem details (`400`, `403` for a wrong token, `409` while a concurrent setup is still running).
- **First run means "no super admin has ever existed".** An installation whose users were created some other way (without a super admin) shows the setup wizard again until a super admin is created; until then SSO and MCP caller provisioning create no user. An archived super admin still counts.
- **Setup endpoints need user management** (`EnableUserManagement()`), are rate-limited per client address (configure forwarded headers behind a proxy), and `GET /beacon/api/setup/roles` needs a signed-in user once setup is complete.
- **SSO requires an admission decision.** With `Beacon:Authentication:Oidc:Enabled`, set `AllowedTenants` (your tenant id) or `AllowAnyTenant: true`; otherwise the host does not start. Guests are blocked by default (`BlockGuests: false` to allow them).
- **SSO users get no role by default.** `DefaultRoleName` no longer defaults to `Viewer`. Set it explicitly to keep the old behaviour, or assign roles to new users.
- **Review the users SSO already provisioned.** Earlier versions gave every new SSO user the `Viewer` role and admitted guests and other tenants. Disable, archive or remove the role of accounts that the new admission rules would refuse; a bearer token the SSO authority issued must now pass those rules too.
- **Archived SSO users are refused** at sign-in instead of being provisioned again. Un-archive a user to restore access.
- **JWT validation is mandatory and strict.** With bearer authentication or an external login endpoint, configure at least one issuer and one audience, leave issuer, audience and lifetime validation on, and keep `ClockSkew` at five minutes or less, or the host does not start.
- **Bearer and login-form tokens must prove they are access tokens.** Outside `/beacon/mcp`, and for the token the external login endpoint returns, Beacon no longer infers the token type from `roles`, `scp`/`scope` or the audience, and SSO no longer has to be enabled for the check. An Entra token needs `azp` or `appid` plus `scp` or `roles`; a token from any other issuer needs the header `typ: at+jwt` or the claim named by the new `AccessTokenClaim` option (default: none). Tokens with `nonce`, `at_hash` or `c_hash` are refused. If your provider's access tokens carry neither, set `AccessTokenClaim` (for example `"scope"`), or those tokens are refused (reason `NotAccessToken`).
- **Login-form JWTs are screened like bearer tokens.** The token the external login endpoint returns must be an access token as above and, when the SSO authority issued it, pass the SSO admission rules (tenant, guests, required roles or groups) — a login API that returns an ID token no longer signs anyone in. `JwtExternalApiAuthenticationProvider` takes an optional `IOptions<OidcAuthenticationOptions>` for the admission rules (resolved from DI automatically).
- **REST bearer tokens need an external Beacon user.** Outside `/beacon/mcp`, a bearer token must name an existing, enabled, non-archived external Beacon user (see [Option 2](#option-2-external-identity-provider-jwtoauth)); roles come from Beacon. Tokens for internal users or super admins, users created by MCP caller provisioning, pre-registered users without an identity provider when several issuers are configured, ID tokens, and tokens with a configured `UserIdClaim` missing are refused. The `401` body is now `{ "error": "invalid_token", "message": "The bearer token was not accepted." }` with `WWW-Authenticate: Bearer error="invalid_token"`, whatever the reason.
- **The login-form JWT provider binds to Beacon users.** `JwtExternalApiAuthenticationProvider` takes an optional `IUserManagementService`; without user management every login-form JWT sign-in fails. Every failure answers "Invalid username or password.".
- **`IUserManagementService` changed** for hosts that implement or call it: `GetOrCreateExternalUserAsync` takes a nullable `defaultRoleName` (null creates a user with no role) and refuses to create users before first-run setup; `GetBearerUserCandidatesAsync` is new; `UpdateLastLoginAsync` takes the Beacon user id (`int`) instead of the external id.
- **No `GET` sign-out, and sign-out needs the antiforgery token.** `GET /beacon/api/auth/signout` was removed (use `POST /beacon/api/auth/logout`); the OIDC front-channel sign-out path is served only with `EnableFrontChannelLogout: true`. `POST /beacon/api/auth/logout` now validates the antiforgery token for every caller, signed in or not, and answers `400` without signing anything out or emitting any cookie when it is missing or invalid. The SPA sends it (`X-XSRF-TOKEN`) and retries once with a fresh token; a custom client must call `GET /beacon/api/csrf` first, keep the cookies it sets, and send the returned token in `X-XSRF-TOKEN`. The SPA's `/logout` page asks for a click unless the app itself sent the user there.
- **Return URLs** containing control characters, whitespace or backslashes fall back to the default post-login page.
- **Internal login** matches internal users only and answers every failure (unknown user, wrong password, disabled or archived account) with the same "Invalid username or password." message.
- **Disabling, re-enabling and archiving revoke API keys.** Saving a user as disabled, archiving them, and re-enabling them each revoke every API key of theirs still active, in the same save. A re-enabled user starts without working keys. See [API Keys → When the owner leaves](/features/api-keys/#when-the-owner-leaves).
- **SSO sessions never carry scoped-caller claims.** Sign-in removes the claim types Beacon uses for API keys and MCP callers (`auth_method`, `scope`, `allowed_projects`, `api_key_id`, `api_key_name`, `caller_kind`, `caller_hash`) from what the identity provider sent.
- **Functional roles.** The Admin role's duties can be given separately through six new, optional roles (see [Functional roles](#functional-roles)); existing Admins keep every duty. Roles with these names are created at level 0 when roles are next listed: a role of your own that already has one of these names is left as it is and grants nothing (a warning names its id); rename it, or map the permission to another name. User administration is now open to UserAdmins, under the guards above.
- **Sessions are checked on the server.** After the upgrade (and its migration `AddUserSecurityStamp`, which adds a nullable column), every existing session is checked against the user store at its next request: a session of a missing, disabled or archived user ends, and the session's roles become the stored ones. A session signed in before the upgrade counts its absolute lifetime (`Beacon:Authentication:Session:AbsoluteLifetimeHours`, default 12 hours) from its last renewal, so users signed in longer than that sign in again. "Remember me" no longer keeps a session beyond the absolute lifetime; raise it if you relied on longer sessions. Changing a password, disabling or re-enabling a user, changing their roles, user name or e-mail address now ends their other sessions. See [Sign-in sessions](#sign-in-sessions).
- **Project membership.** Projects can be restricted to their members (see [Project roles and membership](#project-roles-and-membership)); existing projects stay open. For project access, a stored user is an Admin by their current roles, not by the session's claim, a disabled or archived user reaches no project, and a session without a stored user reaches no project while user management is on. Member search matches display names only. See [Authorization → Upgrading from 4.5: project access](/features/authorization/#upgrading-from-45-project-access).
- **The encryption key is checked in every environment.** A missing or short `Beacon:EncryptionKey`, a passphrase, the sample placeholder, a key printed in the documentation, or a `${...}` value stops the host. Set a new key and list the old one in `Beacon:PreviousEncryptionKeys` to keep reading stored values (see [Configuration → Replacing the Key](/getting-started/configuration/#replacing-the-key)).
- **Data Protection is the host's.** `AddBeaconCookieAuthentication` no longer keeps the key ring in the Beacon database or names the application `Beacon`. Call `AddBeaconDataProtection` to keep Beacon's storage (outside Development with `Beacon:DataProtection:ApplicationName` and `ProtectKeysWith*`), or configure your own; outside Development a key ring persisted without key encryption stops the host. Everyone signs in again once. See [Configuration → Data Protection](/getting-started/configuration/#data-protection-the-keys-behind-the-session-cookie).
- **Local login stays on** by default (`Beacon:UserManagement:LocalLogin:Enabled: true`). With SSO, turn it off and list a break-glass account; see [Local password sign-in](#local-password-sign-in). Break-glass names match the stored user name exactly, case included.
- **Passwords an Admin sets must be replaced at first sign-in.** New internal users and reset passwords are flagged (the migration `AddUserSecurityStamp` also adds `must_change_password`, `false` for existing users). A custom client of `POST /beacon/api/auth/login` must handle `passwordChangeRequired` and send `newPassword`.
- **Role provenance.** Creating a user records the creator as who assigned each role (`AssignedByUserId`). `CreateInternalUserRequest` and `CreateExternalUserRequest` gain `AssignedByUserId`.
- **`IUserManagementService` gains `CompleteRequiredPasswordChangeAsync`** as a default interface member (an implementation recompiles unchanged; without an override it answers the generic sign-in failure); `AuthenticateInternalUserAsync` answers `PasswordChangeRequired` (a new `AuthenticationResult` property) for a flagged user and sets `BeaconUserId`; `HybridAuthenticationProvider` takes an optional `BeaconConfiguration` (without it, it does not fall back to its external login); `LoginRequest` gains `NewPassword` and `LoginResponse` `PasswordChangeRequired` (see [Authorization → Upgrading: sign-in sessions](/features/authorization/#upgrading-sign-in-sessions) for the full public API list).

## See Also

- [Authorization Guide](/features/authorization/) - Permission system details
- [Admin Settings](/features/admin-settings/) - Runtime configuration
- [Configuration Guide](/getting-started/configuration/) - Full configuration reference
