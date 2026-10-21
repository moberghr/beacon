---
title: Build with AI
description: "Point Claude Code, Cursor or any coding agent at Beacon: copy-paste prompts for each goal, one-file documentation bundles, the Claude Code plugin, and a snippet that keeps Beacon in your agent's instructions."
---

Most people add Beacon to an application with a coding agent's help, and these docs are written for that. Every page
is also served as plain Markdown, each section is bundled into one file, and the Claude Code plugin teaches the agent
how to use them. Pick your goal, paste the prompt, and answer the questions the agent asks you.

## Pick your goal, copy the prompt

**Give AI clients access to your own application** (your users, your endpoints as tools, no data sources):

```text wrap title="Paste into your coding agent"
Add an MCP gateway to this ASP.NET Core app with Beacon.
Read https://moberghr.github.io/beacon/llms-gateway.txt first and follow it step by step.
Ask me before every decision it marks as one, and never put a secret in a committed file.
```

**Embed Beacon's data product** (monitoring, alerts, data movement, and an MCP server over your databases):

```text wrap title="Paste into your coding agent"
Integrate Beacon into this ASP.NET Core app.
Read https://moberghr.github.io/beacon/llms-integrate.txt first and follow its checklist step by step.
Ask me before every decision the guide marks as one.
```

**Connect your AI client to a Beacon that is already running:**

```text wrap title="Paste into your coding agent"
Connect this project's AI tooling to our Beacon MCP server at https://beacon.example.com/beacon/mcp.
Read https://moberghr.github.io/beacon/cookbooks/connect-coding-agent.md and follow it.
Read the API key from the BEACON_MCP_API_KEY environment variable; never write the key into a file.
```

**Do one specific task** (publish a saved query as a tool, single sign-on with Entra, alert Slack on a failed check, …):

```text wrap title="Paste into your coding agent"
Read https://moberghr.github.io/beacon/llms-cookbooks.txt, pick the recipe for: <your goal>,
tell me which one you picked, then follow it step by step and show me each "Check it works" result.
```

**Upgrade from Beacon 4.x:**

```text wrap title="Paste into your coding agent"
Upgrade this application from Beacon 4.x to 5.0.
Read https://moberghr.github.io/beacon/getting-started/whats-new.md and apply every item under
"Upgrade from 4.x" that applies to this repository. Build and run the tests afterwards.
```

More prompts, including the ones you say to Claude once it is connected to Beacon, are in the
[prompt library](/cookbooks/prompts/).

## What your agent can read

| File | Content | Use it for |
|---|---|---|
| [`llms.txt`](https://moberghr.github.io/beacon/llms.txt) | The index: every page with a one-line summary, and the bundles | Letting the agent find its own way |
| [`llms-gateway.txt`](https://moberghr.github.io/beacon/llms-gateway.txt) | The MCP gateway guide and the pages it uses | An MCP server for your own application |
| [`llms-integrate.txt`](https://moberghr.github.io/beacon/llms-integrate.txt) | The integration checklist and every page it links | Embedding Beacon's data product |
| [`llms-mcp.txt`](https://moberghr.github.io/beacon/llms-mcp.txt) | The whole MCP section | Anything about the MCP server, its tools and clients |
| [`llms-cookbooks.txt`](https://moberghr.github.io/beacon/llms-cookbooks.txt) | Every recipe and the prompt library | One specific task |
| [`llms-full.txt`](https://moberghr.github.io/beacon/llms-full.txt) | Every page | Questions that span the product |

Every page is also available as Markdown: add `.md` to its URL (for example
`https://moberghr.github.io/beacon/mcp/gateway.md`), or use **Copy as Markdown** under the page title. A single page
is the smallest context for a narrow question. A bundle saves the agent from fetching pages one by one.

## The Claude Code plugin

Beacon's repository is a Claude Code plugin marketplace. Install it once:

```text wrap
/plugin marketplace add moberghr/beacon
/plugin install beacon@moberg-beacon
```

| Skill | Claude uses it when you ask to |
|---|---|
| `beacon-integrate` | Embed Beacon's data product in your application |
| `beacon-gateway` | Add an MCP gateway to your application |
| `beacon-tool` | Make one endpoint available to Claude as a tool, safely |
| `beacon-entra` | Prepare the Microsoft Entra registrations for Claude's connector, or fix an `AADSTS` error |

The skills follow the documentation above: they fetch the current guide instead of relying on what the model remembers.
They read your application before deciding anything, ask the decisions the guide lists, and stop before anything
outward-facing, such as changing your tenant, pushing or deploying.

## Keep Beacon in your agent's instructions

Once Beacon is in your repository, add a short section to its `CLAUDE.md` or `AGENTS.md`, so every later session knows
where Beacon's rules are:

```md title="CLAUDE.md / AGENTS.md"
## Beacon

This application embeds Moberg Beacon 5.x (NuGet `Moberg.Beacon.*`, all at the same version).

- Docs for agents: https://moberghr.github.io/beacon/llms.txt (pick the bundle for the task). Read the relevant
  page before changing anything Beacon wires: middleware order, authentication, MCP tools.
- Secrets (`Beacon:EncryptionKey`, connection strings, API keys) live in user secrets or the secret store, never in
  a committed file.
- An endpoint marked as an MCP tool (`WithBeaconTool` / `[BeaconTool]`) is self-service: it returns or changes only
  the signed-in person's own records, declares `ReadOnly`, has authorization metadata, and takes the person's id from
  the caller (`BindFromCaller`), never from the agent. Add a test with `Moberg.Beacon.Testing` for each one.
- Beacon refuses to start on most wiring mistakes, and the message says the fix: read it before changing code.
```

## Getting good results

- **Let the agent ask.** The guides mark the choices that are yours, such as who may connect, which endpoints become
  tools, and where Beacon's tables live. A prompt that says "ask me before every decision" keeps the agent from
  guessing.
- **Point it at the source of truth.** Beacon's documentation describes the current release. An agent's memory of
  Beacon may be older, and the plugin's skills fetch the docs for that reason.
- **Use the reference hosts.** `src/Beacon.SampleProject/Program.cs` (the full product) and `src/Beacon.SampleGateway`
  (a gateway host) in the [repository](https://github.com/moberghr/beacon) are complete compositions to compare
  against.
- **Make it prove it.** Every recipe ends with "Check it works". Ask the agent to show you each result, and to run
  `beacon-doctor` against a deployed MCP endpoint before you connect Claude to it.
- **Keep secrets out of the chat.** Give the agent environment variable names, not API keys or client secrets.
