---
title: Configuration
description: Configure Beacon connection strings, the encryption key, authentication, AI/LLM, email, scheduling, and the metadata database.
---

Complete reference for configuring Beacon — connection strings, the required encryption key, authentication, AI/LLM, email, scheduling, and the metadata database.

This guide applies to both delivery modes: running the `Beacon.SampleProject` host and embedding the `Beacon.*` NuGet packages in your own ASP.NET Core app. Configuration is identical either way.

## Configuration in Program.cs

Beacon is wired in `Program.cs`. The canonical setup (from `Beacon.SampleProject`) is:

```csharp
using Beacon.AI;
using Beacon.Api;
using Beacon.Core;
using Beacon.Core.PostgreSql;
using Beacon.MCP;
using Beacon.UI;

// Host identity (claims transformation)
builder.Services.AddBeaconHostInfrastructure();

// Core services, scheduler, connectors, metadata provider
builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<BeaconScheduler>();   // your IBeaconScheduler implementation
        options.BaseUrl = "https://localhost:7187";
        options.UseAI = true;
        options.AddEmailAdapter<BeaconMailSender>();
        options.Authorization.Enabled = true;
        options.Authentication.EnableLoginForm = true;
        options.AddAuthenticationProvider<DatabaseAuthenticationProvider>();
        options.UserManagement = new UserManagementOptions { Enabled = true };
    })
    .AddPostgreSqlConnector()
    .AddSqlServerConnector()
    .AddMySqlConnector()
    // ... other connectors ...
    .UsePostgreSql(builder.Configuration.GetConnectionString("BeaconContext")!, "beacon");

builder.Services.AddBeaconCookieAuthentication("/");
builder.Services.AddBeaconOidcAuthentication(builder.Configuration); // optional SSO
builder.Services.AddBeaconApiAuthorization();   // the API, Admin and Execute-scope policies (MapBeaconApi, MapBeaconMcp)
builder.Services.AddBeaconAI(builder.Configuration);
builder.Services.AddBeaconMcp();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseStaticFiles();
app.UseMiddleware<ApiKeyAuthMiddleware>();
app.UseAuthentication();
app.UseMiddleware<BeaconCookieAuthMiddleware>();
app.UseAuthorization();
app.UseAntiforgery();

app.MapOpenApi();                 // /openapi/v1.json
app.MapBeaconApi();               // /beacon/api/* + the SignalR hub
app.MapLoginEndpoints("/beacon", beaconConfiguration);
app.MapBeaconMcp();               // MCP server at /beacon/mcp, behind the Execute-scope policy
app.MapBeaconUi();                // React SPA at root /
```

