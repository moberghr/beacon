# Moberg.Beacon.MCP

Beacon's **Model Context Protocol server**. One governed endpoint, `/beacon/mcp`, through which Claude, Claude
Desktop, Claude Code, Cursor, VS Code and any other MCP client read your catalog and documentation, run read-only SQL,
call approved saved queries and your own endpoints. Every call is project-scoped, masked and audited.

**Docs:** [MCP overview](https://moberghr.github.io/beacon/mcp/) ·
[Server & tools](https://moberghr.github.io/beacon/mcp/server/) ·
[Connect AI clients](https://moberghr.github.io/beacon/mcp/connect/) ·
[Cookbooks](https://moberghr.github.io/beacon/cookbooks/) ·
for agents: [`llms-mcp.txt`](https://moberghr.github.io/beacon/llms-mcp.txt)

## Which package?

| You want | Package |
|---|---|
| AI clients over **your databases**: catalog, read-only SQL, saved queries (Beacon's data product) | `Moberg.Beacon.MCP` (this one), with `Moberg.Beacon.Core`, `Moberg.Beacon.AI`, `Moberg.Beacon.Api` |
| AI clients acting as **your own application's users**, through your endpoints, with no data sources | [`Moberg.Beacon.Gateway`](https://www.nuget.org/packages/Moberg.Beacon.Gateway), see the [gateway recipe](https://moberghr.github.io/beacon/mcp/gateway/) |

## Install and wire

```bash
dotnet add package Moberg.Beacon.MCP
```

```csharp
builder.Services.AddBeaconApiAuthorization();   // the Execute-scope policy MCP needs
builder.Services.AddBeaconAI(builder.Configuration);
builder.Services.AddBeaconMcp();                // or AddBeaconMcp(x => …) to choose the built-in tools

var app = builder.Build();
// … UseBeaconAuthentication(), UseAuthorization() …
app.MapBeaconMcp();      // /beacon/mcp, Execute scope required, MCP credentials only
app.MapMcpDiscovery();   // /.well-known/oauth-protected-resource and /.well-known/mcp/server-card.json
```

Set `Beacon:PublicBaseUrl` to the URL clients use, so the discovery documents advertise it. The complete host is in
[Integrate Beacon](https://moberghr.github.io/beacon/integrate/).

## Tools

| Tool | What it does |
|---|---|
| `get_context` | Project overview: data sources, schemas, tables, documentation status, or an AGENTS.md-style brief |
| `search` | Keyword search over tables, columns and documentation, with semantic matching when local embeddings are on |
| `get_documentation` | Documentation at project, data-source or table level |
| `get_query_context` | The grounding `ask` uses: schema with join paths, glossary and verified examples |
| `dry_run` | Validates SQL through every gate without running it |
| `query` | Runs read-only SQL against one data source, row-capped and masked |
| `ask` | Answers a natural-language question: routing, grounded SQL generation, gated execution |
| `feedback` | Rates an answer; a `correct` verdict becomes a verified example only after an Admin reviews it |
| `q_<name>` | An approved saved query a SecurityAdmin published as a tool |
| `api_<name>` | One of your HTTP endpoints marked with `WithBeaconTool(...)` / `[BeaconTool]`, run as the caller |

## Connect a client

Create an API key with the **Execute** scope, limited to the projects the agent may use (Beacon → **API Keys**), then:

```bash
claude mcp add --transport http beacon https://beacon.example.com/beacon/mcp \
  --header "Authorization: Bearer $BEACON_MCP_API_KEY"
```

Cursor, VS Code, claude.ai and Claude Desktop (signing in with Microsoft Entra) are covered in
[Connect AI clients](https://moberghr.github.io/beacon/mcp/connect/) and the
[cookbooks](https://moberghr.github.io/beacon/cookbooks/).

## What an agent can and cannot do

- **Projects are the boundary.** The caller's projects are evaluated on every call. A revoked membership takes effect on
  the next call.
- **Read-only, twice.** Every statement passes Beacon's SQL execution gate (dialect-aware AST validation, the
  cross-database rule, a row cap written into the SQL). PostgreSQL and MySQL also run it in a read-only transaction.
- **Masked.** PII detection masks sensitive values in results.
- **Locked by the host.** `Beacon:Mcp:ForceReadOnly` and `Beacon:Mcp:ForcePiiDetection` are on by default and bind
  Admins too. Content retention, row data to the LLM and numeric ceilings can be pinned the same way.
- **Credentials, not sessions.** `/beacon/mcp` accepts an API key or a mapped Entra bearer token; a browser session gets
  `403`. People try the tools in Beacon's MCP playground.
- **Audited.** Every tool call is recorded, refusals included, and also emitted as `Beacon.Audit` log events and
  OpenTelemetry spans and metrics when you turn them on.

## Extend it

| Agents should | Use |
|---|---|
| Run reviewed reports with parameters | [Saved query tools](https://moberghr.github.io/beacon/mcp/saved-query-tools/) |
| Call your endpoints as the signed-in person | [Host endpoint tools](https://moberghr.github.io/beacon/mcp/host-endpoint-tools/) |
| Understand your EF Core model | [Host DbContext](https://moberghr.github.io/beacon/mcp/host-dbcontext/) |
| Read your Markdown documentation | [Host docs](https://moberghr.github.io/beacon/mcp/host-docs/) |
| Write SQL from your glossary and verified examples | [Grounding & knowledge base](https://moberghr.github.io/beacon/mcp/knowledge-base/) |

## Let your coding agent do it

```text
Read https://moberghr.github.io/beacon/llms-mcp.txt. It is the documentation of Beacon's MCP server.
Then help me with: <your goal>. Ask me before every decision the docs mark as one,
and never put an API key or secret in a committed file.
```

With Claude Code, the Beacon plugin adds skills for the same: `/plugin marketplace add moberghr/beacon`, then
`/plugin install beacon@moberg-beacon`.

## License

Dual-licensed under AGPL-3.0 or a commercial license. See
[LICENSING.md](https://github.com/moberghr/beacon/blob/main/LICENSING.md).
