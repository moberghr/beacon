---
title: Secrets and keys
description: "Every secret Beacon holds or needs, where it lives, how it is protected, how to rotate it, and how to supply it from a secret store."
---

Beacon needs a few secrets from the host: an encryption key, optional audit and embed keys, a Data Protection key ring and a first-run setup token. It also generates secrets of its own, such as API keys and embed grants, and stores only a hash of them. This page lists each one, the startup rules that protect you from weak values, and the steps to rotate them.

## Overview

| Secret | Where it lives | How it is protected | How to rotate |
|---|---|---|---|
| `Beacon:EncryptionKey` | Host configuration (secret store or env var) | Never stored in the database. Checked at startup by `EncryptionKeyRequirements`. | Move it to `Beacon:PreviousEncryptionKeys`, set a new key, wait for re-encryption. See the runbook below. |
| `Beacon:PreviousEncryptionKeys` | Host configuration | Decrypt only. Never used to write. Not strength-checked. | Remove once re-encryption reports nothing depends on it. |
| `Beacon:Encryption:AllowLegacyCbc` | Host configuration | Switch for reading the old v1 format. Default on. | Turn off once no v1 value remains. |
| `Beacon:Mcp:CallerHashKey` | Host configuration | Keyed hash of MCP caller identities. | Set it before the first key change so hashes stay stable. |
| `Beacon:Audit:IntegrityKey`, `PreviousIntegrityKeys` | Host configuration | Keys the audit trail's integrity hashes. At least 32 characters. | Set a new key, move the old one to `PreviousIntegrityKeys` (verify only). |
| `Beacon:Embed:SigningKey` | Host configuration | Signs embed tokens (HS256). Same strength rules as the encryption key, and it must differ from it. | Replace it. Tokens it signed stop working. There are no previous keys. |
| Data Protection key ring | Beacon database (`data_protection_keys`) or the host's own store | Encrypted at rest with a certificate or key vault. Startup refuses an unencrypted ring outside Development. | Add a new key to the ring. See below. |
| API keys (`sk-sem_...`) | Database, SHA-256 hash only | The raw key is shown once. | Create a new key, revoke the old one. |
| Embed grant secrets (`bge_...`) | Database, SHA-256 hash only | The raw secret is shown once. | Revoke the grant and create another. |
| Passwords | Database, PBKDF2-SHA256 | 600,000 iterations, stored in the hash (`v2$<iter>$...`). | The user changes it. A weaker stored hash is replaced after the next successful sign-in. |
| `Beacon:UserManagement:SetupToken` | Host configuration | At least 32 characters or startup fails. | Change it before first-run setup. It is not used afterwards. |

## The encryption key

`Beacon:EncryptionKey` encrypts data-source connection strings, recipient destinations, LLM credentials and other stored secrets. Startup checks it in every environment.

Generate one:

```bash
openssl rand -base64 32
```

### What the startup check refuses

A key must be base64 that decodes to at least 32 random bytes, with at least 16 distinct byte values, and must not be a value the sample host or documentation ever printed. The messages never contain the key.

| Key | Message |
|---|---|
| unset or blank | ``Beacon:EncryptionKey must be configured. Generate one with `openssl rand -base64 32` and supply it through a secret store or the Beacon__EncryptionKey environment variable.`` |
| `${BEACON_KEY}` | ``Beacon:EncryptionKey contains '${': .NET configuration does not expand placeholders, so the key would be the placeholder text itself. Generate one with `openssl rand -base64 32` and supply it through a secret store or the Beacon__EncryptionKey environment variable.`` |
| a published sample value | ``Beacon:EncryptionKey is a publicly known value (the sample placeholder or a key printed in the documentation), so anything it protects can be read or forged by anyone. Generate one with `openssl rand -base64 32` and supply it through a secret store or the Beacon__EncryptionKey environment variable.`` |
| `c2hvcnQ=` (too short) | ``Beacon:EncryptionKey must be base64 that decodes to at least 32 random bytes. Generate one with `openssl rand -base64 32` and supply it through a secret store or the Beacon__EncryptionKey environment variable.`` |
| base64 of a passphrase, or a repeated pattern | ``Beacon:EncryptionKey decodes to bytes that are not random (printable text, or a repeated pattern): it must be base64 of at least 32 random bytes, not a passphrase encoded as base64. Generate one with `openssl rand -base64 32` and supply it through a secret store or the Beacon__EncryptionKey environment variable.`` |

