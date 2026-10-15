---
title: User Management
description: "Built-in users and roles: internal password and external identity-provider users, first-run setup, functional roles, sessions, local and single sign-on, and the sign-in routes."
---

Beacon has a built-in user store: internal users who sign in with a password stored in Beacon, external users who sign in through your identity provider, global roles, the six functional roles, and server-checked sessions. It is **off by default**. Who may do what once a user exists is described on [Authorization](/features/authorization/).

## Overview

- **Internal users** sign in with a user name (or e-mail address) and a password Beacon stores as a PBKDF2-SHA256 hash (100,000 iterations, 32-byte salt).
- **External users** sign in through single sign-on (OIDC), a REST bearer token, or a login API that returns a JWT. Their roles always come from Beacon, never from the token.
- **Global roles** Admin, Editor and Viewer; **functional roles** Approver, Auditor, SecurityAdmin, UserAdmin, DataSourceAdmin and MigrationOperator, one administrative duty each, without project data.
- **First-run setup** creates the super admin with a setup token.
- **Sessions** are checked against the user store on the server and end after an absolute lifetime.
- **Soft delete**: archiving a user keeps their history.

## Enabling user management

User management is configured in code, in the `AddBeaconServices` lambda:

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<BeaconScheduler>();
        options.Authorization.Enabled = true;
        options.Authentication.EnableLoginForm = true;
        options.AddAuthenticationProvider<DatabaseAuthenticationProvider>();
        options.EnableUserManagement(x =>
        {
            x.AllowInternalUsers = true;
            x.MinimumPasswordLength = 12;
        });
    })
    .UsePostgreSql();

// The SPA is served at /beacon: the login page is /beacon/login
builder.Services.AddBeaconCookieAuthentication("/beacon");

