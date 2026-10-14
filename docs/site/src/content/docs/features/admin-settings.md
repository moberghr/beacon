---
title: Admin Settings
description: Runtime application configuration via the Admin Settings UI — hot-swap LLM providers, manage the base URL, and review a change-history audit trail.
---

Manage runtime application configuration through the Admin Settings UI without restarting the application.

## Overview

Admin Settings provides:

- **Runtime Configuration** - Change settings without redeploying or restarting
- **LLM Provider Management** - Configure and hot-swap AI providers at runtime
- **Change History** - Full audit trail of all setting changes with user attribution
- **Encrypted Storage** - Sensitive values (API keys, endpoints) encrypted in the database
- **Role-Restricted** - Admins, and the [functional roles](/features/authorization/#functional-permissions) Auditor (read) and SecurityAdmin (read and change), reach the settings; the LLM connection stays an Admin's

## Accessing Admin Settings

1. Log in as an Admin, an Auditor or a SecurityAdmin
2. Navigate to **Admin Settings** in the navigation menu (`/admin-settings`)
3. Configure settings across the available tabs

### Who may do what

| Action | Route | Admin | SecurityAdmin | Auditor |
|---|---|---|---|---|
| Read the settings (secrets shown only as set or not set) and their history | `GET /beacon/api/admin-settings` | ✓ | ✓ | ✓ |
| Change the models and the rate and budget limits | `PUT /beacon/api/admin-settings` | ✓ | ✓ | – |
| Change the **Base URL** | `PUT /beacon/api/admin-settings` | ✓ | – (`403`) | – |
| Change the LLM **provider**, **endpoint**, **region**, Bedrock **authentication mode** or any **credential** (API key, session token, AWS keys) | `PUT /beacon/api/admin-settings` | ✓ | – (`403`) | – |
| Test the stored LLM connection | `POST /beacon/api/admin-settings/test-llm` | ✓ | ✓ | – |
| Test with another provider, endpoint, region, authentication mode or credential | `POST /beacon/api/admin-settings/test-llm` | ✓ | – (`403`) | – |

The provider, endpoint, region and authentication mode decide where Beacon sends its LLM requests, with the stored credentials: were they a SecurityAdmin's to change or test, the credentials would follow an endpoint the SecurityAdmin chose. The Base URL is the address of every link Beacon sends out (notifications, invitations), so it is an Admin's too. A SecurityAdmin's save sends the stored values back unchanged (the UI sends credentials and the endpoint as `null`, "leave as is", and the stored Base URL, whose field is read-only for them); a request that changes any of them answers `403` and saves nothing. The refusal is logged with ids only.

## Configuration Tabs

### General Settings

| Setting | Description | Example |
|---------|-------------|---------|
| **Base URL** | Application URL for notification links | `https://yourdomain.com` |

The Base URL is used to generate clickable links in Teams/Slack notifications that take users to the Beacon UI (the React app is served at the root URL).

### AI Configuration

Configure the LLM provider for AI-powered features (documentation generation, natural language alerts).

| Setting | Description | Required |
|---------|-------------|----------|
| **Provider** | LLM provider (OpenAI, Anthropic, AzureOpenAI, Bedrock) | Yes |
| **API Key** | Authentication key for the provider | Yes |
| **Endpoint** | Custom API endpoint URL | No |
| **Region** | AWS region (Bedrock only) | Bedrock only |
| **Model** | Primary model name | Yes |
| **Fast Model** | Lightweight model for quick operations | No |
| **Max Concurrent Requests** | Parallel request limit | No (default: 50) |
| **Tokens Per Minute** | Rate limit | No (default: 80,000) |
| **Requests Per Minute** | Rate limit | No (default: 1,000) |
| **Monthly Budget** | Cost cap in USD | No (default: $100) |

:::caution
API keys are encrypted before storage and masked in the UI. They are never exposed in plain text after saving.
:::

### Change History

View a complete audit log of all settings changes:

- **Setting Key** - Which setting was changed
- **Old Value / New Value** - Previous and new values (masked for sensitive fields)
- **Changed By** - User who made the change
- **Changed At** - Timestamp

## Hot-Swap LLM Providers

Admin Settings can change the LLM provider at runtime without restarting the application.

### How It Works

1. An Admin updates LLM settings via the UI
2. `AppSettingsService` saves encrypted values to the database
3. `LlmProviderManager` receives the update via `ILlmConfigurationUpdater`
4. A new provider instance is created with the updated configuration
5. `DelegatingLlmProvider` (the proxy injected throughout the app) automatically delegates to the new provider
6. All subsequent AI requests use the new provider immediately

### Architecture

```
AppSettingsService.SaveSettingsAsync()
    │
    ├── Save to database (encrypted)
    ├── Invalidate cache
    ├── Update BeaconConfiguration singleton
    │
    └── ILlmConfigurationUpdater.UpdateConfiguration()
            │
            └── LlmProviderManager
                    ├── Mutate LlmConfiguration singleton
                    └── Recreate ILlmProvider via factory
                            │
                            └── DelegatingLlmProvider (proxy)
                                    └── All consumers use new provider
```

### Example: Switching from OpenAI to Anthropic

1. Go to **Admin Settings** > **AI Configuration**
2. Change **Provider** to `Anthropic`
3. Update **API Key** to your Anthropic key
4. Change **Model** to `claude-3-5-sonnet-20241022`
5. Click **Save**
6. AI features immediately use Anthropic — no restart needed

## Initial Configuration

Settings can be pre-configured in two ways.

### Via appsettings.json (Startup Defaults)

```json
{
  "Beacon": {
    "BaseUrl": "https://yourdomain.com",
    "LLM": {
      "Provider": "OpenAI",
      "ApiKey": "sk-your-api-key",
      "Model": "gpt-4o"
    }
  }
}
```

On startup, if Admin Settings in the database are empty, values from `appsettings.json` are used as defaults. Once saved via the Admin Settings UI, database values take precedence.

### Via First-Run Setup

After creating the super admin during first-run setup, navigate to Admin Settings to configure the LLM provider and other settings.

## Database Storage

Settings are stored in two tables.

### app_settings

| Column | Type | Description |
|--------|------|-------------|
| `key` | string | Setting identifier (e.g., `LLM.ApiKey`) |
| `value` | string? | Setting value (encrypted if sensitive) |
| `category` | string | Grouping (General, LLM) |
| `is_sensitive` | bool | If true, value is encrypted |

### app_setting_history

| Column | Type | Description |
|--------|------|-------------|
| `setting_key` | string | Which setting changed |
| `old_value` | string? | Previous value (`***` if sensitive) |
| `new_value` | string? | New value (`***` if sensitive) |
| `changed_at` | DateTime | When the change occurred |
| `changed_by_user_id` | string? | Who made the change |

## Programmatic Access

### Reading Settings

```csharp
public class MyService
{
    private readonly IAppSettingsService _settingsService;

    public MyService(IAppSettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public async Task DoSomethingAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();

        var baseUrl = settings.BaseUrl;
        var llmProvider = settings.LlmProvider;
        var llmModel = settings.LlmModel;
    }
}
```

### Saving Settings

```csharp
var settings = await _settingsService.GetSettingsAsync();
settings.BaseUrl = "https://new-domain.com";

await _settingsService.SaveSettingsAsync(settings, userId: currentUser.Id);
```

### Viewing History

```csharp
var history = await _settingsService.GetHistoryAsync();

// Filter by setting key
var llmHistory = await _settingsService.GetHistoryAsync(key: "LLM.ApiKey");
```

## Implementing ILlmConfigurationUpdater

If you build custom services that need to react to LLM configuration changes, implement `ILlmConfigurationUpdater`:

```csharp
public interface ILlmConfigurationUpdater
{
    void UpdateConfiguration(AppSettingsData settings);
}
```

This interface lives in `Beacon.Core` so that `AppSettingsService` can call it without depending on `Beacon.AI`. The implementation (`LlmProviderManager`) lives in the AI project.

## Caching

Settings are cached in memory for 1 hour to minimize database queries. The cache is invalidated immediately when settings are saved via the Admin Settings UI.

## MCP Settings

MCP server behavior has its own page at `/mcp-settings` (an Auditor or a SecurityAdmin reads it, a SecurityAdmin changes it, and Admins do both; turning read-only enforcement or PII detection off, globally or for a project, is an Admin's alone, `403` otherwise), organised into four tabs — **Pre-prompt** (SQL-generation system prompt, global instruction), **Tool descriptions** (overrides applied to the live `tools/list` response), **Guardrails** (max row limit, read-only enforcement, PII detection with custom patterns, the execution limits, the content-retention switches, and the learning knobs), and **Context preview** (see the grounding context for a project exactly as the tools assemble it).

A scope selector switches between the global defaults and a single project. Per project, each guardrail can either inherit the global value or carry its own override. Prompts and tool descriptions stay global — `tools/list` is answered before a project is known.

A deployment can also pin settings the UI cannot change, through the `Beacon:Mcp` configuration section: `ForceReadOnly` and `ForceNoContentRetention` lock a value outright (locked fields are hidden and a write returns HTTP 409), and `Ceilings` clamp numeric settings downward. See [Deployment locks and ceilings](/features/mcp-server/#deployment-locks-and-ceilings).

See the [MCP Server Guide](/features/mcp-server/#configuration) for details. Additional retrieval, exemplar, and eval settings that aren't on this page are settable through `PUT /beacon/api/mcp/settings` — see the [Knowledge Base guide](/features/knowledge-base/#settings-reference). Like LLM settings, MCP settings apply immediately — no restart required.

## See Also

- [AI Integration](/features/ai-integration/) - AI features that use LLM configuration
- [MCP Server](/features/mcp-server/) - MCP settings, guardrails, and the learning loop
- [Knowledge Base & Grounding](/features/knowledge-base/) - Retrieval, exemplar, and eval settings
- [User Management](/features/user-management/) - Role-based access to Admin Settings
- [Authorization](/features/authorization/#functional-permissions) - The functional permissions (Auditor, SecurityAdmin)
- [Configuration Guide](/getting-started/configuration/) - Startup configuration reference