When a previous key is in use, the refusal also says: `To keep reading the values encrypted with the key configured now, move that key to Beacon:PreviousEncryptionKeys (decrypt only) and set a new Beacon:EncryptionKey.`

### Supplying it

Environment variable (Kubernetes style, from a Secret):

```yaml
env:
  - name: Beacon__EncryptionKey
    valueFrom:
      secretKeyRef:
        name: beacon-secrets
        key: encryption-key
  - name: Beacon__Audit__IntegrityKey
    valueFrom:
      secretKeyRef:
        name: beacon-secrets
        key: audit-integrity-key
```

Azure Key Vault configuration provider. Secret names use `--` for the section separator:

```csharp
builder.Configuration.AddAzureKeyVault(
    new Uri("https://example-vault.vault.azure.net/"),
    new DefaultAzureCredential());
// Vault secrets: Beacon--EncryptionKey, Beacon--Audit--IntegrityKey, Beacon--Embed--SigningKey
```

In Development use user secrets:

```bash
dotnet user-secrets set "Beacon:EncryptionKey" "$(openssl rand -base64 32)"
```

Do not put a key in `appsettings.json`, in source or in a test file.

## Rotating the encryption key

The v2 format records nothing about which key wrote a value, so Beacon tries the current key and then each previous key. That is why a replaced key moves to `Beacon:PreviousEncryptionKeys` instead of being deleted.

1. **Set the integrity key first.** If `Beacon:Audit:IntegrityKey` is unset, the audit trail is signed with a key derived from `Beacon:Mcp:CallerHashKey`, else from the encryption key, and that changes with it. Set both before you rotate.

   ```json
   { "Beacon": { "Mcp": { "CallerHashKey": "<random 32+ chars>" },
                 "Audit": { "IntegrityKey": "<random 32+ chars>" } } }
   ```

2. **Move the old key and set a new one.**

   ```json
   {
     "Beacon": {
       "EncryptionKey": "<new key>",
       "PreviousEncryptionKeys": [ "<old key>" ]
     }
   }
   ```

   Environment form: `Beacon__EncryptionKey=<new>` and `Beacon__PreviousEncryptionKeys__0=<old>`.

3. **Restart.** Beacon reads every value with either key. A value moves to the new key when it is next saved, and `IStoredValueReEncryptionService` rewrites the rest. The sample host runs it as `ReEncryptStoredValuesJob`, enqueued at every start. It runs as a background job, never inline in startup, and uses conditional writes so a concurrent edit is not overwritten.

4. **Read the result.** Each run logs event 7122 (`StoredValuesReEncrypted`).

   While something still depends on a previous key, it is a Warning:

   ```text
   Stored value re-encryption: 4 under a previous key, 0 in the legacy format, 12 re-encrypted with the current key, 0 changed meanwhile, 0 readable with no configured key, 0 failed, 0 keyed hashes that cannot be rewritten under the current key
   ```

   When nothing does, it is an Information line:

   ```text
   Stored value re-encryption: no value of Beacon's own is under a previous key or in the legacy format (values a host stored through IEncryptionService are not scanned); Beacon:PreviousEncryptionKeys can be removed and Beacon:Encryption:AllowLegacyCbc turned off
   ```