// Data Protection keys for the session cookie (or configure your own). Outside Development set
// Beacon:DataProtection:ApplicationName and chain ProtectKeysWith*, or the host does not start.
builder.Services.AddBeaconDataProtection(builder.Configuration, builder.Environment);
```

The host also maps the login and setup endpoints (`app.MapLoginEndpoints("/beacon", beaconConfiguration)`, `app.MapSetupEndpoints("/beacon")`) and the login-form and first-run middleware; see [Host wiring](/integrate/host-setup/) and [Sign-in & identity](/integrate/authentication/).

| Option (`options.UserManagement.*`) | Default | What it does |
|---|---|---|
| `Enabled` | `false` | Turns on the user store, the Users page, first-run setup and `DatabaseAuthorizationProvider` |
| `AllowInternalUsers` | `true` | Allows password users |
| `MinimumPasswordLength` | `8` | Shortest password accepted |
| `RequirePasswordComplexity` | `true` | Requires an upper-case letter, a lower-case letter, a digit and a character that is neither |
| `SetupToken` | unset | The first-run setup token; also bound from `Beacon:UserManagement:SetupToken` (see [First-run setup](#first-run-setup)) |
| `LocalLogin` | on | Password sign-in; also bound from `Beacon:UserManagement:LocalLogin` (see [Local password sign-in](#local-password-sign-in)) |

## First-run setup

While no super admin has ever existed in the database (an archived super admin counts), every page redirects to the setup page, `/beacon/setup`:

1. Open Beacon. You are redirected to **setup**.
2. Enter the **setup token**: the value of `Beacon:UserManagement:SetupToken`, or, when none is configured, the token Beacon prints once to the **process console** (standard error) at startup. The generated token is never written to the log; a log Warning only says that it was printed.
3. Create the super admin (user name, e-mail address, password). The system roles and the functional roles are seeded.
4. Sign in with the new account.

A configured `SetupToken` must be at least 32 characters, or the host does not start. Each replica generates its own token, so a deployment with several replicas must configure one. The token is compared in constant time; creation is atomic (a request that loses a race gets `409` and can retry). A wrong or missing token answers `403`.

The setup endpoints are anonymous and throttled per client address, separately from the login form. Until the super admin exists Beacon provisions **no other user**: an SSO sign-in or MCP caller that would create a user is refused. `GET /beacon/api/setup/roles` is anonymous only while setup is open.

## Managing users

An Admin or a UserAdmin manages users on **Users** (`/beacon/users`):

- **Add user** creates an internal or an external user with roles. Internal users are an Admin's to create; a UserAdmin creates external users only. A password an Admin sets must be replaced at first sign-in (see [Passwords set by an administrator](#passwords-set-by-an-administrator)).
- **Edit user** changes the display name, enabled state and roles. The user name and e-mail address are an Admin's to change.
- **Sign out everywhere** ends every session of the user.
- Disabling or archiving a user revokes their [API keys](/features/api-keys/#when-the-owner-leaves); re-enabling revokes any key still active instead of restoring one.

Set a **display name** for everyone who should be added to projects: project member search matches display names only, and other users see people by display name (or "User {id}"), never by user name or e-mail address.

## Roles

| Role | Level | Read | Write (create, edit, run ad hoc) |
|---|---|---|---|
| **Admin** | 3 | Yes | Yes |
| **Editor** | 2 | Yes | Yes |
| **Viewer** | 1 | Yes | No |
| Functional roles | 0 | No | No |

A **super admin** (`IsSuperAdmin`, the first-run account) passes every check.

The global role is a ceiling, not a grant: [project access](/features/authorization/#project-access) decides where a user may use it. Deleting and archiving are the project action **Manage**, which a project admin holds, an Editor holds in an open project, and the recorded owner of a resource holds. A Viewer reads and runs saved SQL only in projects they may use.

Some resources belong to a person: an alert task is worked by its assignee, a data contract is changed by its owner, an AI actor by its creator. Only an Admin or a UserAdmin gives a subscription, data contract or AI actor a new owner.

### Functional roles

Each functional role grants one [functional permission](/features/authorization/#functional-permissions) and **no project data**: it is a level-0 role with neither read nor write. A user who should also use projects needs a Viewer or Editor role beside it. Admins hold every permission.

| Role | The duty |
|---|---|
| **Approver** | Approves and rejects changes under the [approval workflow](/features/approvals/); reads every project's approval requests |
| **Auditor** | Reads the MCP audit, the MCP and admin settings (secrets masked) and every user's API keys |
| **SecurityAdmin** | Changes the MCP and admin settings (not the LLM connection or base URL), recipients, MCP tool publishing and repository tokens |
| **UserAdmin** | Manages users (external users only), their roles, sessions and API keys, resource owners and the members of any project; restricts projects |
| **DataSourceAdmin** | Creates, tests and deletes data sources; links a data source it can read into a project it administers |
| **MigrationOperator** | Runs data migration between data sources it may query and write |

**Assigning them.** `GET /beacon/api/users/roles` lists every role, with the `permission` a functional role grants. Roles are assigned when a user is created or with `PUT /beacon/api/users/{id}/roles` (`{ "roleIds": [1, 4] }`, the complete set; `204`, `404` for an unknown user, `400` for an unknown role or a separation-of-duties conflict). Role names follow `Beacon:Authorization:Roles` (see [Configuration](/getting-started/configuration/#functional-permissions-role-names-identity-provider-app-roles-separation-of-duties)).

**On the Users page** the dialogs list roles in two groups, **Global roles** and **Functional roles**. For a UserAdmin who is not an Admin, **Add user** offers external users only, the user name and e-mail address are read-only, the Admin and functional roles are disabled, and the dialog opens read-only for their own account and for protected users (Admins, super admins and functional role holders).

**What a UserAdmin cannot do** (`403`): assign or remove the Admin role or a functional role; create an internal user or change a user name or e-mail address; change their own account, roles or project membership; touch a protected user's account, roles, memberships or API keys; open a restricted project; make themselves the owner of scheduled work. Every user created logs `UserCreated` (7114) and every role change `UserRolesChanged` (7113), with ids only. See [Authorization → Rules](/features/authorization/#rules) for what a UserAdmin can do and how to watch it.

**From the identity provider.** With `Beacon:Authorization:IdpRoles:Enabled`, app roles mapped in `IdpRoles:Map` add their permission to an SSO session at sign-in. They are not stored, count for `IdpRoles:ClaimLifetimeMinutes` (default 60), do not show on the Users page and never reach an API key.

**Default roles.** The role SSO and MCP provisioning give new users (`DefaultRoleName`) may not be the Admin role or a functional role; startup fails otherwise.

### Project roles and membership

The global role caps what a user may do; [project access](/features/authorization/#project-access) decides where. **New projects are created restricted**, with their creator as project admin, when Beacon manages membership; projects that existed before project access stay open until restricted. An open project is used by everyone with a role; a restricted one only by its members and Admins.

| Project role | In the project |
|---|---|
| **Viewer** | reads its resources and runs its saved SQL |
| **Editor** | also works ad hoc and creates and changes resources; deletes what they own (in an open project, anything) |
| **Project admin** | also deletes any of its resources, manages its members and links data sources |

A global Viewer is a Viewer in every project, whatever their membership; Editors and Admins act with their membership role (and in an open project with the higher of that and their global role). Project admins, UserAdmins and Admins manage members on the project page's **Members** tab. An Admin or a UserAdmin restricts a project; only an Admin opens a restricted one. A disabled or archived user keeps their membership rows, which grant nothing.

Memberships live in Beacon's database while user management is on. A host can supply its own `IProjectMembershipSource` (for example from identity-provider groups); Beacon's membership routes then answer `409` and the UI hides the Members tab. See [Authorization → Host seams](/features/authorization/#host-seams).

## Authentication providers

| Provider | Signs in |
|---|---|
| `DatabaseAuthenticationProvider` | internal users against Beacon's user store |
| `JwtExternalApiAuthenticationProvider` | external users through your login API, which returns a JWT |
| `HybridAuthenticationProvider` | internal users first, then the external login API |

Register one with `options.AddAuthenticationProvider<T>()`. Single sign-on (OIDC) is separate: `builder.Services.AddBeaconOidcAuthentication(builder.Configuration)` with the `Beacon:Authentication:Oidc` section, see [Single sign-on admission](#single-sign-on-admission).

### JwtExternalApiAuthenticationProvider

The login form posts the credentials to your login API and validates the token it returns. JWT options are code-only (there is no configuration section):

```csharp
builder.Services.AddBeaconJwtAuthentication(jwt =>
{
    jwt.ExternalLoginEndpoint = "https://idp.example.com/api/auth/login";
    jwt.EnableBearerAuthentication = true; // also accept Authorization: Bearer on the REST API
    jwt.Validation.JwksEndpoint = "https://idp.example.com/.well-known/jwks.json"; // or Validation.SigningKey
    jwt.Validation.ValidIssuer = "https://idp.example.com";
    jwt.Validation.ValidAudience = "beacon";
    jwt.AccessTokenClaim = "scope"; // only for a provider that does not set typ: at+jwt
});
```

With `ExternalLoginEndpoint` or `EnableBearerAuthentication` set, the host does not start without a signing key or JWKS endpoint, at least one issuer and one audience; issuer, audience and lifetime validation must stay on, and `ClockSkew` may be at most five minutes.

The token must be an **access token**, shown by positive evidence: a Microsoft Entra token names its client (`azp` or `appid`) and carries `scp` or `roles`; a token from any other issuer has the header `typ: at+jwt` or carries the claim named by `AccessTokenClaim`. A token with `nonce`, `at_hash` or `c_hash` is an ID token and is refused. `AccessTokenClaim` may not name a claim ID tokens carry (`sub`, `aud`, `nonce`, `azp`, `roles`, …); startup fails otherwise. When the SSO authority issued the token, it must also pass the [admission rules](#single-sign-on-admission).

A token proves identity only. Sign-in succeeds only for an **existing, enabled, external Beacon user** the token names, and the session carries that user's Beacon roles. Without user management every login-form JWT and REST bearer token is refused. Every failure answers "Invalid username or password." and the log never carries the user name.

### Option 2: External identity provider (JWT/OAuth)

External users must exist in Beacon before they sign in with a token (single sign-on provisions them on first sign-in). Pre-register them on the Users page, with `POST /beacon/api/users/external`, or in code:

```csharp
var users = serviceProvider.GetRequiredService<IUserManagementService>();

