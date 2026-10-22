---
title: AI providers
description: "Turn on Beacon's AI layer: UseAI and AddBeaconAI, the four LLM providers and their settings, the request queue, runtime switching, local embeddings for semantic retrieval, and what data reaches the provider."
---

Beacon's AI layer powers natural-language questions (`ask`), project documentation, grounding and the learning loop, and AI actors. It is optional, except that the MCP package depends on the AI package, so an MCP host always registers it.

## Turning it on

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options =>
{
    options.UseAI = true;
    // …
});

builder.Services.AddBeaconAI(builder.Configuration);   // binds Beacon:LLM, registers providers and the queue
```

Set both. `UseAI = false` registers stand-ins for the AI-only services; `UseAI = true` without `AddBeaconAI` leaves those services unregistered.

## Choosing a provider

Configure the provider in host configuration (`Beacon:LLM`). At startup Beacon builds the provider from that section. An Admin can also change the provider in **Admin settings** (`/beacon/admin-settings`): saving swaps the active provider immediately, without a restart. If the new configuration cannot be built, no provider is active until it is fixed; the previous one is never kept.

:::note
The provider is built from `Beacon:LLM` each time the process starts. Keep that section in step with what you choose in Admin settings, or save the Admin settings again after a restart.
:::

| `Provider` | Required | Notes |
|---|---|---|
| `OpenAI` | `ApiKey`, `Model` | Calls the public OpenAI API. |
| `Claude` (shown as **Anthropic** in the UI) | `ApiKey`, `Model` | Calls `https://api.anthropic.com`. |
| `AzureOpenAI` | `Endpoint`, `ApiKey`, `Model` (the **deployment name**) | |
| `Bedrock` | `Region`, `Model` (model id) | Needs the `Moberg.Beacon.AI.Bedrock` package and `AddBeaconBedrock()`; without them a stored Bedrock setting leaves no active provider. `BedrockAuthMode`: `IamRole` (default) → the default AWS credential chain (instance role, environment); `AccessKey` → `AwsAccessKeyId` and `AwsSecretAccessKey`; `TemporaryCredentials` → those plus `SessionToken`. A legacy `ApiKey` of `accessKey:secretKey` is used when both key fields are empty; under `IamRole` a warning is logged. |

```json
{
  "Beacon": {
    "LLM": {
      "Provider": "Claude",
      "ApiKey": "<from a secret store>",
      "Model": "<model id>",
      "FastModel": "<smaller model id>",
      "Limits": { "MaxConcurrentRequests": 20 }
    }
  }
}
```

```json
{
  "Beacon": {
    "LLM": {
      "Provider": "Bedrock",
      "Region": "eu-central-1",
      "Model": "<bedrock model id>"
    }
  }
}
```

`Model` and `FastModel` are free text. Use whatever model ids your provider offers. `Model` does the work (SQL generation, documentation, actors); `FastModel` names a smaller model for the short classification calls on the `ask` path (intent classification and data-source routing); every other call uses `Model`. Leave it empty, or equal to `Model`, to use `Model` for all calls; if the fast model cannot be set up or fails when called, those calls also fall back to `Model`.

### The request queue

Every LLM call goes through one queue that allows at most `Limits:MaxConcurrentRequests` calls at a time (default 50) and makes up to three attempts for a retryable failure, with exponential backoff. The concurrency limit is read at startup. Token, request and budget figures (`TokensPerMinute`, `RequestsPerMinute`, `MonthlyBudget`) are recorded and shown in Admin settings; use your provider's own quotas and spend limits for hard caps.

## Local embeddings (optional)

Semantic retrieval (finding the right tables, examples and documentation chunks for a question) can use a local ONNX embedding model. Nothing leaves your process. The model support is a separate package: `dotnet add package Moberg.Beacon.AI.Embeddings.Onnx`, then call `services.AddBeaconOnnxEmbeddings()` before or after `AddBeaconAI`. Without it, `AddBeaconAI` registers a disabled service; if `Enabled` is `true` it logs one warning the first time the embedding service is used (not at startup).

```json
{
  "Beacon": {
    "Embeddings": {
      "Enabled": true,
      "ModelPath": "/models/bge-small-en-v1.5/model.onnx",
      "TokenizerPath": "/models/bge-small-en-v1.5/vocab.txt"
    }
  }
}
```

- Model: **bge-small-en-v1.5** (384 dimensions, 512 tokens). Beacon does not ship the model files; download them and mount them read-only.
- Embeddings are used only when `Enabled` is `true` **and** both files exist. Otherwise Beacon falls back to keyword retrieval.
- On PostgreSQL vectors live in a `pgvector` column with an HNSW index; on SQL Server they are compared in memory.
- Schedule the re-index jobs (`mcp-embedding-reindex`, `mcp-docchunk-reindex`); see [Background jobs](/integrate/background-jobs/#recurring-maintenance-jobs).

## What reaches the LLM provider

By default Beacon sends **schema and documentation, not row values**:

| Data | Sent? |
|---|---|
| Table and column names, types, relationships, documentation, glossary, verified examples | Yes (this is the grounding) |
| The user's question | Yes |
| Sample values from tables | Only with the per-project setting **sample-value collection** (off by default); never from PII or secret-like columns |
| Values probed to ground a question (value grounding) | Only with **value grounding** on (off by default) |
| Query results for an LLM judge in evaluations | Only with **eval judge** on (off by default) |
| Database error text | Redacted: value-carrying errors are reduced to their class |

Set the deployment lock `Beacon:Mcp:ForbidRowDataToLlm: true` to pin all three switches off for every project. Every LLM request that carries row-derived data is logged as event **7123 `RowDataSentToLlm`** with caller, project, data source ids and categories, never the values.

## Features that use AI

| Feature | Needs |
|---|---|
| MCP `ask`, `get_query_context` | AI on; a provider for `ask` |
| Project documentation | AI on, a provider, `EnqueueProjectDocumentation` in your scheduler |
| Learning loop, evaluations | AI on; scheduled maintenance jobs; `EnqueueMcpEval` for eval runs |
| AI actors | AI on, a provider, `EnqueueAiActorThinkCycle` in your scheduler |

The MCP `query`, `dry_run`, `search`, `get_context` and `get_documentation` tools work without an LLM provider.

More: [AI integration](/features/ai-integration/) · [Knowledge base & grounding](/mcp/knowledge-base/) · [AI actors](/features/ai-actors/).
