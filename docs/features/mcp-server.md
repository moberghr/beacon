---
layout: default
title: MCP Server
parent: Features
nav_order: 10
---

# MCP Server
{: .no_toc }

Semantico exposes a **Model Context Protocol (MCP)** server that lets AI assistants (Claude, Cursor, Windsurf, custom agents) query your data sources, search your catalog, and access documentation — all through a standardized protocol.

<details open markdown="block">
  <summary>Table of contents</summary>
  {: .text-delta }
- TOC
{:toc}
</details>

---

## Overview

The MCP server is **project-centric**: each API key is scoped to one or more projects, and all tools automatically resolve which data sources, schemas, and documentation to use based on the active project.

**What you can do through MCP:**
- Ask natural language questions and get SQL + results back
- Execute direct SQL queries against any data source in your project
- Search tables, columns, and documentation by keyword
- Retrieve AI-generated documentation for your project, data sources, or individual tables
- Access project resources (schemas, quality reports, documentation)

---

## Quick Start

### 1. Create an API Key

Go to **Settings > API Keys** (`/semantico/settings/api-keys`) and create a new key:
- Choose a **scope**: `Read`, `Execute`, or `Admin`
- Optionally restrict to specific **projects**
- Copy the key — it's shown only once

The key format is: `sk-sem_...`

### 2. Configure Your MCP Client

Add Semantico to your MCP client configuration. The exact format depends on your client.

**Claude Desktop** (`claude_desktop_config.json`):
```json
{
  "mcpServers": {
    "semantico": {
      "url": "https://your-semantico-host/semantico/mcp/sse",
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
    "semantico": {
      "url": "https://your-semantico-host/semantico/mcp/sse",
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
    "semantico": {
      "serverUrl": "https://your-semantico-host/semantico/mcp/sse",
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

---

## Connection Details

| Property | Value |
|----------|-------|
| **SSE Endpoint** | `GET /semantico/mcp/sse` |
| **Message Endpoint** | `POST /semantico/mcp/message?sessionId={id}` |
| **Transport** | Server-Sent Events (SSE) + JSON-RPC 2.0 |
| **Protocol Version** | `2024-11-05` |
| **Authentication** | `Authorization: Bearer sk-sem_...` header |

The SSE endpoint establishes a persistent connection. The first SSE message returns the message endpoint URL for sending requests. All responses are delivered back through the SSE stream.

---

## Tools

The MCP server exposes **5 tools** that AI clients can call.

### `get_context`

Get an overview of the project: data sources, schemas, tables, quality scores, and documentation status. **This is the recommended starting point** for understanding what data is available.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | integer | No | Specify project if your API key has access to multiple projects |

**Example response** (markdown):
```
# Project: E-Commerce Analytics

2 data sources, 1 repository, documentation available

## Data Sources

### production-db (PostgreSQL)
- 45 tables, 3 schemas
- Quality: 87%
- Code references: 124