await users.CreateExternalUserAsync(new CreateExternalUserRequest
{
    ExternalId = "subject-from-the-token", // the token's sub (or ClaimsMapping.UserIdClaim)
    UserName = "john.doe",
    Email = "john@example.com",
    DisplayName = "John Doe",
    RoleIds = [editorRoleId]
});
```

Beacon finds the user by the token subject together with the token issuer:

- The subject is `ClaimsMapping.UserIdClaim` (`sub` by default). When a custom claim is configured and missing, the token is refused; Beacon never falls back to `sub`.
- A user stored under the token issuer (as SSO stores users) matches. A user pre-registered without an identity provider matches on the subject alone only when exactly one issuer is configured.
- Internal users and super admins never match a token. A disabled or archived match refuses the token.
- Users created by MCP caller provisioning (keyed on the Entra `oid`) get no REST session from a bearer token.

SSO-provisioned users match a bearer token only when its `iss` and `sub` equal the ID token's. For Entra, a v2.0 access token issued to the same application.

### Custom authentication provider

```csharp
public sealed class MyAuthenticationProvider(IMyAuthService auth) : IBeaconAuthenticationProvider
{
    public async Task<AuthenticationResult> AuthenticateAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        var result = await auth.ValidateCredentialsAsync(username, password, cancellationToken);

