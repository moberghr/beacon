---
title: Sign-in and session hardening
description: "Every setting that controls how people sign in to Beacon and how long a session lives, with defaults, ranges, refusal answers, log events and audit actions."
---

This page covers how a person proves who they are (password, single sign-on, bearer token), how long that proof lasts, and what Beacon does when a sign-in is refused. Read it when you move a deployment from a test setup to production, when you turn off local passwords, or when you need to know what an operator sees after a failed sign-in.

Beacon has four ways in. Each ends in a different credential.

| Way in | Credential | Page that covers it |
|---|---|---|
| Browser, password | `Beacon.Auth` session cookie | this page |
| Browser, single sign-on (OIDC) | `Beacon.Auth` session cookie | this page |
| Program or MCP client, bearer token | JWT in `Authorization: Bearer` | this page (JWT validation) |
| Program or MCP client, API key | `Authorization: Bearer sk-sem_...` | [API keys](/security/api-keys/) |

Who may do what after sign-in is a separate question. See [Authorization](/security/authorization/) and [User management](/security/user-management/). Request limits on the login routes are on [Rate limits and request limits](/security/rate-and-request-limits/).

## Where Beacon credentials apply

Beacon checks its own credentials (cookie, API key, bearer token) on `/beacon` and on the path prefixes listed in `Beacon:Authentication:AdditionalPaths`. On every other route, those credentials do nothing, and Beacon never redirects a host route to its login page. The match is by whole path segment, ignoring case, against the path after the host's path base.

| Key | Type | Default | Range | What it does |
|---|---|---|---|---|
| `Beacon:Authentication:AdditionalPaths` | list of strings | empty | each entry starts with `/`, is not `/` alone, has no `?` or `#`; a trailing `/` is ignored | Extra prefixes where Beacon credentials apply. |

Use it for a Warp dashboard that admits Beacon admins, or for the OpenAPI document when you want a Beacon session to open it.

```json
{
  "Beacon": {
    "Authentication": {
      "AdditionalPaths": [ "/warp", "/openapi" ]
    }
  }
}
```

```bash
Beacon__Authentication__AdditionalPaths__0=/warp
Beacon__Authentication__AdditionalPaths__1=/openapi
```

An invalid entry fails startup:

```text
Beacon:Authentication:AdditionalPaths:1 must be a path prefix such as /warp: it starts with '/', is not '/' alone and has no query or fragment.
```

## The session cookie

A successful password or single sign-on sign-in issues the `Beacon.Auth` cookie. Its fixed properties are `HttpOnly = true`, `SameSite = Lax`, and sliding expiration on. These are not configurable.

The `Secure` flag follows the request's scheme by default (`SameAsRequest`). Behind a TLS-terminating proxy that means the flag is missing unless forwarded headers restore the `https` scheme. Set `SecureAlways` to issue the flag on every response.

| Key | Type | Default | Range | What it does |
|---|---|---|---|---|
| `Beacon:Authentication:Cookie:SecureAlways` | bool | `false` (filled `true` by the regulated profile when unset) | `true`, `false` | Always sets the `Secure` flag on the `Beacon.Auth` cookie. It covers that cookie only; the antiforgery and OIDC cookies keep their own policies. |
| `Beacon:Authentication:Session:PersistSsoCookie` | bool | `false` | `true`, `false` | `false`: a single sign-on session cookie is dropped when the browser closes. `true`: it lives until its expiry, within `AbsoluteLifetimeHours`. Password sign-in follows the "remember me" box instead. |

### Plain-HTTP startup warning

After startup, outside Development, Beacon logs one Warning when all of these hold: the server listens on a plain HTTP address, `SecureAlways` is off, and no forwarded scheme is configured. The text begins:

```text
The server listens on plain HTTP outside Development, no forwarded scheme is configured and Beacon:Authentication:Cookie:SecureAlways is off: if Beacon is reached over plain HTTP, the Beacon.Auth session cookie is issued without the Secure flag ...
```

