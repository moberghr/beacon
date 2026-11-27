---
title: "Security settings reference"
description: "One lookup table for every security-relevant configuration key in Beacon: type, default, allowed values, effect and what the regulated profile does with it."
---

This page lists every security-relevant configuration key, grouped by topic. Use it when you have a key name and want its default and range, or when you want to see what can be set. Each group links to the page that explains the setting in depth.

## How keys map to environment variables

Keys are read from .NET configuration: `appsettings.json`, environment variables, or any other configuration source the host adds. In an environment variable, the `:` separator becomes a double underscore, and a list entry gets its index as a final segment.

```bash
# Beacon:RateLimits:Login:PermitLimit
Beacon__RateLimits__Login__PermitLimit=20

# Beacon:DataSources:AllowedHosts (a list)
Beacon__DataSources__AllowedHosts__0=db1.example.com
Beacon__DataSources__AllowedHosts__1=db2.example.com
```

.NET configuration does not expand `${...}` placeholders. A secret key must hold the key itself.

## How to read the "Regulated profile" column

`Beacon:Profile = Regulated` (see [Regulated profile](/security/regulated-profile/)) acts on a key in one of three ways. The column uses these words:

- **fills X**: when the key is not set anywhere, the profile sets it to X. A value you set is never overridden.
- **requires X**: startup fails, with every failing key listed in one message, unless the key meets the condition.
- **not judged**: the profile neither sets nor checks the key.

A `bool` takes `true` or `false`. A `list` is bound from an array. "Unset" means the key has no value and the feature uses the behaviour the Effect column describes.

## Encryption and keys

| Key | Type | Default | Allowed | Effect | Regulated profile |
|---|---|---|---|---|---|
| `Beacon:EncryptionKey` | string | none (required) | Base64 of at least 32 random bytes, at least 16 distinct byte values, not a published key, no `${...}` placeholder | Encrypts connection strings, LLM keys and recipient secrets at rest; checked at startup in every environment. | not judged |
| `Beacon:PreviousEncryptionKeys` | list of string | empty | Earlier encryption keys; list form (`__0`, `__1`) | Keys a replaced encryption key moves to: decrypt only, never used to encrypt. | not judged |
| `Beacon:Encryption:AllowLegacyCbc` | bool | `true` | `true`, `false` | Keeps values in the legacy v1 format (unauthenticated AES-CBC) readable. | requires explicitly `false` |
| `Beacon:Mcp:CallerHashKey` | string | unset (derived from the encryption key) | A secret; must not contain `${` | Keys the hashes of MCP caller identities, so they survive a change of the encryption key. | not judged |
| `Beacon:Audit:IntegrityKey` | string | unset (derived from `CallerHashKey`, else the encryption key) | At least 32 characters; must not contain `${` | Keys the integrity hash of every security audit event. | requires a value |
| `Beacon:Audit:PreviousIntegrityKeys` | list of string | empty | Each at least 32 characters, none containing `${` | Earlier integrity keys, accepted when events are verified, never used to write. | not judged |
| `Beacon:Embed:SigningKey` | string | unset | Same rules as the encryption key, and different from it, from every previous key and from `Beacon:EmbedToken:SigningKey`; required while `Beacon:Embed:Enabled` is on | Signs embed tokens (HS256). | not judged |
| `Beacon:DataProtection:ApplicationName` | string | `Beacon` in Development; outside Development required when Beacon configures Data Protection (`AddBeaconDataProtection`) | Any non-empty name | Names the Data Protection key ring Beacon's default setup uses. | not judged |
| `Beacon:DataProtection:AllowUnencryptedKeys` | bool | `false` | `true`, `false` | Acknowledges a key ring stored without encryption at rest outside Development, turning the startup refusal into a Warning. | requires not `true` |
| `Beacon:UserManagement:SetupToken` | string | unset (a token is generated per process and written to standard error) | At least 32 characters | The secret first-run setup asks for before it creates the first super admin. | not judged |

Details: [Secrets and keys](/security/secrets-and-keys/). The integrity key is also covered in [Audit trail](/security/audit-trail/).

## Sign-in and sessions

