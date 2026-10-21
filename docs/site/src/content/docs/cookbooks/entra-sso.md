---
title: Sign in to Beacon with Microsoft Entra ID
description: "Single sign-on for Beacon's UI with Microsoft Entra ID: an app registration, a tenant allow-list, guests blocked, MFA required, Entra app roles mapped to functional permissions, and a break-glass account when local passwords are off."
---

| | |
|---|---|
| **You end up with** | People in your tenant sign in to Beacon's UI with **Continue with single sign-on**. Other tenants and guests are refused, MFA is required, new users arrive with no access until an Admin grants a role, and one emergency account still signs in with a password. |
| **Time** | about 30 minutes |
| **You need** | A running Beacon host with user management on and the first admin created ([Embed Beacon](/cookbooks/embed-postgresql-warp/) or the sample host), permission to register an application in your Entra tenant, and a way to set host configuration and secrets. |
| **Applies to** | Beacon 5.0 · Full Beacon |

:::tip[Let your AI do it]
Part of this recipe is clicking in the Entra admin center, so the agent edits your host's configuration and guides you through the portal. Paste this into Claude Code or another coding agent, in the repository of your Beacon host.

```text wrap
Read https://moberghr.github.io/beacon/cookbooks/entra-sso.md and follow it step by step in this repository.
Ask me before every decision it marks as one. Edit only non-secret configuration; tell me which user-secrets or
environment variables to set and let me type the client secret myself: never ask me to paste it into this chat.
Do not change anything in my Entra tenant: tell me what to click and wait for me. Show me the result of each
"Check it works" item, and do not turn local password sign-in off until I have confirmed the break-glass sign-in.
```
:::

## Before you start

| Decision | Options | Default |
|---|---|---|
| Who may sign in | Anyone in your tenant, or only members of a group or holders of an app role (`RequiredGroups`, `RequiredRoles`) | Your tenant, plus a group if you can |
| Role a new user gets | None, or a role such as `Viewer` (never `Admin` or a functional role: startup fails) | None: an Admin grants access after the first sign-in |
| MFA | `RequireMfa` on or off | On, after you have seen `mfa` in a test sign-in |
| Local passwords | On, or off with one break-glass account | Off once SSO works |
| Entra app roles to Beacon permissions | Map them, or assign functional roles on the Users page | Assign on the Users page unless your identity team owns the roles |

## 1. Register the application in Entra

In the Microsoft Entra admin center, open **App registrations** and choose **New registration**.

1. **Name**: `Beacon`. **Supported account types**: single tenant.
2. **Redirect URI**: platform **Web**, value `https://beacon.example.com/signin-oidc`. This is your app's public origin plus Beacon's `CallbackPath`, which defaults to `/signin-oidc`. It is **not** under `/beacon`. Register one per environment (add `https://localhost:<port>/signin-oidc` for development).
3. Under **Certificates & secrets**, create a client secret. Copy its **value** now.
4. From **Overview**, note the **Application (client) ID** and the **Directory (tenant) ID**.
5. Under **Token configuration**, add the optional claim `acct` to the ID token. Beacon uses it to tell a guest from a member; without it a guest is recognised only when the token names a foreign identity provider, and Beacon logs a warning once.

Beacon uses the authorization code flow with PKCE and does not keep the tokens.

## 2. Configure Beacon

Store the secret outside source control:

```bash
dotnet user-secrets set "Beacon:Authentication:Oidc:ClientSecret" "<the secret value>"
# elsewhere: the environment variable Beacon__Authentication__Oidc__ClientSecret
```

Then the rest, in `appsettings.json` (the keys are the ones in `src/Beacon.SampleProject/appsettings.json`):

```json
{
  "Beacon": {
    "Authentication": {
      "Oidc": {
        "Enabled": true,
        "Authority": "https://login.microsoftonline.com/<tenant-id>/v2.0",
        "ClientId": "<application-client-id>",
        "AllowedTenants": [ "<tenant-id>" ],
        "BlockGuests": true,
        "RequireMfa": false
      }
    }
  }
}
```