The warning is skipped when the host already honours `X-Forwarded-Proto`. The fix is either `SecureAlways=true` or `UseForwardedHeaders` with only your proxies trusted (see [Rate limits and request limits](/security/rate-and-request-limits/#behind-a-reverse-proxy)).

```json
{
  "Beacon": {
    "Authentication": {
      "Cookie": { "SecureAlways": true }
    }
  }
}
```

```bash
Beacon__Authentication__Cookie__SecureAlways=true
```

Other cookie headers and the antiforgery cookie are on [Browser protections](/security/browser-protections/#session-cookie-flags).

## Session lifetime and revalidation

A session cookie is only a claim. With Beacon user management on, Beacon checks it against the user store: the user must still exist, be enabled and not be archived, and the session must carry the user's current security stamp. The session's roles are replaced by the stored roles. A session that fails the check is signed out.

| Key | Type | Default | Range | What it does |
|---|---|---|---|---|
| `Beacon:Authentication:Session:RevalidateMinutes` | int | `5` | 0 to 1440 | Longest time between checks against the user store. `0` checks every request. |
| `Beacon:Authentication:Session:AbsoluteLifetimeHours` | int | `12` | 1 to 8760 | A session ends this long after its sign-in, however often it is renewed. The user signs in again. |

Values outside the ranges fail startup:

```text
Beacon:Authentication:Session:RevalidateMinutes must be between 0 and 1440.
Beacon:Authentication:Session:AbsoluteLifetimeHours must be between 1 and 8760.
```

The realtime (SignalR) hub follows the same rule: a connection stays open for the revalidation interval (at least one minute), then the hub closes it and the client reconnects. A revoked or demoted user loses the connection as soon as the session would be checked again.

Two older settings still set the cookie's own expiry for password sign-in (the absolute lifetime above caps both):

| Key | Type | Default | What it does |
|---|---|---|---|
| `Authentication.CookieExpirationHours` (code option) | int | `24` | Cookie lifetime without "remember me". |
| `Authentication.RememberMeExpirationDays` (code option) | int | `30` | Cookie lifetime with "remember me". |

### Security stamp and sign out everywhere

Each user has a security stamp. Changing it ends every session that carries the old one. The stamp is renewed by:

- a sign out everywhere (the user's own, or an administrator's `POST /beacon/api/users/{id}/sign-out-everywhere`);
- a change of a user's roles (adding or removing a role), including an approved grant of the Admin role;
- disabling or enabling a user, and archiving (deleting) a user;
- a change of a user's user name or e-mail address;
- a password reset by an administrator;
- a password change by the user, including the password a user chooses at their first sign-in after an administrator set one.

A change of the display name alone does not renew it. The sessions end within one revalidation interval at most.

A person ends their own other sessions with:

```http
POST /beacon/api/auth/logout?everywhere=true
X-XSRF-TOKEN: <antiforgery token>
```

This renews the stamp first, then ends the current session. It also sets `TokensValidAfter` on the user, so a bearer token issued before that moment is refused (see [JWT validation](#jwt-bearer-validation)). If the caller is not a signed-in session of a stored user, nothing is signed out and the answer is 409 problem+json whose `detail` is `Your user could not be found, so your sessions were not signed out everywhere.` On a host without the mediator the `detail` is `Signing out everywhere is not available on this host.`

A plain `POST /beacon/api/auth/logout` ends only the current session and answers `{"success":true}`.

### What an operator sees when a session ends

| Where | Name | Meaning |
|---|---|---|
| Log, event 7118 `SessionEnded` | `Session ended: {Reason} (user {UserId})` | Revalidation refused the session. A session naming no stored user logs `Session ended: {Reason} (subject {SubjectHash})` at Warning. |
| Log, event 7117 `UserSessionsRevoked` | `Every session of user {UserId} signed out, by user {ActorId}` | A sign out everywhere (logged at Information; other stamp renewals write no such event). |
| Audit action | `auth.session_ended` | The same refusal, with a reason code. |
| Audit action | `auth.signed_out`, `auth.signed_out_everywhere` | An explicit sign out. |

## Password sign-in

`POST /beacon/api/auth/login` takes `username`, `password`, optional `rememberMe`, and optional `newPassword`.

```http
POST /beacon/api/auth/login
Content-Type: application/json
X-XSRF-TOKEN: <antiforgery token>

{ "username": "alice", "password": "REPLACE_ME" }
```

| Result | Status | Body |
|---|---|---|
| Signed in | 200 | `{"success":true,"redirectUrl":"/beacon/home"}` |
| Missing field | 400 | `{"success":false,"error":"Username and password are required."}` |
| Wrong password, unknown user, locked or delayed account, disabled user | 401 | `{"success":false,"error":"Invalid username or password."}` |
| An administrator set the password, and no `newPassword` was sent | 401 | `{"success":false,"passwordChangeRequired":true,"error":"..."}`; audit action `auth.password_sign_in_refused` with reason `password_change_required` |
| `newPassword` sent and refused by the password rules | 400 | `{"success":false,"passwordChangeRequired":true,"error":"..."}` |
| Over the per-address login limit | 429 | problem+json, see below |
| No valid antiforgery token | 400 | before the password is checked |

The failure answer is the same for every cause on purpose. A locked or delayed account still has its password checked, and every refused attempt runs the same statements, so neither the answer nor its timing says whether a name exists or is locked.

The 429 answer, from the per-address login limit:

```http
HTTP/1.1 429 Too Many Requests
Content-Type: application/problem+json
Retry-After: 42

{ "title": "Too Many Requests", "status": 429, "detail": "Too many requests; retry in 42 seconds." }
```

The body is shown with its Beacon-set fields; the framework may add its standard problem fields.

### Lockout

After repeated wrong passwords an internal user's password sign-in and password change are refused for a while, whatever password is sent.

| Key | Type | Default | Range | What it does |
|---|---|---|---|---|
| `Beacon:RateLimits:Lockout:Enabled` | bool | `true` | | Applies the lockout. |
| `Beacon:RateLimits:Lockout:MaxFailedAttempts` | int | `5` | 0 to 100 (`0` turns it off) | Wrong passwords before a lockout. |
| `Beacon:RateLimits:Lockout:DurationMinutes` | int | `15` | 1 to 1440 | How long the lockout lasts. |

A successful sign-in, a lockout and an administrator's password reset restart the count. So does a wrong password sent `DurationMinutes` or more after the previous one. The count is stored on the user, not on the client. That caps password guessing hard, and it means anyone who knows a user name can keep that user's password sign-in locked. Hosts that prefer the opposite rely on single sign-on and the per-address login limit and turn the lockout off.

Log event 7124 `UserLockedOut` (Warning): `User {UserId} refused password sign-in for {LockoutSeconds} seconds after {FailedAttempts} wrong passwords (break-glass delay: {BreakGlassDelay})`; audit action `auth.user_locked_out`. The same event is logged when a break-glass delay begins.

### Password rules

| Key | Type | Default | Range | What it does |
|---|---|---|---|---|
| `Beacon:UserManagement:MinimumPasswordLength` | int | `8` | 1 to 128 | Raises the minimum length. The larger of this and the length set in code applies. The regulated profile fills `12` when unset. |
| `UserManagementOptions.RequirePasswordComplexity` (code option) | bool | `true` | | Requires the complexity rules. |

A value outside 1 to 128 fails startup: `Beacon:UserManagement:MinimumPasswordLength must be between 1 and 128.`

A new password must differ from the current one: `The new password must differ from the current one.` When an administrator sets a password, the user must choose their own on the next sign-in, in the same request (`newPassword`), and it must differ from the one the administrator set.

Passwords are stored as PBKDF2-SHA256 with the iteration count in the hash (`v2$<iterations>$<base64>`). The current count is 600,000. A weaker stored hash is replaced only after a successful sign-in, by a write that checks the hash it verified, so it never undoes a concurrent password change.

## Local login off and break-glass accounts

With single sign-on, people should sign in through the identity provider so its multi-factor authentication and conditional access apply. Turn local login off, and keep at most three emergency accounts that may still use a password.

| Key | Type | Default | Range | What it does |
|---|---|---|---|---|
| `Beacon:UserManagement:LocalLogin:Enabled` | bool | `true` | | `false`: `POST /beacon/api/auth/login` is not mapped, Beacon's password providers refuse every account, and the login page shows no password form, except for break-glass names. |
| `Beacon:UserManagement:LocalLogin:BreakGlassUserNames` | list of strings | empty | at most 3; no blank entry | Internal accounts that may sign in with a password while local login is off. Compared exactly, case included, with the stored user name. |

The endpoint stays mapped while at least one break-glass name is configured. Every name gets the same password check, and the decision comes after it, so neither the answer nor its timing tells a break-glass account from any other.

```json
{
  "Beacon": {
    "UserManagement": {
      "LocalLogin": {
        "Enabled": false,
        "BreakGlassUserNames": [ "emergency-admin" ]
      }
    }
  }
}
```

```bash
Beacon__UserManagement__LocalLogin__Enabled=false
Beacon__UserManagement__LocalLogin__BreakGlassUserNames__0=emergency-admin
```

Startup failures:

```text
Beacon:UserManagement:LocalLogin:BreakGlassUserNames may not contain a blank entry.
Beacon:UserManagement:LocalLogin:BreakGlassUserNames lists 4 accounts; at most 3 are allowed: break-glass accounts are for emergencies only.
```

### Accepted and refused, local login off

| Input | Answer | Log | Audit |
|---|---|---|---|
| Right password, name on the list | 200 | Warning 7119 `BreakGlassSignIn`: `Break-glass password sign-in while local login is off (user {UserId})` | `auth.break_glass_signed_in` |
| Right password, name not on the list | 401 `Invalid username or password.` | Information 7120 `PasswordSignInRefused`: `Password sign-in refused: local login is off and the account is not a break-glass account` (a provider that finds the account itself logs `... and user {UserId} is not a break-glass account`) | `auth.password_sign_in_refused` |
| Wrong password | 401 `Invalid username or password.` | none of the above | `auth.password_sign_in_failed` |

The typed user name is never logged. Alert on event 7119: nobody should use a break-glass account in normal operation.

### Break-glass throttle

Break-glass accounts are never locked. Instead, an optional growing delay can be switched on. It is off by default, because anyone who knows a break-glass name could otherwise keep that account delayed. Off, only the per-address login limit applies.

| Key | Type | Default | Range | What it does |
|---|---|---|---|---|
| `Beacon:RateLimits:BreakGlassThrottle:Enabled` | bool | `false` | | Applies the delay. |
| `...:FailedAttemptsBeforeDelay` | int | `5` | 0 to 100 | Wrong passwords before the first delay. |
| `...:InitialDelaySeconds` | int | `30` | 1 to `MaxDelaySeconds` | First delay; doubles after each further wrong password sent once a delay ended. |
| `...:MaxDelaySeconds` | int | `300` | 1 to 3600 | Longest delay. |
| `...:ResetAfterMinutes` | int | `60` | 1 to 1440, and longer than `MaxDelaySeconds` | A wrong password this long after the previous one restarts the count. |

Wrong passwords sent during a delay are not counted and do not lengthen it, so a delay always ends and the right password then signs in. The answer during a delay is the same generic 401.

## Single sign-on (OIDC)

Single sign-on is off by default. Turn it on with an authority, a client and a client secret, and tell Beacon which sign-ins to admit.

| Key | Type | Default | What it does |
|---|---|---|---|
| `Beacon:Authentication:Oidc:Enabled` | bool | `false` | Turns single sign-on on. |
| `...:Authority` | string | none | Issuer URL of the identity provider. Required when enabled. |
| `...:ClientId` | string | none | Required when enabled. |
| `...:ClientSecret` | string | none | Required when enabled. Keep it in a secret store. |
| `...:CallbackPath` | string | `/signin-oidc` | Where the provider returns the browser. |
| `...:Scopes` | list | `openid`, `profile`, `email` | Requested scopes. |
| `...:DefaultRoleName` | string | unset | Role for a user provisioned at first sign-in. Unset: the user signs in with no role and sees nothing until an administrator assigns one. |
| `...:DisplayName` | string | `SSO` | Label on the login button. |
| `...:McpJwksEndpoint` | string | unset | Optional JWKS endpoint of the provider; with single sign-on enabled and this set, Beacon's API and `/beacon/mcp` also accept bearer JWTs from the provider (issuer `Authority`, audience `ClientId`). |
| `...:AllowedTenants` | list | empty | Entra tenant ids (`tid` claim) that may sign in. |
| `...:AllowAnyTenant` | bool | `false` | Skips the tenant check, for providers that issue no `tid` claim. |
| `...:BlockGuests` | bool | `true` | Refuses Entra B2B guests (`acct` claim `1`, or an `idp` claim naming another provider). |
| `...:RequiredGroups` | list | empty | Group object ids (`groups` claim). |
| `...:RequiredRoles` | list | empty | App roles (`roles` claim). When either list is set, the user must hold at least one entry of either. |
| `...:RequireMfa` | bool | `false` | The ID token's `amr` must contain `mfa`, or its `acr` must be one of `AcceptedAcrValues`. |
| `...:AcceptedAcrValues` | list | empty | Exact `acr` values that count as multi-factor. |
| `...:EnableFrontChannelLogout` | bool | `false` | Serves `/signout-oidc`. Off, because an anonymous GET there would end a Beacon session. |

Enabling single sign-on without a tenant rule fails startup:

```text
Beacon:Authentication:Oidc is Enabled but admits every tenant: set AllowedTenants to your tenant id(s), or set AllowAnyTenant to true when your identity provider issues no tenant (tid) claim.
Beacon:Authentication:Oidc is Enabled but Authority, ClientId, or ClientSecret is missing.
```

An example for an Entra tenant that requires multi-factor sign-in and one group:

```json
{
  "Beacon": {
    "Authentication": {
      "Oidc": {
        "Enabled": true,
        "Authority": "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000000/v2.0",
        "ClientId": "11111111-1111-1111-1111-111111111111",
        "ClientSecret": "REPLACE_ME",
        "AllowedTenants": [ "00000000-0000-0000-0000-000000000000" ],
        "BlockGuests": true,
        "RequiredGroups": [ "22222222-2222-2222-2222-222222222222" ],
        "RequireMfa": true
      }
    }
  }
}
```

```bash
Beacon__Authentication__Oidc__Enabled=true
Beacon__Authentication__Oidc__ClientSecret=REPLACE_ME
```

### What a refused person sees

Beacon sends the browser to the login page with a code in the query string. The page shows a message for each.

| Cause | Redirect | Audit |
|---|---|---|
| Not admitted: wrong tenant, guest, missing group or role, a disabled or archived account, first-run setup not finished, or (under `Beacon:Audit:Required`) the sign-in event could not be written | `/beacon/login?ssoError=not_admitted` | `auth.sso_refused` |
| `RequireMfa` is on and the token has neither `amr` `mfa` nor an accepted `acr` | `/beacon/login?ssoError=mfa_required` | `auth.sso_refused` |
| Any other provider failure | `/beacon/login?ssoError=1`, with a Warning `OIDC remote failure (...)` | none |

A successful sign-in records `auth.sso_signed_in`.

What `RequireMfa` covers: the interactive single sign-on sign-in only. Do not describe bearer tokens, MCP access, API keys or break-glass sign-in as protected by it. Check what your provider puts in the ID token before turning it on: a provider that sends neither `amr` nor `acr` locks everyone out.

## JWT bearer validation

A host can accept bearer tokens (`Authorization: Bearer <jwt>`) from an identity provider, and can call an external login endpoint. Beacon validates the token itself. Startup fails unless a signing key or JWKS endpoint, at least one issuer and one audience are set and issuer, audience and lifetime validation are on, because a shared JWKS (Entra's, for one) signs tokens for every tenant and application.

| Key | Type | Default | Range | What it does |
|---|---|---|---|---|
| `Beacon:Authentication:Jwt:EnableBearerAuthentication` | bool | `false` | | Accepts bearer tokens. |
| `...:ExternalLoginEndpoint` | URL | none | absolute `https` | Login form calls this endpoint and receives a JWT. |
| `...:AccessTokenClaim` | string | none | not a claim an ID token can carry | For non-Entra issuers that do not set `typ: at+jwt`: a claim present only in access tokens. |
| `...:Validation:ValidIssuer`, `ValidIssuers` | string, list | none | at least one | Accepted `iss` values. |
| `...:Validation:ValidAudience`, `ValidAudiences` | string, list | none | at least one | Accepted `aud` values. |
| `...:Validation:SigningKey` | string | none | at least 32 UTF-8 bytes | HMAC secret. |
| `...:Validation:JwksEndpoint` | URL | none | absolute `https` | Signing keys of the provider. |
| `...:Validation:ClockSkew` | time span | `00:05:00` | 0 to 5 minutes | Tolerance on token times. |
| `...:Validation:JwksRefreshInterval` | time span | `01:00:00` | 5 minutes to 24 hours | How often the JWKS is fetched again. |
| `...:Validation:JwksMaximumStaleness` | time span | `24:00:00` | `JwksRefreshInterval` to 7 days | How long keys stay usable while the endpoint fails. |
| `...:Validation:ValidateIssuer`, `ValidateAudience`, `ValidateLifetime` | bool | `true` | must stay `true` | Validation switches. |

Plain `http` is accepted only for a loopback host, and the host refuses even that at startup outside Development.

A token issued before the user's last sign out everywhere is refused: its `iat` is compared with `TokensValidAfter`, and a token with no readable `iat` counts as issued before. The refusal is `IssuedBeforeSignOut` and applies on the REST routes and on `/beacon/mcp` (the MCP caller mapper caches `TokensValidAfter` for up to a minute).

```json
{
  "Beacon": {
    "Authentication": {
      "Jwt": {
        "EnableBearerAuthentication": true,
        "Validation": {
          "ValidIssuer": "https://login.example.com/",
          "ValidAudience": "beacon-api",
          "JwksEndpoint": "https://login.example.com/.well-known/jwks.json",
          "JwksRefreshInterval": "01:00:00",
          "JwksMaximumStaleness": "24:00:00"
        }
      }
    }
  }
}
```

## Antiforgery on login and logout

`POST /beacon/api/auth/login` and `POST /beacon/api/auth/logout` need an antiforgery token, anonymous callers included. Without one, a cross-site form post could sign a browser in to another account, or delete its cookie. The request is refused with 400 before the rate limit or the password check. Prime a token with `GET /beacon/api/csrf` and send it as `X-XSRF-TOKEN`. After a sign-in or sign-out Beacon issues a fresh token for the new identity. Details are on [Browser protections](/security/browser-protections/#antiforgery-tokens).

## First-run setup token

While no super admin exists, the setup page needs a token. Beacon uses, in order, the token set in code, then `Beacon:UserManagement:SetupToken`, then a token generated for that process and written once to the console (standard error).

| Key | Type | Default | Range | What it does |
|---|---|---|---|---|
| `Beacon:UserManagement:SetupToken` | string | unset | at least 32 characters when set | Secret the setup page requires. |

While setup is open, Beacon logs `First-run setup is open: no super admin exists yet.` Setup requests have their own per-address limit with the same size as login, in a separate partition. A wrong or missing token is refused with 403 problem+json whose `detail` is `The setup token is missing or invalid. Use the token from the server console or the configured Beacon:UserManagement:SetupToken.`, logged as the Warning `First-run setup rejected: the setup token is missing or invalid.`, and recorded as `auth.setup_token_refused` (whether a token was sent, never its value).

## Making sign-ins auditable

Every sign-in outcome is an event in the security audit trail. See [Security audit trail](/security/audit-trail/) for the trail itself.

| Action | When |
|---|---|
| `auth.password_sign_in_succeeded` | Password sign-in worked. |
| `auth.password_sign_in_failed` | Wrong password or unknown name. |
| `auth.password_sign_in_refused` | Correct password, but local login is off and the account is not break-glass. |
| `auth.break_glass_signed_in` | A break-glass sign-in. |
| `auth.sso_signed_in`, `auth.sso_refused` | Single sign-on outcomes. |
| `auth.signed_out`, `auth.signed_out_everywhere`, `auth.session_ended` | Sessions ending. |
| `auth.user_locked_out` | A lockout began. |
| `auth.setup_token_refused` | A wrong or missing first-run setup token. |

Events carry ids, counts and codes only. They never carry a typed user name, an address or a password.

`Beacon:Audit:Required` (default `false`) makes the trail mandatory for sign-in. On, a successful sign-in whose event cannot be written is refused: the session just issued is withdrawn, the answer is the generic 401 and Beacon logs Error 9115 `SecurityAuditRequiredEventNotWritten` (after the 9107 `SecurityAuditWriteFailed` copy of the event). A required password change that was already saved answers `Your new password is saved, but you could not be signed in. Sign in with your new password.` A break-glass sign-in is exempt, so an audit outage cannot lock out the emergency way in: it is admitted without its event and Beacon logs Error 9116 `SecurityAuditExemptEventNotWritten`.

## Recommended values

| Setting | Development | Production | Regulated profile |
|---|---|---|---|
| `LocalLogin:Enabled` | `true` | `false` once single sign-on works | must be `false` |
| `LocalLogin:BreakGlassUserNames` | empty | one name, password in a vault | one name |
| `Oidc:AllowedTenants` | your test tenant | your tenant | your tenant |
| `Oidc:BlockGuests` | `true` | `true` | `true` |
| `Oidc:RequireMfa` | `false` | `true` if the provider sends `amr` or `acr` | must be `true` when single sign-on is on |
| `Cookie:SecureAlways` | `false` | `true` | filled `true` when unset |
| `Session:RevalidateMinutes` | `5` | `5` | not constrained |
| `Session:AbsoluteLifetimeHours` | `12` | `8` to `12` | not constrained |
| `RateLimits:Lockout` | on | on | must be on (`Enabled` true, `MaxFailedAttempts` above 0) |
| `MinimumPasswordLength` | `8` | `12` | filled `12` when unset; below 12 fails startup |
| `Audit:Required` | `false` | `true` | filled `true` when unset |
| `BreakGlassThrottle:Enabled` | `false` | `false` | not constrained |

The regulated profile (`Beacon:Profile = Regulated`) fills only keys you leave unset, and never overrides a value you configured. It fills `Beacon:Audit:Required`, `Beacon:Authentication:Cookie:SecureAlways` and `Beacon:UserManagement:MinimumPasswordLength`. It fails startup, listing every problem in one message, when local login is on, when single sign-on is on without `RequireMfa`, or when the lockout is off.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Startup fails with "admits every tenant" | Single sign-on enabled with no tenant rule | Set `AllowedTenants`, or `AllowAnyTenant` if the provider issues no `tid`. |
| Everyone lands on `?ssoError=mfa_required` | The ID token has no `amr` `mfa` and no accepted `acr` | Add the provider's `acr` value to `AcceptedAcrValues`, or fix the provider's policy. |
| `?ssoError=not_admitted` for a known person | Wrong tenant, guest account, missing group or role, or the user is disabled or archived | Check `AllowedTenants`, `RequiredGroups`, `RequiredRoles` and the user's state. |
| Password sign-in answers 401 for a correct password | Local login is off and the name is not break-glass, the account is locked or delayed, or the user is disabled | Look for event 7120. Wait out the lockout, or reset the password. |
| 400 on login or logout | No antiforgery token | Prime `GET /beacon/api/csrf` and send `X-XSRF-TOKEN`. |
| 429 on login | Per-address limit | Wait `Retry-After` seconds. Behind a proxy, enable forwarded headers so the limit is per client. |
| Session ends sooner than expected | Absolute lifetime, a renewed security stamp, or a disabled user | Check `AbsoluteLifetimeHours` and events 7117 and 7118. |
| Sign-in refused while `Audit:Required` is on | The audit event could not be written | Fix the audit store; look for events 9107 and 9115. |
| Browser shows the cookie without `Secure` | Plain HTTP behind a proxy | Set `SecureAlways` or enable forwarded headers. |
| Startup fails on a JWT setting | A missing issuer, audience or key, or a value out of range | Read the message; it names the setting. |
| Bearer token refused after sign out everywhere | Its `iat` is before `TokensValidAfter` | Get a new token. |

## Related pages

- [Security overview](/security/)
- [User management](/security/user-management/)
- [Authorization](/security/authorization/)
- [API keys](/security/api-keys/)
- [Rate limits and request limits](/security/rate-and-request-limits/)
- [Browser protections](/security/browser-protections/)
- [Sign-in and identity for hosts](/integrate/authentication/)
- [Production checklist](/integrate/production/)