| Key | Type | Default | Allowed | Effect | Regulated profile |
|---|---|---|---|---|---|
| `Beacon:UserManagement:LocalLogin:Enabled` | bool | `true` | `true`, `false` | Allows password sign-in; off leaves only the break-glass accounts. | requires `false` |
| `Beacon:UserManagement:LocalLogin:BreakGlassUserNames` | list of string | empty | At most 3 entries, none blank | Exact stored user names that may still sign in with a password while local login is off. | not judged |
| `Beacon:UserManagement:MinimumPasswordLength` | int | unset (code default 8) | 1 to 128; the larger of this and the code value applies | Raises the minimum length of internal passwords. | fills 12; requires at least 12 |
| `Beacon:Authentication:Cookie:SecureAlways` | bool | `false` | `true`, `false` | Issues the session cookie with the Secure flag on every request, also over plain http. | fills `true`; requires `true` |
| `Beacon:Authentication:Session:RevalidateMinutes` | int | `5` | 0 to 1440 | How often a session is checked against the user store; 0 checks on every request. | not judged |
| `Beacon:Authentication:Session:AbsoluteLifetimeHours` | int | `12` | 1 to 8760 | The longest a session lives, however often it is renewed. | not judged |
| `Beacon:Authentication:Session:PersistSsoCookie` | bool | `false` | `true`, `false` | Makes a single sign-on session cookie persistent instead of a browser-session cookie. | not judged |
| `Beacon:Authentication:AdditionalPaths` | list of string | empty | Path prefixes starting with `/`, not `/` itself | Host paths (for example `/warp`) that Beacon's authentication and security headers also cover. | not judged |
| `Beacon:Authentication:Oidc:Enabled` | bool | `false` | `true`, `false` | Turns single sign-on on. | not judged |
| `Beacon:Authentication:Oidc:Authority` | string | unset | Issuer URL | The identity provider; required when enabled. | not judged |
| `Beacon:Authentication:Oidc:ClientId` | string | unset | Any | The client id; required when enabled. | not judged |
| `Beacon:Authentication:Oidc:ClientSecret` | string | unset | A secret | The client secret; required when enabled. | not judged |
| `Beacon:Authentication:Oidc:CallbackPath` | string | `/signin-oidc` | A path | The sign-in callback path. | not judged |
| `Beacon:Authentication:Oidc:Scopes` | list of string | `openid`, `profile`, `email` | Scope names | Scopes requested at sign-in. | not judged |
| `Beacon:Authentication:Oidc:DefaultRoleName` | string | unset (no role) | A role name | The role given to a user provisioned at first sign-in. | not judged |
| `Beacon:Authentication:Oidc:DisplayName` | string | `SSO` | Any | The label on the sign-in button. | not judged |
| `Beacon:Authentication:Oidc:McpJwksEndpoint` | string | unset | https URL | Makes the API and `/beacon/mcp` also accept bearer JWTs from the provider. | not judged |
| `Beacon:Authentication:Oidc:AllowedTenants` | list of string | empty | Tenant ids | Entra tenants whose users may sign in; required unless `AllowAnyTenant` is on. | not judged |
| `Beacon:Authentication:Oidc:AllowAnyTenant` | bool | `false` | `true`, `false` | Admits any tenant, for providers that issue no `tid` claim. | not judged |
| `Beacon:Authentication:Oidc:BlockGuests` | bool | `true` | `true`, `false` | Refuses guest accounts of the tenant. | not judged |
| `Beacon:Authentication:Oidc:RequiredGroups` | list of string | empty | Group ids | Groups a user must belong to. | not judged |
| `Beacon:Authentication:Oidc:RequiredRoles` | list of string | empty | Role values | App roles a user must hold. | not judged |
| `Beacon:Authentication:Oidc:EnableFrontChannelLogout` | bool | `false` | `true`, `false` | Accepts front-channel sign-out from the provider. | not judged |
| `Beacon:Authentication:Oidc:RequireMfa` | bool | `false` | `true`, `false` | Admits only sign-ins whose `amr` or `acr` claims show multi-factor authentication. | requires `true` while single sign-on is enabled |
| `Beacon:Authentication:Oidc:AcceptedAcrValues` | list of string | empty | Exact `acr` values | `acr` values that count as multi-factor when `RequireMfa` is on. | not judged |

Details: [Sign-in and sessions](/security/sign-in-and-sessions/). User accounts and break-glass names are in [User management](/security/user-management/).

## Browser protections

| Key | Type | Default | Allowed | Effect | Regulated profile |
|---|---|---|---|---|---|
| `Beacon:Security:Csp` | enum | `Enforce` | `Enforce`, `ReportOnly`, `Off` | The Content-Security-Policy mode on `/beacon` paths. | requires `Enforce` or unset |
| `Beacon:Security:OriginGuard` | enum | `LogOnly` | `LogOnly`, `Enforce` | Whether a state-changing request from another origin is only logged or refused. | requires `Enforce` |
| `Beacon:Security:AllowedOrigins` | list of string | empty | `http` or `https` origins: scheme, host, optional port, no path | Origins the origin check admits besides `Beacon:PublicBaseUrl`. | not judged |
| `Beacon:PublicBaseUrl` | string | unset | Absolute URL | Beacon's public address, used for links, discovery documents and the origin check. | requires an https URL |
| `Beacon:OpenApi:Anonymous` | bool | `false` | `true`, `false` | Publishes the OpenAPI document to anonymous callers instead of signed-in ones; in Development it is always anonymous. | not judged |
| `Beacon:ForwardedHeaders:Enabled` | bool | `false` (sample host only) | `true`, `false` | The sample host trusts `X-Forwarded-*` headers from the proxies listed below. | not judged |
| `Beacon:ForwardedHeaders:KnownProxies` | list of string | empty (sample host only) | IP addresses | The proxies the sample host trusts. | not judged |

`Beacon:ForwardedHeaders:*` is read by the sample host's `Program.cs`, not by Beacon's library. A host calls `UseForwardedHeaders` itself.

Details: [Browser protections](/security/browser-protections/).

## Rate and request limits

Each limit under `Beacon:RateLimits` is a window with two keys. `PermitLimit` is the number of requests allowed per window; `0` turns that limit off. `WindowSeconds` is the window length.

