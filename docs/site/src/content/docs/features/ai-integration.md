---
title: AI Integration
description: How Beacon connects to an LLM provider (OpenAI, Anthropic, Azure OpenAI, AWS Bedrock), what it uses the model for, and what it sends.
---

:::caution[Experimental feature]
Features that use a large language model can produce incorrect, incomplete or misleading results, including invalid
SQL. Review generated documentation and SQL before you rely on it.
:::

Beacon uses one configured LLM provider for every AI feature. This page covers what Beacon uses the provider for and what reaches
it; full provider configuration for a host is in [Integrate → AI](/integrate/ai/).

## Turning AI on

The AI layer lives in the `Beacon.AI` package. A host turns it on in two places:

```csharp
var beacon = builder.Services.AddBeaconServices(builder.Configuration, options =>
{
    options.UseAI = true;                       // default false
});

builder.Services.AddBeaconAI(builder.Configuration);
```

With `UseAI` off (the default), Beacon registers stand-ins for the AI-backed services it needs elsewhere, so the host
starts and the non-AI features work; **Test connection** and the eval harness report that AI is unavailable.

## Providers

| Provider | `Provider` value | Admin Settings card | What it needs |
|---|---|---|---|
| OpenAI | `OpenAI` | OpenAI | API key, model |
| Anthropic | `Claude` | Anthropic | API key, model |
| Azure OpenAI | `AzureOpenAI` | Azure OpenAI | Endpoint (your resource), API key, deployment name as the model |
| AWS Bedrock | `Bedrock` | AWS Bedrock | Region, model id, AWS credentials; needs `Moberg.Beacon.AI.Bedrock` + `AddBeaconBedrock()` |

The model is the string you enter: Beacon does not restrict or recommend model ids. OpenAI and Anthropic are called on
their public API endpoints; there is no base-URL or proxy setting for them. Azure OpenAI uses your resource's endpoint,
and Bedrock your AWS region, so you choose where requests go.

For Bedrock, the authentication mode decides the credentials: the IAM role mode (the default) uses the host's default AWS
credentials (an IAM role, environment variables), the access key mode an access key id and secret access key, and the
temporary credentials mode those keys with a session token. An `ApiKey` that holds a pair as `accessKey:secretKey`, from
an older install, is still used when the key fields are empty; in the IAM role mode a warning is logged when it is (see
[Admin Settings](/security/admin-settings/)).

## Configuration

### Admin Settings

An Admin configures the provider at runtime in **Admin Settings** (`/beacon/admin-settings`), section **AI provider**:
pick a provider card, fill in the fields it shows, use **Test connection**, then **Save changes**. Only an Admin changes
the connection (provider, endpoint, region, credentials); a SecurityAdmin can change the models and the other settings
and test the stored connection. Secrets are stored encrypted. The REST routes are `GET /beacon/api/admin-settings`
(Auditor or SecurityAdmin), `PUT /beacon/api/admin-settings` and `POST /beacon/api/admin-settings/test-llm`
(SecurityAdmin). See [Admin Settings](/security/admin-settings/).

A save swaps the provider in place: every caller injects `ILlmProvider`, registered as `DelegatingLlmProvider`, which
forwards to the provider `LlmProviderManager` currently holds. When the new configuration cannot build a provider, the
manager keeps the previous one and logs a warning.

:::caution[After a restart]
At startup the provider is built from `Beacon:LLM`; saving Admin Settings swaps it immediately. Keep `Beacon:LLM` in
step with Admin Settings, or save them again after a restart.
:::

### Host configuration and limits

The `Beacon:LLM` section (`Provider` = `OpenAI`, `Claude`, `AzureOpenAI` or `Bedrock`; `ApiKey`, `Endpoint`, `Region`,
`SessionToken`, `Model`, `FastModel`, `Limits`) is described key by key in
[Integrate → AI](/integrate/ai/). Never put a provider key in source or a committed `appsettings.json`.

Every LLM call goes through `LlmRequestQueue`. It allows at most `Limits:MaxConcurrentRequests` calls at once (default
50, read once when the host starts) and retries a call that fails with a rate-limit, timeout, `503`/`504` or transient
network error, up to three attempts with exponential backoff; a call that still fails surfaces as
`AiServiceException`. The other values under **Rate limits & budget** on the Admin Settings page (tokens per minute,
requests per minute, monthly budget) are recorded and shown in settings. The **Fast model** serves the short classification calls on the `ask` path (intent classification and data-source routing); when it is empty, equals the main model, cannot be built or fails when called, they use the main model.

## What Beacon uses the model for