5. **Remove the old key** only when the result shows all of these:

   | Field | Needed value |
   |---|---|
   | `UnderPreviousKeys` | 0 |
   | `Unreadable` | 0 (a value no configured key can read) |
   | `Failed` | 0 (a failure makes the run throw: `N stored values could not be re-encrypted; see the log for their ids.`) |
   | `UnresolvedKeyDependencies` | 0 |

   `PreviousKeysRemovable` is true when all four hold.

   `UnresolvedKeyDependencies` counts keyed definition hashes (recipients, data sources) and pending LLM connection changes that cannot be rewritten under the current key. Decide each pending change, or save the recipient or data source back to its approved definition, then run again.

6. **A host that stores its own values** through `IEncryptionService` must check those itself. The scan covers Beacon's own values only.

If the host replaced `IEncryptionService`, the run logs a Warning and does nothing.

### What the key does not cover

The MCP caller hash key (`Beacon:Mcp:CallerHashKey`) keeps audit identities stable across an encryption key change. The embed signing key has no previous-key list: replacing it only ends the tokens it signed, and each token lives at most 900 seconds.

## Legacy v1 format and AllowLegacyCbc

Early versions wrote values as unauthenticated AES-CBC (v1). The current format (v2) is authenticated. A rotation run rewrites v1 values in v2 and counts them as `LegacyFormat`.

| Key | Type | Default | Range | What it does |
|---|---|---|---|---|
| `Beacon:Encryption:AllowLegacyCbc` | bool | `true` | true or false | Keep reading v1 values. The first v1 read is logged once. |

Turn it off when `LegacyFormat` is 0 and `Unreadable` and `Failed` are 0 (`LegacyFormatReadRemovable`). With it off, a v1 value cannot be read at all.

## Audit integrity keys

| Key | Type | Default | Range | What it does |
|---|---|---|---|---|
| `Beacon:Audit:IntegrityKey` | string | unset | 32 or more characters, no `${` | Keys the security audit, MCP audit, execution log and settings history hashes. Unset: derived from `CallerHashKey`, else the encryption key. |
| `Beacon:Audit:PreviousIntegrityKeys` | string list | empty | same | Used to verify older events. Never used to write. |

Rotate by setting a new `IntegrityKey` and moving the old one to `PreviousIntegrityKeys`. Events signed under a key derived from the current or a previous encryption key still verify. See [Audit trail](/security/audit-trail/).

## Embed signing key

`Beacon:Embed:SigningKey` is read only while `Beacon:Embed:Enabled` is on. It must pass the same checks as the encryption key, and must differ from the encryption key, every previous key and the obsolete `Beacon:EmbedToken` key. The token is signed, not encrypted, so a locked parameter value in it is readable by the viewer and must never be a secret. See [Embedding](/integrate/embedding/).

## Data Protection

ASP.NET Core Data Protection protects the `Beacon.Auth` session cookie and antiforgery tokens. It belongs to the host. `AddBeaconCookieAuthentication` configures none of it.

| Situation | Result |
|---|---|
| Host configures Data Protection itself | Beacon changes nothing. The same checks below still run. |
| Host calls `AddBeaconDataProtection(configuration, environment)` | Key ring stored in Beacon's database (`data_protection_keys`) under `Beacon:DataProtection:ApplicationName`. |
| Outside Development, no application name | Startup fails: `Beacon:DataProtection:ApplicationName must be set outside Development when Beacon configures Data Protection (AddBeaconDataProtection). Give each deployment its own name (for example beacon-production), so a sign-in cookie issued by one environment is never accepted by another.` |
| Outside Development, persisted ring without `ProtectKeysWith*` | Startup fails (`DataProtectionKeyEncryptionCheck`). |
| Outside Development, a persisted key still stored without encryption | `DataProtectionKeyRingStartupCheck` revokes it and replaces it at startup. |