| Key | Type | Default | Allowed | Effect | Regulated profile |
|---|---|---|---|---|---|
| `Beacon:RateLimits:Login:PermitLimit` | int | `10` | 0 to 100000 | Password sign-in and first-run setup attempts per client address. | requires greater than 0 |
| `Beacon:RateLimits:Login:WindowSeconds` | int | `60` | 1 to 3600 | Window of the login limit. | not judged |
| `Beacon:RateLimits:Api:PermitLimit` | int | `600` | 0 to 100000 | `/beacon/api` requests per caller, or per address when anonymous. | requires greater than 0 |
| `Beacon:RateLimits:Api:WindowSeconds` | int | `60` | 1 to 3600 | Window of the API limit. | not judged |
| `Beacon:RateLimits:Anonymous:PermitLimit` | int | `30` | 0 to 100000 | Requests answered 401 per client address. | requires greater than 0 |
| `Beacon:RateLimits:Anonymous:WindowSeconds` | int | `60` | 1 to 3600 | Window of the anonymous limit. | not judged |
| `Beacon:RateLimits:Mcp:PermitLimit` | int | `60` | 0 to 100000 | Requests to the MCP route per API key or mapped caller. | requires greater than 0 |
| `Beacon:RateLimits:Mcp:WindowSeconds` | int | `60` | 1 to 3600 | Window of the MCP limit. | not judged |
| `Beacon:RateLimits:Ask:PermitLimit` | int | `10` | 0 to 100000 | Calls of the `ask` tool (MCP and playground) per caller. | requires greater than 0 |
| `Beacon:RateLimits:Ask:WindowSeconds` | int | `60` | 1 to 3600 | Window of the `ask` limit. | not judged |
| `Beacon:RateLimits:Feedback:PermitLimit` | int | `30` | 0 to 100000 | Calls of the `feedback` tool (MCP and playground) per caller. | requires greater than 0 |
| `Beacon:RateLimits:Feedback:WindowSeconds` | int | `60` | 1 to 3600 | Window of the `feedback` limit. | not judged |
| `Beacon:RateLimits:MaxPartitions` | int | `10000` | 100 to 1000000 | The most callers or addresses each limit tracks in memory. | not judged |
| `Beacon:RateLimits:Lockout:Enabled` | bool | `true` | `true`, `false` | Locks an internal user after repeated wrong passwords. | requires on (with `MaxFailedAttempts` above 0) |
| `Beacon:RateLimits:Lockout:MaxFailedAttempts` | int | `5` | 0 to 100 | Wrong passwords in a row before the lock; 0 turns the lockout off. | requires greater than 0 |
| `Beacon:RateLimits:Lockout:DurationMinutes` | int | `15` | 1 to 1440 | How long the lock lasts. | not judged |
| `Beacon:RateLimits:BreakGlassThrottle:Enabled` | bool | `false` | `true`, `false` | Delays repeated wrong passwords on a break-glass account instead of locking it. | not judged |
| `Beacon:RateLimits:BreakGlassThrottle:FailedAttemptsBeforeDelay` | int | `5` | 0 to 100 | Wrong passwords before the first delay. | not judged |
| `Beacon:RateLimits:BreakGlassThrottle:InitialDelaySeconds` | int | `30` | 1 to `MaxDelaySeconds` | The first delay; it doubles with each further failure. | not judged |
| `Beacon:RateLimits:BreakGlassThrottle:MaxDelaySeconds` | int | `300` | 1 to 3600 | The longest delay. | not judged |
| `Beacon:RateLimits:BreakGlassThrottle:ResetAfterMinutes` | int | `60` | 1 to 1440, and longer than `MaxDelaySeconds` | How long without a failure resets the count. | not judged |
| `Beacon:RequestLimits:ApiMaxBodyBytes` | long | `2097152` (2 MiB) | 0 or more; 0 turns it off | Largest request body on Beacon's API routes. | not judged |
| `Beacon:RequestLimits:McpMaxBodyBytes` | long | `4194304` (4 MiB) | 0 or more; 0 turns it off | Largest request body on the MCP route. | not judged |
| `Beacon:RequestLimits:ApiTimeoutSeconds` | int | `300` | 0 or more; 0 turns it off | How long one `/beacon/api` request may run before cancellation. | not judged |
| `Beacon:RequestLimits:HubMaxReceiveMessageBytes` | long | `8192` (8 KiB) | 0 or more; 0 turns it off | Largest message the realtime hub accepts from a client. | not judged |
| `Beacon:RequestLimits:HubMaxParallelInvocationsPerClient` | int | `1` | 0 or more; 0 turns it off | Hub invocations running at once per connection. | not judged |
| `Beacon:RequestLimits:HubClientTimeoutSeconds` | int | `30` | 0 or more; 0 turns it off | Seconds without a client message before the hub drops the connection. | not judged |
| `Beacon:RequestLimits:McpMaxIdleSessions` | int | `1000` | 0 or more; 0 turns it off | Idle MCP sessions kept in memory. | not judged |
| `Beacon:RequestLimits:McpMaxInitializesPerCaller` | int | `60` | 0 or more; 0 turns it off | MCP `initialize` requests one caller may send per window. | not judged |
| `Beacon:RequestLimits:McpInitializeWindowSeconds` | int | `60` | 0 or more; 0 turns it off | Window of the `initialize` limit. | not judged |

Details: [Rate and request limits](/security/rate-and-request-limits/).

## SQL execution

| Key | Type | Default | Allowed | Effect | Regulated profile |
|---|---|---|---|---|---|
| `Beacon:Query:MaxExecutionRows` | int | `100000` | 1 to 10000000 | Row cap the gate writes into every statement Beacon runs for its own features. | not judged |
| `Beacon:Query:StatementTimeoutSeconds` | int | `120` | 1 to 3600 | How long one statement may run. | not judged |
| `Beacon:Query:MaxResultBytes` | long | `268435456` (256 MiB) | 1 or more | Byte budget of one result, estimated as rows arrive; a larger result fails, never truncates. | not judged |
| `Beacon:RowSecurity:Enabled` | bool | `false` | `false` only | Reserved: startup fails if it is `true`, because row-level security is not available in this release. | not judged |
| `Beacon:Reports:NeutralizeFormulas` | bool | `true` | `true`, `false` | Writes CSV and XLSX cells that start with `=`, `+`, `-`, `@`, a tab or a carriage return as text, never as formulas. | not judged |

Details: [SQL execution gate](/security/sql-execution-gate/).

## Data-source connections

| Key | Type | Default | Allowed | Effect | Regulated profile |
|---|---|---|---|---|---|
| `Beacon:DataSources:Transport:EnforceOnExisting` | bool | `false` | `true`, `false` | Refuses a stored connection that does not authenticate its server, instead of logging one Warning. | requires `true` |
| `Beacon:DataSources:Transport:AllowInsecure` | bool | `false` | `true`, `false` | Lifts the transport and driver-option checks and allows loopback addresses; for development hosts only. | requires not `true` |
| `Beacon:DataSources:AllowHostCredentials` | bool | `false` | `true`, `false` | Lets a connection sign in as the Beacon host (integrated security, managed identity, local sockets and similar). | requires not `true` |
| `Beacon:DataSources:AllowedHosts` | list of string | empty (unrestricted) | Host names, IP addresses or `*.example.com`, without scheme, port or path | Every host a connection names must be on this list. | requires at least one entry |
| `Beacon:DataSources:Outbound:AllowPrivateNetworks` | bool | `true` | `true`, `false` | Lets the API connector and Databricks clients connect to private network addresses. | not judged |
| `Beacon:DataSources:Outbound:AllowLoopback` | bool | `false` | `true`, `false` | Lets those clients connect to loopback addresses. | not judged |
| `Beacon:DataSources:BigQuery:MaxBytesBilled` | long | unset (no ceiling) | 1 or more | Lowers every BigQuery source's own bytes-billed limit to this value. | not judged |

