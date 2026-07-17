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
- **Role-Restricted** - Only Admin users can access settings

## Accessing Admin Settings

1. Log in as an Admin user
2. Navigate to **Admin Settings** in the navigation menu (`/admin-settings`)
3. Configure settings across the available tabs

:::note
Admin Settings is only visible to users with the Admin role or Super Admin flag.
:::

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

1. Admin updates LLM settings via the UI
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

MCP server behavior has its own admin page at `/mcp-settings`: custom tool descriptions, the SQL-generation system prompt, a global instruction injected into every `ask` request, max row limits, read-only enforcement, and PII detection with custom patterns. See the [MCP Server Guide](/features/mcp-server/#configuration) for details. Like LLM settings, MCP settings apply immediately — no restart required.

## See Also

- [AI Integration](/features/ai-integration/) - AI features that use LLM configuration
- [MCP Server](/features/mcp-server/) - MCP settings, guardrails, and the learning loop
- [User Management](/features/user-management/) - Role-based access to Admin Settings
- [Configuration Guide](/getting-started/configuration/) - Startup configuration reference
