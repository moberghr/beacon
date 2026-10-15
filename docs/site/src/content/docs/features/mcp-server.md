---
title: MCP Server
description: A Model Context Protocol server that lets AI assistants query your data sources, search your catalog, and read documentation over authenticated HTTP.
---

Beacon exposes a **Model Context Protocol (MCP)** server that lets AI assistants (Claude, Cursor, Windsurf, custom agents) query your data sources, search your catalog, and access documentation — all through a standardized protocol.

## Overview

The MCP server is **project-centric**: each API key is scoped to one or more projects, and all tools automatically resolve which data sources, schemas, and documentation to use based on the active project.

Project resolution is **stateless** — it is computed per call, with no session memory. If the key is restricted to a single project, that project is used automatically; if it can reach several, pass `project_id` on every call (the error lists the candidates when you don't).

**Which projects a caller reaches** is decided on every call by the same [project access](/features/authorization/#project-access) rules as the REST API and the playground — the projects the caller may read:

- an **API key** reaches the projects of its list its owner may still use; a key without a list (created before lists were required) reaches every project its owner may use;
- a mapped **Entra system caller** reaches its configured projects, restricted or not;
- a mapped **Entra user caller** reaches its configured projects within its Beacon user's access — a restricted project only while the user is a member; without a Beacon user, its configured open projects only;
- a **browser session** (cookie or SSO) reaches no project on `/beacon/mcp` — browsers use the [MCP Playground](#mcp-playground) — and neither does a call without an HTTP request behind it (a host-wired stdio transport).

**What a caller may do in a project** follows its project role there, as in the playground: the catalog tools (`get_context`, `search`, `get_documentation`, `get_query_context`) need Read, while the SQL-carrying tools (`ask`, `query`, `dry_run`) and `feedback` work ad hoc in the project and need Query — an Editor or above in it. A Viewer (a Viewer member of a restricted project, or a caller whose global role is Viewer) reads the catalog but runs no SQL there: such a call answers "Access denied: you may read project N but not run ad-hoc queries in it." and is audited, and an `ask` or `query` refused this way records its signal like any other failed call.

**No MCP caller curates grounding.** Curating — reviewing learned patterns, applying documentation patches, verifying relationships, editing or regenerating documentation — needs [Curate](/features/authorization/#project-roles), which Beacon grants only to a signed-in user's session (a project admin member of the project, or an Admin): never to an MCP caller, user or system, whatever its scope, and never to an API key. Golden examples are an Admin's alone. Over MCP, `feedback` records a verdict and curates nothing by itself (see [Who curates grounding](/features/knowledge-base/#who-curates-grounding)).

A membership revoked, a project restricted or a user disabled takes effect on the caller's next call, in the same session. Saved-query tools are visible only in the caller's projects where it may run saved SQL (`Run`), and host endpoint tools only when the caller may read the host's project. A tool call that names a project the caller does not reach answers "Access denied: you do not have access to project N."; one that names none when the caller reaches no project answers "No projects are available to you." Both are audited.

**What you can do through MCP:**
- Ask natural language questions and get SQL + results back
- Pull the exact grounding context Beacon uses for SQL generation — schemas with real sample values, verified join paths, human-verified examples — and write your own SQL (`get_query_context` → `dry_run` → `query`)
- Validate SQL through every safety gate without executing it
- Execute direct SQL queries against any data source in your project
- Search tables, columns, and documentation by keyword, fused with embedding-based semantic matching when a local embedder is enabled
- Retrieve AI-generated documentation for your project, data sources, or individual tables
- Record feedback on answers — verdicts are recorded for an Admin to review, and an Admin may promote a `correct` answer to a verified example that grounds future SQL generation

## Quick Start

### 1. Create an API Key

Go to **API Keys** in the React UI (`/api-keys`) and create a new key:
- Choose a **scope**: `Read` or `Execute` — the MCP endpoint requires `Execute`, because its tools can run SQL; `Read` keys are limited to the read-only REST API. Creating an `Execute` key needs the Editor role or above
- Pick the **projects** the key may use — at least one is required. The key reaches them only while you, its owner, still have access to them (see [Project restrictions](/features/api-keys/#project-restrictions))
- Copy the key — it's shown only once (the key is SHA256-hashed before storage and never persisted in plaintext)

The key format is: `sk-sem_...`

### 2. Configure Your MCP Client

Add Beacon to your MCP client configuration. The exact format depends on your client.

**Claude Desktop** (`claude_desktop_config.json`):
```json
{
  "mcpServers": {
    "beacon": {
      "url": "https://your-beacon-host/beacon/mcp",
      "headers": {
        "Authorization": "Bearer sk-sem_YOUR_API_KEY"
      }
    }
  }
}
```

**Cursor** (`.cursor/mcp.json`):
```json
{
  "mcpServers": {
    "beacon": {
      "url": "https://your-beacon-host/beacon/mcp",
      "headers": {
        "Authorization": "Bearer sk-sem_YOUR_API_KEY"
      }
    }
  }
}
```

**Windsurf** (`.windsurf/mcp.json`):
```json
{
  "mcpServers": {
    "beacon": {
      "serverUrl": "https://your-beacon-host/beacon/mcp",
      "headers": {
        "Authorization": "Bearer sk-sem_YOUR_API_KEY"
      }
    }
  }
}
```

### 3. Start Using It

Once connected, your AI assistant can use the tools described below. Try asking:

> "What tables are available in my project?"
>
> "How many orders were placed last week?"
>
> "Show me the schema for the customers table"

## Connection Details

| Property | Value |
|----------|-------|
| **Endpoint** | `/beacon/mcp` |
| **Transport** | Streamable HTTP + JSON-RPC 2.0 (via `ModelContextProtocol.AspNetCore`) |
| **Authentication** | Required — `Authorization: Bearer sk-sem_...` header with the `Execute` scope |

Map the server with `app.MapBeaconMcp()`, which mounts `/beacon/mcp` behind the Execute-scope policy (registered by `AddBeaconApiAuthorization()`): an API key or mapped Entra caller without the `Execute` scope gets `403`. Do not map it with `app.MapMcp("/beacon/mcp").RequireAuthorization()` — the default policy admits any authenticated caller. The MCP layer also refuses a caller that lacks the `Execute` scope — and a caller that is not authenticated, over HTTP or over a transport without an HTTP request (such as stdio) — so a host that maps the route with a weaker policy still fails closed, but it loses the `403` at the door: a tool call answers a tool error (no tool runs, and the refusal is audited like any tool call), and every other request except `initialize` and `ping` — `tools/list`, resources, prompts, completions — a JSON-RPC error. Clients exchange JSON-RPC messages with the single `/beacon/mcp` endpoint over the Streamable HTTP transport; the server streams responses back on the same connection.

**Protocol niceties the server publishes:**

- The `initialize` response carries **server instructions** describing the recommended tool workflow, and every tool is published with a human-readable **title** and **annotations** (`readOnlyHint`, `idempotentHint`, `destructiveHint`, `openWorldHint`) so clients can reason about safety before calling.
- `query` and `ask` responses include machine-readable **`structuredContent`** (columns, rows, row count, truncation flag — plus the generated SQL and `signal_id` for `ask`) alongside the markdown text, so agents don't have to parse tables back out of prose.
- **Truncated results say so explicitly** — row-capped query results, paged search results, and concise documentation exports all end with a note stating that more data exists and how to get it (raise `max_rows`, repeat with the next `offset`, or pass `response_format: "detailed"`).

## Connecting from Remote Clients

Remote MCP clients with a "connect by URL" flow — **claude.ai** (Settings → Connectors → Add custom connector), **ChatGPT** (developer mode connectors), **VS Code** (`MCP: Add Server` → HTTP) — need just one URL:

```
https://your-beacon-host/beacon/mcp
```

Authentication is a bearer token in the `Authorization` header — a Beacon API key with the `Execute` scope (`Read` keys cannot reach the MCP tools). Paste the key wherever the client asks for a token/header; clients that probe the URL first will find the discovery documents below and learn the auth requirements automatically.

Agent platforms can instead call with **Microsoft Entra ID tokens**, either on behalf of a user or as a system identity. Beacon configuration decides their projects and scope. See [Entra ID callers on MCP](/features/mcp-entra-callers/).

### Discovery endpoints

Beacon publishes anonymous, read-only discovery metadata so remote clients can bootstrap a connection without documentation:

| Endpoint | What it serves |
|----------|----------------|
| `/.well-known/oauth-protected-resource` | [RFC 9728](https://datatracker.ietf.org/doc/rfc9728/) protected-resource metadata: the resource identifier (the canonical `/beacon/mcp` URL), `scopes_supported`, bearer-header auth. By default `scopes_supported` is `Execute` and, when SSO is enabled (`Beacon:Authentication:Oidc`), `authorization_servers` lists the configured OIDC authority; API-key-only deployments omit it. A host whose callers sign in with Microsoft Entra ID publishes the Entra issuer and scopes instead through `Beacon:Mcp:ProtectedResource` — see [Connect Claude](/features/mcp-entra-callers/#connect-claude-claudeai--claude-desktop-custom-connector) |
| `/.well-known/oauth-protected-resource/beacon/mcp` | The same document at the RFC 9728 path-inserted variant clients derive from the `/beacon/mcp` resource path |
| `/.well-known/mcp/server-card.json` | A server card (per the draft SEP-2127 proposal): server name and version, transport (`streamable-http`), endpoint URL, auth summary, and the full tool list with titles |

Unauthenticated requests to `/beacon/mcp` answer `401` with a `WWW-Authenticate: Bearer resource_metadata="…"` header pointing at the metadata document, so OAuth-capable clients discover the auth requirements from the challenge itself (RFC 9728 §5.1). A bearer value that is not a valid token — malformed, wrong algorithm, unknown signing key, expired — gets the same `401`, with `error="invalid_token"` added to the challenge. When the JWKS endpoint cannot be reached and no keys are cached, a bearer JWT gets `503` with `Retry-After` instead: the token was never judged, and a `401` would make an OAuth client throw it away and sign in again for nothing.

Behind a reverse proxy, set `Beacon:PublicBaseUrl` (e.g. `https://beacon.example.com`) so the discovery documents advertise the public origin; when unset, the URLs are derived from the incoming request.

A registry manifest for the official [MCP registry](https://registry.modelcontextprotocol.io) ships in `deploy/registry/server.json` with a publishing runbook alongside it — publishing is a deliberate out-of-band step (DNS verification of the namespace domain).

:::note[Deferred]
**MCP Apps** (interactive `ui://` results, e.g. rendering query results as tables inside the client) and the **MCP tasks extension** (long-running `ask` calls returning a task handle) are deliberately not part of this release — Apps waits on the `ModelContextProtocol.Extensions.Apps` package being published, and task-handle semantics for `ask` change its wire contract and deserve their own release.
:::

## Tools

The MCP server exposes **8 tools** that AI clients can call. A host application can add its own endpoints as further tools (read-only by default, write tools behind an opt-in) — see [Host Endpoint Tools](/features/host-endpoint-tools/). Approved saved queries an admin exposes appear as `q_<name>` tools — see [Saved Query Tools](/features/saved-query-tools/).

![MCP Playground](/img/screenshots/mcp-playground-dark.png)

### `get_context`

Get an overview of the project: data sources, schemas, tables, quality scores, and documentation status. **This is the recommended starting point** for understanding what data is available.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | integer | No | Specify project if your API key has access to multiple projects |
| `format` | string | No | `overview` (default) or `agents_md` — a deterministic project brief for an agent workspace's `AGENTS.md` (see [Host Docs](/features/host-docs/#on-the-mcp-surface)) |

**Example response** (markdown):
```
# Project: E-Commerce Analytics

**Data Sources:** 2
**Documentation:** Generated
**Repositories:** 1

## Data Sources

### production-db (ID: 4)
- **Type:** PostgreSQL
- **Tables:** 45
- **Quality:** 87%
- **Code References:** 124
- **Schemas:** public (40 tables), audit (5 tables)

### analytics-api (ID: 7)
- **Type:** Api
- **Endpoints:** 12
- **Code References:** 9
- **Tags:** users (7 endpoints), orders (5 endpoints)
```

### `ask`

Ask a natural language question about your data. Beacon auto-detects the right data source(s), generates SQL, executes it, and returns results.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `question` | string | **Yes** | — | Natural language question (e.g., "How many orders were placed last week?") |
| `project_id` | integer | No | — | Specify project if needed |
| `execute` | boolean | No | `true` | Set to `false` to get the generated SQL without executing it |

**How it works:**
1. **Routing phase** — The LLM determines which data source(s) to query (skipped for single-source projects)
2. **SQL generation** — Generates SQL using your actual schema as context
3. **Execution** — Runs the query with safety guardrails (read-only, row limits, PII detection)

**Cross-source queries:** If your question spans multiple data sources, Beacon queries each source separately and joins results in an in-memory SQLite database.

### `query`

Execute a direct SQL query against a specific data source. Use this when you already know the exact query you want to run.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `datasource_name` | string | No* | — | Name of the data source |
| `datasource_id` | integer | No* | — | ID of the data source (alternative to name) |
| `sql` | string | No | — | SQL query (SELECT only) for database sources |
| `api_query` | string | No | — | JSON query definition for REST API sources |
| `max_rows` | integer | No | `100` | Maximum rows to return (max: 1000) |
| `project_id` | integer | No | — | Specify project if needed |

*Either `datasource_name` or `datasource_id` is required.

**For REST API data sources**, pass a JSON query definition:
```json
{
  "method": "GET",
  "path": "/api/users",
  "parameters": { "limit": 10 },
  "resultMapping": { ... }
}
```

### `get_query_context`

Get the grounding context Beacon assembles internally for the `ask` tool, scoped to your question — so **you** (or your agent) can write well-grounded SQL instead of delegating generation to Beacon's LLM. The context contains M-Schema table renderings *with real sample values*, join paths (verified foreign keys and inferred relationships kept apart), coverage notes when the table neighbourhood was capped, human-verified golden query examples, learned patterns from usage, and matching business-glossary terms.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `question` | string | **Yes** | — | The question you plan to answer with SQL — the context is retrieved and ranked against it |
| `datasource_name` | string | No* | — | Name of the data source to ground against |
| `datasource_id` | integer | No* | — | ID of the data source (alternative to name) |
| `project_id` | integer | No | — | Specify project if needed |
| `max_chars` | integer | No | `12000` | Maximum characters of context returned (min 1000, max 30000) |

*If the project has exactly one data source it is auto-selected; with several, pass `datasource_name` or `datasource_id` (the error lists the candidates as `id: name` pairs).

`get_query_context` needs Read in the project. Its value matches come from live probes of the data source only for a caller who may also query the project (Editor and above); a Viewer gets the matches the stored sample values already hold, and no probe runs for it.

The context shows sample values and value matches only while sample-value collection and value grounding are on for the project and every project sharing the data source (both off by default), and never under the deployment lock `Beacon:Mcp:ForbidRowDataToLlm`; they pass the same name and value screens as everywhere else (see [Sample values](/features/data-sources/#sample-values)). Unlike the REST metadata endpoint, which returns stored sample values to an Admin only, `get_query_context` returns them to any caller with Read in the project, since they are what grounds its SQL. A response that carries them is recorded in the row-data egress log (event `7123`, "returned to the MCP client"; see [What reaches the LLM provider](#what-reaches-the-llm-provider)).

The response opens with a header naming the data source and SQL dialect, followed by the grounding context. Sections marked **(authoritative)** are human-verified. When the context exceeds `max_chars` it is cut at the last complete section and ends with an explicit truncation note. `structuredContent` carries `data_source_id`, `data_source`, `dialect`, and `truncated`.

**Intended workflow:** `get_query_context` → write your own SQL → `dry_run` to validate → `query` to execute. Use this instead of `ask` when your agent wants full control over the SQL it runs.

### `dry_run`

Validate a SQL query through all of Beacon's safety gates **without executing it**. Use before `query` to catch problems for free.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `datasource_name` | string | No* | Name of the data source to validate against |
| `datasource_id` | integer | No* | ID of the data source (alternative to name) |
| `sql` | string | **Yes** | The SQL query to validate (SELECT only) |
| `project_id` | integer | No | Specify project if needed |

*Either `datasource_name` or `datasource_id` is required.

**The four gates**, in order:

1. **`guardrail`** — the regex read-only backstop plus PII detection (reports the columns that would be masked)
2. **`ast`** — dialect-aware AST parse rejecting DML/DDL, stacked queries, and comment-hidden writes (skipped only when read-only enforcement is disabled)
3. **`schema`** — schema-catalog column check that catches hallucinated tables/columns without a database round-trip
4. **`provider_dry_run`** — the database's own validation (`EXPLAIN` / `sp_describe_first_result_set`); runs only when every earlier gate passed. Engines with no dry-run strategy (SQLite and friends) report the gate as **skipped** rather than silently passing, and the verdict is invalid with an advisory issue — a validation that never ran is never counted as a pass

Gate issues are collected rather than first-failure-wins, so one call reports everything to fix. The response gives a per-gate verdict and, when valid, the exact SQL that would execute with the row limit applied. `structuredContent` carries the machine-readable verdict: `{ valid, issues: [{gate, error}], executable_sql, pii_columns }`. An INVALID verdict is still a successful tool call — `isError` is reserved for resolution failures.

### `get_documentation`

Retrieve AI-generated documentation at three levels of detail.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | integer | No | Specify project if needed |
| `datasource_name` | string | No | Get docs for a specific data source |
| `table_name` | string | No | Get detailed docs for a specific table or API endpoint |
| `schema_name` | string | No | Schema name or API tag (optional qualifier for table_name) |
| `response_format` | string | No | `concise` (summary sections) or `detailed` (everything). Project level defaults to `concise`; data-source, table and document level default to `detailed` |
| `document` | string | No | Path (or exact title) of a [host-imported document](/features/host-docs/); returns it in full |

**Three levels:**

1. **Project level** (no parameters) — Full generated project documentation. Defaults to `concise`: the export is cut at ~8,000 characters on a line boundary with an explicit truncation note; pass `response_format: "detailed"` for the full document
2. **Data source level** (`datasource_name` only) — Tables, schemas, code references, quality scores. Defaults to `detailed`; `concise` omits the LLM schema context section
3. **Table level** (`table_name`) — Columns with types, relationships, quality rules; `detailed` (the default) adds code references and lineage, `concise` omits them

Table documentation, code references and lineage come from the resolved project only: on a data source several projects share, another project's documentation and repositories never appear.

When the host ships documents with [`ExposeDocs`](/features/host-docs/), the project-level response ends with an *Imported documents* listing (title + path) and `document: "<path>"` returns one of them.

### `search`

Search tables, columns, and documentation across all data sources in the project. Keyword matching is fused with embedding-based semantic matching (reciprocal rank fusion) when a [local embedder](/features/knowledge-base/#local-embeddings) is configured; without one, search falls back to keyword-only rather than failing.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `query` | string | **Yes** | — | Search keyword (e.g., "customer", "order_date", "revenue") |
| `project_id` | integer | No | — | Specify project if needed |
| `max_results` | integer | No | `20` | Maximum results to return (max: 50) |
| `offset` | integer | No | `0` | Result offset for paging — use with `max_results` to page through large result sets |

Each result line includes the item type (`[TABLE]`, `[COLUMN]`, `[DOC]`, or `[IMPORTED_DOC]` with the document path for [host-imported documents](/features/host-docs/)), the data source and schema-qualified table (plus the column name for column hits), and the description, ordered by relevance. When more matches exist beyond the current page, the response ends with an explicit note giving the `offset` to request next.

### `feedback`

Record whether a previous `ask` answer was correct. The verdict is recorded on the answer's signal with the user who recorded it, and it does this:

- An `incorrect` verdict keeps the answer out of the common-query and join patterns Beacon learns.
- A `correct` verdict puts the answer on the [golden candidates](/features/knowledge-base/#how-cases-are-created) list (the **Golden candidates** tab of the MCP Learning page), where an Admin may promote it to a verified example (golden pair) that grounds future SQL generation for the same data source; feedback never promotes it by itself. A `correct` verdict counts in [pattern mining](/features/knowledge-base/#from-signal-to-injected-lesson) only when a curator recorded it from an interactive session (a project admin member, or an Admin, signed in); a verdict through this tool — an MCP caller or an API key — is attributed to the user it acts for but never counts as a curator's, whatever that user's roles, and neither does the asker's own.

The tool's description, its answer, the server instructions and the playground's feedback form all say so.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `signal_id` | integer | **Yes** | The `signal_id` from the `ask` response you are rating |
| `verdict` | string | **Yes** | `correct` or `incorrect` |
| `corrected_sql` | string | No | The corrected SQL, if you fixed it |
| `note` | string | No | A short note — **only when the user explicitly asked to record one**, never a summary of the conversation |

Every `ask` response that ran a data query ends with a `_signal_id: N_` marker — pass that value back here. Conceptual answers from the knowledge base don't record a query signal and carry no marker.

A signal is rated only in its own project: the signal must belong to a project, that project must be the one the call resolves to (in the playground, the selected project; on the transport, one of the caller's projects), and the caller needs Query there. Anything else answers "Query signal N not found." — the same as a signal that does not exist — and is audited. Within its project, a verdict is recorded only by the caller who asked, a project admin of that project, or an Admin; anyone else gets an error saying so, and the refusal is audited too. The same rule applies to `POST /beacon/api/eval/feedback`, which answers `403`.

The verdict is always recorded, with the user who recorded it. Whether `corrected_sql` and `note` are *stored* depends on the project's **Allow explicit feedback content** setting — see [Content retention](#content-retention) below. When that setting is off, the verdict still lands and the rest is dropped.

`corrected_sql` is checked before it is stored, in the dialect of the data source the rated answer ran on: its comments are removed, and it must pass the same read-only gate and catalog check a saved query step passes (a single read-only statement, columns that exist in Beacon's metadata — a data source Beacon holds no metadata for refuses it). Nothing runs. Comment characters inside a string literal are kept when the dialect reads them as part of it; a comment that cannot be removed safely, or characters a reviewer cannot see, refuse the SQL — see [the grounding SQL check](/features/knowledge-base/#who-curates-grounding). Corrected SQL that cannot be kept never refuses the call: the verdict is recorded and only the SQL is dropped — SQL the check refuses, SQL on a signal without a single data source (a cross-source or knowledge answer: there is no dialect to check it in), and SQL the project's retention setting does not keep. The log records the signal id only.

**The answer** starts with "Feedback recorded for signal N.", says what the verdict does, and — when `corrected_sql` was given — whether it was kept: "The corrected SQL was kept with the verdict, without its comments.", or "The corrected SQL was not kept:" with the reason (the project does not retain query content; the answer did not run on a single data source; it did not pass the SQL check, with the check's reason). `POST /beacon/api/eval/feedback` answers the same as `correctedSqlRecorded` (true when corrected SQL was given and kept) and `correctedSqlNotRecordedReason` (null when it was kept or none was given); the playground's feedback form shows the reason.

## Safety & Guardrails

The MCP server enforces several safety measures:

| Feature | Description | Default |
|---------|-------------|---------|
| **Read-only enforcement** | Only `SELECT` queries are allowed | Enabled |
| **Database names** | SQL may name only its data source's configured database (catalog, project); other qualifiers are refused | Always on |
| **Database boundary** | Functions and procedures that reach other servers, files or storage, SQL-text extension functions, PostgreSQL `COPY … PROGRAM` / server-file `COPY`, Databricks file paths and four-part names are refused | Always on |
| **Shared data sources** | A source several live projects link runs, and is grounded, with the strictest guardrails among them | Always on |
| **Row limits** | Maximum rows returned per query | 100 (single), 500 (cross-source), max 1000 |
| **PII detection** | Automatically detects and flags sensitive data patterns | Enabled |
| **Query timeout** | Queries are cancelled after 30 seconds | Always on |
| **Audit logging** | Every tool call is recorded by `McpAuditService` with user, timing, and parameters | Always on |
| **Usage signals** | `ask`, `query`, and `dry_run` calls are recorded by `McpSignalService` to feed the learning loop | Always on |
| **Content retention** | Whether questions, SQL, and error text are persisted at all — per project, lockable deployment-wide | Retained |

**Database names.** `ask`, `query`, `dry_run` and the saved-query tools refuse SQL that names another database than the data source's own — `database.schema.table` on SQL Server, Azure Synapse, PostgreSQL and Snowflake, `database.table` on MySQL, `catalog.schema.table` on Databricks, `project.dataset.table` on BigQuery — with the gate code `cross_database`, also when read-only enforcement is off. The allowed database is the one the data source's connection configures; a source without one — or whose connection settings cannot be read — accepts no database qualifier. Unqualified and schema-qualified names always pass. The same always-on stage refuses `dblink…`, `lo_…`, `xp_…`, `OPENROWSET`, `OPENQUERY`, `OPENDATASOURCE`, `EXTERNAL_QUERY` and the other functions that reach other servers, files or storage — on Databricks also `http_request`, `read_kafka` and the other stream readers, `reflect` and `java_method` — the PostgreSQL extension functions that run SQL text (`pg_background_…`, pgRouting's `pgr_…`), PostgreSQL `COPY … TO/FROM PROGRAM` and `COPY` to or from a server file, Databricks file paths and SQL Server four-part names, so turning read-only enforcement off never opens them. See [Database names in SQL](/features/authorization/#database-names-in-sql).

**Shared data sources.** The guardrails a tool applies are the data source's: when several live projects link it, read-only enforcement and PII detection are on if any of them turns them on, value grounding and sample values are off if any of them turns them off, all their custom PII patterns apply, and the lowest row limit, statement timeout, result size, plan-cost limit and value-grounding probe budget win — whichever project the caller works in. `ask` applies them over every data source it routes to, and grounding (`ask`, `get_query_context`) applies the value-grounding, sample-value and PII settings. A metadata refresh, which no project asks for, collects sample values while collection is on in the effective settings of every live project that links the source (each project's override, else the global value); the global value alone decides only for a source no live project links. See [Guardrails of shared data sources](/features/authorization/#guardrails-of-shared-data-sources).

| Setting | Strictest among the linking projects |
|---------|--------------------------------------|
| `enforceReadOnly`, `enablePiiDetection` | on when any project turns it on |
| `enableValueGrounding`, `enableSampleValueCollection` | off when any project turns it off |
| `customPiiPatterns` | every project's patterns |
| `maxRowLimit`, `statementTimeoutSeconds`, `maxResultBytes`, `maxExplainCost`, `valueGroundingMaxProbes` | the lowest (`maxExplainCost`: any limit over none) |

`McpAuditService` fires on every tool invocation, including the failure path — it is never short-circuited. Audit rows and usage signals record the caller by its Beacon user (`Users.Id`): an API key's owner, a mapped caller's provisioned user, or the signed-in user in the playground — never the session's own identifier — so a caller can rate the signals of its own `ask` calls. A tool call refused for a missing `Execute` scope (or an unauthenticated caller) is audited too, with the tool name, the caller's identifiers and a permission error; when `Beacon:Mcp:Audit:Required` is set and that row cannot be written, the refusal answers with the withheld-result message instead. `McpSignalService` records the three SQL-carrying tools (`ask`, `query`, `dry_run`); the catalog tools (`get_context`, `search`, `get_documentation`, `get_query_context`) are audit-only by design.

## Content retention

By default Beacon stores what an agent asked and what SQL ran — that is what makes the learning loop and the audit trail useful. For a project handling regulated or sensitive data you can turn that off without losing the audit trail itself.

Switch **Retain query content** off for a project and Beacon keeps the **structure** of every interaction and stops persisting its **content**:

| Kept | Dropped |
|---|---|
| Tool, project, data source, user, caller | The natural-language question |
| Tables and columns referenced | Generated, corrected, and user-supplied SQL |
| Row counts, timings, success flags, verdicts | Routing rationale and feedback notes |
| An **error class** (see below) | Free-text database error messages |
| The audit row itself — always | The audit row's raw parameters |

An audit row is still written for **every** tool call, success or failure. The lock changes what the row contains, never whether it exists.

**Error classes.** Instead of the provider's message, Beacon stores one of nine values: `schema`, `syntax`, `permission`, `timeout`, `not_found`, `validation`, `execution`, `cancelled`, `unknown`. You keep the ability to chart failure modes without keeping text that may quote customer data.

**Audit parameters** become a structural summary — which tool ran, how many bytes of input it received, and which tables it touched:

```json
{ "tool": "query", "params": [{ "name": "input", "bytes": 412 }], "tables": ["orders", "customers"] }
```

**What else changes under the lock**

- The `feedback` tool records the verdict but not the corrected SQL or note. Signals of a locked project carry no question, so they are not offered as golden candidates.
- Pattern mining keeps schema corrections and join patterns (structure) and skips common-query and documentation-gap mining (content). Join patterns are stored without example questions or SQL, and the LLM lesson extractor is not called, because its prompt would carry the question and SQL.
- The optional eval judge is forced off, and eval results are stored without generated SQL or judge commentary.

**Enforcement.** Every write path applies the rule, and an EF Core `SaveChangesInterceptor` backs them up: content is stripped on the way to the database even if a future code path forgets, and it fails closed — if the settings can't be read, the row is treated as locked. All of this is logged with identifiers only.

Retention is **not retro-active**: rows written before you turned the lock on keep their content until they age out of the retention window. A row that is *updated* after the lock is on is cleaned up by the interceptor.

## SQL Accuracy Stack

Natural-language questions through the `ask` tool don't go through a naive prompt-to-SQL pipe. Every generated query passes a layered accuracy stack:

1. **Grounded context** — before generation, Beacon assembles an M-Schema rendering (column name, type, nullability, description) — *with real sample values and value matches* when a project turns sample-value collection and value grounding on (both off by default; see [What reaches the LLM provider](#what-reaches-the-llm-provider)) — so filters match actual data formats (`'shipped'` vs `'SHIPPED'`), plus curated join paths, matching business-glossary terms, human-verified query examples, and lessons mined from past usage. See the [Knowledge Base guide](/features/knowledge-base/) for what goes in and how you curate it.
2. **AST read-only validation** — generated SQL is parsed into an abstract syntax tree in the data source's own dialect (PostgreSQL, SQL Server, MySQL, BigQuery, Snowflake, Databricks); SQL for a source whose dialect is unknown is refused. DML/DDL statements, stacked queries, comment-hidden writes, row-locking clauses, functions that read files, reach other servers or sleep, linked-server (four-part) names and file path references are rejected before anything reaches your database — defense-in-depth beyond the regex guardrail.
3. **Database read-only backstop** — on PostgreSQL and MySQL, MCP-executed SQL additionally runs inside a `READ ONLY` transaction (on PostgreSQL as a batch of exactly one command, which the driver and the server refuse to split into several statements; on MySQL also with a read-only session default and statement batches turned off), so a write that somehow slipped past both parsers is still refused by the database itself — defence in depth, not a replacement for a read-only login. BigQuery and Databricks have no read-only session: their connectors refuse, on the read-only path, anything their own dialect's parser does not read as a single `SELECT` in the configured project or catalog, and BigQuery additionally runs only what a dry run reports as a `SELECT`. SQL Server, Azure Synapse and Snowflake rely on the parser gates (SQL Server also runs the statement in a transaction that is always rolled back, and fails the run when the statement ends that transaction itself). On all five, the read-only login you connect with is the database-level boundary; on PostgreSQL and MySQL it is still required.

The same gate and read-only path run every query Beacon executes outside MCP too — previews, subscriptions, data-quality rules, AI actors, migration reads; see [How a query runs](/features/queries/#how-a-query-runs).
4. **Dry-run repair loop** — if execution fails, the SQL is retried with the database error and a refreshed schema context; the error goes into the prompt redacted by its class (see [Database errors](#database-errors)); truncated or degenerate repairs are rejected rather than executed.
5. **Row limits & PII masking** — results are capped and sensitive values (emails, phone numbers, SSNs, credit cards, tokens, and custom regex patterns) are masked (`a***z`) before leaving the server.

## Learning Loop

The MCP server is self-improving. `McpSignalService` records a usage signal for every SQL-carrying call — the question, the generated SQL, the tables and columns referenced, the routing decision, and the outcome. Recurring background jobs turn those signals into knowledge that grounds future generation:

![MCP Learning](/img/screenshots/mcp-learning-dark.png)

| Job | Schedule | What it does |
|---|---|---|
| `mcp-learning-aggregate` | Every 6 hours | Mines `ask` / `query` signals into learned patterns, retires stale ones, and runs replay verification |
| `mcp-learning-cleanup` | Daily 03:00 | Drops signals past the retention window |
| `mcp-embedding-reindex` | Every 12 hours | Re-embeds schema metadata and exemplars |
| `mcp-docchunk-reindex` | Every 12 hours | Re-chunks and re-embeds project documentation |

Two things are worth knowing about how patterns get promoted:

- A candidate pattern is not trusted on a confidence score alone. Before promotion, Beacon **replays the project's golden cases** with and without the candidate injected, and keeps it only if it flips at least one case from failing to passing.
- `dry_run` signals are recorded for analytics but **excluded from mining** — their "question" is SQL, not natural language, and gate rejections would skew the failure statistics.

Review the queue on the **MCP Learning** page (`/mcp-learning`), where pending patterns and proposed documentation patches can be approved or rejected. The full mechanics — lesson extraction, decay, retention, and every setting — are in the [Knowledge Base guide](/features/knowledge-base/#the-learning-loop).

## Configuration

Admins and SecurityAdmins customize the MCP server behavior at **MCP Settings** in the React UI (`/mcp-settings`); Auditors read it. The routes: `GET /beacon/api/mcp/settings` and `GET /beacon/api/mcp/projects/{projectId}/settings` for an Auditor or a SecurityAdmin, the `PUT`s for a SecurityAdmin (Admins hold both; see [Functional permissions](/features/authorization/#functional-permissions)).

**Global defaults or per project.** A scope selector at the top of the page switches between the global defaults and any single project. In project scope each field gets an **Override** checkbox: leave it unchecked and the project inherits the global value (shown greyed out); check it and the project keeps its own. Prompts and tool descriptions stay global by design — `tools/list` is answered before any project is known.

The tabs:

**Pre-prompt**
- **Ask tool system prompt** — the LLM prompt used for SQL generation in the `ask` tool
- **Global instruction** — prepended to the user context of every LLM-aware tool

**Tool descriptions**
- Override the description returned by `tools/list` for `get_context`, `ask`, `query`, `get_documentation`, and `search`. Leave a field blank to keep the built-in description. Overrides are applied to the live tool list, so connected clients see your wording. `dry_run`, `get_query_context`, and `feedback` currently use their built-in descriptions only.

**Guardrails**
- **Max row limit** — the ceiling on rows returned (default: 1000)
- **Read-only enforcement** — the SELECT-only restriction; locked on by default (see below)
- **PII detection** — enable/disable, plus custom PII regex patterns; detection is locked on by default (see below)
- **Execution** — statement timeout (default 30s), max result size (default 256 KB), max EXPLAIN cost (blank = no limit), max concurrent queries per API key (default 4), and the two retention switches: **Retain query content** and **Allow the feedback tool to store question and SQL text**
- **Learning** — master switch, auto-approve threshold, injection budget, and signal retention window

### Deployment locks and ceilings

Some settings must be non-negotiable no matter what an administrator clicks. The `Beacon:Mcp` configuration section pins values at the host level. Read-only enforcement and PII detection are pinned **by default**: `ForceReadOnly` and `ForcePiiDetection` are `true` unless the host's configuration sets them to `false`.

```json
{
  "Beacon": {
    "Mcp": {
      "ForceReadOnly": true,
      "ForcePiiDetection": true,
      "ForceNoContentRetention": true,
      "ForbidRowDataToLlm": true,
      "Ceilings": {
        "MaxRowLimit": 1000,
        "StatementTimeoutSeconds": 60,
        "MaxResultBytes": 1048576,
        "MaxConcurrentQueriesPerKey": 8,
        "MaxSqlChars": 100000,
        "MaxQuestionChars": 4000
      }
    }
  }
}
```

- A **lock** (`ForceReadOnly` and `ForcePiiDetection`, both on by default; `ForceNoContentRetention` and `ForbidRowDataToLlm`, off by default) pins the effective value for the global settings and every project. `ForbidRowDataToLlm` pins three settings off: sample-value collection, value grounding and the eval judge (see [What reaches the LLM provider](#what-reaches-the-llm-provider)). Nobody can change a locked setting at runtime — an Admin included; only the host's configuration lifts the lock. The global page shows a locked switch disabled with a note, the project scope hides locked fields and names them in a banner, `GET /beacon/api/mcp/settings/locks` lists them (`lockedFields`), and an API call that tries to set one gets **HTTP 409** with the RFC 7807 type `/errors/setting-locked`.
- With `ForceReadOnly` on, the SQL execution gate also judges every statement read-only whatever the settings a caller hands it — the `ask`, `query` and `dry_run` tools and the in-memory join of a cross-source query alike. Only a deployment that sets `ForceReadOnly: false` lets an Admin turn read-only enforcement off (globally or per project), and only there does the setting reach the gate. The host logs a warning at startup for each of these two locks that is off.
- A **ceiling** clamps a numeric value downward only — it can lower what an administrator configured, never raise it. Clamped fields are flagged in the UI.
- `Ceilings:MaxSqlChars` (default `100000`) and `Ceilings:MaxQuestionChars` (default `4000`) cap the length of SQL and of `ask` questions. They **always apply** at their defaults, even when the section is absent. With no section, read-only enforcement and PII detection are locked on, content retention and row data to the LLM are not locked, there are no deployment-set ceilings, and these two length caps hold.
- Settings resolve as **lock → project override → global value → built-in default**, then ceilings are applied.
- Saving the global page never bakes a locked or clamped value into the stored row, so lifting a lock or raising a ceiling restores what was configured underneath.
- **Every change is recorded.** Each setting a save changes — globally or for a project — is written to the settings history the [Admin Settings](/features/admin-settings/) page keeps: key `Mcp.{Setting}` or `Mcp.Project.{projectId}.{Setting}`, the value before and after (an empty override is "inherit"; a long prompt or description is cut to 2,000 characters), when, and who (the caller's Beacon user id). The log notes the change by setting name and ids only.

:::caution[Upgrading]
Read-only enforcement and PII detection can no longer be turned off at runtime unless the deployment unlocks them. A
deployment that had either switched off globally or for a project runs with it on after the upgrade; the stored value is
kept, and takes effect again only if the host sets `Beacon:Mcp:ForceReadOnly` (or `ForcePiiDetection`) to `false`. The
host logs one warning after startup listing the stored rows a lock overrides (the global row and project ids), and a
statement refused only because the lock turned read-only enforcement on names `Beacon:Mcp:ForceReadOnly` in its reason.
:::

### What reaches the LLM provider

Beacon sends prompts to the LLM provider configured in [Admin Settings](/features/admin-settings/). Besides the question,
the schema (table and column names, types, descriptions, join paths) and your curated knowledge (glossary, documentation,
verified examples, learned patterns), a prompt can carry data read from your data sources. Each kind is off by default
or screened, and each is logged:

| Row data (egress category) | When | Screened |
|---|---|---|
| Sample values (`sample-values`, `Examples: [...]`) in `ask` and repair prompts — and returned by `get_query_context` | sample-value collection on (off by default) in the effective settings of the project and of every project sharing the source | name and value rules of [Sample values](/features/data-sources/#sample-values), again at every read |
| Value matches (`value-matches`, `## Value matches`) for literal-looking words of the question | value grounding on (off by default), same sharing rule; live probes only for callers who may query | PII, secret-like and financial-amount column names are never probed or matched; a PII- or credential-shaped hit is dropped |
| Distinct column values in documentation prompts (`column-values`, enum discovery) | sample-value collection on for the project and every project sharing the source | the same name and value rules |
| Result rows in the eval judge's prompt (`result-rows`) | the eval judge on (off by default) and content retention on | PII columns masked, at most 20 rows |
| Database errors in repair prompts and in lesson extraction (`database-errors` for lesson extraction) | an `ask` or cross-source `ask` repair after a failed dry run or execution; the learning job mining failed `ask` signals | redacted by class ([Database errors](#database-errors)); under the lock repair prompts and lesson extraction send only the class and code |
| Stored documentation of the business-domain, data-model and glossary sections (`stored-documentation`), which can list values discovered in the data | knowledge answers — the documentation, its chunks and the project search results cut from these sections (keyword and semantic); contextual-retrieval blurbs of documentation chunks; a documentation instruction on such a section | under the lock these sections, their chunks and their search results are left out of knowledge answers and blurbs, and an instruction on one is refused (`400`); regenerate the documentation instead |

- **The deployment lock `Beacon:Mcp:ForbidRowDataToLlm`** (off by default) pins sample-value collection, value grounding
  and the eval judge off for the global settings and every project: no sample is collected or shown, no value is probed,
  documentation discovers no column values, the judge sees no rows, repair prompts and lesson extraction send only an
  error's class and code, and stored documentation sections that can list discovered values stay out of every prompt. A
  request that would turn one of the three settings on answers `409`, and a project override of one is refused even when
  it agrees with the lock; a save under the lock keeps the values stored before it (globally and per project), so
  lifting the lock restores them. After startup the host logs one warning listing the stored rows the lock overrides
  (global row and project ids), and one naming the projects whose stored documentation still has value-listing sections
  — values discovered before the lock stay in that documentation until it is regenerated.
- **Egress log.** Every request whose prompt carries row data is logged at Information with event id `7123`
  (`RowDataSentToLlm`) as it is handed to the provider — the data may leave even when the provider then fails, so a
  failure adds a Warning under the same event with the failure's type and code. Each record names the categories, the
  data source ids, the project id and the caller: the caller's Beacon user id, or for background work the job itself
  (`learning-aggregation`, `documentation-indexing`, `pattern-replay`; an eval run names the user who started it, else
  `eval-run`; documentation generation the user who asked for it), and `unknown` when the caller cannot be resolved
  (which never fails the request). A `get_query_context` response carrying sample values or value matches is recorded
  under the same event as returned to the MCP client. Never a value or prompt text; no table is written. See
  [MCP observability](/features/mcp-observability-and-audit/#row-data-sent-to-the-llm-provider).
- **Where the data goes.** OpenAI and Anthropic are called on their public API endpoints; there is no base-URL or proxy
  setting for them. Azure OpenAI takes your resource's endpoint (choose its region for residency) and Bedrock your AWS
  region, both in the LLM connection settings.

### Database errors

A database quotes row values back in its errors — a failed conversion names the value it could not convert, a key
violation the key — so Beacon judges an error by its **class** (SQLSTATE, error number or the engine's wording) before
it leaves its hands:

- An error that **carries values** — a data exception or an integrity violation (SQLSTATE `22xxx`, `23xxx`), SQL Server's
  conversion, overflow, duplicate-key, truncation and NULL errors, MySQL's incorrect-value, duplicate-entry,
  out-of-range and too-long errors, BigQuery, Databricks and Snowflake cast and parse errors — is replaced by its class,
  code and a generic description, none of its message kept:
  `Database error 22P02 (data exception): a value could not be converted to integer. The database's message is withheld because it can quote row values.`
- An error that **carries no values** — an unknown or ambiguous name, a grouping, syntax, type, permission or
  division-by-zero error — keeps its message with quoted text, key and row details and unquoted values replaced by
  `<value>`. Quoted text survives only in an error about a name, and only when every part of it is a name of the
  statement or the catalog.
- An error Beacon does not recognise is replaced like a value-carrying one, whatever it says — quoted or not:
  `Database error (unrecognised): not an error Beacon recognises. The database's message is withheld because it can quote row values.`
  So is an error the statement's own code raised, whose wording is its author's: a SQLSTATE of class `P0`
  (PostgreSQL's `RAISE`, Databricks' `raise_error`), SQL Server's errors 50000 and above (`THROW`, `RAISERROR`) and
  MySQL's SQLSTATE class `45` (`SIGNAL`) keep only their code, even when the text reads like a name, syntax or
  permission error. Where the driver leaves the code out of the message, Beacon puts it in front
  (`Msg 50000, Level 16, State 1: …`, `45000: …`), also in the raw message a caller reads while PII detection is off.
- Beacon's own fixed texts that arrive with the database's errors (a host source's failure, a read stopped at the row
  ceiling, a refused read of a migration write target) hold no value and are kept. The whole message is redacted
  before it is cut to 2,000 characters; an error that cannot be screened in time is replaced as a whole and counted
  (`beacon.error_redaction.fallbacks`).

Where the redacted form is used:

| Where the error goes | Redacted |
|---|---|
| Repair prompts (`ask`, cross-source `ask`) and lesson extraction | always; under the lock `Beacon:Mcp:ForbidRowDataToLlm` only the class and code (`Database error 42703 (undefined column).`) |
| Learning signals (`ask`, `query`, `dry_run`, cross-source) and the stored history of ad-hoc queries | always |
| The error returned by `ask`, `query`, `dry_run`, cross-source queries, saved-query tools and REST previews and executions, and the audit row | while PII detection is on for the project (or the strictest project sharing the source); the raw message otherwise |
| Host data sources | never the database's message: `Query failed on the host database.` |

Logs never carry the message at all: a failed query is logged by exception type, error code, the types of the
exceptions it wraps and the method that threw it (`PostgresException (error code 22P02, inner SocketException, at NpgsqlConnector.ReadMessage)`).

:::caution[Upgrading]
- **Defaults.** A new installation starts with sample-value collection and value grounding off. Stored settings are not
  changed: an installation that saved its global MCP settings keeps the values it stored (the migration
  `DefaultSampleValueCollectionOff` only changes the column default for a row created later). An installation that never
  saved them reads the new defaults, so both are off until a project or the global settings turn them on; the host
  then logs at startup which row-data settings are in effect. With sample-value collection off, documentation
  generation lists no discovered column values (logged once per run).
- **Sampling, in both directions.** A project that turns sample-value collection on now collects even while the global
  value is off (before, the global value had to be on too). A refresh no longer reads excluded columns at all, and more
  names are excluded: person names, Croatian personal data, connection strings, `dsn`, webhook URLs, `hmac` and key
  material named with `hmac`, `aes`, `rsa`, `pgp`, `gpg` or `ssh` beside `key`. Surrogate keys such as `ProductKey` are
  no longer excluded and are sampled again. The next refresh replaces what changed.
- **Settings API.** A `PUT /beacon/api/mcp/settings` body that omits `enableSampleValueCollection` or
  `enableValueGrounding` now stores `false` (the default) for it; send every field you read. The settings page shows
  both switches and sends back every setting it loaded, the ones it does not show included.
- **Errors.** Database errors returned to callers and written to the audit are in the generic class form above while PII
  detection is on, and learning signals and ad-hoc query history always hold the redacted form. An error of no class
  Beacon recognises is now withheld even without a quote in it, and an error raised by the statement's own code
  (SQL Server 50000 and above, MySQL SQLSTATE class 45) reaches a caller led by its code. Failure logs name the
  failure as `Type (error code …, inner …, at …)` instead of separate type and code fields.
- **Masking.** Banking identifiers (`iban`, `oib`, `jmbg` as whole name segments, `account_number`) are PII names: query
  results mask such columns on every path while PII detection is on. Data-quality custom SQL masks a text value (an
  `actual_value` or `message`) that looks like PII, whatever its column is called.
- **Under the lock**, an instruction on a business-domain, data-model or glossary documentation section answers `400`.
:::

The per-project settings have their own endpoints: `GET` / `PUT /beacon/api/mcp/projects/{projectId}/settings` (admin only). The `GET` returns the project's overrides, the resolved effective values, and the names of any locked or clamped fields.

**Context preview**
- Render the grounding context for a project exactly as the tools would assemble it — useful for spotting missing documentation before an agent hits it.

Changes take effect without a restart. These settings are part of [Admin Settings](/features/admin-settings/); the retrieval, exemplar, and eval knobs that aren't on this page are settable through `PUT /beacon/api/mcp/settings` and documented in the [Knowledge Base guide](/features/knowledge-base/#settings-reference).

## MCP Playground

You don't need to wire up an external MCP client to try the server. The React UI ships an **MCP Playground** (`/mcp-playground`) where you select a project and ask questions interactively — the same tools, routing, guardrails, and audit trail as a real MCP session. Use it to validate documentation quality and tune the system prompt before pointing Claude or Cursor at your data.

The playground runs as the signed-in user and checks their [project access](/features/authorization/#project-access) on every call: the catalog tools (`get_context`, `get_documentation`, `search`, `get_query_context`) need Read in the selected project, and `ask`, `query`, `dry_run` and `feedback` need Query. A restricted project the user is not a member of, or a project outside an API key's list, is refused before any tool runs; a call without a signed-in request is refused outright. Tool calls over `/beacon/mcp` decide the caller's projects by the same rules (see [Overview](#overview)).

The companion **MCP Learning** page (`/mcp-learning`) shows the learning loop at work: usage signals, success rate, learned schema patterns awaiting approval, and proposed documentation patches you can apply or reject.

## Manual Connection (Advanced)

If your MCP client doesn't support Streamable HTTP configuration natively, you can drive the protocol manually by POSTing JSON-RPC messages to the single `/beacon/mcp` endpoint. Every request carries the `Authorization: Bearer` header.

### Step 1: Initialize the Session

```bash
curl -X POST "https://your-host/beacon/mcp" \
  -H "Authorization: Bearer sk-sem_YOUR_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "initialize",
    "params": {
      "protocolVersion": "2024-11-05",
      "clientInfo": { "name": "my-client", "version": "1.0" }
    }
  }'
```

### Step 2: List Available Tools

```bash
curl -X POST "https://your-host/beacon/mcp" \
  -H "Authorization: Bearer sk-sem_YOUR_KEY" \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc": "2.0", "id": 2, "method": "tools/list"}'
```

### Step 3: Call a Tool

```bash
curl -X POST "https://your-host/beacon/mcp" \
  -H "Authorization: Bearer sk-sem_YOUR_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 3,
    "method": "tools/call",
    "params": {
      "name": "get_context",
      "arguments": {}
    }
  }'
```

The server streams the JSON-RPC response back on the same connection.

## Troubleshooting

**"No projects are available to you" error** — the caller reaches no project: none of the API key's projects is one its owner may still use, a mapped Entra user is not a member of the restricted projects it is configured for, or the call came from a browser session (use the [MCP Playground](#mcp-playground)). Check the key's projects and the owner's or user's project memberships.

**"Access denied: you do not have access to project N"** — the project named in `project_id` is not one the caller reaches (see [Overview](#overview)).

**"Access denied: you may read project N but not run ad-hoc queries in it"** — the caller is a Viewer in the project: it reads the catalog, but `ask`, `query` and `dry_run` need an Editor role there (and, in a restricted project, an Editor membership). Saved-query tools stay available to Viewers.

**"Multiple projects available" error** — Your API key has access to multiple projects. Pass `project_id` in your tool calls, or restrict the API key to a single project.

**"Query validation failed" error** — The query contains write operations (INSERT, UPDATE, DELETE) which are blocked by read-only enforcement. Only SELECT queries are allowed.

**Connection drops or timeouts** — Streaming connections may be interrupted by proxies or load balancers. Reconnect by re-initializing against `/beacon/mcp` — a fresh session will be created.

**Authentication fails** — Verify your API key starts with `sk-sem_`, hasn't expired, and hasn't been revoked. Check the `Authorization: Bearer` header format.

**"Permission denied: this credential does not have the Execute scope"** — the key (or Entra caller) has only `Read`, or it has `Execute` but its owner no longer has the Editor role or above. Use an `Execute` key of a user with write permission.

**Answers miss obvious joins or misread a business term** — the model can only use what it was given. Check the [Knowledge Base guide](/features/knowledge-base/): register the join path, add the glossary term, or promote a correct answer to a golden example.

## Upgrading from 4.5: project access

MCP now uses the same [project access](/features/authorization/#project-access) as the REST API and the playground, decided on every call (see [Overview](#overview) and the full [upgrade notes](/features/authorization/#upgrading-from-45-project-access)):

- **API keys without a project list** now reach their owner's projects on MCP; before, they reached no project there. This widens what such keys can do over MCP — re-issue them with a project list to confine them. Keys with a list reach only the listed projects their owner may still use.
- **Mapped Entra user callers** reach a restricted project only while their Beacon user is a member of it, and nothing while their user is disabled or archived; a user caller without a Beacon user reaches its configured open projects only. System callers keep their configured projects. See [Entra ID callers on MCP](/features/mcp-entra-callers/).
- **Saved-query tools** are listed only in projects where the caller may run saved SQL, and each run is re-checked right before its SQL: when a data source the version reads is no longer linked to the project, or the caller may no longer run saved SQL there, the call fails without running anything. **Host endpoint tools** need the caller to read the host's project.
- **A call without an authenticated caller is refused**, over HTTP or over a transport without an HTTP request (such as a host-wired stdio transport), and such a call reaches no project. Browser sessions reach no project on `/beacon/mcp`, as before.
- **The SQL-carrying tools need Query in the project**, as in the playground: `ask`, `query` and `dry_run` (and `feedback`) answer "Access denied: you may read project N but not run ad-hoc queries in it." to a caller that is only a Viewer there, while the catalog tools still answer it. Mapped or provisioned Entra users keep the default `Viewer` role (`Beacon:Mcp:Callers:Users:DefaultRoleName`) and so read the catalog only: give them the Editor role — and, in a restricted project, an Editor membership — to run SQL tools. API keys are already capped by their owner's role.
- **Error messages** no longer mention API keys: "Access denied: you do not have access to project N." and "No projects are available to you."
- **Audit rows and signals** record the caller's Beacon user id. API keys and mapped callers were already recorded so; a signed-in session (the playground) was recorded by its external id — with no user when that id is not a number — so it could not rate the signals of its own `ask` calls. Now it can.
- **Eval runs without a project** are refused: the run is marked `Failed` before any case runs.
- **Database names must be the data source's own.** SQL naming another database, catalog or project than the one the data source's connection configures is refused (`cross_database`), and a data source without a configured database accepts no database qualifier. SQL the gate cannot parse is refused even with read-only enforcement off.
- **Shared data sources take the strictest guardrails** of the live projects that link them: a project with a looser row limit, PII, read-only, value-grounding or sample-value setting gets the stricter one on a data source it shares.
- **Sample values and value grounding are off by default** for a new installation, and the deployment lock `Beacon:Mcp:ForbidRowDataToLlm` keeps all row data from the LLM provider; see [What reaches the LLM provider](#what-reaches-the-llm-provider).
- **`get_query_context` runs live value probes only for callers who may query the project**; a Viewer's context resolves values from the stored samples alone.

## See Also

- [Knowledge Base & Grounding](/features/knowledge-base/) — what grounds generated SQL, and how to curate it
- [API Keys](/features/api-keys/) — scopes, project restrictions, and rotation
- [Entra ID callers on MCP](/features/mcp-entra-callers/) — user and system JWT callers, their projects, scope and audit
- [Admin Settings](/features/admin-settings/) — LLM provider configuration
- [AI Integration](/features/ai-integration/) — documentation generation that feeds the MCP catalog tools
