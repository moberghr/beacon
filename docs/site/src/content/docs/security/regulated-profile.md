---
title: Regulated profile
description: "One setting, Beacon:Profile = Regulated, that fills the safe values a regulated deployment needs and refuses to start when any required setting is weaker."
---

A regulated deployment needs many security settings to be on at the same time: audit that fails closed, retention windows, request limits, multi-factor sign-in, an approval workflow, encrypted and verified connections. Setting each one by hand is easy to get wrong, and one forgotten key silently weakens the whole deployment.

`Beacon:Profile = Regulated` is a preset that checks all of them in one place. It fills the keys you left unset, and it stops the host from starting, with the full list, when a required setting is missing or weaker than the profile allows. Read this page when you prepare a deployment for an audit, or when a host fails to start with `Beacon:Profile is Regulated, so ...`.

## What the profile is

| Value of `Beacon:Profile` | Effect |
|---|---|
| unset, empty or `None` | Nothing changes. Every setting keeps its own default. |
| `Regulated` | Fills the keys in [Keys the profile fills](#keys-the-profile-fills) when they are unset, and fails startup on every required setting that is weaker. |
| anything else | Startup fails with `Beacon:Profile names no profile; known profiles: None, Regulated (or leave it unset).` |

The value is read by name, case-insensitive, and surrounding spaces are ignored (`regulated` and ` REGULATED ` both work). A number such as `1` is not accepted.

The profile follows three rules:

1. **Fill what is unset.** If the configuration key is not present, the profile supplies its value.
2. **Never override what you set.** A value you put in configuration is kept. If it is weaker than the profile requires, the host fails to start.
3. **Some settings are never filled.** Turning off password sign-in or making every project members-only changes how people work, so the host must decide. These settings are listed as "must be set" below.

For a key the profile fills, a weaker value set in code (with the configuration key left unset) is raised to the profile's value.

```bash
# Environment-variable form
Beacon__Profile=Regulated
```

```json
{
  "Beacon": {
    "Profile": "Regulated"
  }
}
```

## Keys the profile fills

When `Beacon:Profile` is `Regulated` and the configuration key below is **not present**, the profile supplies this value. The list is `RegulatedProfile.DefaultedKeys`, in the order the startup report prints it.

| Key | Filled value | Meaning |
|---|---|---|
| `Beacon:Mcp:Audit:Required` | `true` | An MCP tool result whose audit row cannot be written is withheld. |
| `Beacon:Mcp:ForbidRowDataToLlm` | `true` | No value read from a data source goes to the LLM provider. See [LLM and prompt safety](/security/llm-data-safety/). |
| `Beacon:Retention:Locked` | `true` | No retention window may keep data forever. See [Data retention](/security/data-retention/). |
| `Beacon:Mcp:Ceilings:MaxRowLimit` | `1000` | An Admin cannot raise the MCP row limit above the built-in default. |
| `Beacon:Mcp:Ceilings:StatementTimeoutSeconds` | `30` | Same, for the statement timeout in seconds. |
| `Beacon:Mcp:Ceilings:MaxResultBytes` | `262144` | Same, for the result size in bytes. |
| `Beacon:Mcp:Ceilings:MaxConcurrentQueriesPerKey` | `4` | Same, for concurrent queries per caller. |
| `Beacon:Audit:Required` | `true` | A sign-in whose security audit event cannot be written gets no session. |
| `Beacon:Audit:ExecutionLog:Required` | `true` | A statement whose execution log row cannot be written does not run. |
| `Beacon:Authentication:Cookie:SecureAlways` | `true` | The session cookie always carries the `Secure` flag. |
| `Beacon:UserManagement:MinimumPasswordLength` | `12` | Internal passwords need at least 12 characters. |

The four ceiling values are the built-in MCP settings defaults (`McpSettingsData`). Filling them means the ceiling exists, so an Admin cannot store a higher value. A ceiling you set yourself is kept.

The profile sets no retention window. It only turns the lock on, so the host must configure the windows (see [Retention](#retention)).

## What the profile requires

`RegulatedProfile.Assess` walks one list of requirements, in the groups below. A requirement is judged on the value Beacon resolved at startup. "Filled" means the profile supplies the value when the key is unset. "Default" means the setting is already strong without any configuration, so only a weaker value fails. "Host must set" means the profile never supplies it.

The text in the last column is what the failure message prints after the setting name.

### MCP locks and ceilings

| Setting | Required | Status |
|---|---|---|
| `Beacon:Mcp:ForceReadOnly` | `true` | Default (`true`). `false` fails. |
| `Beacon:Mcp:ForcePiiDetection` | `true` | Default (`true`). `false` fails. |
| `Beacon:Mcp:Audit:Required` | `true` | Filled. |
| `Beacon:Mcp:ForbidRowDataToLlm` | `true` | Filled. |
| `Beacon:Mcp:Ceilings:MaxRowLimit` | set | Filled (`1000`). |
| `Beacon:Mcp:Ceilings:StatementTimeoutSeconds` | set | Filled (`30`). |
| `Beacon:Mcp:Ceilings:MaxResultBytes` | set | Filled (`262144`). |
| `Beacon:Mcp:Ceilings:MaxConcurrentQueriesPerKey` | set | Filled (`4`). |

Each ceiling failure reads: `must be set: an Admin can raise the MCP setting to the built-in maximum`. See [Deployment locks](/security/deployment-locks/).

### Retention

| Setting | Required | Status |
|---|---|---|
| `Beacon:Retention:Locked` | `true` | Filled. `false` fails: `must be true: a retention window may keep run history, logs or the MCP audit forever`. |

When the lock is on (filled or set), startup also requires `Beacon:Retention:Days`, every target's window, `Beacon:Retention:ContentDays` and `Beacon:Mcp:Audit:RetentionDays` to be greater than zero. Those are separate failures from the profile's own. They are listed in [Data retention](/security/data-retention/#locked-retention).

### Request limits and password guessing

| Setting | Required | Status |
|---|---|---|
| `Beacon:RateLimits:Login:PermitLimit` | greater than 0 | Default (10 per window). `0` fails: `must be greater than zero: the limit is off`. |
| `Beacon:RateLimits:Api:PermitLimit` | greater than 0 | Default (600). |
| `Beacon:RateLimits:Anonymous:PermitLimit` | greater than 0 | Default (30). |
| `Beacon:RateLimits:Mcp:PermitLimit` | greater than 0 | Default (60). |
| `Beacon:RateLimits:Ask:PermitLimit` | greater than 0 | Default (10). |
| `Beacon:RateLimits:Feedback:PermitLimit` | greater than 0 | Default (30). |
| `Beacon:RateLimits:Lockout` | on (`Enabled` true and `MaxFailedAttempts` above 0) | Default (`Enabled` true, 5 attempts, 15 minutes). Off fails: `must be on (Enabled true, MaxFailedAttempts greater than zero): password guessing is limited only per client address`. |

The break-glass delay (`Beacon:RateLimits:BreakGlassThrottle`) is not required. A delay would let anyone who knows the emergency account name keep it delayed. See the [request limits](/getting-started/configuration/#request-limits) reference.

### Sign-in and access

| Setting | Required | Status |
|---|---|---|
| `Beacon:UserManagement:LocalLogin:Enabled` | `false` | Host must set. Default is `true`. List emergency accounts in `BreakGlassUserNames`. |
| `Beacon:Authentication:Oidc:RequireMfa` | `true` when single sign-on is on | Host must set. Passes when `Beacon:Authentication:Oidc:Enabled` is not `true`. Check that your identity provider sends `amr` or `acr` claims first. |
| `UserManagement.Enabled` (code, `EnableUserManagement`) | `true` | Host must set in `Program.cs`. |
| `Beacon:Authorization:RequireProjectMembership` | `true` | Host must set. Default is `false`. |
| `Beacon:UserManagement:MinimumPasswordLength` | at least `12` | Filled (`12`). |
| `Beacon:ApiKeys:EnforceMaxLifetimeOnExistingKeys` | `true` | Host must set. Default is `false`: keys stored without an expiry keep working. |
| `Beacon:Authentication:Cookie:SecureAlways` | `true` | Filled. `false` fails. |
| `Beacon:DataProtection:AllowUnencryptedKeys` | not `true` | Default (off). `true` fails. |

### Browser protections

These two are judged on the bound `Beacon:Security` options, so a value set in code (`Configure` or `PostConfigure` of `BeaconHostSecurityOptions`) counts the same as one in configuration. That judgement lives in `Beacon.Api` (`IRegulatedProfileBoundSettings`).

| Setting | Required | Status |
|---|---|---|
| `Beacon:Security:Csp` | `Enforce` (or unset) | Default (`Enforce`). `ReportOnly` or `Off` fails. |
| `Beacon:Security:OriginGuard` | `Enforce` | Host must set. Default is `LogOnly`. List the browser origins in `Beacon:Security:AllowedOrigins` first. |
| `Beacon:PublicBaseUrl` | an absolute `https` URL | Host must set. Otherwise links, discovery documents and the origin check follow the request's `Host` header. |

### Approval workflow

All of these are set in code with `EnableApprovalWorkflow`. See [Approvals](/security/approvals/).

| Setting | Required | Status |
|---|---|---|
| `ApprovalWorkflow.Enabled` | `true` | Host must set. |
| `ApprovalWorkflow.CoversMigrationJobs` | `true` | Default (`true`). |
| `ApprovalWorkflow.CoversCustomSqlRules` | `true` | Default (`true`). |
| `ApprovalWorkflow.CoversRecipientDestinations` | `true` | Default (`true`). |
| `ApprovalWorkflow.CoversDataSourceConnections` | `true` | Host must set. Default is `false`. |
| `ApprovalWorkflow.CoversLlmConnection` | `true` | Host must set. Default is `false`. |
| `ApprovalWorkflow.CoversAdminRoleGrants` | `true` | Host must set. Default is `false`. |
| `ApprovalWorkflow.AllowSelfApproval` | `false` | Default (`false`). |

The messages read `must be true (in code): ... without a second person's approval` (and `must be false (in code): a requester can approve their own change`).

### Audit and encryption

| Setting | Required | Status |
|---|---|---|
| `Beacon:Audit:Required` | `true` | Filled. |
| `Beacon:Audit:ExecutionLog:Required` | `true` | Filled. |
| `Beacon:Audit:IntegrityKey` | set, from a secret store | Host must set. Otherwise the audit trail is signed with a key derived from the encryption key, which changes with it. At least 32 characters. |
| `Beacon:Encryption:AllowLegacyCbc` | `false` | Host must set. Turn it off once the re-encryption run reports no value left in the legacy format. |

### Data-source and LLM connections

| Setting | Required | Status |
|---|---|---|
| `Beacon:DataSources:Transport:EnforceOnExisting` | `true` | Host must set. Default is `false`: a stored connection that does not authenticate its server keeps connecting. |
| `Beacon:DataSources:Transport:AllowInsecure` | not `true` | Default (off). |
| `Beacon:DataSources:AllowHostCredentials` | not `true` | Default (off). |
| `Beacon:DataSources:AllowedHosts` | at least one host | Host must set. |
| `Beacon:LLM:AllowInsecureEndpoint` | not `true` | Default (off). |

## The startup failure

The profile is validated at startup (`ValidateOnStart`). Every failed requirement becomes one failure line, and all of them are reported together, so you fix the list once, not one key per restart. Each line has this shape:

```text
Beacon:Profile is Regulated, so <setting> <requirement>.
```

An example for a host that fails on these six requirements only (it already meets the others):

```text
Microsoft.Extensions.Options.OptionsValidationException:
Beacon:Profile is Regulated, so Beacon:UserManagement:LocalLogin:Enabled must be false: every account can sign in with a password (list emergency accounts in BreakGlassUserNames).;
Beacon:Profile is Regulated, so Beacon:Authorization:RequireProjectMembership must be true: a project open to everyone is readable by every signed-in user.;
Beacon:Profile is Regulated, so Beacon:Security:OriginGuard must be Enforce (list the origins browsers use in Beacon:Security:AllowedOrigins first): a request from another origin is only logged.;
Beacon:Profile is Regulated, so Beacon:PublicBaseUrl must be set to Beacon's https address: links, discovery documents and the origin check otherwise follow the request's Host header.;
Beacon:Profile is Regulated, so Beacon:Audit:IntegrityKey must be set, from a secret store: the audit trail is otherwise signed with a key derived from the encryption key, which changes with it.;
Beacon:Profile is Regulated, so Beacon:DataSources:AllowedHosts must list the hosts data sources may reach: a connection can reach any host on the Beacon host's networks.
```

The real message lists every unmet requirement from the tables above, not only these. The failure names configuration keys, never a value. A secret you set can never appear in it, and a wrong value is described by what it must be, not echoed back.

A code option is named as `ApprovalWorkflow.CoversAdminRoleGrants`, with `(in code)` in the text, because it has no configuration key.

## The startup report

Beacon also logs one report of these same checks (`DeploymentProfileStartupReport`, an `IStartupFilter` in `Beacon.Api`). It uses four log events.

| Event id | Name | Level | When |
|---|---|---|---|
| 9130 | `DeploymentProfileWeakSettings` | Warning | At least one setting is weaker than the profile requires, and the setting is one the report judges for this host. |
| 9131 | `DeploymentProfileHolds` | Information | Profile is `Regulated` and every requirement holds. Names the keys the profile filled, or `none`. |
| 9132 | `DeploymentProfileSurfacesOn` | Information | Profile is `Regulated`, every requirement holds, and `Beacon:Embed:Enabled` or `Beacon:Migrations:Enabled` is on. |
| 9133 | `DeploymentProfileOtherWeakSettings` | Information | Settings the report did not warn about are weaker than the profile requires. |

Which settings go to 9130 depends on the profile:

- **Under `Regulated`**, every weak setting is in the 9130 Warning. 9133 is not written.
- **Without a profile**, the 9130 Warning names only the settings the report judged before the profile grew: the MCP locks and ceilings, the retention lock, the five request limits, the lockout, local login and MFA (`RegulatedProfile.OriginalSettings`). All other weak settings are named in one 9133 Information. This keeps an alert on the Warning from starting to fire at an upgrade. With nothing weak, nothing is logged.

The text of each event, with the placeholders filled:

```text
9130  3 security settings are weaker than the regulated profile requires (profile: None): Beacon:Mcp:Audit:Required must be true: an MCP call whose audit row cannot be written still runs; ... . Set Beacon:Profile to Regulated to require them; see the Regulated profile section of the configuration docs
9131  Beacon:Profile is Regulated and every setting it requires holds; filled by the profile because the host left them unset: Beacon:Mcp:Audit:Required, Beacon:Retention:Locked, ...
9132  Beacon:Profile is Regulated and these surfaces are on; review that the deployment needs them: Beacon:Embed:Enabled, Beacon:Migrations:Enabled
9133  4 other settings are weaker than the regulated profile requires (profile: None): Beacon:Security:OriginGuard, Beacon:PublicBaseUrl, .... Beacon:Profile set to Regulated requires them; see the Regulated profile section of the configuration docs
```

9130 carries the setting and its requirement. 9133 carries the setting names only. All four carry configuration names only, never a value.

Use 9132 as a review prompt. The profile allows the anonymous embed routes and the data-migration write path, but a reviewer should know they are on. See [Deployment locks](/security/deployment-locks/).

## Gateway mode

The Beacon MCP gateway (`AddBeaconMcpGateway`) registers a marker (`McpGatewayDeployment`). In that mode Beacon serves only its gateway part. It serves no query, migration, data contract, recipient, data source, LLM or user-role change, so the approval workflow governs nothing there.

The effect on the profile is narrow: every requirement whose setting starts with `ApprovalWorkflow.` is not judged. All other groups are judged exactly as in a normal host, so the gateway needs the same keys in its configuration (audit integrity key, public base URL, retention windows, request limits and the rest). A gateway host also does not need `EnableApprovalWorkflow` in code.

## A complete starting configuration

This `appsettings.json` has every "host must set" key. Replace each `REPLACE_ME` and `<...>` value from your secret store. It assumes OIDC single sign-on with an emergency account.

```json
{
  "Beacon": {
    "Profile": "Regulated",
    "EncryptionKey": "<base64 of 32 random bytes, from a secret store>",
    "PublicBaseUrl": "https://beacon.example.com",
    "UserManagement": {
      "LocalLogin": {
        "Enabled": false,
        "BreakGlassUserNames": [ "REPLACE_ME" ]
      }
    },
    "Authentication": {
      "Oidc": {
        "Enabled": true,
        "Authority": "https://login.example.com/<tenant id>/v2.0",
        "ClientId": "<client id>",
        "ClientSecret": "<from a secret store>",
        "AllowedTenants": [ "<tenant id>" ],
        "RequireMfa": true
      }
    },
    "Authorization": { "RequireProjectMembership": true },
    "ApiKeys": { "EnforceMaxLifetimeOnExistingKeys": true },
    "Security": {
      "OriginGuard": "Enforce",
      "AllowedOrigins": [ "https://beacon.example.com" ]
    },
    "Audit": { "IntegrityKey": "<at least 32 characters, from a secret store>" },
    "Encryption": { "AllowLegacyCbc": false },
    "DataSources": {
      "AllowedHosts": [ "db.internal.example.com" ],
      "Transport": { "EnforceOnExisting": true }
    },
    "Retention": { "Days": 90, "ContentDays": 30 },
    "Mcp": { "Audit": { "RetentionDays": 365 } }
  }
}
```

Keys left out are filled by the profile: `Mcp:Audit:Required`, `Mcp:ForbidRowDataToLlm`, the four `Mcp:Ceilings` values, `Retention:Locked`, `Audit:Required`, `Audit:ExecutionLog:Required`, `Authentication:Cookie:SecureAlways` and `UserManagement:MinimumPasswordLength`.

The same settings as environment variables:

```bash
Beacon__Profile=Regulated
Beacon__PublicBaseUrl=https://beacon.example.com
Beacon__UserManagement__LocalLogin__Enabled=false
Beacon__Authorization__RequireProjectMembership=true
Beacon__Security__OriginGuard=Enforce
Beacon__DataSources__AllowedHosts__0=db.internal.example.com
```

User management and the approval workflow are code options. Set them in `Program.cs`:

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options =>
{
    // ... the scheduler and sign-in wiring as before

    options.EnableUserManagement();
    options.EnableApprovalWorkflow(x =>
    {
        x.CoversDataSourceConnections = true;
        x.CoversLlmConnection = true;
        x.CoversAdminRoleGrants = true;
    });
});
```

`CoversMigrationJobs`, `CoversCustomSqlRules` and `CoversRecipientDestinations` are already `true`, and `AllowSelfApproval` is already `false`. Do not change them.

## Migrating an existing deployment

Turn the profile on in a test environment first. Never start with production.

1. **Read the weak settings without the profile.** Start the host with `Beacon:Profile` unset. Read the 9130 Warning and the 9133 Information. Together they list every setting that is weaker than the profile requires.
2. **Turn the profile on in the test environment.** Set `Beacon__Profile=Regulated`. The host stops with the full failure list described above.
3. **Fix group by group.** Work through the tables in order. After each group, restart and read the shorter list.
   1. *Retention*: choose windows. See [Data retention](/security/data-retention/).
   2. *Browser protections*: set `PublicBaseUrl`, list `AllowedOrigins`, and run `OriginGuard` as `LogOnly` first. Read what it logs, then switch to `Enforce`.
   3. *Sign-in and access*: turn local login off and name a break-glass account. Turn on `RequireMfa` after your identity provider sends the `amr` or `acr` claims. Turn on `RequireProjectMembership` and check that every project has members, because data sources no project links become Admin-only.
   4. *Approval workflow*: add the three `Covers*` switches in code.
   5. *Audit and encryption*: set `Audit:IntegrityKey` from a secret store. Turn `AllowLegacyCbc` off only after the re-encryption run reports nothing left in the legacy format; values still in that format become unreadable.
   6. *Connections*: list `AllowedHosts`, then turn `EnforceOnExisting` on. A stored connection that does not authenticate its server stops connecting, so fix those connections first.
4. **Confirm.** When every requirement holds, the log shows event 9131 and, if the embed or migration surface is on, event 9132. Review 9132.
5. **Promote** the same configuration to production.

## Recommended values

| Topic | Development | Production | Regulated |
|---|---|---|---|
| `Beacon:Profile` | unset | unset or `Regulated` | `Regulated` |
| `Beacon:Mcp:ForceReadOnly`, `ForcePiiDetection` | `true` (default) | `true` | `true` (required) |
| `Beacon:Mcp:Audit:Required` | `false` | `true` | `true` (filled) |
| `Beacon:Mcp:ForbidRowDataToLlm` | `false` | by decision | `true` (filled) |
| `Beacon:Retention:Days` / `ContentDays` | `0` (forever) | `90` / `30` | set above zero (required through the lock) |
| `Beacon:Retention:Locked` | `false` | `false` or `true` | `true` (filled) |
| `Beacon:UserManagement:LocalLogin:Enabled` | `true` | `false` with SSO | `false` (required) |
| `Beacon:Authorization:RequireProjectMembership` | `false` | `true` | `true` (required) |
| `Beacon:Security:OriginGuard` | `LogOnly` (default) | `Enforce` | `Enforce` (required) |
| `Beacon:Audit:Required` | `false` | `true` | `true` (filled) |
| `Beacon:Audit:IntegrityKey` | unset | set | set (required) |
| `Beacon:DataSources:Transport:EnforceOnExisting` | `false` | `true` | `true` (required) |
| `Beacon:DataSources:AllowedHosts` | empty | set | set (required) |

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Startup fails with `Beacon:Profile names no profile; known profiles: None, Regulated` | The value is misspelled, or is a number. | Use `Regulated` or `None`, or remove the key. |
| Startup fails with a list that starts `Beacon:Profile is Regulated, so ...` | One or more required settings are missing or weaker. | Fix every line. The list is complete for one start. |
| A key you set to `false` is refused although the profile fills it | The profile never overrides a configured value. | Remove the key to take the filled value, or set it as required. |
| `Beacon:Retention:Locked is on, so Beacon:Retention:Days must be greater than zero: ...` appears with the profile on | The profile turned `Retention:Locked` on and no windows are set. | Set `Days`, `ContentDays` and `Mcp:Audit:RetentionDays`. See [Data retention](/security/data-retention/). |
| `ApprovalWorkflow.CoversAdminRoleGrants must be true (in code)` | A code option, so no configuration key can fix it. | Set it in `EnableApprovalWorkflow` in `Program.cs`. |
| The same approval failure appears in the gateway | It should not: approval requirements are skipped there. | Check that the host calls `AddBeaconMcpGateway`. |
| `OriginGuard must be Enforce` although configuration says `Enforce` | The value is judged on the bound options, so code can override configuration. | Search for `Configure<BeaconHostSecurityOptions>` or `PostConfigure<BeaconHostSecurityOptions>`. |

## Related pages

- [Deployment locks](/security/deployment-locks/)
- [Data retention and content lock](/security/data-retention/)
- [LLM and prompt safety](/security/llm-data-safety/)
- [Approvals](/security/approvals/)
- [Authorization](/security/authorization/)
- [Configuration reference: Regulated profile](/getting-started/configuration/#regulated-profile-beaconprofile)
