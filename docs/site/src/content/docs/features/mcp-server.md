---
title: MCP Server
description: A Model Context Protocol server that lets AI assistants query your data sources, search your catalog, and read documentation over authenticated HTTP.
---

Beacon exposes a **Model Context Protocol (MCP)** server that lets AI assistants (Claude, Cursor, Windsurf, custom agents) query your data sources, search your catalog, and access documentation — all through a standardized protocol.

## Overview

The MCP server is **project-centric**: each API key is scoped to one or more projects, and all tools automatically resolve which data sources, schemas, and documentation to use based on the active project.

**What you can do through MCP:**
- Ask natural language questions and get SQL + results back
- Pull the exact grounding context Beacon uses for SQL generation — schemas with real sample values, verified join paths, human-verified examples — and write your own SQL (`get_query_context` → `dry_run` → `query`)
- Validate SQL through every safety gate without executing it
- Execute direct SQL queries against any data source in your project
- Search tables, columns, and documentation by keyword, fused with embedding-based semantic matching when a local embedder is enabled
- Retrieve AI-generated documentation for your project, data sources, or individual tables
- Record feedback on answers — a `correct` verdict becomes a verified example that grounds future SQL generation

## Quick Start

### 1. Create an API Key

Go to **API Keys** in the React UI (`/api-keys`) and create a new key:
- Choose a **scope**: `Read`, `Execute`, or `Admin` — the MCP endpoint requires `Execute` or `Admin`, because its tools can run SQL; `Read` keys are limited to the REST read API
- Optionally restrict to specific **projects**
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
| **Authentication** | Required — `Authorization: Bearer sk-sem_...` header with `Execute` or `Admin` scope |

The server is mounted with `app.MapMcp("/beacon/mcp").RequireAuthorization(...)` and enforces the Execute scope for API-key callers (§1.4). Clients exchange JSON-RPC messages with the single `/beacon/mcp` endpoint over the Streamable HTTP transport; the server streams responses back on the same connection.

**Protocol niceties the server publishes:**

- The `initialize` response carries **server instructions** describing the recommended tool workflow, and every tool is published with a human-readable **title** and **annotations** (`readOnlyHint`, `idempotentHint`, `destructiveHint`, `openWorldHint`) so clients can reason about safety before calling.
- `query` and `ask` responses include machine-readable **`structuredContent`** (columns, rows, row count, truncation flag — plus the generated SQL and `signal_id` for `ask`) alongside the markdown text, so agents don't have to parse tables back out of prose.
- **Truncated results say so explicitly** — row-capped query results, paged search results, and concise documentation exports all end with a note stating that more data exists and how to get it (raise `max_rows`, repeat with the next `offset`, or pass `response_format: "detailed"`).

## Tools

The MCP server exposes **8 tools** that AI clients can call.

![MCP Playground](/img/screenshots/mcp-playground-dark.png)

### `get_context`

Get an overview of the project: data sources, schemas, tables, quality scores, and documentation status. **This is the recommended starting point** for understanding what data is available.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | integer | No | Specify project if your API key has access to multiple projects |

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
4. **`provider_dry_run`** — the database's own validation (`EXPLAIN` / `sp_describe_first_result_set`); runs only when every earlier gate passed

Gate issues are collected rather than first-failure-wins, so one call reports everything to fix. The response gives a per-gate verdict and, when valid, the exact SQL that would execute with the row limit applied. `structuredContent` carries the machine-readable verdict: `{ valid, issues: [{gate, error}], executable_sql, pii_columns }`. An INVALID verdict is still a successful tool call — `isError` is reserved for resolution failures.

### `get_documentation`

Retrieve AI-generated documentation at three levels of detail.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | integer | No | Specify project if needed |
| `datasource_name` | string | No | Get docs for a specific data source |
| `table_name` | string | No | Get detailed docs for a specific table or API endpoint |
| `schema_name` | string | No | Schema name or API tag (optional qualifier for table_name) |
| `response_format` | string | No | `concise` (summary sections) or `detailed` (everything). Project level defaults to `concise`; data-source and table level default to `detailed` |

**Three levels:**

1. **Project level** (no parameters) — Full generated project documentation. Defaults to `concise`: the export is cut at ~8,000 characters on a line boundary with an explicit truncation note; pass `response_format: "detailed"` for the full document
2. **Data source level** (`datasource_name` only) — Tables, schemas, code references, quality scores. Defaults to `detailed`; `concise` omits the LLM schema context section
3. **Table level** (`table_name`) — Columns with types, relationships, quality rules; `detailed` (the default) adds code references and lineage, `concise` omits them

### `search`

Search tables, columns, and documentation across all data sources in the project. Keyword matching is fused with embedding-based semantic matching (reciprocal rank fusion) when a local embedder is enabled; without one, search is keyword-only.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `query` | string | **Yes** | — | Search keyword (e.g., "customer", "order_date", "revenue") |
| `project_id` | integer | No | — | Specify project if needed |
| `max_results` | integer | No | `20` | Maximum results to return (max: 50) |
| `offset` | integer | No | `0` | Result offset for paging — use with `max_results` to page through large result sets |