### analytics-api (REST API)
- 12 endpoints, 2 tags
```

---

### `ask`

Ask a natural language question about your data. Semantico auto-detects the right data source(s), generates SQL, executes it, and returns results.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `question` | string | **Yes** | — | Natural language question (e.g., "How many orders were placed last week?") |
| `project_id` | integer | No | — | Specify project if needed |
| `execute` | boolean | No | `true` | Set to `false` to get the generated SQL without executing it |

**How it works:**
1. **Routing phase** — The LLM determines which data source(s) to query (skipped for single-source projects)
2. **SQL generation** — Generates SQL using your actual schema as context
3. **Execution** — Runs the query with safety guardrails (read-only, row limits, PII detection)

**Cross-source queries:** If your question spans multiple data sources, Semantico queries each source separately and joins results in an in-memory SQLite database.

---

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

---

### `get_documentation`

Retrieve AI-generated documentation at three levels of detail.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | integer | No | Specify project if needed |
| `datasource_name` | string | No | Get docs for a specific data source |
| `table_name` | string | No | Get detailed docs for a specific table or API endpoint |
| `schema_name` | string | No | Schema name or API tag (optional qualifier for table_name) |

**Three levels:**

1. **Project level** (no parameters) — Full generated project documentation
2. **Data source level** (`datasource_name` only) — Tables, schemas, code references, quality scores
3. **Table level** (`table_name`) — Columns with types, relationships, code references, quality rules, lineage

---

### `search`

Search tables, columns, and documentation across all data sources in the project.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `query` | string | **Yes** | — | Search keyword (e.g., "customer", "order_date", "revenue") |
| `project_id` | integer | No | — | Specify project if needed |
| `max_results` | integer | No | `20` | Maximum results to return (max: 50) |

Results include item type (`[TABLE]`, `[COLUMN]`, `[DOC]`), data source, description, and quality score.

---

## Resources

The MCP server also exposes **4 resources per project** that clients can read directly.

| Resource URI | Description |
|-------------|-------------|
| `semantico://project/{id}/documentation` | AI-generated project documentation (markdown) |
| `semantico://project/{id}/schema` | Full schema context across all data sources |
| `semantico://project/{id}/quality` | Data quality report with scores and trends |
| `semantico://project/{id}/report` | Comprehensive project report (sources, repos, stats) |

---

## Safety & Guardrails

The MCP server enforces several safety measures:

| Feature | Description | Default |
|---------|-------------|---------|
| **Read-only enforcement** | Only `SELECT` queries are allowed | Enabled |
| **Row limits** | Maximum rows returned per query | 100 (single), 500 (cross-source), max 1000 |
| **PII detection** | Automatically detects and flags sensitive data patterns | Enabled |
| **Query timeout** | Queries are cancelled after 30 seconds | Always on |
| **Audit logging** | All tool calls are logged with user, timing, and parameters | Always on |

---

## Configuration

Administrators can customize the MCP server behavior at **Settings > MCP** (`/semantico/settings/mcp`):

- **Custom tool descriptions** — Override the default description for each tool
- **System prompt** — Customize the LLM prompt used for SQL generation in the `ask` tool
- **Global instruction** — Additional instructions injected into every `ask` request
- **Max row limit** — Change the maximum rows returned (default: 1000)
- **Read-only enforcement** — Toggle SELECT-only restriction
- **PII detection** — Enable/disable and add custom PII regex patterns

---

## Manual Connection (Advanced)

If your MCP client doesn't support SSE configuration natively, you can connect manually using the SSE protocol.

### Step 1: Open SSE Connection

```bash
curl -N -H "Authorization: Bearer sk-sem_YOUR_KEY" \
  https://your-host/semantico/mcp/sse
```

The first message returns the message endpoint:
```
event: endpoint
data: https://your-host/semantico/mcp/message?sessionId=abc-123
```

### Step 2: Initialize the Session

```bash
curl -X POST "https://your-host/semantico/mcp/message?sessionId=abc-123" \
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

### Step 3: List Available Tools

```bash
curl -X POST "https://your-host/semantico/mcp/message?sessionId=abc-123" \
  -H "Authorization: Bearer sk-sem_YOUR_KEY" \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc": "2.0", "id": 2, "method": "tools/list"}'
```

### Step 4: Call a Tool

```bash
curl -X POST "https://your-host/semantico/mcp/message?sessionId=abc-123" \
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

Responses arrive on the SSE stream (step 1).

---

## Troubleshooting

**"No project found" error**
: Your API key must be associated with at least one project. Check API key settings.

**"Multiple projects available" error**
: Your API key has access to multiple projects. Pass `project_id` in your tool calls, or restrict the API key to a single project.

**"Query validation failed" error**
: The query contains write operations (INSERT, UPDATE, DELETE) which are blocked by read-only enforcement. Only SELECT queries are allowed.

**Connection drops or timeouts**
: SSE connections may be interrupted by proxies or load balancers. Reconnect by opening a new SSE connection — a fresh session will be created.

**Authentication fails**
: Verify your API key starts with `sk-sem_`, hasn't expired, and hasn't been revoked. Check the `Authorization: Bearer` header format.
