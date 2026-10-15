---
title: Admin Settings
description: The Admin settings page — the base URL and the LLM provider, changed at runtime and swapped without a restart, who may read or change each setting, and the settings change history.
---

**Admin settings** (`/beacon/admin-settings`) holds the base URL Beacon puts in outgoing links and the LLM provider connection. A save applies to the running process at once, without a restart. Every change is recorded in a change history that also covers MCP settings and migration-write-target changes.

## Who may do what

The page is open to Admins and to two [functional permissions](/features/authorization/#functional-permissions): an **Auditor** reads it, a **SecurityAdmin** reads and changes it. The LLM connection and the base URL stay an Admin's.

| Action | Route | Admin | SecurityAdmin | Auditor |
|---|---|---|---|---|
| Read the settings (secrets shown only as set or not set) and the change history | `GET /beacon/api/admin-settings` | ✓ | ✓ | ✓ |
| Change the models and the rate and budget values | `PUT /beacon/api/admin-settings` | ✓ | ✓ | – |
| Change the **Base URL** | `PUT /beacon/api/admin-settings` | ✓ | – (`403`) | – |
| Change the LLM **provider**, **endpoint**, **region**, Bedrock **authentication mode** or any **credential** | `PUT /beacon/api/admin-settings` | ✓ | – (`403`) | – |
| Test the stored LLM connection | `POST /beacon/api/admin-settings/test-llm` | ✓ | ✓ | – |
| Test with another provider, endpoint, region, authentication mode or credential | `POST /beacon/api/admin-settings/test-llm` | ✓ | – (`403`) | – |

The provider, endpoint, region and authentication mode decide where Beacon sends its LLM requests with the stored credentials, and the base URL is the address of every link Beacon sends out, so only an Admin changes them. For anyone else the page shows those fields disabled with a note, and a request that changes any of them answers `403` and saves nothing. An Auditor sees the page read-only. API keys never reach these routes.

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

**Bedrock credentials.** Leave the API key empty to use the default AWS credential chain (instance role, environment); **IAM role** under **Credentials** is this setting. For explicit credentials, set the API key to `accessKey:secretKey`, plus a session token for temporary credentials: `llmApiKey` and `llmSessionToken` on `PUT /beacon/api/admin-settings`, or `Beacon:LLM:ApiKey` and `Beacon:LLM:SessionToken` in configuration (see [Configuration → LLM](/getting-started/configuration/#ai--llm)). The role or keys need the `bedrock:InvokeModel` permission for the chosen model.

**Test connection** sends a tiny request (at most 16 output tokens) with the stored connection and the models on the form, without saving.

Credentials and the Azure endpoint are encrypted at rest with `Beacon:EncryptionKey` and never returned: the page shows only whether each is set. Over the API, `null` for a credential or the endpoint keeps the stored value. The REST API sends enums as integers; configuration takes the names.

### Rate limits & budget

**Max concurrent requests**, **Tokens per minute**, **Requests per minute** and **Monthly budget (USD)** (defaults 50, 80,000, 1,000 and 100) are recorded and shown in settings. The LLM request queue takes its concurrency from `Beacon:LLM:Limits:MaxConcurrentRequests` when the host starts.

### Recent changes

The page lists the latest changes from the settings history: when, which key, and who.

## How a save applies

1. The page sends `PUT /beacon/api/admin-settings`.
2. `AppSettingsService` writes the values to `app_settings` (sensitive ones encrypted), records each changed key in `app_setting_history`, and clears its cache.
3. The base URL in the running process (`BeaconConfiguration.BaseUrl`) is replaced.
4. `ILlmConfigurationUpdater.UpdateConfiguration` hands the settings to `LlmProviderManager`, which builds a new provider and swaps it in atomically. `DelegatingLlmProvider`, the proxy every feature holds, uses the new provider for the next request.

**At startup** the LLM provider is built from `Beacon:LLM` and the base URL comes from the host's code (`options.BaseUrl`; the sample host reads `Beacon:BaseUrl`). Saving Admin settings swaps them immediately. Keep `Beacon:LLM` and the base URL in your configuration in step with Admin settings, or save the settings again after a restart. See [Configuration → AI / LLM](/getting-started/configuration/#ai--llm).

Settings are cached in memory for one hour; a save clears the cache.

## Change history

`app_setting_history` records one row per changed value: `setting_key`, `old_value`, `new_value` (`***` for sensitive values), `changed_at` and `changed_by_user_id`. It is returned with `GET /beacon/api/admin-settings` (the latest 200 rows), so Auditors, SecurityAdmins and Admins read it. Three families of keys are written:

| Keys | Written when |
|---|---|
| `General.BaseUrl`, `LLM.*` (for example `LLM.Provider`, `LLM.ApiKey`, `LLM.Model`) | Admin settings are saved |
| `Mcp.{Setting}`, `Mcp.Project.{projectId}.{Setting}` | an MCP setting changes, globally or for a project |
| `DataSource.{id}.AllowMigrationWrites` | an Admin marks or unmarks a data source as a [migration write target](/features/data-migration/) |

Security-relevant actions outside settings (role changes, membership changes, sign-ins refused) are structured log events, not history rows; see [MCP observability and audit](/features/mcp-observability-and-audit/) and [Security model](/features/security-model/).

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

On the global settings a locked switch is shown **disabled** with a lock and a note naming the configuration key; in a project's scope locked fields are not offered and a banner names them. A write that contradicts a lock answers `409`, an Admin's included; `GET /beacon/api/mcp/settings/locks` lists the pinned settings. `Beacon:Mcp:Ceilings` clamp numeric settings downward: `MaxRowLimit` caps the row limit the MCP tools apply, and `MaxSqlChars` and `MaxQuestionChars` are always-on length limits; the other ceilings clamp the recorded setting shown on this page. Every MCP settings change is recorded in the history above. See [Configuration → Deployment locks](/getting-started/configuration/#deployment-locks) and [MCP server](/features/mcp-server/).

## See also

- [AI integration](/features/ai-integration/) — the features that use the LLM connection
- [MCP server](/features/mcp-server/) — MCP settings and guardrails
- [Authorization](/features/authorization/#functional-permissions) — Auditor and SecurityAdmin
- [Configuration](/getting-started/configuration/) — `Beacon:LLM` and the startup values