| Key | Type | Default | Range | What it does |
|---|---|---|---|---|
| `Beacon:DataProtection:ApplicationName` | string | `Beacon` in Development, required elsewhere | any name, one per deployment | Isolates cookies between environments. |
| `Beacon:DataProtection:AllowUnencryptedKeys` | bool | `false` | true or false | Acknowledge an unencrypted ring (for example one machine whose key directory only the service account can read). Startup logs a Warning, and keys stored without encryption are not revoked. |

With a certificate:

```csharp
var certificate = X509CertificateLoader.LoadPkcs12FromFile(
    "/etc/beacon/dp-cert.pfx", builder.Configuration["DataProtection:CertPassword"]);

builder.Services.AddBeaconDataProtection(builder.Configuration, builder.Environment)
    .ProtectKeysWithCertificate(certificate);
```

With Azure Key Vault:

```csharp
builder.Services.AddBeaconDataProtection(builder.Configuration, builder.Environment)
    .ProtectKeysWithAzureKeyVault(
        new Uri("https://example-vault.vault.azure.net/keys/beacon-dp"),
        new DefaultAzureCredential());
```

Use a certificate or key only that environment holds. See [Host setup](/integrate/host-setup/).

## Hashed secrets

- **API keys** start with `sk-sem_`. Only a SHA-256 hash is stored. The raw key is shown once and never logged. See [API keys](/security/api-keys/).
- **Embed grant secrets** start with `bge_`. Only the SHA-256 is stored. Revoke and recreate to rotate.
- **Passwords** use PBKDF2-SHA256 at 600,000 iterations, with the count stored in the hash. Unversioned legacy hashes (100,000 iterations) are replaced after a successful sign-in, by a write conditional on the hash that was verified.

## Setup token

`Beacon:UserManagement:SetupToken` is the secret the first-run page needs to create the first super admin. At least 32 characters or startup fails. If neither the option nor the key is set, Beacon generates a random token per process and prints it once to standard error while no super admin exists. With several replicas, set it explicitly, because each replica would otherwise generate its own. A wrong token records `auth.setup_token_refused`.

## Recommended values

| Item | Development | Production | Regulated profile |
|---|---|---|---|
| `Beacon:EncryptionKey` | user secrets | secret store | secret store |
| `Beacon:Encryption:AllowLegacyCbc` | default | off after migration | must be `false` |
| `Beacon:Audit:IntegrityKey` | unset | set | must be set |
| Data Protection | `AddBeaconDataProtection` | plus `ProtectKeysWith*` | must not set `AllowUnencryptedKeys` |
| `Beacon:Embed:SigningKey` | only if embed is on | secret store | secret store |

See [Regulated profile](/security/regulated-profile/) for the full list.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Startup: `Beacon:EncryptionKey contains '${'` | The placeholder was not expanded. Supply the real value through an environment variable or secret store. |
| Startup fails after changing the key | Stored values are unreadable. Put the old key in `Beacon:PreviousEncryptionKeys`. |
| `Unreadable` above 0 | No configured key reads some value. Find the key that wrote it, add it to `PreviousEncryptionKeys`, and run again. |
| Previous key will not become removable | Look at `UnresolvedKeyDependencies`. Decide pending change requests. |
| Audit verification fails after a key change | `IntegrityKey` was unset. Set the old derived source as `PreviousIntegrityKeys` or restore the old key. |
| Everyone signed out after a deploy | Data Protection ring changed or the application name differs. Keep one ring and one name per environment. |
| Startup refuses an unencrypted key ring | Chain `ProtectKeysWith*`, or set `AllowUnencryptedKeys` if you accept the risk. |

## Related pages

- [Data-source connection security](/security/data-source-connections/)
- [Audit trail](/security/audit-trail/)
- [API keys](/security/api-keys/)
- [Regulated profile](/security/regulated-profile/)
- [Deployment locks](/security/deployment-locks/)
- [Configuration](/getting-started/configuration/)
- [Host setup](/integrate/host-setup/)