        if (!result.Success)
        {
            return AuthenticationResult.Failed("Invalid username or password.");
        }

        return AuthenticationResult.Succeeded(new AuthenticatedUser
        {
            UserId = result.User.Id,
            UserName = result.User.UserName,
            Email = result.User.Email,
            DisplayName = result.User.FullName,
            Roles = result.User.Roles
        });
    }

    [Obsolete("Beacon never calls it.")]
    public Task<bool> ValidateSessionAsync(string userId, CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task SignOutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
```

While local login is off, whichever provider authenticated a password, only a break-glass account signs in (see below).

## Single sign-on admission

With OIDC enabled, Beacon admits only subjects that pass its rules, **before** it provisions anything: the tenant allow-list (`AllowedTenants`, or an explicit `AllowAnyTenant`), the guest block (`BlockGuests`, on by default), then the optional `RequiredRoles`/`RequiredGroups`. A bearer token on the REST API, and a token returned to the login form, that the SSO authority issued passes the same rules. A refused sign-in is logged at Warning with the reason, tenant, issuer and a hash of the subject.

With `RequireMfa: true` (off by default) an interactive SSO sign-in must show multi-factor authentication in its ID token (`amr` containing `mfa`, or an `acr` listed in `AcceptedAcrValues`) or it is refused and the login page says so. With Microsoft Entra ID, request `amr` as an optional ID-token claim. `RequireMfa` applies to the interactive SSO sign-in. Bearer tokens (REST and `/beacon/mcp`), API keys, the login-form JWT flow and password sign-in (break-glass included) are authenticated by their own credentials; set the MFA requirement for tokens in the identity provider that issues them.

SSO provisions new users **without a role** unless `DefaultRoleName` is set; they see "Your account has no access yet" until a role is assigned. The full option table is on [Configuration → OIDC / SSO](/getting-started/configuration/#optional-oidc--sso).

## Local password sign-in

Password sign-in through the login form (`POST /beacon/api/auth/login`) is **on** by default. A deployment where people sign in through single sign-on should turn it off, so every sign-in passes the identity provider's MFA, conditional access and offboarding, and keep one emergency account:

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

- `POST /beacon/api/auth/login` is **not mapped**, unless a break-glass account is listed. Then every name gets the same password check (so timing reveals nothing), and only a stored, enabled, internal user whose stored user name is on the list signs in, whichever provider authenticated the password. Other accounts get the generic answer and the attempt logs `PasswordSignInRefused` (7120), without the name typed.
- The login page shows only the single sign-on button. A break-glass account opens the password form at `/beacon/login?local=1`.
- A break-glass sign-in logs `BreakGlassSignIn` (7119) at Warning. Treat every one as an incident.

`BreakGlassUserNames` holds up to 3 names, each compared exactly (case included) with the stored user name; the holder may type that name or the account's e-mail address. A blank entry or a longer list fails startup, and so does turning local login off with no OIDC, no `BeaconApiOptions.ExternalLogin` (a host-owned login page) and no break-glass account. The JWT login API (`ExternalLoginEndpoint`) does not count for this check. The first-run super admin is a password account: list it (or another internal Admin) before turning local login off.

Password sign-in, and the first-run setup, are throttled per client address: 10 login attempts per 60 seconds, then `429` with `Retry-After`. Behind a reverse proxy configure forwarded headers, or the limit applies to the proxy's address.

## Passwords set by an administrator

Whoever sets a password can sign in with it, so a password an Admin sets (a new internal user, or a reset through `IUserManagementService.ResetPasswordAsync` in host code) must be replaced at the user's first sign-in. There is no REST route or UI action that sets another user's password.

- The right user name and password answer `401` with `passwordChangeRequired: true` (no session); a wrong password answers as usual.
- The login page asks for a new password and sends the same sign-in again with `newPassword`. When the rules accept it (and it differs from the one set), it becomes the user's password, the requirement clears, the security stamp changes and the user is signed in. A refused password answers `400` with the reason.
- Changing your own password (`POST /beacon/api/user-settings/change-password`) also clears the requirement.

## Sign-in sessions

A `Beacon.Auth` session is checked on the server, not only by its cookie:

- **Revalidation.** With user management on, a session is checked against the user store at most every `Beacon:Authentication:Session:RevalidateMinutes` (default `5`; `0` checks every request). The user must exist, be enabled, not archived, and carry the security stamp the session signed in with; otherwise the session is signed out (`401` on the API). The session's roles are replaced by the stored roles (plus any identity-provider mapped permissions within their lifetime). A host without user management keeps its own principals: only the absolute lifetime applies.
- **Security stamp.** It changes when a password is changed or reset, when the user is disabled, re-enabled or archived, when their roles, user name or e-mail address change, and on **Sign out everywhere**. Changing your own password keeps the current session and ends the others.
- **Sign out everywhere.** An Admin, or a UserAdmin for a user they may change, ends every session of a user from the user dialog (`POST /beacon/api/users/{id}/sign-out-everywhere`). A user ends all their own sessions from the account menu (`POST /beacon/api/auth/logout?everywhere=true`), offered when `/auth/me` reports `signOutEverywhereEnabled`. When the sessions cannot be signed out (user management off, or an API key or MCP caller) the answer is `409`. API keys are not affected.
- **Absolute lifetime.** A session ends `Beacon:Authentication:Session:AbsoluteLifetimeHours` (default **12**) after sign-in, however often sliding expiration or "remember me" renews its cookie. The requested cookie lifetimes (`options.Authentication.CookieExpirationHours`, default 24, and `RememberMeExpirationDays`, default 30, for "remember me") are capped by it.
- **Realtime.** A SignalR connection is checked when opened and closed when the session's next check is due (at least one minute); the shell reconnects through a new check.

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

Both are validated at startup (`RevalidateMinutes` 0–1440, `AbsoluteLifetimeHours` 1–8760). A session a check ends logs `SessionEnded` (7118); a sign-out everywhere logs `UserSessionsRevoked` (7117).

The cookie is `HttpOnly`, `SameSite=Lax` and `Secure` when the request is HTTPS. It is protected by ASP.NET Core Data Protection, which belongs to the host: see [Configuration → Data Protection](/getting-started/configuration/#data-protection-the-keys-behind-the-session-cookie), including what to do after restoring a Beacon database into another environment.

## Who assigned a role

Every `user_roles` row records when the role was assigned (`AssignedAt`) and who assigned it (`AssignedByUserId`, the assigner's external id): when an Admin or UserAdmin creates a user, changes a user's roles, or calls `IUserManagementService.AssignRoleAsync`. It is empty where Beacon grants the role itself: the default role at a first SSO or MCP sign-in, and the super admin's Admin role at setup. Identity-provider mapped permissions are never stored.

## User entity

| Field | Description |
|---|---|
| `ExternalId` | a GUID for internal users; the token subject for external users |
| `UserName` | unique user name |
| `Email`, `DisplayName` | optional |
| `IsInternalUser` | the password is stored in Beacon |
| `PasswordHash` | PBKDF2-SHA256 hash; null for external users |
| `IsSuperAdmin` | passes every check |
| `IsEnabled` | enabled or disabled |
| `LastLoginAt` | last sign-in with the user's own identity (password, SSO, an identity-provider token; never an API key) |
| `SecurityStamp` | changes whenever every session of the user must end |
| `MustChangePassword` | the user must choose a new password before signing in |
| `UserRoles` | assigned roles, with `AssignedAt` and `AssignedByUserId` |

The `users`, `roles` and `user_roles` tables live in the `beacon` schema and are created by Beacon's migrations.

## Routes

All routes are under `/beacon/api`. The OpenAPI document is at `/openapi/v1.json`; see also [Routes & endpoints](/reference/routes/).

### Sign-in and the current user

| Route | Description |
|---|---|
| `POST /auth/login` | Sign in with user name and password. `401` with `passwordChangeRequired: true` when an Admin set the password (send `newPassword`). Not mapped while local login is off and no break-glass account is listed. Throttled per address |
| `POST /auth/logout` | End the current session; needs the antiforgery token (`X-XSRF-TOKEN`), `400` without it. `?everywhere=true` ends every session of the user |
| `GET /auth/me` | The current user, and flags such as `realtimeEnabled` and `signOutEverywhereEnabled`. Anonymous |
| `GET /auth/permissions` | `canRead`, `canWrite`, `isAdmin` and the functional `permissions` held now |
| `GET /auth/sso` | Whether SSO is offered, its button label and `localLoginEnabled`. Anonymous |
| `GET /auth/sso/challenge` | Starts the SSO sign-in |
| `GET /csrf` | Issues the antiforgery token for a browser session (`400` for callers authenticated by an `Authorization` header) |
| `GET /user-settings` | The caller's own settings |
| `POST /user-settings/change-password` | Change your own password (needs the current one); ends your other sessions |

### Users

For an Admin or a UserAdmin (see [Functional roles](#functional-roles) for a UserAdmin's limits).

| Route | Description |
|---|---|
| `GET /users` | the user directory |
| `GET /users/roles` | every role, with the permission each functional role grants |
| `POST /users/internal` | create an internal user with roles (Admin only) |
| `POST /users/external` | create an external user with roles |
| `PUT /users/{id}` | change a user's display name, e-mail address, user name or enabled state (user name and e-mail address: Admin only) |
| `POST /users/{id}/toggle-enabled` | enable or disable a user |
| `PUT /users/{id}/roles` | replace a user's roles (ends their sessions when they change) |
| `POST /users/{id}/sign-out-everywhere` | end every session of the user (`204`; `409` when user management is off) |

### First-run setup

| Route | Description |
|---|---|
| `GET /setup/status` | whether first-run setup is needed |
| `POST /setup/superadmin` | create the super admin (first run only; needs `setupToken`, `403` when missing or wrong) |
| `GET /setup/roles` | the roles, anonymous only while setup is open |

## Troubleshooting

| Symptom | Check |
|---|---|
| No login page | `EnableLoginForm = true`, `AddBeaconCookieAuthentication("/beacon")`, the login endpoints and middleware mapped; the page is `/beacon/login` |
| Setup page does not appear | it appears only while no super admin has ever existed (an archived one counts); other users never close it |
| Setup token rejected | the configured token, or the one printed to the process console of the replica that served the page |
| External user gets `401` | the user exists, is enabled and is external; issuer and audience configured; the token is an access token. The server logs a reason code (`NoBeaconUser`, `DisabledUser`, `IdToken`, `NotAccessToken`, `NotAdmitted`) with tenant, issuer and a subject hash |
| `NotAccessToken` from a non-Entra provider | set `AccessTokenClaim` to a claim the provider puts in access tokens only |
| SSO user sees "no access yet" | assign a role, or set `DefaultRoleName`; the user then selects **Check again** |
| Login answers `429` | 10 attempts per 60 seconds per address; behind a proxy, configure forwarded headers |

## Upgrading from 4.5

- **First-run setup needs a token** (`Beacon:UserManagement:SetupToken`, at least 32 characters, or the one printed to the process console). Automation calling `POST /beacon/api/setup/superadmin` must send `setupToken`. Setup is "no super admin has ever existed", and until then SSO and MCP provisioning create no user.
- **SSO requires an admission decision.** With OIDC enabled set `AllowedTenants` or `AllowAnyTenant: true`, or the host does not start. Guests are blocked by default. New SSO users get no role unless `DefaultRoleName` is set. Review the users earlier versions provisioned with `Viewer`.
- **JWT validation is mandatory and strict**, tokens must prove they are access tokens (set `AccessTokenClaim` if your provider's access tokens carry neither `typ: at+jwt` nor an Entra client claim), and REST bearer tokens must name an existing, enabled, external Beacon user.
- **Sign-out** is `POST /beacon/api/auth/logout` with the antiforgery token; `GET /beacon/api/auth/signout` was removed, and the OIDC front-channel sign-out path is served only with `EnableFrontChannelLogout: true`.
- **Disabling, re-enabling and archiving revoke API keys.**
- **Sessions are checked on the server** and end 12 hours after sign-in by default; "remember me" no longer outlasts the absolute lifetime. The `AddUserSecurityStamp` migration adds `security_stamp` and `must_change_password`.
- **New projects are restricted**, and a stored user is an Admin by current roles only. See [Authorization → Upgrading](/features/authorization/#upgrading-from-45-project-access).
- **The encryption key is checked in every environment**, and **Data Protection is the host's** (call `AddBeaconDataProtection` or configure your own; everyone signs in again once).
- **Local login stays on** by default; with SSO, turn it off and list a break-glass account.
- **Passwords an Admin sets must be replaced at first sign-in**; a custom login client must handle `passwordChangeRequired` and send `newPassword`.
- **Functional roles** are created when roles are next listed; a role of yours with one of their names grants nothing until renamed.
- **Public API.** `IUserManagementService.GetOrCreateExternalUserAsync` takes a nullable `defaultRoleName`; `CompleteRequiredPasswordChangeAsync` is a new default member; `LoginRequest` gains `NewPassword` and `LoginResponse` `PasswordChangeRequired` (positional records: recompile); `CreateInternalUserRequest`/`CreateExternalUserRequest` gain `AssignedByUserId`.

## See also

- [Authorization](/features/authorization/): project access and functional permissions
- [API keys](/features/api-keys/)
- [Admin settings](/features/admin-settings/)
- [Configuration](/getting-started/configuration/): every sign-in key
