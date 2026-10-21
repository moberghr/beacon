---
title: Connect Claude Code, Cursor or VS Code
description: "Connect a coding agent to Beacon's MCP server with a project-limited API key kept out of your repository, and prove the connection with a first tool call."
---

| | |
|---|---|
| **You end up with** | Claude Code, Cursor or VS Code calling Beacon's tools (`get_context`, `search`, `query` and the rest) against one project, with the key outside every committed file |
| **Time** | about 10 minutes |
| **You need** | A running Beacon with MCP mapped (`app.MapBeaconMcp()`, see [MCP and AI clients](/mcp/connect/#wiring)), a Beacon account with the Editor role or above, and a project with at least one data source |
| **Applies to** | Beacon 5.0 · Full Beacon |

:::tip[Let your AI do it]
Paste this into your coding agent. It cannot create the key for you (keys are created in a signed-in browser session only), so it will stop and ask you for it.

```text wrap
Read https://moberghr.github.io/beacon/cookbooks/connect-coding-agent.md and follow it step by step in this repository.
Ask me for the Beacon URL and which client I use. I create the API key myself in the Beacon UI and put it in the
BEACON_MCP_API_KEY environment variable: never ask me to paste the key into the chat, never write it into a file that
is committed, and never commit .cursor/mcp.json or .vscode/mcp.json with a key in it. Show me the result of each
"Check it works" item.
```
:::

## Before you start

| Decision | Options | Default |
|---|---|---|
| Client | Claude Code, Cursor, VS Code | The one you use; the steps are independent |
| Where the config lives | The repository (shared with the team, no key inside) or your user profile (yours alone) | The repository, with the key read from an environment variable |
| Projects the key reaches | One project, or several | One. With several, the agent must pass `project_id` on every call |
| Expiry | A date within `Beacon:ApiKeys:MaxLifetimeDays` (365 by default) | 90 days |

## 1. Create an API key

1. In Beacon, open **API Keys** (`/beacon/api-keys`) and choose **Generate key**.
2. **Key name**: something that says where it is used, for example `Cursor on my laptop`.
3. **Scopes**: tick **Execute**. The MCP endpoint requires it; a `Read` key gets `403`. The checkbox is disabled unless you hold the Editor role or above.
4. **Projects**: pick the project the agent should use. At least one is required.
5. **Expiration date**: set a short one, or leave it empty for 90 days.
6. Generate the key and copy it (`sk-sem_…`). It is shown once; Beacon keeps only a hash.

The key acts as you, in the projects you pick, and never with more than your own role allows. If you later lose the Editor role, the key acts as a Read key and Beacon's MCP endpoint refuses it (403) until you regain the role. More on this in [API keys](/security/api-keys/#scopes).

## 2. Put the key in an environment variable

Set it in your shell profile or secret manager, not in the repository:

```bash
export BEACON_MCP_API_KEY='sk-sem_…'
```

Start the client from a shell where the variable is set (an IDE launched from the dock does not see variables set only in a terminal session).

## 3. Add the server to your client

Use `https://beacon.example.com/beacon/mcp` below; replace it with your Beacon's public URL.

### Claude Code

Shared with the team: commit this `.mcp.json` at the repository root. Claude Code expands `${BEACON_MCP_API_KEY}` when it starts, so no key is in the file.

```json
{
  "mcpServers": {
    "beacon": {
      "type": "http",
      "url": "https://beacon.example.com/beacon/mcp",
      "headers": { "Authorization": "Bearer ${BEACON_MCP_API_KEY}" }
    }
  }
}
```

Only for you: add it from the command line instead.

```bash
claude mcp add --transport http beacon https://beacon.example.com/beacon/mcp \
  --header "Authorization: Bearer $BEACON_MCP_API_KEY"
```

Your shell expands `$BEACON_MCP_API_KEY` before Claude Code sees it, so the key is stored in Claude Code's own configuration under your user profile, not in the repository. Do not run this command in a directory whose configuration you commit.

### Cursor

Put the file in your user profile, `~/.cursor/mcp.json`, so it is never in a repository:

```json
{
  "mcpServers": {
    "beacon": {
      "url": "https://beacon.example.com/beacon/mcp",
      "headers": { "Authorization": "Bearer ${env:BEACON_MCP_API_KEY}" }
    }
  }
}
```

If you want the project-level `.cursor/mcp.json` instead, add it to `.gitignore` or use the `${env:…}` form above. Check Cursor's own documentation if the `${env:…}` interpolation does not resolve in your version; the header and URL are all Beacon needs.

### VS Code

`.vscode/mcp.json` prompts for the key once and keeps it out of the file:

```json
{
  "inputs": [
    { "type": "promptString", "id": "beacon-key", "description": "Beacon API key", "password": true }
  ],
  "servers": {
    "beacon": {
      "type": "http",
      "url": "https://beacon.example.com/beacon/mcp",
      "headers": { "Authorization": "Bearer ${input:beacon-key}" }
    }
  }
}
```

## 4. Keep the key out of git

- [ ] `git grep sk-sem_` finds nothing in the repository.
- [ ] `.cursor/mcp.json` is either absent or contains `${env:…}` only; `.vscode/mcp.json` contains `${input:…}` only.
- [ ] If a key ever reached a commit, revoke it on **API Keys** (**Revoke**, effective on the next request) and create a new one.

## Check it works

**From the client.** Restart it, then ask: *"Use Beacon's get_context to describe my project."* The answer lists the project's data sources and tables. In Claude Code, `/mcp` shows the `beacon` server as connected.

- [ ] The client lists Beacon's built-in tools (all eight unless the host chose fewer): `get_context`, `search`, `get_documentation`, `get_query_context`, `ask`, `query`, `dry_run`, `feedback`.
- [ ] `get_context` answers with your project.

**From a terminal**, independent of any client. Beacon's discovery card needs no key:

```bash
BEACON=https://beacon.example.com
curl -s $BEACON/.well-known/mcp/server-card.json | jq '.tools[].name'
```

Then the three-step handshake with the key. `initialize` returns an `Mcp-Session-Id` header that the next calls must send:

```bash
H=(-H "Authorization: Bearer $BEACON_MCP_API_KEY" -H "Content-Type: application/json" -H "Accept: application/json, text/event-stream")

SESSION=$(curl -s -D - -o /dev/null "${H[@]}" $BEACON/beacon/mcp \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"curl","version":"1"}}}' \
  | awk -F': ' 'tolower($1)=="mcp-session-id"{print $2}' | tr -d '\r')

curl -s "${H[@]}" -H "Mcp-Session-Id: $SESSION" -H "MCP-Protocol-Version: 2025-06-18" $BEACON/beacon/mcp \
  -d '{"jsonrpc":"2.0","method":"notifications/initialized"}'

curl -s "${H[@]}" -H "Mcp-Session-Id: $SESSION" -H "MCP-Protocol-Version: 2025-06-18" $BEACON/beacon/mcp \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'
```

- [ ] `initialize` returns a non-empty `$SESSION`.
- [ ] `tools/list` returns the tools above (plus any `q_…` saved-query tools your project exposes).

## If something goes wrong

| Symptom | Cause and fix |
|---|---|
| `401` with a `WWW-Authenticate` header | No or invalid key. The variable is empty in the client's environment, the key expired or was revoked, or the header is not `Authorization: Bearer sk-sem_…`. |
| `403` at the door, or "Permission denied: this credential does not have the Execute scope" | The key has only `Read`, or you no longer hold the Editor role. Create an `Execute` key as a user who does. |
| "No projects are available to you." | None of the key's projects is one you may still use. Check your project membership. |
| "Multiple projects available (IDs: …)" | The key reaches several projects. Pass `project_id` on every call, or create a key limited to one project. |
| "Access denied: you may read project N but not run ad-hoc queries in it." | You are a Viewer there. `ask`, `query` and `dry_run` need Editor. |
| 400 "A new session can only be created by an initialize request" | A curl call skipped `initialize`, or `$SESSION` is empty. Rerun the handshake from step 1. |
| The client shows no tools but the key works in curl | The client did not start with the variable. Start it from the shell where `BEACON_MCP_API_KEY` is set. |

## Next

- [Ask your data questions with an AI agent](/cookbooks/ask-your-data/): the tool workflow and prompts that get good answers.
- [Give an AI agent access to one project only](/cookbooks/agent-one-project/): restricted projects, masking and the audit.
- [MCP server](/mcp/server/): every tool, argument and guardrail.
- [API keys](/security/api-keys/): scopes, expiry and revocation.