Each result line includes the item type (`[TABLE]`, `[COLUMN]`, `[DOC]`), the data source and schema-qualified table (plus the column name for column hits), and the description, ordered by relevance. When more matches exist beyond the current page, the response ends with an explicit note giving the `offset` to request next.

### `feedback`

Record whether a previous `ask` answer was correct. A `correct` verdict is saved as a verified example (golden pair) that grounds future SQL generation for the same data source.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `signal_id` | integer | **Yes** | The `signal_id` from the `ask` response you are rating |
| `verdict` | string | **Yes** | `correct` or `incorrect` |
| `corrected_sql` | string | No | The corrected SQL, if you fixed it |
| `note` | string | No | A short note |

Every `ask` response that ran a data query ends with a `_signal_id: N_` marker — pass that value back here. Conceptual answers from the knowledge base don't record a query signal and carry no marker.

## Safety & Guardrails

The MCP server enforces several safety measures:

| Feature | Description | Default |
|---------|-------------|---------|
| **Read-only enforcement** | Only `SELECT` queries are allowed | Enabled |
| **Row limits** | Maximum rows returned per query | 100 (single), 500 (cross-source), max 1000 |
| **PII detection** | Automatically detects and flags sensitive data patterns | Enabled |
| **Query timeout** | Queries are cancelled after 30 seconds | Always on |
| **Audit logging** | Every tool call is recorded by `McpAuditService` with user, timing, and parameters | Always on |
| **Usage signals** | `ask` and `query` calls are recorded by `McpSignalService` to feed the learning loop | Always on |

`McpAuditService` fires on every tool invocation, including the failure path — it is never short-circuited. `McpSignalService` records the SQL-learning tools (`ask`, `query`); the read-only catalog tools are audit-only by design.

## SQL Accuracy Stack

Natural-language questions through the `ask` tool don't go through a naive prompt-to-SQL pipe. Every generated query passes a layered accuracy stack:

1. **M-Schema grounding** — the LLM context contains a structured schema rendering (column name, type, nullability, description) *including real sample values* from each column, so filters match actual data formats (`'shipped'` vs `'SHIPPED'`).
2. **AST read-only validation** — generated SQL is parsed into an abstract syntax tree with a dialect-aware parser (PostgreSQL, SQL Server, MySQL, BigQuery, Snowflake, Databricks). DML/DDL statements, stacked queries, and comment-hidden writes are rejected before anything reaches your database — defense-in-depth beyond the regex guardrail.
3. **Dry-run repair loop** — if execution fails, the SQL is retried with the database error and a refreshed schema context; truncated or degenerate repairs are rejected rather than executed.
4. **Row limits & PII masking** — results are capped and sensitive values (emails, phone numbers, SSNs, credit cards, tokens, and custom regex patterns) are masked (`a***z`) before leaving the server.

## Learning Loop

The MCP server is self-improving. `McpSignalService` records a usage signal for every tool invocation (which questions were asked, which data sources and tables were used, whether execution succeeded). Recurring background jobs then process these signals:

![MCP Learning](/img/screenshots/mcp-learning-dark.png)

- **Pattern aggregation** runs every 6 hours, consolidating recorded signals into learned query patterns that improve routing and SQL generation for the `ask` tool.
- **Signal cleanup** runs daily, removing old signals to keep the learning store compact.

This loop runs entirely in the background and requires no configuration.

## Configuration

Administrators can customize the MCP server behavior at **MCP Settings** in the React UI (`/mcp-settings`):

- **Custom tool descriptions** — Stored per tool but not yet applied to the live tool list (wiring ships in a follow-up)
- **System prompt** — Customize the LLM prompt used for SQL generation in the `ask` tool
- **Global instruction** — Additional instructions injected into every `ask` request
- **Max row limit** — Change the maximum rows returned (default: 1000)
- **Read-only enforcement** — Toggle SELECT-only restriction
- **PII detection** — Enable/disable and add custom PII regex patterns

These settings are part of [Admin Settings](/features/admin-settings/); changes take effect without a restart.

## MCP Playground

You don't need to wire up an external MCP client to try the server. The React UI ships an **MCP Playground** (`/mcp-playground`) where you select a project and ask questions interactively — the same tools, routing, guardrails, and audit trail as a real MCP session. Use it to validate documentation quality and tune the system prompt before pointing Claude or Cursor at your data.

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

**"No project found" error** — Your API key must be associated with at least one project. Check API key settings.

**"Multiple projects available" error** — Your API key has access to multiple projects. Pass `project_id` in your tool calls, or restrict the API key to a single project.

**"Query validation failed" error** — The query contains write operations (INSERT, UPDATE, DELETE) which are blocked by read-only enforcement. Only SELECT queries are allowed.

**Connection drops or timeouts** — Streaming connections may be interrupted by proxies or load balancers. Reconnect by re-initializing against `/beacon/mcp` — a fresh session will be created.

**Authentication fails** — Verify your API key starts with `sk-sem_`, hasn't expired, and hasn't been revoked. Check the `Authorization: Bearer` header format.
