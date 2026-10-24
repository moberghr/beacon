---
title: Sign-in & identity
description: "Every way a person or program proves who it is to Beacon: first-run setup, local passwords and break-glass accounts, OIDC/Entra SSO with MFA, bearer JWTs, API keys, sessions and your own sign-in."
---

Beacon recognises five kinds of caller. You can combine them.

| Caller | How it authenticates | Typical use |
|---|---|---|
| Person, password | Beacon's login form against Beacon's user store | Small teams, break-glass |
| Person, SSO | OIDC code flow (Entra ID or any OIDC provider) | Organisations with an identity provider |
| Person, your app | Your host's own authentication scheme | Beacon embedded inside your product |
| Program, API key | `Authorization: Bearer sk-sem_…` | Scripts, CI, MCP clients |
| Program or delegated user, JWT | `Authorization: Bearer <access token>` | Entra-authenticated MCP clients, service-to-service |

## Beacon's user store and first-run setup

`options.EnableUserManagement()` keeps users and roles in Beacon's database. While no administrator exists:

1. Every browser request is redirected to `/beacon/setup`.
2. The setup page asks for a **setup token**: `Beacon:UserManagement:SetupToken` when set (at least 32 characters), otherwise a random token Beacon prints **once to the process console (stderr)** at startup, never to the logger.
3. Whoever presents the token creates the first super admin. `POST /beacon/api/setup/superadmin` is anonymous but throttled per IP; once an administrator exists, setup is closed for good.

:::caution
With several replicas, each one generates its own console token. Set `Beacon:UserManagement:SetupToken` from your secret store for multi-instance deployments, and remove it once setup is done.
:::

Until the first administrator exists, SSO and MCP sign-ins cannot provision other users, and every request except setup, the health check (`/beacon/api/health`) and the MCP discovery documents is redirected to `/beacon/setup`.

### Headless first run (scripts and CI)

The setup token appears on the host's console as `Beacon first-run setup token (enter it on the setup page): <token>`.

```bash
BEACON=https://localhost:7187

curl -s $BEACON/beacon/api/setup/status

curl -s -X POST $BEACON/beacon/api/setup/superadmin \
  -H "Content-Type: application/json" \
  -d '{"setupToken":"<token>","userName":"admin","displayName":"Admin","email":"admin@example.com","password":"<password>","confirmPassword":"<password>"}'

# Sign in (no antiforgery token needed for login); keep the cookie jar for later calls.
curl -s -c jar.txt -X POST $BEACON/beacon/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"<password>","rememberMe":false}'

curl -s -b jar.txt $BEACON/beacon/api/auth/me
```

State-changing calls with the cookie need an antiforgery token: `GET /beacon/api/csrf` returns `{"token":"…"}` and sets a cookie; send the token as `X-XSRF-TOKEN` **with the same cookie jar**:

```bash
TOKEN=$(curl -s -b jar.txt -c jar.txt $BEACON/beacon/api/csrf | jq -r .token)

# A project (you become its ProjectAdmin) …
curl -s -b jar.txt -c jar.txt -X POST $BEACON/beacon/api/projects \
  -H "Content-Type: application/json" -H "X-XSRF-TOKEN: $TOKEN" \
  -d '{"name":"Acme Shop","description":"Shop data"}'
# → {"projectId":1,…}

# … and an Execute-scoped API key for it (the key is returned once).
curl -s -b jar.txt -c jar.txt -X POST $BEACON/beacon/api/api-keys \
  -H "Content-Type: application/json" -H "X-XSRF-TOKEN: $TOKEN" \
  -d '{"name":"ci-mcp","scopes":["Execute"],"allowedProjectIds":[1]}'
# → {"plainTextKey":"sk-sem_…",…}
```

The full request shapes are in the OpenAPI document (`/openapi/v1.json`), once setup is complete.

### Passwords

