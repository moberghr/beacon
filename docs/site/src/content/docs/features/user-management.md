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
- **Role-Based Access Control** - Admin, Editor, and Viewer roles with level-based permissions
- **First-Run Setup** - Guided wizard to create the initial super admin on first launch
- **User Administration** - Manage users, assign roles, and enable/disable accounts via the UI

### Key Features

- Opt-in by default — user management is disabled unless explicitly enabled
- Flexible authentication — internal passwords, external JWT, or hybrid
- Three predefined roles — Admin, Editor, Viewer with clear permission boundaries
- Super admin — bypasses all authorization checks
- Audit trail — tracks who assigned roles and when
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
5. System roles (Admin, Editor, Viewer) are seeded automatically
6. Log in with your new credentials

A configured `SetupToken` must be at least 32 characters, or the host does not start. The token is compared in constant time, and creation is atomic: concurrent attempts create exactly one super admin (a request that loses the race is told setup is complete, or gets `409` and can retry). Each replica generates its own token, so a multi-replica deployment must configure `SetupToken`. Super-admin creation is logged with the new user's id.

The setup endpoints are rate-limited per client address, in a budget separate from the login form's. Behind a reverse proxy, configure forwarded headers (`UseForwardedHeaders` with your proxy as a known proxy) so the limit applies per client rather than to the proxy's address. `GET /beacon/api/setup/roles` is anonymous only while setup is open.

Until the super admin exists, Beacon provisions **no other user**: an SSO sign-in or an MCP caller that would create a user is refused, with a log line that says first-run setup has not been completed. Users that already exist are unaffected.

### 3. Manage Users

After setup, navigate to **Users** in the React UI (`/users`) to:

- Create new internal users
- Assign roles
- Enable/disable accounts (disabling or archiving an account revokes its [API keys](/features/api-keys/#when-the-owner-leaves), and re-enabling it revokes any key still active instead of restoring one)
- View user details and login history

## Roles and Permissions

Beacon includes three predefined system roles:

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

Besides the role levels, some resources belong to a person: an alert task is worked by its [assignee](/features/tasks/#assignment-watching-snooze-priority), a [data contract](/features/data-quality/) is changed, evaluated or deleted by its owner and an [AI actor](/features/ai-actors/) by its creator; an Admin may do all of these, and only an Admin gives a contract or an actor a new owner. Reading the user directory and the role list, the MCP settings and the data-migration jobs and runs, and setting a project repository's access token, are an Admin's only. These Admin checks read the `ClaimTypes.Role` claim `Admin` of an enabled user (see [Authorization](/features/authorization/#admin-role-claim)).

:::caution[Upgrading]
`GET /users`, `GET /users/roles`, `GET /mcp/settings`, `GET /mcp/projects/{projectId}/settings`, `GET /migrations/jobs`, `GET /migrations/jobs/{id}` and `GET /migrations/executions` now need the Admin role (API keys never pass), as do `PUT /projects/repositories/{id}/token` and a project created with an access token. The sidebar hides User Management, MCP Settings and Data Migration from non-admins. `GET /home/trends` clamps `days` to 1–90. New Admin-only routes set a data contract's owner (`PUT /data-quality/contracts/{id}/owner`) and an AI actor's creator (`PUT /ai-actors/{id}/owner`). A disabled or archived user's session is no Admin for these checks, whatever its claims.
:::

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
| `LastLoginAt` | DateTime? | Last successful login timestamp |
| `UserRoles` | list | Assigned roles with audit info |

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

## Database Schema

User management creates these tables in your Beacon schema:

- **`users`** - User accounts (internal and external)
- **`roles`** - System roles (Admin, Editor, Viewer)
- **`user_roles`** - Many-to-many join with audit fields (assigned_by, assigned_at)

These tables are created automatically by the EF Core migration `20260206112218_UserManagement`.

## API Endpoints

### Authentication

These are part of Beacon's REST minimal-API surface under `/beacon/api/*` (OpenAPI document at `/openapi/v1.json`).

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/beacon/api/auth/login` | POST | Authenticate with username/password |
| `/beacon/api/auth/logout` | POST | Clear session cookie; requires the antiforgery token (`X-XSRF-TOKEN` header), `400` without it (there is no `GET` sign-out) |

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

When OIDC SSO is enabled, Beacon admits only subjects that pass the configured rules — `AllowedTenants` (or an explicit `AllowAnyTenant`), `BlockGuests` (on by default) and the optional `RequiredRoles`/`RequiredGroups` — and provisions nothing for anyone else. A bearer token on the REST API, and a token returned to the login form, that the SSO authority issued passes the same rules before it is bound to a user. A refused sign-in is logged at Warning with the reason, the tenant, the issuer and a hash of the subject — never the subject, an e-mail or other claim values. See [Configuration → OIDC / SSO](/getting-started/configuration/#optional-oidc--sso) for the options.

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

## See Also

- [Authorization Guide](/features/authorization/) - Permission system details
- [Admin Settings](/features/admin-settings/) - Runtime configuration
- [Configuration Guide](/getting-started/configuration/) - Full configuration reference