Your host must call `builder.Services.AddBeaconOidcAuthentication(builder.Configuration);`. The [embed recipe](/cookbooks/embed-postgresql-warp/#4-register-beacon-and-map-its-endpoints) and the sample host already do.

What these keys do (the full table is in [Sign-in & identity](/integrate/authentication/#oidc--sso)):

| Key | Effect |
|---|---|
| `Enabled`, `Authority`, `ClientId`, `ClientSecret` | All four are required once enabled, or the host does not start. |
| `AllowedTenants` | Entra tenant ids (`tid`) that may sign in. Required: with SSO on and no tenant listed the host does not start. `AllowAnyTenant: true` is only for providers that issue no `tid`, and logs a warning with an Entra authority. |
| `BlockGuests` | Default `true`. Refuses Entra B2B guests. |
| `RequiredGroups`, `RequiredRoles` | When either is set, the user needs at least one listed group object id or app role. Entra puts groups in the token only if you add a groups claim under **Token configuration**. |
| `DefaultRoleName` | The role a user gets at their first sign-in. Unset: no role. Cannot be `Admin` or the role of a functional permission. |
| `RequireMfa`, `AcceptedAcrValues` | See step 4. |
| `CallbackPath` | Change it only if you register a different redirect URI. |

Admission runs before Beacon looks anyone up or creates a user: tenant first, then the guest block, then the group or role requirement. A person who fails any of them never gets a Beacon user.

Behind a reverse proxy, configure `UseForwardedHeaders` so Beacon builds an `https` redirect URI ([Production checklist](/integrate/production/#reverse-proxy)).

## 3. Sign in and grant the first roles

1. Restart the host. The login page now shows **Continue with single sign-on**.
2. Click it and sign in with an account from your tenant. The first sign-in creates the user; with no `DefaultRoleName` the page says **Your account has no access yet.**
3. Sign in as the admin you created at first-run setup, open **Users**, edit the new user and assign a role.

Beacon matches a returning user by the token's `sub` and issuer, and it always takes roles from its own store: it removes every role, group and `roles` claim the token carries before building the session. **An Entra role or group named `Admin` does not make anyone a Beacon Admin.** While Beacon manages users, Admin standing comes from the stored user's live roles, and only an existing Admin can assign the Admin role. A UserAdmin cannot. Nobody is provisioned before the first-run setup has created the super admin.

## 4. Require MFA

`RequireMfa: true` refuses an SSO sign-in unless the ID token's `amr` claim contains `mfa`, or its `acr` claim is one of `AcceptedAcrValues`. Enforce MFA in Entra with a Conditional Access policy, and let Beacon check that it happened.

1. Sign in once with `RequireMfa` off and look at the ID token's `amr` claim. [Single sign-on admission](/security/user-management/#single-sign-on-admission) advises requesting `amr` as an optional ID-token claim under **Token configuration**.
2. When `mfa` is there, set `"RequireMfa": true`. A sign-in without it lands on the login page with a message that the sign-in did not use multi-factor authentication.

`RequireMfa` covers the interactive SSO sign-in only. It does not apply to bearer tokens, MCP callers, API keys or password sign-in, break-glass included. Require MFA for tokens in Entra.

## 5. Map Entra app roles to functional permissions (optional)

Beacon's six functional permissions (`Approver`, `Auditor`, `SecurityAdmin`, `UserAdmin`, `DataSourceAdmin`, `MigrationOperator`) can come from Entra app roles at sign-in. They carry no project data and are never `Admin`, `Editor` or `Viewer`.

1. In the app registration, open **App roles** and create roles, for example with the value `Beacon.Auditor`. Assign people or groups to them under **Enterprise applications**, **Users and groups**.
2. Map them:

```json
{
  "Beacon": {
    "Authorization": {
      "IdpRoles": {
        "Enabled": true,
        "Map": { "Beacon.Auditor": "Auditor", "Beacon.SecurityAdmin": "SecurityAdmin" },
        "ClaimLifetimeMinutes": 60
      }
    }
  }
}
```

A mapped permission is added to the session when the user signs in and is never stored. It counts for `ClaimLifetimeMinutes` (default 60, at most 1440), does not show on the Users page and never reaches an API key; signing in again renews it. The host does not start when `Enabled` is true with an empty `Map`, or a value that is not one of the six permission names. More in [Access control](/integrate/access-control/).

## 6. Turn local passwords off and keep a break-glass account

Do this last. Confirm first that SSO works and that the super admin from setup can sign in with a password.

```json
{
  "Beacon": {
    "UserManagement": {
      "LocalLogin": { "Enabled": false, "BreakGlassUserNames": [ "<the super admin's stored user name>" ] }
    }
  }
}
```

With `Enabled: false` the login page shows no password form, and only the listed accounts may still sign in with a password. A name is a stored, enabled, internal user name, matched exactly and case-sensitively; at most three. Each break-glass sign-in logs `BreakGlassSignIn` (event 7119) as a warning: treat it as an incident. The host does not start if local login is off and nobody could sign in. Open `/beacon/login?local=1` to reach the password form.

`HostOwnsSignIn` is not for this recipe. It is for a host whose own identity signs people in and which decides whom Beacon provisions: it removes first-run setup and the super admin. Leave it off ([User management](/security/user-management/#when-the-host-owns-sign-in)).

## Check it works

- [ ] The host starts. No `Beacon:Authentication:Oidc is Enabled but ...` error.
- [ ] `curl -s https://beacon.example.com/beacon/api/auth/sso` answers `{"enabled":true,...}`, and the login page shows **Continue with single sign-on**.
- [ ] An account from your tenant signs in and the user appears on **Users** with no role (or `DefaultRoleName`).
- [ ] An account from another tenant, and a guest, are sent back to `/beacon/login` with "Your account is not permitted to use Beacon."
- [ ] With `RequireMfa` on, a sign-in without MFA is refused with the multi-factor message, and one with MFA works.
- [ ] With `IdpRoles` on, a user holding `Beacon.Auditor` can open the pages that permission opens.
- [ ] With local login off, the break-glass account signs in at `/beacon/login?local=1`, and every other password sign-in is refused.

## If something goes wrong

| Symptom | Cause and fix |
|---|---|
| Entra shows `AADSTS50011` (reply URL does not match) | The registered redirect URI is not exactly `<origin>/signin-oidc`, including `https` and the port. Behind a proxy, fix forwarded headers so Beacon sends `https`. |
| Startup: `... admits every tenant` | List your tenant in `AllowedTenants`. |
| Startup: `DefaultRoleName cannot be the Admin role ...` | Pick `Viewer` or `Editor`, or leave it unset. |
| Startup: local login is off and nobody could sign in | List a break-glass user name, or turn SSO on, or set `LocalLogin:Enabled` back to `true`. |
| Everyone is sent back with "Your account is not permitted ..." | Tenant, guest, group or role admission failed. The host log has one Warning with the reason, the tenant and a hash of the subject, never the name. |
| "Single sign-on failed" | The token was not valid for the app (wrong client secret, expired secret, wrong authority). Check the host log for `OIDC remote failure`. |
| Signed in but "Your account has no access yet." | The new user has no role. An Admin assigns one on **Users**, or set `DefaultRoleName`. |
| First SSO sign-in is refused before setup | Nobody is provisioned until first-run setup has created the super admin. Complete setup first. |
| An Entra `Admin` role does nothing in Beacon | By design: roles come from Beacon's store. Assign the Admin role on **Users**. |
| Mapped permission disappeared | It lasts `ClaimLifetimeMinutes` after the sign-in. Sign in again. |

## Next

- [Sign-in & identity](/integrate/authentication/): every OIDC option, sessions, bearer tokens and API keys.
- [Authorization](/security/authorization/): global roles, functional permissions and project access.
- [User management](/security/user-management/): roles, first-run setup and the Users page.
- [Connect Claude through Entra](/mcp/entra-callers/): MCP callers signing in with Entra tokens, which `Oidc:McpJwksEndpoint` and `Beacon:Mcp:Callers` handle separately from this sign-in.
