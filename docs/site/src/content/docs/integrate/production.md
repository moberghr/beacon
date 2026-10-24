---
title: Production checklist
description: "What to set before Beacon faces real users and real data: secrets, Data Protection, reverse proxy, sign-in, locks, audit, MCP, scaling, observability and upgrades."
---

Go through this list before the first real user signs in. Each line links to where it is explained.

## Secrets and keys

- [ ] `Beacon:EncryptionKey` comes from a secret store or environment variable, never a file in source control; its backup is stored separately from database backups. → [Configuration](/getting-started/configuration/)
- [ ] You know how to **rotate** it: move the old key to `Beacon:PreviousEncryptionKeys:0`, set the new one, set `Beacon:Mcp:CallerHashKey` to the old key first if you want MCP caller hashes to stay comparable, run the re-encryption job, then remove the old key once it reports nothing left under it: pending changes of recipients, data-source connections and the LLM connection made under the old key can keep it needed until they are decided. Set `Beacon:Audit:IntegrityKey` before the first rotation: while it is unset, security audit events are signed under a key derived from the caller hash key or the encryption key, and verify as *unknown key* once that secret leaves the configuration. → [Keys and rotation](/security/audit-trail/#keys-and-rotation)
- [ ] `Beacon:DataProtection:ApplicationName` is set (one value per environment), and the key ring is **encrypted at rest** (`ProtectKeysWithCertificate` / `ProtectKeysWithAzureKeyVault`), otherwise the host does not start outside `Development`. → [Host wiring](/integrate/host-setup/#data-protection)
- [ ] OIDC client secrets, LLM API keys and SMTP passwords come from the secret store.
- [ ] `Beacon:UserManagement:SetupToken` is set for multi-instance first runs, and removed afterwards.

## Reverse proxy

- [ ] TLS terminates at the proxy, and the host runs `UseForwardedHeaders` with **only your proxies** listed, first in the pipeline. The per-address request limits (`Beacon:RateLimits`: login 10 / 60 s, anonymous refusals 30 / 60 s) and the cookie's Secure flag depend on it.

```csharp
var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
forwarded.KnownProxies.Clear();
forwarded.KnownNetworks.Clear();
forwarded.KnownProxies.Add(IPAddress.Parse("10.0.0.5"));
app.UseForwardedHeaders(forwarded);
```

- [ ] `Beacon:PublicBaseUrl` is the public origin (e.g. `https://beacon.example.com`), so MCP discovery documents do not echo the client's `Host` header. → [Connect AI clients](/mcp/connect/#public-url-and-discovery)
- [ ] `options.BaseUrl` (or **Admin settings → Base URL**) is the public UI URL **including `/beacon`**, so notification links work.
- [ ] HTTPS redirection applies to `/beacon/mcp` too, because API keys travel in headers.
- [ ] HSTS is sent by the host (`app.UseHsts()`) or the proxy, and `AllowedHosts` lists your real host names.
- [ ] The origin check knows your public origin (`Beacon:PublicBaseUrl` or `Beacon:Security:AllowedOrigins`); after a release without its `LogOnly` warnings, `Beacon:Security:OriginGuard` is `Enforce`. → [Security headers and the origin check](/integrate/host-setup/#security-headers-and-the-origin-check)
- [ ] Proxy and Kestrel timeouts allow long requests (AI documentation, `ask`): the reference host uses 5 minutes.

## Sign-in and access

- [ ] Authorization is on (`options.Authorization.Enabled = true`).
- [ ] SSO admission is narrow: `AllowedTenants`, `BlockGuests` (default on), `RequiredGroups` or `RequiredRoles`; `RequireMfa` if your provider issues `amr`/`acr`. → [Sign-in & identity](/integrate/authentication/#oidc--sso)
- [ ] Local passwords off once SSO works, with one break-glass account in a vault; alert on event 7119 `BreakGlassSignIn`.
- [ ] `Beacon:Authorization:RequireProjectMembership: true`; projects planned; data sources linked only where needed. → [Access control](/integrate/access-control/)
- [ ] Few Admins; duties given as functional permissions; `SeparationOfDuties` on if your auditors expect it.
- [ ] Approval workflow on with at least two Approvers, `RequireApprovedVersionToRun` for regulated data. → [Approvals](/security/approvals/)
- [ ] API keys: decide `Beacon:ApiKeys:MaxLifetimeDays` and `MaxOwnerInactivityDays`.

## Data and SQL

- [ ] Every data source uses a **read-only login**, including PostgreSQL and MySQL. → [Connectors](/integrate/connectors/#creating-read-only-logins)
- [ ] Deployment locks reviewed: `Beacon:Mcp:ForceReadOnly` and `ForcePiiDetection` stay `true`; consider `ForbidRowDataToLlm` and `ForceNoContentRetention`. → [Access control](/integrate/access-control/#5-deployment-locks)
- [ ] `Beacon:Query:MaxExecutionRows` (default 100,000) and `StatementTimeoutSeconds` (default 120) fit your workloads.
- [ ] `Beacon:Mcp:Ceilings:MaxRowLimit` set if project admins must not raise the MCP row limit.
- [ ] `Beacon:Migrations:Enabled` stays `false` unless you need data migration. It is the only write path.
- [ ] Notification destinations restricted as needed: `Beacon:Notifications:AllowedHosts:*`, `DisabledTypes`; private networks stay unreachable unless listed in `AllowedPrivateNetworks`. → [Notifications](/features/notifications/)

## Audit and observability

- [ ] `Beacon:Mcp:Audit:RetentionDays` set to your retention policy and the purge job scheduled; `Audit:Required: true` if a tool result must never be returned without its audit row. → [MCP observability & audit](/mcp/observability-and-audit/)
- [ ] Security log events (structured log events 7101 to 7123: membership, roles, sessions, break-glass, access refused, row data to LLM) are shipped to your log pipeline and alerted on. The log events themselves are not stored in Beacon's database; the actions most of them report are also recorded in the security audit trail. → [Security model](/security/#12-audit-and-security-events)
- [ ] Security audit trail: `Beacon:Audit:IntegrityKey` set from a secret store (before you change the encryption key or the caller hash key), the verification job (`ISecurityAuditVerificationService.VerifyNewEventsAsync`, hourly) and the retention purge (`ISecurityAuditRetentionService.PurgeExpiredAsync`) scheduled, `Beacon:Audit:RetentionDays` set to your retention policy (at least 365; events are kept forever while it is unset), the `Beacon.Audit` events 9104 to 9110 shipped to your SIEM, and alerts on any increment of `beacon.security_audit.write_failures` and `beacon.security_audit.verification_failures`. → [Security audit trail](/security/audit-trail/)
- [ ] To make the trail append-only at the database as well, Beacon's connection uses a role that cannot update or delete `security_audit_events` rows other than through the purge, or triggers do the same. → [What the trail does and does not prove](/security/audit-trail/#what-the-trail-does-and-does-not-prove)
- [ ] Where MCP audit content is retained, the SQL of every statement a call ran is stored with it: reviewed against your privacy policy; `Beacon:Mcp:Audit:RequireContent` pins retention on if the evidence must be kept. → [MCP observability & audit](/mcp/observability-and-audit/#keeping-audit-content-the-requirecontent-lock)
- [ ] Structured JSON console logs (`Logging:Console:FormatterName=json`, `IncludeScopes=true`) if your platform correlates by trace id.
- [ ] OpenTelemetry: add `AddBeaconInstrumentation()` to your tracer and meter providers; you choose the exporter. Keep notification HTTP calls out of traces (their URLs carry secrets). `NotificationRequests.IsNotificationRequest` identifies them.
- [ ] Health check `GET /beacon/api/health` (anonymous, checks the database within 5 s) wired to your load balancer. It answers before the first administrator exists too, so a brand-new deployment passes the probe while setup is still pending.
- [ ] Regular expressions: every pattern Beacon runs on user, catalog or model text carries its own match timeout (250 ms to 1 s), so no setting is needed. For your own host code, you can add a process-wide backstop with `AppContext.SetData("REGEX_DEFAULT_MATCH_TIMEOUT", TimeSpan.FromSeconds(2))` (or the same name under `configProperties` in `runtimeconfig.json`) before the first regex is built; it applies only to patterns that set no timeout of their own.

## Scaling

- [ ] Several instances: shared Data Protection key ring (Beacon's database does this), a SignalR backplane (`ConfigureSignalR`), **session affinity for `/beacon/mcp`** (legacy MCP clients hold an `Mcp-Session-Id` in memory), and a job runner that fires each recurring job once.
- [ ] Migrations run once per deployment, not from every replica at the same time. → [Metadata database](/integrate/database/#creating-and-migrating-the-schema)
- [ ] Database pool sized for job workers plus web traffic.

## Upgrades

- [ ] All `Moberg.Beacon.*` packages on the same version.
- [ ] Read the release notes of every version you upgrade to: a release can change defaults, and the notes say what to do.
- [ ] Back up the metadata database before migrating; migrations are append-only and run forward.
- [ ] After upgrading every instance, run one-off jobs the release notes name (for example recipient-secret encryption).