Details: [Data-source connections](/security/data-source-connections/).

## MCP locks and ceilings

| Key | Type | Default | Allowed | Effect | Regulated profile |
|---|---|---|---|---|---|
| `Beacon:Mcp:ForceReadOnly` | bool | `true` | `true`, `false` | Pins read-only enforcement on, and makes the gate enforce it whatever a caller asks. | requires `true` |
| `Beacon:Mcp:ForcePiiDetection` | bool | `true` | `true`, `false` | Pins PII detection on everywhere. | requires `true` |
| `Beacon:Mcp:ForceNoContentRetention` | bool | `false` | `true`, `false` | Pins `RetainQueryContent` to false; cannot be combined with `Audit:RequireContent`. | not judged |
| `Beacon:Mcp:ForbidRowDataToLlm` | bool | `false` | `true`, `false` | Turns off sample values, value grounding and the eval judge, so no row value reaches the LLM provider. Also leaves chunks of the business-domain, data-model and glossary documentation sections out of the `ask` prompt (`EnableDocChunksInAsk`). | fills `true`; requires `true` |
| `Beacon:Mcp:DataNotice` | bool | `true` | `true`, `false` | Ends every successful tool result with a block saying its content is data, not instructions. | not judged |
| `Beacon:Mcp:Audit:Required` | bool | `false` | `true`, `false` | Withholds a tool result whose audit row could not be written. | fills `true`; requires `true` |
| `Beacon:Mcp:Audit:RetentionDays` | int | unset (keep forever) | 0 or more; 0 keeps forever | Days MCP audit rows are kept. | not judged (but `Retention:Locked` requires it above 0) |
| `Beacon:Mcp:Audit:RequestIdHeader` | string | `X-Request-Id` | A valid HTTP header name; empty reads none | Upstream correlation header recorded with each call. | not judged |
| `Beacon:Mcp:Audit:RequireContent` | bool | `false` | `true`, `false` | Pins `RetainQueryContent` to true, so the audit keeps every question and statement; cannot be combined with `ForceNoContentRetention`. | not judged |
| `Beacon:Mcp:Ceilings:MaxRowLimit` | int | unset (no ceiling) | Greater than 0 | The most rows any MCP setting may allow. | fills 1000; requires a value |
| `Beacon:Mcp:Ceilings:StatementTimeoutSeconds` | int | unset (no ceiling) | Greater than 0 | The longest statement timeout any setting may allow. | fills 30; requires a value |
| `Beacon:Mcp:Ceilings:MaxResultBytes` | int | unset (no ceiling) | Greater than 0 | The largest result any setting may allow. | fills 262144; requires a value |
| `Beacon:Mcp:Ceilings:MaxConcurrentQueriesPerKey` | int | unset (no ceiling) | Greater than 0 | The most queries one caller may run at once. | fills 4; requires a value |
| `Beacon:Mcp:Ceilings:MaxExplainCost` | decimal | unset (no ceiling) | Greater than 0 | The highest planner cost estimate a statement may have. | not judged |
| `Beacon:Mcp:Ceilings:LearningSignalRetentionDays` | int | unset (no ceiling) | Greater than 0 | The longest learning-signal retention any setting may resolve to. | not judged |
| `Beacon:Mcp:Ceilings:MaxSqlChars` | int | `100000` | Greater than 0 | Longest SQL text the gate evaluates. | not judged |
| `Beacon:Mcp:Ceilings:MaxQuestionChars` | int | `4000` | Greater than 0 | Longest question the `ask` tool accepts. | not judged |
| `Beacon:Mcp:Ceilings:AskTimeoutSeconds` | int | `120` | 1 to 3600 | Wall-clock time one `ask` call may take. | not judged |
| `Beacon:Mcp:Ceilings:AskMaxLlmCalls` | int | `12` | At least 2 | LLM calls one `ask` call may make. | not judged |
| `Beacon:Mcp:Ceilings:AskSchemaContextChars` | int | `120000` | At least 10000 | Longest schema context one `ask` sends to the LLM. | not judged |

The keys marked "unset (no ceiling)" are ceilings only when set. Under the regulated profile an unset ceiling among `MaxRowLimit` (1000), `StatementTimeoutSeconds` (30), `MaxResultBytes` (262144) and `MaxConcurrentQueriesPerKey` (4) is filled with the built-in value of the matching MCP setting, so an Admin cannot raise it past that.

### MCP callers