Passwords are hashed with PBKDF2-SHA256 (600,000 iterations stored in the hash, 32-byte salt; older hashes are upgraded at the next successful sign-in, see [Password hashes](/security/user-management/#password-hashes)). Policy is code-configured: `MinimumPasswordLength` (default 8) and `RequirePasswordComplexity` (default `true`). A password an administrator sets must be changed at first sign-in. Users change their own password under **Settings**; there is no REST route that sets another user's password.

Password sign-in is throttled to **10 attempts per 60 seconds per client IP** (429 with `Retry-After`), and an internal user is locked out for 15 minutes after 5 wrong passwords (the same generic answer, whatever password is sent; break-glass accounts are never locked, and `Beacon:RateLimits:Lockout:Enabled` `false` turns the lockout off). IPv6 clients are limited per `/64` block. Behind a proxy the per-IP limit only works if your host runs `UseForwardedHeaders` with the proxy listed. See [Production checklist](/integrate/production/#reverse-proxy) and [Request limits](/getting-started/configuration/#request-limits).

### Turning password sign-in off (break-glass)

With SSO in place, turn local passwords off and keep up to three named break-glass accounts:

```json
{
  "Beacon": {
    "UserManagement": {
      "LocalLogin": {
        "Enabled": false,
        "BreakGlassUserNames": [ "beacon-breakglass" ]
      }
    }
  }
}
```

- `Enabled: false` unmaps `POST /beacon/api/auth/login` (unless break-glass names exist), hides the password form, and makes every password provider refuse.
- `BreakGlassUserNames`: at most three **stored user names**, matched exactly and case-sensitively, enabled and not archived. Each use logs `BreakGlassSignIn` (event 7119, Warning). Treat every occurrence as an incident.
- Startup fails if local login is off and nobody else could sign in (no SSO, no external login, no break-glass name).

## OIDC / SSO

```csharp
builder.Services.AddBeaconOidcAuthentication(builder.Configuration);
```

```json
{
  "Beacon": {
    "Authentication": {
      "Oidc": {
        "Enabled": true,
        "Authority": "https://login.microsoftonline.com/<tenant-id>/v2.0",
        "ClientId": "<app-client-id>",
        "ClientSecret": "<from a secret store>",
        "AllowedTenants": [ "<tenant-id>" ],
        "RequiredGroups": [ "<group-object-id>" ],
        "DefaultRoleName": "Viewer",
        "RequireMfa": true
      }
    }
  }
}
```

| Key | Default | What it does |
|---|---|---|
| `Enabled` | `false` | Master switch. When on, startup validates the section. |
| `Authority`, `ClientId`, `ClientSecret` | Required | Required when enabled. Code flow with PKCE; tokens are not saved in the cookie. Keep the secret in a secret store. |
| `CallbackPath` | `/signin-oidc` | Redirect URI path to register with the identity provider. |
| `Scopes` | `openid profile email` | Requested scopes (replaces the list). |
| `AllowedTenants` | None | Entra `tid` values that may sign in. **Required** unless `AllowAnyTenant`. |
| `AllowAnyTenant` | `false` | Skip the tenant check (identity providers without `tid`). Warns at startup with an Entra authority. |
| `BlockGuests` | `true` | Refuse Entra B2B guests (`acct=1`, or `idp` naming another provider). |
| `RequiredGroups` / `RequiredRoles` | None | When either is set, the user needs at least one listed group object id or app role. |
| `DefaultRoleName` | none | Role given to a user created at first SSO sign-in. Unset: new users get **no** role until someone grants one. Cannot be `Admin` or a functional role. |
| `RequireMfa` | `false` | Refuse sign-ins whose ID token has no `amr` containing `mfa` and no `acr` listed in `AcceptedAcrValues`. |
| `AcceptedAcrValues` | None | `acr` values that count as MFA. |
| `EnableFrontChannelLogout` | `false` | Serve `/signout-oidc` for identity-provider-initiated logout. |
| `DisplayName` | `SSO` | Not used. The login page shows a fixed "Continue with single sign-on" button, and the SSO config endpoint returns only `enabled` and `localLoginEnabled`. |

Admission runs **before** any user lookup: tenant → guest block → required group or role. MFA covers the interactive SSO sign-in only. It does not apply to bearer tokens, API keys, MCP callers or break-glass passwords.

Entra app roles can also grant Beacon's [functional permissions](/integrate/access-control/#3-functional-permissions) at sign-in (`Beacon:Authorization:IdpRoles`).

## Bearer JWTs

Bearer access tokens work for the REST API and for MCP. They are configured in code:

```csharp
builder.Services.AddBeaconJwtAuthentication(jwt =>
{
    jwt.EnableBearerAuthentication = true;
    jwt.Validation.JwksEndpoint = "https://login.microsoftonline.com/<tenant-id>/discovery/v2.0/keys";
    jwt.Validation.ValidIssuers = [ "https://login.microsoftonline.com/<tenant-id>/v2.0", "https://sts.windows.net/<tenant-id>/" ];
    jwt.Validation.ValidAudiences = [ "api://<beacon-app-id>", "<beacon-app-id>" ];
});
// …
app.UseBeaconJwtBearerAuthentication();   // in the pipeline, after ApiKeyAuthMiddleware
```

| Option | Default | Notes |
|---|---|---|
| `EnableBearerAuthentication` | `false` | Turns the middleware on. |
| `Validation.JwksEndpoint` or `Validation.SigningKey` | Required | One is required. The JWKS endpoint is an `https` URL (`http` to a loopback host in Development only); a signing key is at least 32 bytes. |
| `Validation.ValidIssuer(s)`, `ValidAudience(s)` | Required | At least one of each; issuer, audience and lifetime validation must stay on. |
| `Validation.ClockSkew` | 5 min | At most 5 minutes. |
| `AccessTokenClaim` | None | For non-Entra providers: a claim that proves the token is an access token (ID-token claims such as `sub` or `nonce` are refused). |
| `ClaimsMapping` | `sub`, `preferred_username`, `email`, `roles`, `name` | Which claims carry the user id, user name, email, roles and display name. |
| `ExternalLoginEndpoint` | None | Let the login form validate credentials against your own token endpoint (an `https` URL). Internal users still sign in first. |

A token must prove it is an **access** token (Entra: `azp`/`appid` plus `scp` or `roles`; others: `typ: at+jwt` or `AccessTokenClaim`) and must bind to an existing, enabled Beacon user, so bearer tokens on the REST API need Beacon's user management. On `/beacon/mcp`, tokens go through the **MCP caller mapping** instead (`Beacon:Mcp:Callers`). See [Connect AI clients](/mcp/connect/#option-b-entra-id-callers).

## API keys

Users create API keys under **API keys** (`/beacon/api-keys`). A key:

- is `sk-sem_` followed by about 43 random base64 characters, shown **once**, stored as a SHA-256 hash;
- has the **Read** or **Execute** scope (Read: reads only; Execute: everything the owner may do, including running SQL and MCP);
- names **at least one project** the owner can read, and reaches only those;
- expires: 90 days by default, at most `Beacon:ApiKeys:MaxLifetimeDays` (365);
- acts as its owner, capped by the lower of its scope and the owner's current role, never with the Admin bypass and never with a functional permission;
- stops working when its owner is disabled or archived.

Send it as `Authorization: Bearer sk-sem_…`. Details: [API keys](/security/api-keys/).

## Sessions

| Setting | Default | What it does |
|---|---|---|
| `Beacon:Authentication:Session:RevalidateMinutes` | `5` | How often a session is re-checked against the user store (user enabled, not archived, unchanged security stamp; roles reloaded). `0` checks every request. |
| `Beacon:Authentication:Session:AbsoluteLifetimeHours` | `12` | A session ends this long after sign-in, however often it is renewed. "Remember me" cannot extend it. |

The `Beacon.Auth` cookie is HttpOnly, SameSite=Lax and Secure on HTTPS. Changing a user's roles, password, enabled state or archiving them renews their **security stamp**, which ends every session they have. Users can sign out everywhere themselves; a UserAdmin can do it for them (`POST /beacon/api/users/{id}/sign-out-everywhere`). Realtime connections close when the session ends.

## Your app owns sign-in

When Beacon runs inside your product and your users already sign in to it:

1. Leave `EnableUserManagement()` and `EnableLoginForm` off.
2. Register your authentication scheme as usual; keep `AddBeaconCookieAuthentication("/beacon")` only if you also want the Beacon cookie.
3. Make your principal carry `ClaimTypes.NameIdentifier` and a role claim of `Admin`, `Editor` or `Viewer` (your claims transformation can map your roles).
4. Tell the UI where to send signed-out users:

```csharp
builder.Services.AddBeaconApiServices(x =>
    x.ExternalLogin = new ExternalLoginOptions { LoginUrl = "/account/login" });
```

Without Beacon's user store:

- project membership comes from an `IProjectMembershipSource` you register, or projects stay open;
- only the `Admin` role claim grants functional permissions, unless `Beacon:Authorization:Roles:HonourHostRoleClaims` is `true`, in which case a role claim named like a permission's role grants it.

## Choosing a combination

| Deployment | Recommended |
|---|---|
| Internal tool, small team | User store + login form; one break-glass account. |
| Company with Entra ID | User store + OIDC (`AllowedTenants`, `RequiredGroups`, `RequireMfa`), local login off with one break-glass name, IdP app roles for functional permissions. |
| Embedded in your SaaS | Your sign-in + `ExternalLogin` + a membership source; Beacon cookie optional. |
| AI clients | API keys per person (Execute scope, one project each), or Entra callers for organisation-wide MCP access. |