:::note
The React SPA is served at the **root URL `/`** by the `Beacon.UI` Razor Class Library (it builds from `src/Beacon.UI/web` into `src/Beacon.UI/wwwroot`). The REST API lives under `/beacon/api/*`, the MCP server at `/beacon/mcp`, the SignalR hub at `/beacon/api/hub`, and the OpenAPI document at `/openapi/v1.json`. The hub is wired automatically by `AddBeaconApiServices()` / `MapBeaconApi()` — see [Real-time](/getting-started/installation/#real-time-signalr) for the opt-out.
:::

## Base URL Configuration

`BaseUrl` is the public origin where Beacon is hosted. It's used to build clickable links in notifications (e.g. Teams cards) back to the details page in the app.

```csharp
options.BaseUrl = "https://localhost:7187";        // development
options.BaseUrl = "https://staging.example.com";   // staging
options.BaseUrl = "https://beacon.example.com";     // production
options.BaseUrl = builder.Configuration["Beacon:BaseUrl"]; // from config
```

### appsettings.json

```json
{
  "Beacon": {
    "BaseUrl": "https://beacon.example.com"
  }
}
```

`Beacon:EncryptionKey` never goes into `appsettings.json`: see [Encryption Key Configuration](#encryption-key-configuration-required).

:::note
The SPA is served at the root `/`, so notification links resolve to paths like `{BaseUrl}/notifications/{id}` — no `/beacon` UI prefix.
:::

### Public Base URL (behind a reverse proxy)

`Beacon:PublicBaseUrl` is the public origin advertised in the MCP discovery documents (`/.well-known/*`) and in the `WWW-Authenticate: Bearer resource_metadata="…"` challenge that unauthenticated MCP requests receive.

```json
{
  "Beacon": {
    "PublicBaseUrl": "https://beacon.example.com"
  }
}
```

Leave it empty and those URLs are derived from the incoming request's `Host` header — which a client or a misconfigured proxy controls. The host logs a startup warning when it is unset. Set it in any deployment that sits behind a proxy or load balancer. See the [MCP Server guide](/features/mcp-server/#discovery-endpoints).

## Encryption Key Configuration (Required)

`Beacon:EncryptionKey` is **required**. It encrypts sensitive data — most importantly data-source connection strings — at rest using AES-256.

### Generate a Secure Key

```bash
openssl rand -base64 32
```

This produces 32 random bytes, base64-encoded. Generate your own; never copy a key printed in a guide.

### What Beacon Accepts

The key is checked when the host starts, in **every** environment (Development included), and a refusal stops the host with a message that never contains the key:

- it must be **base64 that decodes to at least 32 random bytes** — a passphrase or a short value is refused, and so is base64 of text (every decoded byte printable) or of a repeated pattern (fewer than 16 distinct byte values), which decodes to 32 bytes that are not random;
- it may not be a **publicly known value**: the placeholder the sample host used to ship, or a key printed in earlier versions of this documentation;
- it may not contain **`${`**: .NET configuration does not expand placeholders, so `"${BEACON_ENCRYPTION_KEY}"` in `appsettings.json` would make the text itself the key.

### Supplying the Key

:::caution
Never commit the encryption key to source control.
:::

**Development — User Secrets:**
```bash
dotnet user-secrets set "Beacon:EncryptionKey" "$(openssl rand -base64 32)"
```

**Production — environment variable.** The variable name maps onto the configuration key with a double underscore; nothing goes into `appsettings.json`:
```bash
export Beacon__EncryptionKey="<the generated value>"
```

**Production — a secrets provider**, for example Azure Key Vault through its configuration provider (a secret named `Beacon--EncryptionKey` becomes `Beacon:EncryptionKey`):
```csharp
builder.Configuration.AddAzureKeyVault(new Uri(builder.Configuration["KeyVaultUri"]!), new DefaultAzureCredential());
```

### Replacing the Key

Values are always written with `Beacon:EncryptionKey`. To replace a key (because it was refused at upgrade, or to rotate it), set the new one and list the old one in `Beacon:PreviousEncryptionKeys`. Previous keys are used **only to decrypt**: a value written with one keeps working and moves to the new key when it is saved again or re-encrypted (below). Previous keys are secrets too — supply them the same way, as a list (`Beacon__PreviousEncryptionKeys__0`, …; a single `Beacon__PreviousEncryptionKeys` value is read as one key, with a startup warning) — and remove them once no stored value uses them. They are not checked for strength (a weak or published value is accepted there, by design), since their purpose is to retire a weak key. The first value each previous key decrypts is logged once (Information, with the key's position in the list only).

```bash
export Beacon__EncryptionKey="<the new generated value>"
export Beacon__PreviousEncryptionKeys__0="<the key configured until now>"
```

**Moving stored values to the new key.** `IStoredValueReEncryptionService` re-encrypts, with the current key, every stored value only a previous key decrypts: data-source connection data, repository access tokens, sensitive settings and recipient destinations and headers, archived rows included. Each write is conditional on the value being unchanged, so a concurrent save wins; values no configured key decrypts are logged by id and left alone. The sample host enqueues it once as a Warp job (`ReEncryptStoredValuesJob`) at **every startup while `Beacon:PreviousEncryptionKeys` lists a key**; a host with its own jobs does the same. While a previous key is configured, startup also logs (at Warning, best effort after the host has started) how many stored values are still under a previous key and how many no configured key decrypts. When that count, or the job's log, reports none left, remove the previous keys.

**MCP caller hashes.** The keyed hash Beacon stores for MCP callers (`CallerHash`) is derived from `Beacon:Mcp:CallerHashKey` when it is set, else from the encryption key. Before changing the encryption key, set `Beacon:Mcp:CallerHashKey` to the encryption key in use now (as a secret, like the key itself): the derivation is the same, so the hashes stay the same and audit rows keep grouping together across the change. Without it, rows written before and after a key change no longer group together.

### Key Derivation

The AES-256 key is the SHA-256 of the configured key text — unchanged, so every stored value keeps decrypting. With a key of at least 32 random bytes (which is all Beacon accepts), one SHA-256 is a sound extraction step; a salted or stretched derivation only adds strength to low-entropy passphrases, which are refused. Changing the derivation would need a new ciphertext format that earlier Beacon versions cannot read, which makes a rollback lose every value written in between.

### What Gets Encrypted

- Data-source connection strings (stored in the metadata database)
- Notification recipient destinations and webhook headers (Slack/Teams webhook URLs, Jira API tokens, webhook auth headers). Rows saved by an older version are encrypted by the `IRecipientSecretEncryptionService` job (see [Notification Destinations](#notification-destinations-optional)) or when they are next saved.
- Other sensitive configuration values as needed

API keys are **not** encrypted — they are SHA256-hashed, and the raw key is shown to the user once at creation.

### Startup Errors

A missing or refused key stops the host with an `InvalidOperationException` that says why, never the key itself:

```
Beacon:EncryptionKey must be configured. Generate one with `openssl rand -base64 32` and supply it through a secret store or the Beacon__EncryptionKey environment variable.
Beacon:EncryptionKey must be base64 that decodes to at least 32 random bytes. …
Beacon:EncryptionKey is a publicly known value (the sample placeholder or a key printed in the documentation), … 
Beacon:EncryptionKey contains '${': .NET configuration does not expand placeholders, …
```

## Authentication

Beacon authentication is **cookie-based**. The `Beacon.Auth` cookie is `HttpOnly` with `SameSite=Lax`. Identity is resolved through a pluggable `IBeaconAuthenticationProvider`. There is **no basic auth and no default `admin`/`admin` account** — the first-run setup flow creates the initial admin user.

### Login Form (cookie auth)

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<BeaconScheduler>();
        options.Authentication.EnableLoginForm = true;
        options.AddAuthenticationProvider<DatabaseAuthenticationProvider>();
    })
    .UsePostgreSql(connectionString, "beacon");

// Login redirect target — the SPA serves the login form at /login
builder.Services.AddBeaconCookieAuthentication("/");

// Data Protection (protects the session cookie): Beacon's default, or your own setup — see below
builder.Services.AddBeaconDataProtection(builder.Configuration, builder.Environment)
    .ProtectKeysWithAzureKeyVault(new Uri(builder.Configuration["DataProtection:KeyUri"]!), new DefaultAzureCredential());

app.MapLoginEndpoints("/beacon", beaconConfiguration);
```

#### Data Protection (the keys behind the session cookie)

ASP.NET Core Data Protection signs and encrypts the `Beacon.Auth` cookie and the antiforgery tokens; whoever holds its key ring can issue a session cookie. It belongs to the **host**: `AddBeaconCookieAuthentication` sets no application name and no key storage, so a host that configures Data Protection itself keeps exactly its own setup.

- **Beacon's default** — `AddBeaconDataProtection(configuration, environment)` stores the key ring in the Beacon database (`beacon.data_protection_keys`; `beacon.DataProtectionKeys` on SQL Server) under the application name `Beacon:DataProtection:ApplicationName`. Outside Development the name is **required** (startup fails without it): give each deployment its own (`beacon-production`, `beacon-uat`, …), so a cookie issued by one environment is never accepted by another. In Development it defaults to `Beacon`.
- **Keys encrypted at rest** — outside Development the host **does not start** when the key ring is persisted to a configured repository without key encryption (no `ProtectKeysWith*` call, or the null encryptor). Protect it with a key or certificate that only that environment holds: `ProtectKeysWithAzureKeyVault` (Azure Key Vault key), `ProtectKeysWithCertificate` (a certificate from the machine store or a secret store), or DPAPI-NG on Windows. Development may keep the keys unencrypted. A host that protects the key store by other means (for example a directory only the service account can read) can acknowledge that with `Beacon:DataProtection:AllowUnencryptedKeys: true` instead: startup then logs a Warning rather than refusing.
- **Keys written before encryption are revoked** — at every start outside Development, when the persisted key ring has key encryption, each key that is not revoked and is still stored **without** encryption (written before `ProtectKeysWith*` was added, or by a Development process pointed at the same store) is revoked and a new, encrypted key is created. The sessions those keys protected end once and their users sign in again; the Warning (`DataProtectionKeyRing`, 7121) names the key ids and the count, never key material. A host that does not generate keys itself (`AutoGenerateKeys` off), or whose repository refuses the write, does not start and is told which keys to revoke where the key ring is managed. Never point a Development host at a shared key store.
- **No repository configured** — a host that leaves the key ring to the framework's default location (no `PersistKeysTo*`) gets a startup Warning outside Development: the keys may then live on one machine only, unencrypted, and sessions may end at every restart or not carry over between instances. With Beacon's default, `Beacon:DataProtection:ApplicationName` set to `Beacon` keeps the key ring an earlier version stored in the database, and its sessions.
- **Your own setup** — call `AddDataProtection()` with your own `SetApplicationName`, `PersistKeysTo*` and `ProtectKeysWith*`, and do not call `AddBeaconDataProtection`. Several instances of a deployment must share one key ring.

```json
{
  "Beacon": {
    "DataProtection": {
      "ApplicationName": "beacon-production"
    }
  }
}
```

**Restoring a Beacon database into another environment.** A copy of the Beacon database (a restore into UAT, a DR test, a refresh of a test system) carries the users, and with Beacon's default the key ring rows too. Per-environment key encryption and application names keep a copied key ring useless elsewhere; on top of that, after every such restore:

1. **Sign everyone out in the copy.** Give every user a new security stamp — a request of a session signed in before acts as nobody at once, and the session ends at its next revalidation (within `Beacon:Authentication:Session:RevalidateMinutes`):
   - PostgreSQL (12 and later): `UPDATE beacon.users SET security_stamp = md5(random()::text || clock_timestamp()::text || id::text);`
   - SQL Server: `UPDATE beacon.Users SET SecurityStamp = LOWER(REPLACE(CONVERT(nvarchar(36), NEWID()), '-', ''));`
2. **Give the copy its own key ring.** Delete the copied key rows in the copy — `beacon.data_protection_keys` on PostgreSQL, `beacon.DataProtectionKeys` on SQL Server (or revoke every key with `IKeyManager.RevokeAllKeys`) — so it creates keys of its own, protected with that environment's key.
3. **If a copy may have been exposed, rotate the source's keys too** — revoke every key of the source environment (`IKeyManager.RevokeAllKeys(DateTimeOffset.UtcNow, "reason")`) and sign everyone out there the same way.

#### Local password sign-in

Password sign-in through the login form is **on** by default, for compatibility. With single sign-on, turn it off so people sign in through the identity provider (and its multi-factor authentication and conditional access), and keep one emergency account:

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

| Option | Description | Default |
|--------|-------------|---------|
| `Enabled` | Password sign-in through the login form for every account. `false`: `POST /beacon/api/auth/login` is not mapped, Beacon's password providers refuse every account (the hybrid provider does not fall back to its external login, and the external JWT login refuses too), and the login page shows no password form. | `true` |
| `BreakGlassUserNames` | Up to 3 user names that may still sign in with a password while `Enabled` is `false` — for emergencies, when the identity provider is unavailable. Each is compared **exactly** (case included) with the user name the account is stored with, whether its holder types that name or an e-mail address. With at least one listed, the endpoint stays mapped; the login page shows the password form at `/login?local=1`. Every name gets the same password check, and whichever provider authenticated the password (Beacon's, the external JWT login, a host's, or `IBeaconAuthenticationService.SignInAsync`), only a stored internal user, enabled and not archived, whose stored user name is listed signs in. Each such sign-in is logged at Warning (`BreakGlassSignIn`, 7119); a password sign-in by any other account is refused with the generic answer and logged without the name typed (`PasswordSignInRefused`, 7120). | empty |

The first-run super admin is a password account: list it as the break-glass account (or another Admin-held local account) before turning local login off, or nobody can sign in with a password at all. A blank entry or more than 3 names fails startup. With `Enabled` false, startup also fails when nobody could sign in at all (single sign-on off, no external login page and no break-glass account), and warns, by position in the list and never the name, about each break-glass name that matches no enabled internal user exactly.

#### Session lifetime and revalidation

A `Beacon.Auth` session is checked against the user store at most every `RevalidateMinutes` (with user management on: a missing, disabled or archived user, or a changed security stamp, signs it out; its roles become the stored ones), and ends `AbsoluteLifetimeHours` after its sign-in however often its cookie is renewed. See [User Management → Sign-in sessions](/features/user-management/#sign-in-sessions).

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

| Option | Description | Default |
|--------|-------------|---------|
| `RevalidateMinutes` | How often, at most, a session is checked against the user store (0–1440; `0` checks every request). | `5` |
| `AbsoluteLifetimeHours` | Hours after its sign-in at which a session ends, whatever sliding expiration or "remember me" would allow (1–8760). Also caps the session cookie's expiry — at sign-in and whenever the cookie is renewed (a check, or sliding expiration; a cookie scheme without sliding expiration keeps its expiry) — and the lifetime of a realtime connection. | `12` |

A value out of range fails startup.

A realtime (SignalR) connection is checked against the user store when it is opened and is closed by the server when the session's next check would be due (`RevalidateMinutes`, at least one minute); the shell reconnects, which checks the session again, so a revoked or demoted user loses the connection within that interval. The expiry is reported for the hub's requests only and never written to the cookie: the hub's requests never renew the cookie, sliding expiration included. API keys get no hub connection.

### Authentication Providers

| Provider | Use Case |
|----------|----------|
| `DatabaseAuthenticationProvider` | Internal users with passwords stored in Beacon |
| `JwtExternalApiAuthenticationProvider` | External JWT / OAuth identity provider |
| `HybridAuthenticationProvider` | Both internal and external users |

### Optional: OIDC / SSO

```csharp
builder.Services.AddBeaconOidcAuthentication(builder.Configuration);
```

```json
{
  "Beacon": {
    "Authentication": {
      "Oidc": {
        "Enabled": true,
        "Authority": "https://login.microsoftonline.com/{YOUR_TENANT_ID}/v2.0",
        "ClientId": "{YOUR_CLIENT_ID}",
        "CallbackPath": "/signin-oidc",
        "Scopes": ["openid", "profile", "email"],
        "AllowedTenants": ["{YOUR_TENANT_ID}"],
        "BlockGuests": true,
        "RequiredRoles": [],
        "RequiredGroups": [],
        "RequireMfa": false,
        "AcceptedAcrValues": [],
        "DisplayName": "Microsoft",
        "McpJwksEndpoint": "https://login.microsoftonline.com/{YOUR_TENANT_ID}/discovery/v2.0/keys"
      }
    }
  }
}
```

Supply `ClientSecret` like the encryption key — the `Beacon__Authentication__Oidc__ClientSecret` environment variable or a secrets provider, never `appsettings.json` (a `${...}` placeholder there is not expanded).

Beacon decides who may sign in **before** it provisions anything. A subject that is not admitted gets a "not permitted" message on the login page and no Beacon user is created for it.

| Option | Description | Default |
|--------|-------------|---------|
| `AllowedTenants` | Entra tenant ids (`tid` claim) whose users may sign in. **Startup fails** when SSO is enabled with no tenant listed and `AllowAnyTenant` off. | empty |
| `AllowAnyTenant` | Skip the tenant check. Only for an identity provider that issues no `tid` claim (the `Authority` then decides who can sign in). | `false` |
| `BlockGuests` | Refuse Entra B2B guests: an `acct` claim of `1`, or an `idp` claim that is not exactly the token issuer or one of the tenant's own Entra issuers. Applies to tokens that carry `tid`. A token with neither `acct` nor `idp` is admitted as a member (a one-time Warning says so). | `true` |
| `RequiredRoles` / `RequiredGroups` | App roles (`roles` claim) / group object ids (`groups` claim). When either is set, a user must hold at least one listed role or group (case-insensitive). An app role listed here must exist in the app registration and be assigned, or nobody is admitted. | empty |
| `DefaultRoleName` | Role given to a user provisioned on first SSO sign-in. **Unset means no role**: the user can sign in but sees nothing until an administrator assigns one. | unset |
| `EnableFrontChannelLogout` | Serve the OIDC front-channel sign-out path (`/signout-oidc`). Off by default: it is an anonymous `GET` that ends the session. | `false` |
| `RequireMfa` | Refuse an SSO sign-in that the ID token does not show as multi-factor: its `amr` claim must contain `mfa`, or its `acr` claim must be one of `AcceptedAcrValues`. A refused sign-in is logged (reason `MfaNotPerformed`, with the tenant, the issuer and a hash of the subject) and the login page says that multi-factor authentication is required; nothing is provisioned. Check what your identity provider puts in the ID token before turning it on. With Microsoft Entra ID, add **`amr`** as an optional claim of the ID token in the app registration (Token configuration → Add optional claim → ID → `amr`, or `optionalClaims.idToken` in the manifest): a v2.0 ID token carries it only when it is requested, and it contains `mfa` only when the user completed multi-factor authentication. It applies to the interactive SSO sign-in only — see the boundary below. | `false` |
| `AcceptedAcrValues` | `acr` values (exact) that count as multi-factor authentication when `RequireMfa` is on. | empty |

**What `RequireMfa` covers.** Only the interactive SSO sign-in (`/beacon/api/auth/sso/challenge`) is checked. It does not apply to bearer tokens on the REST API or `/beacon/mcp` (an Entra caller's token is not checked for `amr` or `acr`; require MFA for those clients with Conditional Access on the token issuance), to API keys, to the login-form external JWT flow, or to password sign-in — turn local login off and keep only a break-glass account (see [Local password sign-in](#local-password-sign-in)), whose use bypasses the identity provider by design.

For reliable guest detection, add `acct` as an optional claim (ID and access tokens) in the Entra app registration: `idp` alone only reveals guests whose home is another identity provider, and while `BlockGuests` is on Beacon logs a one-time Warning when a token carries neither claim. With `AllowAnyTenant` on and an Entra authority, Beacon logs a Warning at startup, since every tenant the authority accepts can then sign in.

Prefer **app roles** in `RequiredRoles` over `RequiredGroups`: Entra leaves the `groups` claim out of a token when the user belongs to more groups than fit (the "overage" case, about 200 for a JWT), and such a user then fails the group requirement. As defence in depth, set the enterprise application to **assignment required** and assign only the approved users or groups. `DisplayName` labels the SSO button on the login page.

### API Keys

Beacon issues API keys for programmatic and MCP access — see the [API Keys Guide](/features/api-keys/):

- SHA256-hashed at rest; the raw key is shown **once** at creation
- Carry scopes: `Read` (read-only requests) and `Execute` (everything else, including MCP)
- Support optional project restrictions
- Always expire — see [API Keys](#api-keys) for the lifetime settings
- Authenticated by `ApiKeyAuthMiddleware`, which runs **before** `UseAuthentication`

### JWT Bearer for MCP Clients

MCP clients can also authenticate with **JWT bearer** tokens issued by your OIDC provider. When OIDC is enabled, set `McpJwksEndpoint` to the provider's JWKS (signing keys) URL — the sample host then enables bearer tokens with the OIDC authority as the issuer and the client id as the audience. This lets AI assistants use the same identity provider as your users, without cookies or long-lived API keys.

In your own host, bearer tokens are enabled with `AddBeaconJwtAuthentication`. Whenever bearer authentication or an external login endpoint is on, **at least one issuer and one audience are required**, issuer, audience and lifetime validation cannot be turned off, and `ClockSkew` may be at most five minutes — the host refuses to start otherwise:

```csharp
builder.Services.AddBeaconJwtAuthentication(jwt =>
{
    jwt.EnableBearerAuthentication = true;
    jwt.Validation.JwksEndpoint = "https://login.microsoftonline.com/{tenant}/discovery/v2.0/keys";
    jwt.Validation.ValidIssuer = "https://login.microsoftonline.com/{tenant}/v2.0";
    jwt.Validation.ValidAudience = "{beacon-client-id}"; // the application (client) id GUID for v2.0 access tokens
});
```

Match the issuer and audience to the access-token version your app registration issues (`accessTokenAcceptedVersion` in its manifest). A **v2.0** access token (`accessTokenAcceptedVersion: 2`) has the issuer `https://login.microsoftonline.com/{tenant}/v2.0` and the application's client id GUID as `aud`. A **v1.0** token (the default, `null` or `1`) has the issuer `https://sts.windows.net/{tenant}/` and the Application ID URI (for example `api://{beacon-client-id}`) as `aud`. List both in `ValidIssuers` / `ValidAudiences` only if you accept both versions.

On `/beacon/mcp` the token is mapped by the MCP caller configuration (see [Entra MCP callers](/features/mcp-entra-callers/)). On every other route a bearer token must be an access token, must pass the SSO admission rules when the SSO authority issued it, and must name an **existing, enabled, external Beacon user** — by its subject and issuer, or a pre-registered user's subject when only one issuer is configured (see [User Management → Option 2](/features/user-management/#option-2-external-identity-provider-jwtoauth)). The session gets that user's **Beacon roles**, never roles from the token. Any other token gets `401` with `WWW-Authenticate: Bearer error="invalid_token"`. This needs user management; without it, bearer tokens are refused outside `/beacon/mcp`.

The access-token check needs positive evidence and is the same whether or not SSO is enabled; the token returned to the [login form](/features/user-management/#jwtexternalapiauthenticationprovider) passes it too:

- **Microsoft Entra ID** (issuer `login.microsoftonline.com` or `sts.windows.net`): the token names the client it was issued to (`azp` or `appid`) and carries `scp` or `roles`.
- **Any other issuer**: the JWT header is `typ: at+jwt` (RFC 9068), or the token carries the claim named by `AccessTokenClaim`. Set it for a provider whose access tokens lack `at+jwt` — a claim it puts in access tokens and never in ID tokens. A claim any ID token can carry (`sub`, `aud`, `nonce`, `azp`, `roles`, …) stops the host at startup.
- A token with `nonce`, `at_hash` or `c_hash` is an ID token and is always refused; `roles` alone never counts as evidence.

```csharp
builder.Services.AddBeaconJwtAuthentication(jwt =>
{
    jwt.EnableBearerAuthentication = true;
    jwt.Validation.JwksEndpoint = "https://idp.example.com/.well-known/jwks.json";
    jwt.Validation.ValidIssuer = "https://idp.example.com";
    jwt.Validation.ValidAudience = "beacon";
    jwt.AccessTokenClaim = "scope"; // default: not set (only typ: at+jwt is accepted from this issuer)
});
```

:::note
For complete user-management and provider documentation (external JWT setup, custom providers, roles), see the [User Management Guide](/features/user-management/).
:::

## Authorization

Authorization is controlled via options and a pluggable provider.

```csharp
options.Authorization.Enabled = true;
options.AddAuthorizationProvider<MyCustomProvider>();
```

### Custom Authorization Provider

```csharp
public class MyAuthorizationProvider : IBeaconAuthorizationProvider
{
    private readonly IBeaconUserContext _userContext;

    public MyAuthorizationProvider(IBeaconUserContext userContext)
    {
        _userContext = userContext;
    }

    public Task<bool> HasReadPermissionAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_userContext.IsAuthenticated);

    public Task<bool> HasWritePermissionAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_userContext.HasClaim(BeaconClaims.Role, "Admin"));

    // Implement other methods...
}

// Register it
options.AddAuthorizationProvider<MyAuthorizationProvider>();
```

When `Authorization.Enabled` or user management is on, Beacon enforces Viewer vs Editor server-side on `/beacon/api`: `GET` requests need read permission (Viewer or above) and every other method needs write permission (Editor or above). The exceptions are running query previews and changing your own password, which a Viewer may do. Read-scoped API keys cannot call mutating endpoints, whatever role the key's user holds. A route gated by a [functional permission](/features/authorization/#functional-permissions) admits its holder instead, with no Viewer or Editor role.

### Functional permissions: role names, identity-provider app roles, separation of duties

The Admin role's duties are split into six functional permissions — `Approver`, `Auditor`, `SecurityAdmin`, `UserAdmin`, `DataSourceAdmin` and `MigrationOperator` (see [Functional permissions](/features/authorization/#functional-permissions)). Admins keep all of them; nothing here needs configuring to keep the current behaviour.

```json
{
  "Beacon": {
    "Authorization": {
      "Roles": {
        "Approver": "Approver",
        "Auditor": "Compliance Reviewer",
        "SecurityAdmin": "SecurityAdmin",
        "UserAdmin": "UserAdmin",
        "DataSourceAdmin": "DataSourceAdmin",
        "MigrationOperator": "MigrationOperator",
        "HonourHostRoleClaims": false
      },
      "IdpRoles": {
        "Enabled": false,
        "Map": {
          "beacon.auditor": "Auditor"
        },
        "ClaimLifetimeMinutes": 60
      },
      "SeparationOfDuties": {
        "Enabled": false
      }
    }
  }
}
```

| Option | Description | Default |
|--------|-------------|---------|
| `Roles:{Permission}` | The Beacon role that grants the permission. Beacon creates the role (level 0, a system role) when roles are next listed or seeded, under this name. The name may not be `Admin`, `Editor` or `Viewer`, nor one another permission uses (startup fails), and is compared ignoring case. Only a level-0 system role with the name grants the permission: a role someone created under it grants nothing (seeding logs its id). | the permission's name |
| `Roles:HonourHostRoleClaims` | On a host **without** Beacon user management: let the session's role claims named like a permission's role grant that permission. Off, such a session holds functional permissions only through the `Admin` role claim (all of them). Ignored with Beacon user management, where stored roles decide. | `false` |
| `IdpRoles:Enabled` | Map identity-provider app roles onto permissions at single sign-on. A mapped permission is added to the session's claims when the user signs in and is **never stored**: it is honoured for `ClaimLifetimeMinutes` after that sign-in, does not appear on the Users page, and an [API key](/features/api-keys/) never carries it. Startup fails when it is on with an empty `Map`. | `false` |
| `IdpRoles:Map` | App role value (the `roles` claim, or the identity's role claim type) → permission name. Only the six permissions may be mapped, never `Admin`, `Editor` or `Viewer` (startup fails otherwise); with separation of duties on, not both permissions of a conflicting pair. App role values are compared exactly. | empty |
| `IdpRoles:ClaimLifetimeMinutes` | How long a mapped permission counts after the sign-in that mapped it (1–1440; startup fails otherwise). The session cookie lives longer and slides; signing in again renews the mapped permissions. | `60` |
| `SeparationOfDuties:Enabled` | Refuse (`400`) role assignments that give one user both permissions of a conflicting pair: Approver + SecurityAdmin, Auditor + SecurityAdmin, Auditor + UserAdmin, UserAdmin + Approver, UserAdmin + MigrationOperator (also when an Admin assigns them). On, a mapped permission that conflicts with the user's stored roles is not added at sign-in, and a caller who holds both permissions of a pair anyway (roles assigned before the switch) holds neither. The startup log counts the users concerned. | `false` |

`Beacon:Authentication:Oidc:DefaultRoleName` and `Beacon:Mcp:Callers:Users:DefaultRoleName` may not name the `Admin` role or a functional permission's role: startup fails, since every provisioned user would get it.

:::note
For complete authorization documentation, see the [Authorization Guide](/features/authorization/).
:::

## User Management

```csharp
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
```

| Option | Description | Default |
|--------|-------------|---------|
| `Enabled` | Enable user-management features | `false` |
| `AllowInternalUsers` | Allow password-based users | `true` |
| `MinimumPasswordLength` | Minimum password length | `8` |
| `RequirePasswordComplexity` | Require mixed case, numbers, symbols | `true` |
| `SetupToken` | Secret the first-run setup page must present; at least 32 characters, or startup fails. Also read from `Beacon:UserManagement:SetupToken`. When unset, Beacon generates one per process and prints it once to the process console (standard error, never the log) while no super admin exists. Required when several replicas serve the setup page. | unset (generated) |
| `LocalLogin` | Password sign-in through the login form, with break-glass accounts. Also bound from `Beacon:UserManagement:LocalLogin`. See [Local password sign-in](#local-password-sign-in). | on |

A password an Admin sets (a new internal user, a password reset) must be replaced by the user at their first sign-in; see [User Management → Passwords set by an administrator](/features/user-management/#passwords-set-by-an-administrator).

:::caution[Upgrading from 4.5]
Identity and admission are tighter: first-run setup needs a setup token, SSO needs `AllowedTenants` (or `AllowAnyTenant`) and no longer gives new users a role by default, JWT issuer, audience and lifetime validation are mandatory, REST bearer tokens must name an existing external Beacon user, and `GET /beacon/api/auth/signout` is gone. See [User Management → Upgrading from 4.5](/features/user-management/#upgrading-from-45) for what to change.
:::

:::caution[Upgrading: credentials at rest and local login]
- **`Beacon:EncryptionKey` is checked in every environment.** A passphrase, a short key, the sample placeholder, a key printed in these docs or a `${...}` value now stops the host. Set a new key (`openssl rand -base64 32`) and list the old one in `Beacon:PreviousEncryptionKeys` so stored values stay readable; see [Replacing the Key](#replacing-the-key). The sample host no longer ships a key: set one with User Secrets.
- **Data Protection is the host's.** `AddBeaconCookieAuthentication` no longer stores the key ring in the Beacon database or sets the application name `Beacon`. To keep Beacon's storage, call `AddBeaconDataProtection` — and, outside Development, set `Beacon:DataProtection:ApplicationName` and protect the keys with `ProtectKeysWith*`, or the host does not start. A host that does neither falls back to the framework's default key storage (startup warns): sessions may not survive a restart or work across instances. Keys written before key encryption was turned on are revoked and replaced at startup. Either way everyone signs in again once after the upgrade. See [Data Protection](#data-protection-the-keys-behind-the-session-cookie).
- **Local login stays on** (`Beacon:UserManagement:LocalLogin:Enabled` defaults to `true`). With SSO, turning it off and keeping a break-glass account is recommended.
:::

## API Keys

Every new [API key](/features/api-keys/) expires. A key created without an expiry date lives 90 days; no key may be created to live longer than `MaxLifetimeDays`. Keys created before these rules keep their stored expiry; one stored without an expiry keeps working unless `EnforceMaxLifetimeOnExistingKeys` is set.

```json
{
  "Beacon": {
    "ApiKeys": {
      "MaxLifetimeDays": 365,
      "EnforceMaxLifetimeOnExistingKeys": false,
      "MaxOwnerInactivityDays": 0
    }
  }
}
```

| Option | Description | Default |
|--------|-------------|---------|
| `MaxLifetimeDays` | Longest lifetime a new key may be created with, in days (1–3650). The 90-day default is capped at this value. A value out of range fails startup. | `365` |
| `EnforceMaxLifetimeOnExistingKeys` | When `true`, a key stored without an expiry (created before expiry became mandatory) expires `MaxLifetimeDays` days after it was created. Nothing stored changes, so turning it off again restores such keys. When `false`, they keep working and each logs a warning with its key id once per process. | `false` |
| `MaxOwnerInactivityDays` | When set, a key stops working (`401`, logged as `owner_inactive`) while its owner has not signed in to Beacon with their own identity for that many days (a call to `/beacon/mcp` or the REST API with the owner's delegated token counts as a sign-in; an API key never does); it works again after the owner signs in. `0` turns the rule off (0–3650). | `0` |

:::caution[Upgrading from 4.5]
API-key scopes are enforced on every route, the `Admin` scope is retired, `Execute` follows the owner's current role, new keys must expire, keys are managed from a browser session only, a disabled or archived user's keys are revoked, and `/beacon/mcp` should be mapped with `app.MapBeaconMcp()`. See [API Keys → Upgrading from 4.5](/features/api-keys/#upgrading-from-45) for what to change.
:::

## Metadata Refresh

A forced [metadata refresh](/features/data-sources/#refreshing-metadata) re-reads a data source's tables and columns from the live database. Each data source is force-refreshed at most once per cooldown; a second request inside it answers `429`.

```json
{
  "Beacon": {
    "Metadata": {
      "ForcedRefreshCooldownMinutes": 15
    }
  }
}
```

| Option | Description | Default |
|--------|-------------|---------|
| `ForcedRefreshCooldownMinutes` | Minutes after a successful forced refresh of a data source before the next one is accepted (0–1440; `0` turns the cooldown off). A failed refresh does not start it, changing the data source clears it, and a changed value applies to the next request. Kept per Beacon process, so each instance of a multi-instance host keeps its own. An Admin or a DataSourceAdmin is not held to it (one refresh of a data source still runs at a time). A value out of range fails startup. | `15` |

## Query Execution Row Cap

Every statement Beacon runs for one of its own features — a query preview, a subscription run, an AI actor's check —
returns at most this many rows, and runs at most this long. The cap is written into the SQL (a statement's own larger
`LIMIT` / `TOP` / `FETCH` is lowered to it). On PostgreSQL, MySQL, SQL Server, Azure Synapse and Snowflake the read also
stops one row past the cap; BigQuery and Databricks read the capped statement's result whole, which the cap in the SQL
keeps to one row past it. A larger result fails the run instead of being cut short, with a message that names
`Beacon:Query:MaxExecutionRows`. A data migration's read has its own budget instead, `Beacon:Migrations:MaxRows` (see
[Data Migration](#data-migration)).

```json
{
  "Beacon": {
    "Query": {
      "MaxExecutionRows": 100000,
      "StatementTimeoutSeconds": 120
    }
  }
}
```

| Option | Description | Default |
|--------|-------------|---------|
| `MaxExecutionRows` | The most rows one statement may return (1–10,000,000). Every row is held in memory while a result is processed, so raise it only as far as the host's memory allows. A value out of range fails startup. | `100000` |
| `StatementTimeoutSeconds` | How long one statement may run before it is cancelled (1–3,600): a query step, a final query on the in-memory join, a migration read. A subscription's own timeout may only be shorter: saving one above this value is refused, and a stored one above it runs with this value. Metadata samples stop sooner (30 seconds), and the MCP tools keep their own `statementTimeoutSeconds` setting. A value out of range fails startup. | `120` |

## Data Migration

Data migration is the one feature that writes to a connected database, and it is **off unless the host turns it on**.
While it is off, the migration endpoints are not mapped (`404`), the shell hides the feature, no job is created or run
(by anyone, an Admin included), and no data source can be marked as a migration write target. It is a deployment lock:
nothing changes it at runtime. See [Data Migration](/features/data-migration/#turning-data-migration-on).

```json
{
  "Beacon": {
    "Migrations": {
      "Enabled": true,
      "MaxRows": 100000
    }
  }
}
```

| Option | Description | Default |
|--------|-------------|---------|
| `Enabled` | Maps the migration endpoints and lets migrations run, into the data sources an Admin marks as migration write targets (which Beacon never reads). | `false` |
| `MaxRows` | The most rows one migration run may read and write (1–10,000,000), in place of `Beacon:Query:MaxExecutionRows`. The cap is written into the source SQL; a larger source fails the run before anything is written. A run holds every row in memory. A value out of range fails startup. | `100000` |

:::note[Upgrading]
A host that used data migration before must set `Beacon:Migrations:Enabled` to `true`, and an Admin must mark each
destination as a migration write target; until then its jobs do not run.
:::

## Approval Workflow

The query change approval workflow (maker-checker on what queries run) is **off unless the host turns it on**, in code:
Beacon cannot tell a new installation from an existing one, so turning it on is the host's decision. See
[Queries → Approval workflow](/features/queries/#approval-workflow) for what it covers.

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options =>
{
    // ...
    options.EnableApprovalWorkflow(workflow =>
    {
        workflow.RequireApprovedVersionToRun = true; // strict mode, recommended for regulated deployments
    });
});
```

| Option (`options.ApprovalWorkflow.*`) | Description | Default |
|--------|-------------|---------|
| `Enabled` | Every change to a query's executable content (an edit, a new query, a version restore, an AI actor's refinement or new query) waits for a second person's approval; subscription runs execute only stored SQL that is the query's active version; approving an AI actor's plan takes an Approver other than its creator; publishing a query as an MCP tool is refused to whoever requested or approved the version it runs. | `false` |
| `AllowSelfApproval` | Lets the requester of a change (and whoever shaped it: an AI plan's reviewer and revision requesters) approve it, publish the query as an MCP tool, an AI actor's creator and a plan's revision requesters approve its plans, and a caller Beacon cannot name submit a change. For a host with a single person who can approve. | `false` |
| `RequireApprovedVersionToRun` | Strict mode: a subscription runs only when its query's active version carries an approved request for exactly its content (the approval recorded the version's content hash) and the stored SQL is that content — the rule saved-query MCP tools then follow too; a run that may not is recorded as skipped. Without it, a run whose stored SQL differs from the active version is still refused, a query never versioned runs as stored, and a tool may run a version approved before Beacon recorded content hashes. Recommended for regulated deployments. | `false` |

**Startup notes.** Beacon logs once at startup how query changes are governed: the workflow off is a **Warning**
outside Development (Information in Development); `AllowSelfApproval` on is a **Warning**; with the workflow on, an
Information line names the run rule (strict or not). With the workflow on, once the host has started, Beacon counts in
the background what the rules leave open and logs a **Warning** for each count above zero (counts, option names and
tool names only — never SQL):

| Warning | Logged when | Remedy |
|---------|-------------|--------|
| Queries never versioned | Queries with steps and no active version (stored before the workflow was on). Outside strict mode they run as stored, each run logged; in strict mode they do not run. | Save each query to submit its SQL, and approve it. |
| Queries with subscriptions whose stored SQL is not their active version's content | Always with the workflow on: every run of them is skipped. | Save the query to submit its stored SQL for approval, or restore a version and approve the restore. |
| Active versions not approved for their content hash | Strict mode only: queries with a subscription or an enabled MCP tool whose active version went live while the workflow was off or was approved before Beacon kept content hashes; neither runs. | Restore the active version and approve the restore. |
| Fewer than two users can approve | `AllowSelfApproval` off and Beacon user management on, counting enabled users who hold the Approver permission (or Admin) through their stored roles; skipped when permissions come from identity-provider roles. | Give a second user the Approver role (or Admin), or turn on `AllowSelfApproval` on a host with one approver. |
| MCP tools on approvals without a content hash | Outside strict mode: enabled tools whose active version's approval recorded no hash; names up to 20 tools. | Restore the version and approve the restore; strict mode would stop these tools until then. |

A count that cannot be read (the database is unreachable at startup, say) is logged as **Information**, never a
failure. Each subscription run adds its own **Warning**: a run of a never-versioned query outside strict mode; a run
refused because the stored SQL is not the approved version, naming the remedy (save the query to submit its SQL, or
restore a version and approve it); and a run refused because the active version's snapshot cannot be read (restore a
readable version and approve it).

## AI / LLM Configuration (Optional — Experimental)

:::caution[Experimental]
AI-powered features (auto-documentation, natural-language → query, anomaly detection) are experimental and may produce incorrect results. Configure only if you will validate AI-generated content.
:::

The LLM provider is **runtime-swappable** — it can be changed at runtime via Admin Settings without a restart. All LLM calls go through a request queue that enforces concurrency limits. Do not hard-code provider assumptions; read capabilities through the abstraction.

:::note
LLM settings in `appsettings.json` act as startup defaults. Values configured in [Admin Settings](/features/admin-settings/) take precedence and support hot-swapping providers.
:::

### Supported Providers

The `Provider` value is one of: `OpenAI`, `Claude` (Anthropic), `AzureOpenAI`, `Bedrock` (AWS).

### Basic Configuration

```json
{
  "Beacon": {
    "LLM": {
      "Provider": "OpenAI",
      "ApiKey": "sk-your-api-key-here",
      "Model": "gpt-4o"
    }
  }
}
```

### Complete Configuration

```json
{
  "Beacon": {
    "LLM": {
      "Provider": "OpenAI",
      "ApiKey": "sk-your-api-key-here",
      "Model": "gpt-4o",
      "FastModel": "gpt-4o-mini",
      "Limits": {
        "MaxConcurrentRequests": 50,
        "TokensPerMinute": 80000,
        "RequestsPerMinute": 1000,
        "MonthlyBudget": 100.00
      }
    }
  }
}
```

`FastModel` is optional: Beacon routes simple, high-volume operations (classification, short summaries) to the fast model and reserves the main `Model` for complex generation. If omitted, sensible per-provider defaults are used (`gpt-4o-mini` for OpenAI, `claude-haiku-4-20250514` for Claude).

### Anthropic Claude

```json
{
  "Beacon": {
    "LLM": {
      "Provider": "Claude",
      "ApiKey": "sk-ant-...",
      "Model": "claude-sonnet-4-20250514"
    }
  }
}
```

### Azure OpenAI

Azure requires the `Endpoint` of your resource; `Model` is your deployment name.

```json
{
  "Beacon": {
    "LLM": {
      "Provider": "AzureOpenAI",
      "ApiKey": "your-azure-key",
      "Model": "your-deployment-name",
      "Endpoint": "https://your-resource.openai.azure.com"
    }
  }
}
```

### AWS Bedrock

Bedrock requires a `Region`. Credentials come either from the default AWS chain (IAM role, environment variables) — leave `ApiKey` empty — or explicitly as `"accessKey:secretKey"` (plus an optional `SessionToken` for temporary credentials).

```json
{
  "Beacon": {
    "LLM": {
      "Provider": "Bedrock",
      "ApiKey": "",
      "Region": "eu-west-1",
      "Model": "eu.anthropic.claude-sonnet-4-5-20250929-v1:0"
    }
  }
}
```

### Rate Limiting

The request queue caps concurrency and throughput:

| Setting | Description |
|---------|-------------|
| `MaxConcurrentRequests` | Max parallel AI requests |
| `TokensPerMinute` | Token throughput cap |
| `RequestsPerMinute` | Request rate cap — match your provider tier |
| `MonthlyBudget` | Monthly spend cap (USD) tracked by the usage service |

### Production — Use Environment Variables

Keep the key out of `appsettings.json` (a `${...}` placeholder there is not expanded); the environment variable maps onto the configuration key with double underscores:

```bash
export Beacon__LLM__ApiKey="<your provider key>"
```

## Metadata Database Provider

Beacon's metadata database runs on **EF Core 10** with dual-provider support (PostgreSQL and SQL Server). The default schema is `beacon` (configurable). Dapper is used for hot paths.

### PostgreSQL

```csharp
using Beacon.Core;
using Beacon.Core.PostgreSql;

builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<BeaconScheduler>();
    })
    .UsePostgreSql(
        builder.Configuration.GetConnectionString("BeaconContext")!,
        "beacon"); // optional schema, defaults to "beacon"
```

```json
{
  "ConnectionStrings": {
    "BeaconContext": "Host=localhost;Database=beacon;Username=postgres;Password=yourpassword"
  }
}
```

### SQL Server

```csharp
using Beacon.Core;
using Beacon.Core.SqlServer;

builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<BeaconScheduler>();
    })
    .UseSqlServer(
        builder.Configuration.GetConnectionString("BeaconContext")!,
        "beacon");
```

```json
{
  "ConnectionStrings": {
    "BeaconContext": "Server=localhost;Database=beacon;User Id=sa;Password=YourPassword123!;TrustServerCertificate=True"
  }
}
```

## Data-Source Connectors

Register a connector for each kind of data source you intend to monitor. Beacon ships nine connectors:

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options => { /* ... */ })
    .AddPostgreSqlConnector()
    .AddSqlServerConnector()
    .AddMySqlConnector()
    .AddBigQueryConnector()
    .AddSnowflakeConnector()
    .AddDatabricksConnector()
    .AddAzureSynapseConnector()
    .AddCloudWatchConnector()
    .AddApiConnector()
    .UsePostgreSql(connectionString, "beacon");
```

| Connector | Package |
|---|---|
| PostgreSQL | `Beacon.Connector.PostgreSql` |
| SQL Server | `Beacon.Connector.SqlServer` |
| MySQL | `Beacon.Connector.MySql` |
| Google BigQuery | `Beacon.Connector.BigQuery` |
| Snowflake | `Beacon.Connector.Snowflake` |
| Databricks | `Beacon.Connector.Databricks` |
| Azure Synapse | `Beacon.Connector.AzureSynapse` |
| AWS CloudWatch | `Beacon.Connector.CloudWatch` |
| Generic REST API | `Beacon.Connector.Api` |

Individual data-source connection strings are entered in the UI and encrypted at rest with your `Beacon:EncryptionKey`. See the [Data Sources Guide](/features/data-sources/).

## Connection String Reference

### PostgreSQL

```
Host=hostname;Port=5432;Database=dbname;Username=user;Password=pass
Host=hostname;Database=dbname;Username=user;Password=pass;SSL Mode=Require
Host=hostname;Database=dbname;Username=user;Password=pass;Pooling=true;MinPoolSize=0;MaxPoolSize=100
```

### SQL Server

```
Server=hostname;Database=dbname;User Id=user;Password=pass;TrustServerCertificate=True
Server=hostname;Database=dbname;Integrated Security=True
Server=hostname;Database=dbname;User Id=user;Password=pass;Encrypt=True;TrustServerCertificate=False
```

### MySQL

```
Server=hostname;Port=3306;Database=dbname;Uid=user;Pwd=pass
Server=hostname;Database=dbname;Uid=user;Pwd=pass;SslMode=Required
```

## Scheduling

Beacon does not bundle a job runner. All recurring work goes through the `IBeaconScheduler` abstraction — you plug in the scheduler your host already uses:

```csharp
namespace Beacon.Core.Worker;

public interface IBeaconScheduler
{
    void AddOrUpdate(int subscriptionId, string subscriptionName, string cron);
    void Remove(int subscriptionId, string subscriptionName);

    // optional — data-quality contract evaluation schedules
    void AddOrUpdateDataQualityJob(int contractId, string contractName, string cron);
    void RemoveDataQualityJob(int contractId, string contractName);

    // optional — fire-and-forget background work used by the AI features;
    // returns the job id (used to correlate JobStatusChanged push events)
    string EnqueueProjectDocumentation(int projectId, int userId, string? notifyUserId);
    string EnqueueAiActorThinkCycle(int actorId, int subscriptionId);
}
```

The optional members have default implementations that throw, so hosts that don't use data-quality contracts or AI features only implement the two subscription methods.

Beacon calls `AddOrUpdate` whenever a subscription is created or its cron changes, and `Remove` when it's deleted or disabled. Your implementation maps those calls onto recurring jobs that invoke `IJobService.ExecuteQuery(subscriptionId)` on the cron schedule.

### Scheduler Implementation

```csharp
using Beacon.Core.Worker;

public class BeaconScheduler : IBeaconScheduler
{
    public void AddOrUpdate(int subscriptionId, string subscriptionName, string cron)
    {
        // register/update a recurring job in your job runner that calls
        // IJobService.ExecuteQuery(subscriptionId) on the given cron schedule
    }

    public void Remove(int subscriptionId, string subscriptionName)
    {
        // remove the recurring job
    }
}

// Register it
options.AddBeaconScheduler<BeaconScheduler>();
```

Any job runner with recurring/cron support works. We recommend [Moberg Warp](https://moberghr.github.io/warp/) — define an `IJob` that calls `IJobService.ExecuteQuery` and map `AddOrUpdate`/`Remove` onto Warp's `AddOrUpdateRecurringJob`/remove APIs; you get retries, concurrency guards, and a job dashboard out of the box. Quartz.NET is another valid choice. `Beacon.SampleProject` ships a complete working reference implementation you can copy as a starting point.

## Email Adapter

Beacon does not ship a default email implementation. Provide your own `IEmailAdapter` and register it via `options.AddEmailAdapter<...>()`.

```csharp
using Beacon.Core.Adapters.Mail;
using System.Net;
using System.Net.Mail;

public class SmtpEmailAdapter : IEmailAdapter
{
    private readonly IConfiguration _configuration;

    public SmtpEmailAdapter(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task SendEmailAsync(string to, string subject, string body, Stream? attachment = null)
    {
        using var smtpClient = new SmtpClient
        {
            Host = _configuration["Email:SmtpHost"],
            Port = int.Parse(_configuration["Email:SmtpPort"]),
            EnableSsl = true,
            Credentials = new NetworkCredential(
                _configuration["Email:Username"],
                _configuration["Email:Password"])
        };

        var message = new MailMessage
        {
            From = new MailAddress(
                _configuration["Email:FromAddress"],
                _configuration["Email:FromName"]),
            Subject = subject,
            Body = body,
            IsBodyHtml = true
        };

        message.To.Add(to);

        if (attachment != null)
        {
            message.Attachments.Add(new Attachment(attachment, "results.csv", "text/csv"));
        }

        await smtpClient.SendMailAsync(message);
    }
}
```

Register it:

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<BeaconScheduler>();
        options.AddEmailAdapter<SmtpEmailAdapter>();
    })
    .UsePostgreSql(connectionString, "beacon");
```

```json
{
  "Email": {
    "SmtpHost": "smtp.gmail.com",
    "SmtpPort": "587",
    "Username": "alerts@yourdomain.com",
    "Password": "your-app-password",
    "FromAddress": "alerts@yourdomain.com",
    "FromName": "Beacon Alerts"
  }
}
```

Teams and Jira notifications work out of the box — configure them per recipient in the UI. See the [Notifications Guide](/features/notifications/).

## Notification Destinations (Optional)

:::caution[Upgrading]
This version changes how notifications are managed and delivered. Before upgrading a host:

- **Recipients outside the destination policy stop delivering.** Plain `http://` webhooks, private or internal webhook hosts, an on-premises Jira, retired Office 365 connector URLs (`outlook.office.com`) and anything else outside the rules below fail with "the destination is not allowed by policy". Allow them with `AllowedHosts:{Type}` and, for private addresses, `AllowedPrivateNetworks:{Type}`, or re-create them with a supported URL.
- **Only `Authorization`, `Api-Key`, `X-Api-Key` and `Ocp-Apim-Subscription-Key` webhook headers are sent.** Add any other header a webhook needs to `AllowedHeaders`; until then those recipients fail.
- **Email recipients must be bare addresses** (`ops@example.com, risk@example.com`); `Name <address>` forms fail until re-saved.
- **The system HTTP proxy is ignored** unless `UseSystemProxy` is `true`. Hosts that can only reach the internet through a proxy must turn it on (and then list `AllowedHosts:Webhook`). In proxy mode the proxy, not Beacon, has the last word on which address a call reaches; see below.
- **Rolling back gets harder.** This version stores every recipient it saves with encrypted secrets, and an earlier version cannot read them: after a rollback, recipients created or edited on this version stop delivering until their destinations and headers are entered again.
- **Encrypt the stored secrets, explicitly and once.** Recipients saved by an earlier version keep working but stay unencrypted until `IRecipientSecretEncryptionService.EncryptStoredSecretsAsync` runs or they are saved again. Nothing runs it automatically. Run it yourself only when every node runs this version (no older instance is still reading recipients) and you no longer intend to roll back, because it rewrites every stored secret, archived recipients included, and after it a rollback means re-entering all of them. With Warp, enqueue the sample's job once, for example from a maintenance endpoint or a one-off script:

  ```csharp
  var publisher = scope.ServiceProvider.GetRequiredService<IPublisher>();
  await publisher.Enqueue(new EncryptRecipientSecretsJob());   // handler calls EncryptStoredSecretsAsync
  await publisher.SaveChangesAsync();
  ```

  It is idempotent, never overwrites a recipient edited while it runs, logs its counts, and fails (logging nothing but recipient ids) if any recipient could not be encrypted, so it can simply be run again. Once it has succeeded, set `RequireEncryptedSecrets` to `true`.
- **Only Admins create, change or delete recipients.** API keys carry no role, so no API key can manage recipients any more; other users and keys can still list them (names and types) and attach them.
- **Changing subscriptions needs the Execute scope**: an API key with only the Read scope can no longer create, change, run, archive or reactivate subscriptions, or attach and detach their recipients (data contracts already needed it).
- **Failures show a generic reason.** The notification history records, for example, "the destination returned HTTP 5xx" or "connection refused by policy", never the destination's response or the connection error. Failures recorded by earlier versions may still contain the old detail.
- **API responses no longer contain destinations** except, masked, for Admins on `GET /beacon/api/recipients`: `destination` is null on subscription and data-contract details, `RecipientData.Destination` is empty from `ISubscriptionService`, and recipient search no longer matches destinations.
:::

Only Admins create, change or delete recipients; other users see recipient names and types only, and Admins see secrets masked (`********`). A masked or empty value sent back on update keeps the stored secret only while the destination stays exactly the same (scheme, host, port, path and query); otherwise the secrets must be entered again. Every destination is checked when it is saved and again before each delivery:

- Slack, Teams, Jira and webhooks need an `https` URL without user credentials.
- Slack must be `hooks.slack.com`; Teams `*.webhook.office.com`, `*.logic.azure.com` or `*.environment.api.powerplatform.com` (Power Automate workflows); Jira `*.atlassian.net` or `api.atlassian.com`.
- Webhooks may target any public host, and email any domain, unless you configure an allow-list.
- Webhook custom headers are limited to `Authorization`, `Api-Key`, `X-Api-Key`, `Ocp-Apim-Subscription-Key` and the names in `AllowedHeaders`. Headers that control the connection, routing, cookies or cloud metadata (`Host`, `Content-Length`, `X-Forwarded-*`, `Metadata-Flavor`, …) can never be added.
- Email destinations are bare addresses separated by commas; they are stored and sent as a normalised list.

Notification calls never follow redirects, speak HTTP/1.1 only, time out after 30 seconds and cap response bodies (which are never logged). By default they connect directly, and only to public addresses: Beacon resolves the host itself, refuses loopback, private (RFC 1918), CGNAT, link-local (including `169.254.169.254`), the Azure wire server (`168.63.129.16`), unique-local IPv6, everything outside IPv6 global unicast, and the IPv4 addresses carried inside mapped, NAT64 and 6to4 IPv6 addresses, and connects to the address it checked, so a DNS answer that changes after the check cannot redirect the call. Each notification type has its own allowances: an `AllowedPrivateNetworks:Jira` entry never lets a webhook reach that network. A failed delivery records a generic reason only, and is logged at Warning with the notification and recipient ids, HTTP status and exception types, never a message, URL or body.

If outbound traffic must go through a corporate proxy, set `UseSystemProxy` to `true`. Notification calls then use the system proxy (`HTTPS_PROXY`, `HTTP_PROXY`, `NO_PROXY`, or the platform's proxy settings), and Beacon checks each destination before sending instead: it resolves the host and refuses the call unless every address it resolves to is public or allowed. A destination Beacon cannot resolve is refused, so the host must still be able to resolve the notification services' names. Destinations the proxy settings bypass are connected to directly and checked as in the default mode. In proxy mode generic webhooks must be limited to `AllowedHosts:Webhook` (or Webhook disabled); the host refuses to start otherwise.

:::caution[Proxy mode does not pin the destination]
Through a proxy, Beacon's address check is advisory, not a guarantee. The proxy resolves the host name again and connects wherever its own answer points; Beacon cannot pin that connection to the address it checked. A name that resolves to a public address for Beacon and to an internal one for the proxy (or that changes in between) reaches the internal address. In proxy mode the destination guarantee therefore rests on the proxy's own resolution and egress policy: it must refuse internal, link-local and cloud-metadata destinations itself. Keep `AllowedHosts:Webhook` short, and use the default direct mode wherever the host can reach the notification services without a proxy.
:::

```json
{
  "Beacon": {
    "Notifications": {
      "AllowedHosts": {
        "Jira": [ "jira.example.internal" ],
        "Teams": [ "*.webhook.office365.us" ],
        "Webhook": [ "hooks.example.com", "*.partner.example" ],
        "Email": [ "example.com" ]
      },
      "DisabledTypes": [ "Webhook" ],
      "AllowedPrivateNetworks": {
        "Jira": [ "10.20.0.0/16", "jira.example.internal" ]
      },
      "AllowedHeaders": [ "X-Signature" ],
      "Nat64Prefixes": [ "2001:db8:64::/96" ],
      "UseSystemProxy": false,
      "RequireEncryptedSecrets": false
    }
  }
}
```

| Key | Default | Meaning |
|-----|---------|---------|
| `AllowedHosts:Slack` / `Teams` / `Jira` | empty | Extra hosts on top of the built-in vendor hosts. `*.example.com` matches any subdomain, not `example.com` itself. |
| `AllowedHosts:Webhook` | empty (any public host) | When set, the only hosts a webhook may target. Required when `UseSystemProxy` is on (unless Webhook is disabled). |
| `AllowedHosts:Email` | empty (any domain) | When set, the only domains an email recipient may use. |
| `DisabledTypes` | empty | Notification types this host does not deliver (`Email`, `Teams`, `Slack`, `Jira`, `Webhook`). |
| `AllowedPrivateNetworks:Webhook` / `Slack` / `Teams` / `Jira` | empty | IP addresses, CIDR ranges (no host bits set) or host names that calls of that type only may still reach, for example an on-premises Jira. A host-name entry never reaches link-local, metadata or Azure wire-server addresses, in any IPv6 form; only an explicit address or range does. |
| `AllowedHeaders` | empty | Webhook header names allowed on top of the built-in four. Reserved names are refused at startup. |
| `Nat64Prefixes` | empty | Extra NAT64 prefixes (IPv6 `/96`) used on the host's network, besides `64:ff9b::/96`; addresses in them are judged by the IPv4 address they carry. |
| `UseSystemProxy` | `false` | Send notification calls through the system HTTP proxy, with the destination checked before sending (see above). The proxy itself is not checked, and the proxy's own egress policy, not Beacon, decides which address each call finally reaches. |
| `RequireEncryptedSecrets` | `false` | Refuse to send to a recipient whose destination or headers are still stored unencrypted. Turn it on once you have run `EncryptStoredSecretsAsync` and it succeeded. |

A malformed entry (including a wildcard over a whole top-level domain such as `*.com`, a reserved header name, a NAT64 prefix that is not a `/96`, or a switch that is not `true` or `false`) fails the host at startup, naming the entry. The effective policy (types, host patterns, entry counts; no secrets) is logged once at startup.

Notification URLs are secrets (a Slack webhook's token is in its path). If the host traces outgoing HTTP calls with OpenTelemetry, leave notification calls out:

```csharp
.AddHttpClientInstrumentation(x => x.FilterHttpRequestMessage = y => !NotificationRequests.IsNotificationRequest(y))
```

Other APM agents (Application Insights auto-collection, Datadog, Dynatrace, …) may record full outgoing URLs, vendor webhook paths included; configure them to exclude or redact notification calls the same way.

## MCP Deployment Locks

Most MCP behaviour is configured at runtime on the **MCP Settings** page, per project. A setting that must hold regardless of what an administrator clicks is pinned here instead — and read-only enforcement and PII detection are pinned **by default**:

```json
{
  "Beacon": {
    "Mcp": {
      "ForceReadOnly": true,
      "ForcePiiDetection": true,
      "ForceNoContentRetention": false,
      "ForbidRowDataToLlm": false,
      "Ceilings": {
        "MaxRowLimit": 1000,
        "StatementTimeoutSeconds": 60,
        "MaxResultBytes": 1048576,
        "MaxExplainCost": 500.0,
        "MaxConcurrentQueriesPerKey": 8,
        "MaxSqlChars": 100000,
        "MaxQuestionChars": 4000
      }
    }
  }
}
```

| Key | Effect |
|---|---|
| `ForceReadOnly` | **Default `true`.** Pins read-only enforcement on, globally and for every project, and makes the SQL execution gate judge every statement read-only whatever the caller's settings say (the `ask`, `query` and `dry_run` tools and the in-memory join included). Attempts to disable it are refused with HTTP 409 — an Admin's too. Set it to `false` only to let an Admin turn read-only enforcement off at runtime; the host then logs a warning at startup. |
| `ForcePiiDetection` | **Default `true`.** Pins PII detection on, globally and for every project; attempts to disable it are refused with HTTP 409, an Admin's too. Set it to `false` only to let an Admin turn PII detection off at runtime; the host then logs a warning at startup. |
| `ForceNoContentRetention` | Pins the content lock on for every project — questions, SQL, and free-text errors are never persisted. See [Content retention](/features/mcp-server/#content-retention). |
| `ForbidRowDataToLlm` | **Default `false`.** Pins sample-value collection, value grounding and the eval judge off, globally and for every project, so no value read from a data source goes to the LLM provider: no sample is collected or shown (documentation discovers no column values either), nothing is probed for value grounding, the eval judge sees no rows, lesson extraction sends only a database error's class and code, and the business-domain, data-model and glossary documentation sections (which can list discovered values) stay out of every prompt — an instruction on such a section answers HTTP 400. Attempts to turn one of the three settings on are refused with HTTP 409, an Admin's too; stored values are kept (a save keeps them, globally and per project) and listed in a startup warning, and a second startup warning names the projects whose stored documentation still has value-listing sections (regenerate it). See [What reaches the LLM provider](/features/mcp-server/#what-reaches-the-llm-provider). |
| `Ceilings.*` | Clamp a numeric setting **downward only**. A ceiling can lower what an administrator configured; it can never raise it. |
| `Ceilings.MaxSqlChars` | Maximum length of SQL accepted by the MCP query tools. Default `100000`. |
| `Ceilings.MaxQuestionChars` | Maximum length of a natural-language question accepted by `ask`. Default `4000`. |

`MaxSqlChars` and `MaxQuestionChars` **always apply**: they use their defaults even when the `Ceilings` section is absent, and you can only tune them, not switch them off.

The whole section is optional — omit it and read-only enforcement and PII detection are locked on, content retention and row data to the LLM are not locked, and there are no deployment-set ceilings; the SQL and question length caps always apply. Every ceiling you do specify must be greater than zero, and the host fails to start otherwise, so a typo surfaces at boot rather than silently disabling a limit.

Sample-value collection and value grounding are off by default (no lock needed); a project turns them on for itself on the MCP Settings page. While no global MCP settings row is stored, the host logs at startup (Information) which of the settings that send row data to the LLM provider are in effect.

Settings resolve as **lock → project override → global value → built-in default**, with ceilings applied last. On the global settings page a locked switch is shown disabled with a note; in project scope locked fields are hidden and named in a banner; clamped fields are flagged. `GET /beacon/api/mcp/settings/locks` lists the locked settings.

:::caution[Upgrading]
Read-only enforcement and PII detection can no longer be turned off at runtime unless the deployment unlocks them
(`ForceReadOnly: false`, `ForcePiiDetection: false`). A deployment that had either switched off runs with it on after the
upgrade; the stored value is kept and applies again once the lock is lifted.
:::

## Schema Configuration

Beacon runs in the **`beacon`** schema, and only that schema. There is nothing to configure:

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<BeaconScheduler>();
    })
    .UsePostgreSql();   // connection string from ConnectionStrings:BeaconContext
```

Setting `Beacon:Schema` to anything other than `beacon` is rejected at startup:

```
Beacon only supports the 'beacon' schema, but 'tenant_a' was configured.
Remove Beacon:Schema (or set it to 'beacon') and move any existing Beacon tables into 'beacon'.
```

:::note[Why it is fixed]
Beacon ships EF Core migrations, and a migration snapshot bakes the schema in. On any other schema
EF compares model against snapshot, raises `PendingModelChangesWarning`, and `UseBeacon()`'s
`Database.Migrate()` throws before applying anything. Adding a migration cannot resolve it — the
next snapshot bakes one schema too. Rejecting the setting up front turns an opaque EF boot failure
into a message that says what to change.
:::

## Metadata Loading (Large Databases)

Beacon introspects the schemas of your monitored data sources to power the database explorer, documentation, and MCP context. For very large databases you can bound that work:

```json
{
  "Beacon": {
    "MetadataLoading": {
      "Enabled": true,
      "MaxTables": 500,
      "MaxColumnsPerTable": 200,
      "LoadTableNamesOnly": false,
      "IncludeSchemas": ["public", "app"],
      "ExcludeSchemas": ["information_schema", "pg_catalog"]
    }
  }
}
```

| Setting | Description |
|---------|-------------|
| `Enabled` | Set `false` to disable metadata loading entirely |
| `MaxTables` | Cap on tables loaded per data source (`0` = unlimited) |
| `MaxColumnsPerTable` | Cap on columns loaded per table (`0` = unlimited) |
| `LoadTableNamesOnly` | Load only table names, skipping columns |
| `IncludeSchemas` | Whitelist — only load these schemas |
| `ExcludeSchemas` | Blacklist — skip these schemas |

## Local Embeddings (Optional)

Beacon can run a **local ONNX embedding model in-process** to add semantic retrieval to catalog search and to the grounding context it builds for AI-generated SQL. There is no network egress — the model and tokenizer are local files you supply.

Embeddings are **disabled by default**; without them every retrieval path falls back to keyword/lexical matching rather than failing.

```json
{
  "Beacon": {
    "Embeddings": {
      "Enabled": true,
      "ModelPath": "/opt/beacon/models/bge-small-en-v1.5.onnx",
      "TokenizerPath": "/opt/beacon/models/vocab.txt"
    }
  }
}
```

| Setting | Description |
|---------|-------------|
| `Enabled` | Master switch (default `false`) |
| `ModelPath` | Path to the ONNX model file — a 384-dimension bge-small-en-v1.5 |
| `TokenizerPath` | Path to the WordPiece (BERT) vocabulary file |

Both files must exist and `Enabled` must be `true`, or the service reports itself unavailable and callers take the lexical path.

:::caution[pgvector on PostgreSQL]
On PostgreSQL, Beacon stores the vectors in a **pgvector** column with an HNSW cosine index so nearest-neighbour search runs in the database. The migration that creates it runs `CREATE EXTENSION IF NOT EXISTS vector`, so the **pgvector extension must be available on the PostgreSQL server** and the Beacon database user must be allowed to create it — otherwise migrations fail on startup. On managed Postgres this usually means enabling the extension for the instance first.

On SQL Server the same vectors are compared in memory from their byte representation; semantic retrieval works there without any extension.
:::

Indexing runs as recurring background jobs (`mcp-embedding-reindex` and `mcp-docchunk-reindex`, every 12 hours). See the [Knowledge Base guide](/features/knowledge-base/#semantic-retrieval).

## Security

### Database User Permissions

**Beacon metadata database (PostgreSQL):**
```sql
CREATE USER beacon WITH PASSWORD 'strong-password';
GRANT ALL PRIVILEGES ON DATABASE beacon TO beacon;
GRANT ALL ON SCHEMA beacon TO beacon;
```

**Monitored data sources (read-only):** connect every data source Beacon reads with a login that can only read. Only
PostgreSQL and MySQL also refuse writes in Beacon's read-only session, as defence in depth; on SQL Server, Azure Synapse,
Snowflake, BigQuery and Databricks the read-only login is the only database-side guarantee (see [Read-only guarantees per engine](/features/data-sources/#read-only-guarantees-per-engine)). The one exception is a
[migration write target](/features/data-sources/#migration-write-targets), which Beacon never reads; its login holds the
[destination privileges](/features/data-migration/#destination-privileges) its migrations need.
```sql
CREATE USER beacon_readonly WITH PASSWORD 'strong-password';
GRANT CONNECT ON DATABASE your_database TO beacon_readonly;
GRANT USAGE ON SCHEMA public TO beacon_readonly;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO beacon_readonly;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO beacon_readonly;
```

### Connection String Security

- Use User Secrets for development
- Use Azure Key Vault or similar for production
- Use read-only users for monitored data sources
- Enable SSL/TLS in production
- Rotate passwords regularly

**User Secrets (development):**
```bash
dotnet user-secrets init
dotnet user-secrets set "ConnectionStrings:BeaconContext" "Host=localhost;Database=beacon;Username=postgres;Password=devpassword"
```

**Azure Key Vault (production):**
```csharp
builder.Configuration.AddAzureKeyVault(
    new Uri($"https://{keyVaultName}.vault.azure.net/"),
    new DefaultAzureCredential());
```

### Reverse Proxy / Forwarded Headers

When Beacon runs behind a reverse proxy (nginx, Traefik, a cloud load balancer), enable forwarded-headers processing so rate limiting and redirects see the real client IP and scheme. It is **off by default** and trusts only the proxies you whitelist:

```json
{
  "Beacon": {
    "ForwardedHeaders": {
      "Enabled": true,
      "KnownProxies": ["10.0.0.5"]
    }
  }
}
```

:::caution
Only enable this when a trusted proxy actually sits in front of Beacon, and always whitelist its address. Blindly trusting `X-Forwarded-For` lets clients spoof their IP past the login rate limiter.
:::

## Logging

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Beacon": "Debug",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}
```

:::caution[No PII in logs]
User-supplied query text, connection strings, full row payloads, and auth tokens are never logged — Beacon logs identifiers and counts only.
:::

## Complete appsettings.json Example

```json
{
  "ConnectionStrings": {
    "BeaconContext": "Host=localhost;Database=beacon;Username=beacon;Password=secretpass;Pooling=true;MaxPoolSize=50"
  },
  "Beacon": {
    "Schema": "beacon",
    "BaseUrl": "https://beacon.example.com",
    "DataProtection": {
      "ApplicationName": "beacon-production"
    },
    "UserManagement": {
      "LocalLogin": {
        "Enabled": false,
        "BreakGlassUserNames": [ "superadmin" ]
      }
    },
    "ForwardedHeaders": {
      "Enabled": false,
      "KnownProxies": []
    },
    "LLM": {
      "Provider": "OpenAI",
      "Model": "gpt-4o",
      "FastModel": "gpt-4o-mini"
    }
  },
  "Email": {
    "SmtpHost": "smtp.gmail.com",
    "SmtpPort": "587",
    "Username": "alerts@yourdomain.com",
    "Password": "app-specific-password",
    "FromAddress": "alerts@yourdomain.com",
    "FromName": "Beacon Alerts"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Beacon": "Information"
    }
  }
}
```

:::note
LLM settings in `appsettings.json` are optional startup defaults. Once configured via [Admin Settings](/features/admin-settings/), database values take precedence.
:::

`Beacon:EncryptionKey` (required) and the other secrets are not in this file: supply them through environment variables (`Beacon__EncryptionKey`) or a secrets provider. .NET configuration does not expand `${...}` placeholders.

## Next Steps

- [Features](/features/) — explore Data Sources, Queries, Subscriptions, and more
- [Quick Start](/getting-started/quick-start/) — create your first alert end to end
- [MCP Server](/features/mcp-server/) — integrate Beacon with AI agents over MCP