These keys map Entra tokens to Beacon callers. They require bearer JWT authentication with issuer and audience validation (see [Settings set in code](#settings-set-in-code)).

| Key | Type | Default | Allowed | Effect | Regulated profile |
|---|---|---|---|---|---|
| `Beacon:Mcp:Callers:AllowedTenants` | list of string | empty | Tenant ids; required and non-empty when callers are configured | Tenants whose tokens may be mapped to a caller. | not judged |
| `Beacon:Mcp:Callers:Users:Enabled` | bool | `false` | `true`, `false` | Turns on user mode: delegated Entra user tokens. | not judged |
| `Beacon:Mcp:Callers:Users:RequiredRoles` | list of string | empty | Role values | App roles a user must hold. | not judged |
| `Beacon:Mcp:Callers:Users:RequiredGroups` | list of string | empty | Group ids | Groups a user must belong to. | not judged |
| `Beacon:Mcp:Callers:Users:AllowAnyTenantUser` | bool | `false` | `true`, `false` | Admits every user of the tenant when no role or group is required. | not judged |
| `Beacon:Mcp:Callers:Users:ProjectIds` | list of int | empty | Positive project ids | Projects every user caller may reach. | not judged |
| `Beacon:Mcp:Callers:Users:GroupProjects` | map of group to list of int | empty | Positive project ids | Projects a caller reaches through a group. | not judged |
| `Beacon:Mcp:Callers:Users:Scope` | enum | `Execute` | `Read`, `Execute` | The API scope user callers get. | not judged |
| `Beacon:Mcp:Callers:Users:AutoProvision` | bool | `true` | `true`, `false` | Creates a Beacon user on a caller's first call. | not judged |
| `Beacon:Mcp:Callers:Users:DefaultRoleName` | string | `Viewer` | Not Admin and not a functional role; required while `AutoProvision` is on | The role a provisioned caller gets. | not judged |
| `Beacon:Mcp:Callers:Systems` | list of objects | empty | Each with `Name` (unique) and exactly one of `ClientId` or `ObjectId`, plus `ProjectIds`, `Scope` (default `Execute`) and `HostClaims` | App-only or service-user callers. | not judged |
| `Beacon:Mcp:ProtectedResource:AuthorizationServers` | list of string | empty | Issuer identifiers | Authorization servers published in the discovery document. | not judged |
| `Beacon:Mcp:ProtectedResource:DeriveAuthorizationServerFromJwtIssuer` | bool | `false` | `true`, `false` | Publishes the bearer JWT issuer when `AuthorizationServers` is empty. | not judged |
| `Beacon:Mcp:ProtectedResource:ScopesSupported` | list of string | empty | Scope names | Scopes published in the discovery document. | not judged |
| `Beacon:Mcp:ProtectedResource:ResourceName` | string | unset | Any | Display name in the discovery document. | not judged |
| `Beacon:Mcp:ProtectedResource:ResourceDocumentation` | string | unset | URL | Documentation link in the discovery document. | not judged |

Details: [Deployment locks](/security/deployment-locks/) for the locks and ceilings, and [Entra callers](/mcp/entra-callers/) for the callers.

## Deployment locks for optional surfaces

Both surfaces are off by default and are deployment locks: while off, their routes are not mapped.

| Key | Type | Default | Allowed | Effect | Regulated profile |
|---|---|---|---|---|---|
| `Beacon:Migrations:Enabled` | bool | `false` | `true`, `false` | Enables data migration, the only write path; off maps no endpoint and refuses every run. | not judged (reported as on at startup) |
| `Beacon:Migrations:MaxRows` | int | `100000` | 1 to 10000000 | The most rows one migration read may return. | not judged |
| `Beacon:Embed:Enabled` | bool | `false` | `true`, `false` | Enables the embed surface; never on in gateway mode. | not judged (reported as on at startup) |
| `Beacon:Embed:TokenTtlSeconds` | int | `300` | 1 to `MaxTokenTtlSeconds` | Lifetime of an embed token. | not judged |
| `Beacon:Embed:MaxTokenTtlSeconds` | int | `900` | 1 to 900 | The longest token lifetime any grant may ask for. | not judged |
| `Beacon:Embed:MaxGrantDays` | int | `90` | 1 to 366 | The longest expiry of a grant. | not judged |
| `Beacon:Embed:MaxRows` | int | `1000` | 1 to 100000 | Row cap of an embed render, applied with the project limit (the smaller wins). | not judged |
| `Beacon:Embed:MaxOrigins` | int | `10` | 1 to 50 | The most frame origins one grant may list. | not judged |
| `Beacon:Embed:MaxConcurrentRenders` | int | `8` | 1 to 256 | Renders running at once per process. | not judged |
| `Beacon:Embed:MaxConcurrentRendersPerGrant` | int | unset (a quarter of `MaxConcurrentRenders`, at least 1) | 1 to `MaxConcurrentRenders` | Renders running at once per grant. | not judged |
| `Beacon:Embed:RateLimits:ExchangePerGrantPerMinute` | int | `60` | 1 to 10000 | Token exchanges per grant per minute. | not judged |
| `Beacon:Embed:RateLimits:RenderPerGrantPerMinute` | int | `120` | 1 to 10000 | Renders per grant per minute. | not judged |
| `Beacon:Embed:RateLimits:RefusalsPerIpPerMinute` | int | `30` | 1 to 10000 | Refusals per client address per minute before it is answered 429. | not judged |
| `Beacon:Embed:RateLimits:PagePerClientPerMinute` | int | `60` | 1 to 10000 | Anonymous page loads per client address and grant per minute. | not judged |

`Beacon:Embed:SigningKey` is in [Encryption and keys](#encryption-and-keys). The obsolete `Beacon:EmbedToken:SigningKey` is read only to refuse a new signing key that equals it.

Details: [Deployment locks](/security/deployment-locks/) and [Embedding](/integrate/embedding/).

## Audit

| Key | Type | Default | Allowed | Effect | Regulated profile |
|---|---|---|---|---|---|
| `Beacon:Audit:Required` | bool | `false` | `true`, `false` | Refuses a successful sign-in whose audit event cannot be written. | fills `true`; requires `true` |
| `Beacon:Audit:RetentionDays` | int | unset (keep forever) | 365 to 36500 | Days security audit events are kept before the verified purge deletes them. | not judged |
| `Beacon:Audit:ExecutionLog:Required` | bool | `false` | `true`, `false` | Refuses a statement whose Started row in the execution log cannot be written; startup fails if no execution log is registered. | fills `true`; requires `true` |
| `Beacon:Audit:ExecutionLog:RetentionDays` | int | unset (keep forever) | Greater than 0 | Days execution log rows are kept. | not judged |
| `Beacon:Telemetry:CaptureContent` | bool | `false` | `true`, `false` | Puts the tool input on the current span as `beacon.tool.input`, where the project's content retention also allows content. | not judged |

`Beacon:Audit:IntegrityKey` and `PreviousIntegrityKeys` are in [Encryption and keys](#encryption-and-keys), and `Beacon:Mcp:Audit:*` in [MCP locks and ceilings](#mcp-locks-and-ceilings).

Details: [Audit trail](/security/audit-trail/) and [Data retention](/security/data-retention/).

## Retention

| Key | Type | Default | Allowed | Effect | Regulated profile |
|---|---|---|---|---|---|
| `Beacon:Retention:Days` | int | `0` (keep forever) | 0 to 36500 | The window for every retention target that has no window of its own. | not judged (but `Locked` requires it above 0) |
| `Beacon:Retention:Targets:{name}` | map of name to int | empty | 0 to 36500; the name must be a registered target | A window for one target, replacing `Days` for it. | not judged |
| `Beacon:Retention:ContentDays` | int | `0` (off) | 0 to 36500 | Days after which a content store's query text and result rows are cleared; the row stays. | not judged (but `Locked` requires it above 0) |
| `Beacon:Retention:Locked` | bool | `false` | `true`, `false` | Refuses keep-forever windows for all stores except the exempt governance ones, and caps the learning-signal retention. | fills `true`; requires `true` |

Details: [Data retention](/security/data-retention/), which also lists the valid target names.

## LLM and outbound delivery

| Key | Type | Default | Allowed | Effect | Regulated profile |
|---|---|---|---|---|---|
| `Beacon:LLM:AllowInsecureEndpoint` | bool | `false` | `true`, `false` | Accepts an http LLM endpoint on any host; the credentials and every prompt then travel unencrypted. | requires not `true` |
| `Beacon:Notifications:AllowedHosts:{Type}` | list of string | empty | Exact hosts or `*.domain`; type is `Slack`, `Teams`, `Jira`, `Webhook` or `Email` (Email takes domains) | Extra hosts per type; for `Webhook` and `Email` a non-empty list is the whole allow-list. | not judged |
| `Beacon:Notifications:AllowedPrivateNetworks:{Type}` | list of string | empty | An IP address, a CIDR range with no host bits set, or a host name; type is `Webhook`, `Slack`, `Teams` or `Jira` | Private, loopback or link-local destinations that type may still reach. | not judged |
| `Beacon:Notifications:AllowedHeaders` | list of string | empty | Header names that are not reserved | Webhook header names allowed beyond the built-in four. | not judged |
| `Beacon:Notifications:DisabledTypes` | list of enum | empty | `Teams`, `Email`, `Jira`, `Slack`, `Webhook` | Notification types this host does not deliver. | not judged |
| `Beacon:Notifications:Nat64Prefixes` | list of string | empty | IPv6 `/96` ranges with no host bits set | Extra NAT64 prefixes, judged by the IPv4 address they carry. | not judged |
| `Beacon:Notifications:UseSystemProxy` | bool | `false` | `true`, `false` | Sends notification calls through the system proxy; then `AllowedHosts:Webhook` is required (or `Webhook` in `DisabledTypes`). | not judged |
| `Beacon:Notifications:RequireEncryptedSecrets` | bool | `false` | `true`, `false` | Refuses to send to a recipient whose destination or headers are still stored unencrypted. | not judged |

The `Nat64Prefixes` list also applies to the LLM clients' address guard.

Details: [LLM data safety](/security/llm-data-safety/). Notification delivery is explained in [Notifications](/features/notifications/).

## Authorization

| Key | Type | Default | Allowed | Effect | Regulated profile |
|---|---|---|---|---|---|
| `Beacon:Authorization:RequireProjectMembership` | bool | `false` | `true`, `false` | A project open to everyone becomes one only its members can reach; needs a project membership source. | requires `true` |
| `Beacon:Authorization:Roles:Approver` | string | `Approver` | At most 50 characters; not a built-in role; unique per permission | The role name through which the Approver permission is held. | not judged |
| `Beacon:Authorization:Roles:Auditor` | string | `Auditor` | Same | Role name for the Auditor permission. | not judged |
| `Beacon:Authorization:Roles:SecurityAdmin` | string | `SecurityAdmin` | Same | Role name for the SecurityAdmin permission. | not judged |
| `Beacon:Authorization:Roles:UserAdmin` | string | `UserAdmin` | Same | Role name for the UserAdmin permission. | not judged |
| `Beacon:Authorization:Roles:DataSourceAdmin` | string | `DataSourceAdmin` | Same | Role name for the DataSourceAdmin permission. | not judged |
| `Beacon:Authorization:Roles:MigrationOperator` | string | `MigrationOperator` | Same | Role name for the MigrationOperator permission. | not judged |
| `Beacon:Authorization:Roles:HonourHostRoleClaims` | bool | `false` | `true`, `false` | On a host without Beacon user management, lets the session's role claims grant the permission whose role they name. | not judged |
| `Beacon:Authorization:IdpRoles:Enabled` | bool | `false` | `true`, `false` | Maps identity-provider app roles onto functional permissions at single sign-on; needs at least one `Map` entry. | not judged |
| `Beacon:Authorization:IdpRoles:Map` | map of string to string | empty | App role value to one of `Approver`, `Auditor`, `SecurityAdmin`, `UserAdmin`, `DataSourceAdmin`, `MigrationOperator` | Which app role grants which permission. | not judged |
| `Beacon:Authorization:IdpRoles:ClaimLifetimeMinutes` | int | `60` | 1 to 1440 | How long a mapped permission is honoured after the sign-in that mapped it. | not judged |
| `Beacon:Authorization:SeparationOfDuties:Enabled` | bool | `false` | `true`, `false` | Refuses roles that grant both permissions of a conflicting pair. | not judged |
| `Beacon:ApiKeys:MaxLifetimeDays` | int | `365` | 1 to 3650 | The longest lifetime a new API key may be issued with. | not judged |
| `Beacon:ApiKeys:EnforceMaxLifetimeOnExistingKeys` | bool | `false` | `true`, `false` | Makes a key stored without an expiry expire `MaxLifetimeDays` after it was created. | requires `true` |
| `Beacon:ApiKeys:MaxOwnerInactivityDays` | int | `0` (off) | 0 to 3650 | Stops a key working while its owner has not signed in for this many days. | not judged |

Details: [Authorization](/security/authorization/) and [API keys](/security/api-keys/).

## Abuse limits for background work

These keys bound how often cost-bearing or load-bearing work can be started.

| Key | Type | Default | Allowed | Effect | Regulated profile |
|---|---|---|---|---|---|
| `Beacon:Subscriptions:MinIntervalMinutes` | int | `5` | 1 to 1440 | The shortest time between two runs of a subscription's schedule. | not judged |
| `Beacon:Subscriptions:MaxAttachmentBytes` | int | `10485760` (10 MiB) | 65536 to 104857600 | The largest file a subscription attaches; a larger export keeps the first rows that fit. | not judged |
| `Beacon:Metadata:ForcedRefreshCooldownMinutes` | int | `15` | 0 to 1440; 0 turns it off | Minutes a data source refuses another forced metadata refresh (429). | not judged |
| `Beacon:AiActors:ThinkCooldownMinutes` | int | `15` | 0 to 1440; 0 turns it off | The shortest time between two think cycles of one AI actor. | not judged |
| `Beacon:AiActors:PlanExpiryDays` | int | `7` | 1 to 365 | Days a plan waits for approval before it expires. | not judged |
| `Beacon:GitHub:MaxFilesPerRepository` | int | `5000` | Any | The most files one repository scan reads. | not judged |
| `Beacon:GitHub:MaxFileBytes` | int | `1000000` | Any | The largest file a repository scan reads. | not judged |
| `Beacon:GitHub:MaxTreeResponseBytes` | int | `33554432` (32 MiB) | Any | The largest repository tree response a scan accepts. | not judged |
| `Beacon:GitHub:ScanTimeout` | TimeSpan | 5 minutes | A time span | How long one repository scan may run. | not judged |
| `Beacon:GitHub:TaskDeadline` | TimeSpan | 3 hours | More than zero, at most 24 hours | The deadline of a repository scan task. | not judged |

Details: [Subscriptions](/features/subscriptions/), [AI actors](/features/ai-actors/) and [Data sources](/features/data-sources/).

## Profile

| Key | Type | Default | Allowed | Effect | Regulated profile |
|---|---|---|---|---|---|
| `Beacon:Profile` | enum | unset (none) | `Regulated`; unset or blank means none; any other value fails startup | Opts into the regulated preset: fills the keys marked "fills" above and fails startup on every key marked "requires". | not applicable |

Details: [Regulated profile](/security/regulated-profile/).

## Settings set in code

These options have no configuration key. They are set in `Program.cs`, in the `AddBeaconServices` callback or on the call that registers the feature. The regulated profile judges the approval workflow and user management options too.

### Approval workflow

Set with `options.EnableApprovalWorkflow(x => { ... })`. Details: [Approvals](/security/approvals/).

```csharp
options.EnableApprovalWorkflow(x =>
{
    x.RequireApprovedVersionToRun = true;
    x.CoversDataSourceConnections = true;
});
```

| Option | Type | Default | Effect | Regulated profile |
|---|---|---|---|---|
| `Enabled` (set by `EnableApprovalWorkflow`) | bool | `false` | Every change to a query's executable content waits for a second person's approval. | requires `true` |
| `AllowSelfApproval` | bool | `false` | Lets the requester approve their own change. | requires `false` |
| `RequireApprovedVersionToRun` | bool | `false` | Strict mode: a subscription or saved-query tool runs only a version with an approved request for exactly its content. | not judged |
| `CoversMigrationJobs` | bool | `true` | A new or changed migration job runs only once approved. | requires `true` |
| `CoversCustomSqlRules` | bool | `true` | A new or changed Custom SQL rule is evaluated only once approved. | requires `true` |
| `CoversRecipientDestinations` | bool | `true` | A new recipient or changed destination receives nothing until approved. | requires `true` |
| `CoversDataSourceConnections` | bool | `false` | A new data source or changed connection makes no connection until approved. | requires `true` |
| `CoversLlmConnection` | bool | `false` | A change of the LLM provider, endpoint, region or authentication mode is staged until a live Admin approves. | requires `true` |
| `CoversAdminRoleGrants` | bool | `false` | Granting the Admin role waits for approval by a live Admin other than the requester. | requires `true` |
| `LlmConnectionRequestDays` | int | `7` | Days a staged LLM connection change waits before it expires; values are clamped to 1 to 30. | not judged |

In gateway mode the profile does not judge any approval workflow option.

### User management

Set with `options.EnableUserManagement()`, with further options on `options.UserManagement`.

```csharp
options.EnableUserManagement();
options.UserManagement.RequirePasswordComplexity = true;
```

| Option | Type | Default | Effect | Regulated profile |
|---|---|---|---|---|
| `UserManagement.Enabled` (set by `EnableUserManagement`) | bool | `false` | Turns on Beacon's user store: per-user roles, functional permissions and project memberships. | requires `true` |
| `UserManagement.AllowInternalUsers` | bool | `true` | Allows accounts with a password stored in Beacon. | not judged |
| `UserManagement.HostOwnsSignIn` | bool | `false` | The host signs users in; Beacon does not. | not judged |
| `UserManagement.RequirePasswordComplexity` | bool | `true` | Enforces complexity rules wherever a password is set. | not judged |
| `UserManagement.MinimumPasswordLength` | int | `8` | The code value; the key `Beacon:UserManagement:MinimumPasswordLength` can only raise it. | requires at least 12 (the profile also fills the key) |
| `UserManagement.SetupToken` | string | unset | The first-run setup token; the configuration key of the same name is read when this is unset. | not judged |

### JWT bearer authentication

Set with `AddBeaconJwtAuthentication`. There is no configuration key for it. Details: [User management](/security/user-management/) and [Authentication](/integrate/authentication/).

```csharp
builder.Services.AddBeaconJwtAuthentication(jwt =>
{
    jwt.EnableBearerAuthentication = true;
    jwt.Validation.ValidIssuer = "https://login.example.com/tenant/v2.0";
    jwt.Validation.ValidAudience = "api://REPLACE_ME";
    jwt.Validation.JwksEndpoint = "https://login.example.com/tenant/discovery/keys";
});
```

| Option | Type | Default | Effect | Regulated profile |
|---|---|---|---|---|
| `EnableBearerAuthentication` | bool | `false` | Accepts `Authorization: Bearer` tokens on the API and the MCP route. | not judged |
| `ExternalLoginEndpoint` | string | unset | An https login endpoint the login form calls for a token; http is refused outside Development. | not judged |
| `AccessTokenClaim` | string | unset (only `typ: at+jwt` tokens from issuers other than Entra) | A claim that marks an access token from a non-Entra issuer that does not set `typ: at+jwt`; a claim any ID token can carry is refused at startup. | not judged |
| `Validation.ValidIssuer`, `ValidIssuers` | string, list | unset | Accepted issuers. | not judged |
| `Validation.ValidAudience`, `ValidAudiences` | string, list | unset | Accepted audiences. | not judged |
| `Validation.SigningKey` | string | unset | HMAC key, at least 32 bytes (UTF-8); with it only HS256, HS384 and HS512 are accepted. One of `SigningKey` or `JwksEndpoint` is required. | not judged |
| `Validation.JwksEndpoint` | string | unset | An https JWKS URL; with it only RS, PS and ES algorithms are accepted. | not judged |
| `Validation.ClockSkew` | TimeSpan | 5 minutes | Tolerance on token lifetime; at most 5 minutes. | not judged |
| `Validation.JwksRefreshInterval` | TimeSpan | 1 hour | How long fetched JWKS keys are used before they are fetched again; 5 minutes to 24 hours. | not judged |
| `Validation.JwksMaximumStaleness` | TimeSpan | 24 hours | How long keys are still used while the endpoint cannot be reached; at least the refresh interval, at most 7 days. | not judged |
| `Validation.ValidateLifetime` | bool | `true` | Checks the token lifetime; startup refuses `false` while tokens are validated. | not judged |
| `Validation.ValidateIssuer` | bool | `true` | Checks the issuer; startup refuses `false` while tokens are validated, and a valid issuer must be listed. | not judged |
| `Validation.ValidateAudience` | bool | `true` | Checks the audience; startup refuses `false` while tokens are validated, and a valid audience must be listed. | not judged |
| `ClaimsMapping.UserIdClaim` | string | `sub` | Claim read as the user id. | not judged |
| `ClaimsMapping.UserNameClaim` | string | `preferred_username` | Claim read as the user name. | not judged |
| `ClaimsMapping.EmailClaim` | string | `email` | Claim read as the e-mail address. | not judged |
| `ClaimsMapping.RolesClaim` | string | `roles` | Claim read as the roles. | not judged |
| `ClaimsMapping.DisplayNameClaim` | string | `name` | Claim read as the display name. | not judged |

### Host endpoint tools and saved-query tools

Set on `AddHostEndpointTools` (MCP) and on `BeaconConfiguration.SavedQueryTools`. Details: [Host endpoint tools](/mcp/host-endpoint-tools/) and [Saved-query tools](/mcp/saved-query-tools/).

```csharp
.AddHostEndpointTools(o =>
{
    o.AllowWriteTools = false;
    o.MaxConcurrentCallsPerCaller = 8;
})
```

| Option | Type | Default | Effect | Regulated profile |
|---|---|---|---|---|
| `HostEndpointToolOptions.AllowWriteTools` | bool | `false` | Lets endpoints marked `ReadOnly = false` become write tools (`readOnlyHint=false`, user callers only, audited as `api_<name>:write`). | not judged |
| `HostEndpointToolOptions.AllowReadOnlyPost` | bool | `true` | Admits a read-only tool on a verb other than GET or HEAD; off hides such tools. | not judged |
| `HostEndpointToolOptions.TrustedAuthenticationSchemes` | list of string | empty | Host authentication schemes an exposed endpoint's policy may name; a scheme not listed fails startup, because endpoint tools never run that scheme's handler. | not judged |
| `HostEndpointToolOptions.MaxConcurrentCalls` | int | `64` | The most endpoint tool calls in flight at once; at least 1. | not judged |
| `HostEndpointToolOptions.MaxConcurrentCallsPerCaller` | int | `16` | The most calls one MCP caller has in flight; at least 1. | not judged |
| `HostEndpointToolOptions.MaxResponseBytes` | int | `1048576` (1 MiB) | The largest response body an endpoint tool returns; must be positive. | not judged |
| `HostEndpointToolOptions.RequestTimeout` | TimeSpan | 30 seconds | How long one endpoint tool call may run; must be positive. | not judged |
| `HostEndpointToolOptions.QueueWait` | TimeSpan | 5 seconds | How long a call waits for a free slot before it is refused. | not judged |
| `SavedQueryTools.NamedToolLimit` | int | `25` | Above this many saved-query tools, Beacon serves `search_saved_queries` and `run_saved_query` instead of one tool each; must not be negative. | not judged |

## Related pages

- [Regulated profile](/security/regulated-profile/): what the profile fills and requires, with recommended values
- [Secrets and keys](/security/secrets-and-keys/)
- [Sign-in and sessions](/security/sign-in-and-sessions/)
- [Browser protections](/security/browser-protections/)
- [Rate and request limits](/security/rate-and-request-limits/)
- [SQL execution gate](/security/sql-execution-gate/)
- [Data-source connections](/security/data-source-connections/)
- [Deployment locks](/security/deployment-locks/)
- [Audit trail](/security/audit-trail/) and [Data retention](/security/data-retention/)
- [LLM data safety](/security/llm-data-safety/)
- [Authorization](/security/authorization/), [API keys](/security/api-keys/) and [Approvals](/security/approvals/)
- [Configuration](/getting-started/configuration/): the general configuration reference
