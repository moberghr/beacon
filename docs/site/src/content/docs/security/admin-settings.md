---
title: Admin Settings
description: "The Admin settings page: the base URL and the LLM provider, changed at runtime and swapped without a restart, who may read or change each setting, and the settings change history."
---

**Admin settings** (`/beacon/admin-settings`) holds the base URL Beacon puts in outgoing links and the LLM provider connection. A save applies to the running process at once, without a restart. Every change is recorded in a change history that also covers MCP settings and migration-write-target changes.

## Who may do what

The page is open to Admins and to two [functional permissions](/security/authorization/#functional-permissions): an **Auditor** reads it, a **SecurityAdmin** reads and changes it. The LLM connection, the LLM concurrency and per-minute limits, and the base URL stay an Admin's.

| Action | Route | Admin | SecurityAdmin | Auditor |
|---|---|---|---|---|
| Read the settings (secrets shown only as set or not set) and the change history | `GET /beacon/api/admin-settings` | ✓ | ✓ | ✓ |
| List the LLM providers this host installed | `GET /beacon/api/admin-settings/llm-providers` | ✓ | ✓ | ✓ |
| Change the models and the monthly budget | `PUT /beacon/api/admin-settings` | ✓ | ✓ | – |
| Change **Max concurrent requests**, **Tokens per minute**, **Requests per minute** or **Enforce requests/tokens per minute** | `PUT /beacon/api/admin-settings` | ✓ | – (`403`) | – |
| Change the **Base URL** | `PUT /beacon/api/admin-settings` | ✓ | – (`403`) | – |
| Change the LLM **provider**, **endpoint**, **region**, Bedrock **authentication mode** or any **credential** | `PUT /beacon/api/admin-settings` | ✓ | – (`403`) | – |
| Test the stored LLM connection | `POST /beacon/api/admin-settings/test-llm` | ✓ | ✓ | – |
| Test with another provider, endpoint, region, authentication mode or credential | `POST /beacon/api/admin-settings/test-llm` | ✓ | – (`403`) | – |

The provider, endpoint, region and authentication mode decide where Beacon sends its LLM requests with the stored credentials, the base URL is the address of every link Beacon sends out, and the concurrency and per-minute limits decide how much LLM traffic runs at all, so only an Admin changes them. For anyone else the page shows those fields disabled with a note, and a request that changes any of them answers `403` and saves nothing. An Auditor sees the page read-only. API keys never reach these routes.

While the [approval workflow](/security/approvals/#migration-jobs-custom-sql-rules-and-recipients) covers the LLM connection (`CoversLlmConnection`), an Admin's change of the provider, endpoint, region or authentication mode, or a first stored provider, is not applied on save: it waits for a second live Admin's approval, and the stored connection and models stay in use until then. The other values of the same save apply at once; a change of credentials or models alone, and testing the connection, are not reviewed.

## The page

### General

| Field | What it does |
|---|---|
| **Base URL** | The address Beacon puts in notification links (`{BaseUrl}/notifications/{id}` in Teams, Slack and webhook messages). The UI is served under `/beacon`, so include it: `https://beacon.example.com/beacon` |

### AI provider

Pick the provider; the page shows only the fields it needs.

| Provider (UI) | `AiProvider` value (API `llmProvider`, `Beacon:LLM:Provider`) | Fields |
|---|---|---|
| **OpenAI** | `OpenAI` (0) | API key, Model, Fast model |
| **Anthropic** | `Claude` (1) | API key, Model, Fast model |
| **Azure OpenAI** | `AzureOpenAI` (2) | Endpoint, API key, Deployment (model), Fast deployment |
| **AWS Bedrock** | `Bedrock` (3) | Region, Credentials, Model, Fast model |

AWS Bedrock is installed only when the host references `Moberg.Beacon.AI.Bedrock` and calls `AddBeaconBedrock()`. The page offers only the providers the host installed (a stored provider that is not installed shows as **Package not installed**); picking another one is refused with `400`.

**Bedrock credentials.** The authentication mode under **Credentials** decides which credentials the client uses. **IAM role** (the default) uses the default AWS credential chain (instance role, environment). **Access key** uses the AWS access key id and secret access key, and **Temporary credentials** uses them with a session token. Both of these modes need both keys: one without the other fails instead of falling back to the default chain. On `PUT /beacon/api/admin-settings` the fields are `llmBedrockAuthMode`, `llmAwsAccessKeyId`, `llmAwsSecretAccessKey` and `llmSessionToken`; in configuration they are `Beacon:LLM:BedrockAuthMode`, `AwsAccessKeyId`, `AwsSecretAccessKey` and `SessionToken` (see [Configuration → LLM](/getting-started/configuration/#ai--llm)). The role or keys need the `bedrock:InvokeModel` permission for the chosen model.

An install that stored the key pair as `accessKey:secretKey` in the API key field keeps working. With both key fields empty, the access key and temporary credential modes use that pair. The IAM role mode uses it as well, but logs a warning (without the keys) that static keys from the API key field are in use instead of the default chain; clear the API key field to use the IAM role, or move the keys to the access key mode. When a saved configuration cannot build a provider, no provider is active until the configuration is fixed: AI features answer that the LLM provider is not configured, and the previous provider is never kept. The log names the failure's type only.

**Test connection** sends a tiny request (at most 16 output tokens) with the stored connection and the models on the form, without saving.

Credentials and the Azure endpoint are encrypted at rest with `Beacon:EncryptionKey` and never returned: the page shows only whether each is set. Over the API, `null` for a credential or the endpoint keeps the stored value. The REST API sends enums as integers; configuration takes the names.

### Rate limits & budget

**Max concurrent requests** (default 50) applies to the LLM request queue as soon as it is saved: a higher limit lets waiting calls run at once, a lower one takes effect as running calls finish (none is cancelled), and background work keeps at most half the slots.

**Tokens per minute** and **Requests per minute** (defaults 80,000 and 1,000) apply only while **Enforce requests/tokens per minute** is on; it is off by default, and while it is off the two fields are recorded but not applied. When it is on, each call reserves one request and an estimate of its tokens (prompt characters divided by four, plus the call's maximum answer tokens) before it is sent, and the tokens are settled with the usage the provider reports. The reservation is taken before the call waits for a concurrency slot and held while it waits; a call that is never sent (refused, cancelled or timed out) gives it back. The tokens are settled only when the response reports its input tokens; otherwise the estimate stands. Calls the limits cannot admit at once wait in arrival order, so a large call is never overtaken by smaller ones; an interactive call may pass waiting background work, and background work leaves a quarter of each per-minute allowance for interactive calls. A call that cannot get its share within its queue wait fails with *The LLM is busy. Try again later.* `0` means unlimited for that dimension.

**Monthly budget (USD)** (default 100) is recorded, not enforced: Beacon keeps no record of spend, so calls continue past it. Use the provider's own spend limits for a hard cap.

Over the API, `llmRateLimitsEnabled` left out or `null` keeps the stored switch. Only an Admin changes these four values (`403` otherwise): a SecurityAdmin sees them and sends the displayed ones back, and a SecurityAdmin's save neither stores nor applies them, so the host-configured limits, or the ones an Admin saved, stay in force. Once an Admin saves them, the stored limits replace the host-configured ones (`Beacon:LLM:Limits`), also after a restart, and the first time they differ from the configured ones the host logs it at Information (numbers only).

Saving the settings records each changed value, the enforcement switch included (`LLM.RateLimitsEnabled`), in the change history and the security audit.

### Recent changes

The page lists the latest changes from the settings history: when, which key, and who.

## How a save applies

1. The page sends `PUT /beacon/api/admin-settings`.
2. `AppSettingsService` writes the values to `app_settings` (sensitive ones encrypted), records each changed key in `app_setting_history`, and clears its cache.
3. The base URL in the running process (`BeaconConfiguration.BaseUrl`) is replaced.
4. `ILlmConfigurationUpdater.UpdateConfiguration` hands the settings to `LlmProviderManager`, which builds a new provider and swaps it in atomically. A save by anyone but an Admin writes no LLM limit and keeps the limits in force. `DelegatingLlmProvider`, the proxy every feature holds, uses the new provider for the next request.

**At startup** the LLM provider is built from `Beacon:LLM` and the base URL comes from the host's code (`options.BaseUrl`; the sample host reads `Beacon:BaseUrl`). Saving Admin settings swaps them immediately. After a restart the stored settings replace the configured ones once they are read: a stored base URL, the stored LLM configuration when a provider was stored, and the LLM limits (concurrency, per-minute limits and their enforcement) only when an Admin stored them, also when no provider was stored. A save that stores no provider keeps the provider from `Beacon:LLM` in force, as a restart would: it applies the limits (an Admin's save) and the budget only. See [Configuration → AI / LLM](/getting-started/configuration/#ai--llm).

Settings are cached in memory for one hour; a save clears the cache.

## Change history

`app_setting_history` records one row per changed value: `setting_key`, `old_value`, `new_value` (`***` for sensitive values), `changed_at` and `changed_by_user_id`. It is returned with `GET /beacon/api/admin-settings` (the latest 200 rows), so Auditors, SecurityAdmins and Admins read it. Three families of keys are written:

| Keys | Written when |
|---|---|
| `General.BaseUrl`, `LLM.*` (for example `LLM.Provider`, `LLM.ApiKey`, `LLM.Model`) | Admin settings are saved |
| `Mcp.{Setting}`, `Mcp.Project.{projectId}.{Setting}` | an MCP setting changes, globally or for a project |
| `DataSource.{id}.AllowMigrationWrites` | an Admin marks or unmarks a data source as a [migration write target](/features/data-migration/) |

Every save that changes settings is also recorded in the [security audit trail](/security/audit-trail/) (`settings.admin_settings_changed`, `settings.mcp_settings_changed`, `settings.mcp_project_settings_changed`), naming the keys that changed and the history rows that hold their values, with a keyed digest of each value before and after; the values themselves stay in the history. Security-relevant actions outside settings (sign-ins, role changes, membership changes and the rest) are recorded in the security audit trail, not as history rows; see [Security model](/security/#12-audit-and-security-events).

## Programmatic access

```csharp
public sealed class MyService(IAppSettingsService settingsService)
{
    public async Task UseAsync(CancellationToken ct)
    {
        var settings = await settingsService.GetSettingsAsync(ct);   // BaseUrl, LlmProvider, LlmModel, …

        settings.LlmModel = "<model-id>";
        await settingsService.SaveSettingsAsync(settings, userId: "<the caller's external id>", ct);

        var llmHistory = await settingsService.GetHistoryAsync(key: "LLM.Model", ct);
    }
}
```

`SaveSettingsAsync` applies no permission check: the REST route does. Code that calls it directly is responsible for who may.

`ILlmConfigurationUpdater` (`void UpdateConfiguration(AppSettingsData settings)`) lives in `Beacon.Core` so `AppSettingsService` can call it without referencing `Beacon.AI`; `LlmProviderManager` implements it.

## MCP settings

MCP behaviour has its own page, **MCP settings** (`/beacon/mcp-settings`): an Auditor or a SecurityAdmin reads it, a SecurityAdmin changes it, Admins do both. Turning read-only enforcement or PII detection off, globally or for a project, is an Admin's alone (`403` otherwise). Its tabs are **Pre-prompt**, **Tool descriptions**, **Guardrails** and **Context preview**; a scope selector switches between the global defaults and a project's overrides.

The deployment can pin settings no one can change at runtime, through `Beacon:Mcp`:

| Lock | Default | Pins |
|---|---|---|
| `ForceReadOnly` | `true` | read-only enforcement on, globally and for every project |
| `ForcePiiDetection` | `true` | PII detection on |
| `ForceNoContentRetention` | `false` | content retention off (questions, SQL and free-text errors never persisted) |
| `ForbidRowDataToLlm` | `false` | sample-value collection, value grounding and the eval judge off |

On the global settings a locked switch is shown **disabled** with a lock and a note naming the configuration key; in a project's scope locked fields are not offered and a banner names them. A write that contradicts a lock answers `409`, an Admin's included; `GET /beacon/api/mcp/settings/locks` lists the pinned settings. `Beacon:Mcp:Ceilings` clamp numeric settings downward: `MaxRowLimit` caps the row limit the MCP tools apply, `MaxSqlChars` and `MaxQuestionChars` are always-on length limits, `AskTimeoutSeconds` and `AskMaxLlmCalls` are the always-on time and LLM-call budgets of one `ask` call, and `AskSchemaContextChars` bounds the schema context one `ask` sends to the LLM; the other ceilings clamp the setting of the same name shown on this page. Every MCP settings change is recorded in the history above. See [Configuration → Deployment locks](/getting-started/configuration/#deployment-locks) and [MCP server](/mcp/server/).

## See also

- [AI integration](/features/ai-integration/): the features that use the LLM connection
- [MCP server](/mcp/server/): MCP settings and guardrails
- [Authorization](/security/authorization/#functional-permissions): Auditor and SecurityAdmin
- [Configuration](/getting-started/configuration/): `Beacon:LLM` and the startup values
