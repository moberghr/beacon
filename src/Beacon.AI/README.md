# Beacon.AI

AI-powered extensions for Beacon providing LLM-based documentation generation, alert creation, and autonomous AI actors.

## Overview

Beacon.AI is an **optional** add-on package for Beacon that adds artificial intelligence capabilities:

- **Automatic Documentation Generation**: Generate comprehensive database documentation using LLMs
- **Natural Language Alerts**: Convert plain English descriptions to SQL queries
- **AI Actors**: Autonomous agents that monitor and manage queries/subscriptions
- **Multi-Agent Workflows**: Parallel documentation generation using specialized domain agents

## Installation

```bash
dotnet add package Beacon.AI
```

## Supported LLM Providers

- **OpenAI** (GPT-4, GPT-4 Turbo, GPT-4o)
- **Anthropic Claude** (Claude 3.5 Sonnet, Claude 3 Opus)
- **Azure OpenAI** (GPT-4 deployments)
- **AWS Bedrock** (Claude and other Bedrock-hosted models)

The active provider is **runtime-swappable** via admin settings — `LlmProviderManager` holds the current
provider and `DelegatingLlmProvider` proxies calls, so no provider-specific assumptions are baked into handlers.
All LLM calls are funneled through a request queue that enforces a configurable concurrency limit.

## Configuration

### Basic Setup

```csharp
builder.Services.AddBeacon(builder.Configuration, options =>
{
    options.UsePostgreSql(connectionString, "beacon");
    options.UseAI = true; // Enable AI features
});
```

### LLM Configuration (appsettings.json)

```json
{
  "Beacon": {
    "LLM": {
      "Provider": "OpenAI",
      "ApiKey": "your-api-key-here",
      "Model": "gpt-4o",
      "BaseUrl": "https://api.openai.com/v1"
    }
  }
}
```

### Provider Examples

**OpenAI:**
```json
{
  "Provider": "OpenAI",
  "Model": "gpt-4o",
  "ApiKey": "sk-..."
}
```

**Anthropic Claude:**
```json
{
  "Provider": "Anthropic",
  "Model": "claude-3-5-sonnet-20241022",
  "ApiKey": "sk-ant-..."
}
```

**Azure OpenAI:**
```json
{
  "Provider": "AzureOpenAI",
  "Model": "gpt-4",
  "ApiKey": "your-azure-key",
  "BaseUrl": "https://your-resource.openai.azure.com"
}
```

## Features

### 1. Documentation Generation

Generate comprehensive database documentation automatically:

```csharp
// Via MediatR
var documentation = await mediator.Send(new GenerateDocumentationCommand
{
    DataSourceId = dataSourceId,
    UserId = userId,
    IncludeSampleData = false
});

// Export to PDF, HTML, or Markdown
await mediator.Send(new ExportDocumentationToPdfCommand
{
    DocumentationId = documentation.Id,
    OutputPath = "docs/database.pdf"
});
```

### 2. Multi-Agent Documentation

Use multiple specialized AI agents working in parallel:

```csharp
var documentation = await mediator.Send(new GenerateMultiAgentDocumentationCommand
{
    DataSourceId = dataSourceId,
    UserId = userId,
    MaxConcurrentAgents = 5,
    Progress = progressReporter
});
```

### 3. Natural Language Alerts

Create alerts from plain English:

```csharp
var alert = await mediator.Send(new GenerateAlertCommand
{
    DataSourceId = dataSourceId,
    Description = "Alert me when daily failed logins exceed 100",
    UserId = userId
});
```

### 4. AI Actors

Autonomous agents that monitor and optimize your database:

```csharp
var actor = await mediator.Send(new CreateAiActorCommand
{
    Name = "Performance Monitor",
    Instructions = "Monitor slow queries and create subscriptions for anything over 5 seconds",
    DataSourceId = dataSourceId,
    RequiresApproval = true
});
```

## Dependencies

This package includes:

- **Anthropic** - Claude API client
- **Azure.AI.OpenAI** - Azure OpenAI client
- **OpenAI** - OpenAI API client
- **AWSSDK.BedrockRuntime** - AWS Bedrock client
- **QuestPDF** - PDF generation
- **Markdig** - Markdown processing
- **Microsoft.Extensions.AI** - Unified AI abstractions

## Important Notes

⚠️ **AI features are experimental** and may produce incorrect or incomplete results. Always review and validate AI-generated content before use in production.

⚠️ **API Costs**: LLM API calls incur costs. Monitor your usage and set appropriate rate limits.

⚠️ **Required Configuration**:
- An `EncryptionKey` must be configured in `appsettings.json` for secure storage of API keys
- LLM provider API key must be configured

## Documentation

For complete documentation, visit: https://github.com/MiBu/semantico

## License

GNU AGPL v3.0 or Commercial license - see LICENSE file for details

## Support

- GitHub Issues: https://github.com/MiBu/semantico/issues
- Documentation: https://github.com/MiBu/semantico/wiki

## Related Packages

- **Beacon.Core** - Core library (required)
- **Beacon.Core.PostgreSql** - PostgreSQL support
- **Beacon.Core.SqlServer** - SQL Server support
- **Beacon.Api** - REST minimal-API endpoints + OpenAPI for the React shell
- **Beacon.MCP** - MCP server (tools + resources) for AI assistants
- **Beacon.UI** - React SPA (Vite + TypeScript + Tailwind) shipped as a Razor Class Library, served at root `/`