| Feature | Where it is described |
|---|---|
| The MCP `ask` tool and knowledge answers: routing, SQL generation and repair | [MCP Server](/mcp/server/#ask) |
| The learning loop's lesson extraction, the optional eval judge and contextual-retrieval blurbs | [Knowledge Base & Grounding](/mcp/knowledge-base/) |
| Project documentation | [below](#project-documentation) |
| AI actors | [AI Actors](/features/ai-actors/) |

Local embeddings for semantic search do not use the LLM provider: they run in-process from an ONNX model (see
[Local embeddings](/mcp/knowledge-base/#local-embeddings)).

## Project documentation

Beacon generates documentation per **project**, from the metadata of the data sources the project links and the code
references found in its repositories. Generation runs as a background job. The result has these sections:
Project Overview, Business Domains, Data Model, Data Flows, Code Lineage, Data Quality, API Documentation (for API
data sources) and Glossary.

| Action | Route | Needs |
|---|---|---|
| Read the latest documentation and its history | `GET /beacon/api/projects/{id}/documentation` | Read in the project |
| Generate or regenerate | `POST /beacon/api/projects/{id}/generate-documentation` | Curate in the project |
| Edit a section | `PUT /beacon/api/projects/documentation-sections/{id}` | Curate in the project |
| Ask the LLM to rewrite a section (returns the new text, saves nothing) | `POST /beacon/api/projects/documentation-sections/{id}/instruct` | Query in the project |
| Export as Markdown, or HTML with `asHtml=true` | `GET /beacon/api/projects/documentation/{id}/export` | Read in the project |

Curate is held by project admin members and Admins (see [Who curates grounding](/mcp/knowledge-base/#who-curates-grounding)).
In the UI, the **Documentation** tab of a project (`/beacon/projects/{id}`) shows the latest documentation and lets a
curator edit its sections.

The documentation is also grounding: it is chunked, optionally embedded, and retrieved when an agent asks a question
over MCP, so a better table description improves generated SQL (see
[Documentation chunking](/mcp/knowledge-base/#documentation-chunking)).

**Column values.** Generation lists distinct values of enum-like columns only while sample-value collection is on for
the project and every project sharing the data source (off by default), never from PII or secret-like columns, and
never under the deployment lock `Beacon:Mcp:ForbidRowDataToLlm`. Each prompt that carries such values is logged as
event `7123`.

## What reaches the provider

A prompt carries the question, schema (table and column names, types, descriptions, join paths) and your curated
knowledge (documentation, glossary, verified examples, learned patterns). Row data (sample values, value matches,
discovered column values, eval-judge rows, database errors) is off by default or screened, and every prompt that
carries it is logged as event `7123` (`RowDataSentToLlm`, category `Beacon.AI.Services.LlmProviders.LlmRowDataEgress`)
with identifiers only. The full table, and the lock `Beacon:Mcp:ForbidRowDataToLlm` that keeps all row data from the
provider, are in [What reaches the LLM provider](/mcp/server/#what-reaches-the-llm-provider).

Everything Beacon sends about a data source is read through the caller's authorized project: schema context,
documentation, code references, glossary, golden examples, learned patterns and value matches. The AI services take
that project as a parameter (`projectId` on `IKnowledgeGraphService` and `IValueGroundingService`), read a data source
only through a project that links it, and never mix in another project's material from a shared data source. See
[Project isolation](/mcp/knowledge-base/#project-isolation).

## Usage and cost records

Beacon records token counts and estimated cost where a feature stores them: AI actor think cycles and plans (and the
actor's totals). Documentation generation records no cost. Use your provider's own usage reporting for billing.

## Troubleshooting

**"No active LLM provider" on Admin Settings.** No provider has been saved in Admin Settings yet. Pick a provider,
test it and save.

**AI calls fail after a restart although Admin Settings looks right.** The provider was built from `Beacon:LLM` at
startup, which logs a warning when it cannot build one (for example Azure OpenAI without `Endpoint`, Bedrock without
`Region`). Save Admin Settings again, and bring `Beacon:LLM` in step.

**Test connection fails.** Check the key, the model or deployment name, and for Azure OpenAI the endpoint. For Bedrock,
check the region, the model id and that the host's AWS credentials may invoke the model.

**Test connection reports that AI is unavailable.** The host did not set `UseAI = true` or did not call `AddBeaconAI`.

**Calls fail after retries.** Rate-limit and transient errors are retried three times; persistent failures usually mean
the provider's own quota. Lower `Beacon:LLM:Limits:MaxConcurrentRequests` and restart, or raise the quota with the
provider.

**Debug logging.** Raise the log level for the AI namespaces:

```json
{
  "Logging": {
    "LogLevel": {
      "Beacon.AI.Services.LlmProviders": "Debug",
      "Beacon.AI.Services.Ai": "Debug",
      "Beacon.AI.Services.Documentation": "Debug"
    }
  }
}
```

## Related features

- [Integrate → AI](/integrate/ai/): host configuration of the provider
- [Admin Settings](/security/admin-settings/): the runtime provider configuration
- [Knowledge Base & Grounding](/mcp/knowledge-base/): how documentation, join paths and glossary terms ground SQL generation
- [MCP Server](/mcp/server/): the tools that use the provider
- [AI Actors](/features/ai-actors/): LLM-driven monitoring agents
- [Anomaly Detection](/features/anomaly-detection/): statistical detection, without an LLM
