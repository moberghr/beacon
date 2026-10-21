---
title: Prompt library
description: "Copy-paste prompts for working with Beacon through AI: for coding agents wiring Beacon into a repository, for people asking their data questions through Beacon's MCP tools, for people using a gateway application's tools, and for admins running Beacon."
---

Prompts that work, grouped by who types them and where. Replace the parts in `<angle brackets>`. Each prompt names the
Beacon page the AI should read, so the answer follows the current docs and not the model's memory of them.

:::tip[The pattern behind every prompt]
1. **Point at the source:** a `…/llms-*.txt` bundle or a page's `.md` URL.
2. **Say the goal** in one sentence.
3. **Keep the decisions yours:** "ask me before every decision the guide marks as one".
4. **Ask for proof:** "show me each Check it works result", "show me the SQL before you run it".
:::

## In your repository (coding agents)

For Claude Code, Cursor, Copilot or any agent that edits code. With Claude Code, the [Beacon plugin](/getting-started/with-ai/#the-claude-code-plugin)
adds skills that do the same with less typing.

### Set Beacon up

```text wrap title="MCP gateway for this application"
Add an MCP gateway to this ASP.NET Core app with Beacon.
Read https://moberghr.github.io/beacon/llms-gateway.txt first and follow it step by step.
Before writing code, read the app and tell me: how users sign in, where the users table and its Entra object id are,
which database and migrator we use, and which endpoints look like self-service reads. Then ask me the decisions
the guide lists.
```

```text wrap title="Embed Beacon's data product"
Integrate Beacon into this ASP.NET Core app, with <PostgreSQL | SQL Server> for Beacon's own data and
<Warp | Hangfire | our own scheduler> for jobs.
Read https://moberghr.github.io/beacon/llms-integrate.txt and follow its checklist in order.
Ask me before every decision it marks as one, keep secrets in user secrets, and show me each verification step.
```

```text wrap title="Single sign-on with Microsoft Entra ID"
Turn on Entra ID single sign-on for Beacon in this app.
Read https://moberghr.github.io/beacon/cookbooks/entra-sso.md and follow it. Print the app registration steps for me
to run in the portal instead of changing the tenant yourself.
```

```text wrap title="Upgrade to Beacon 5.0"
Upgrade this application from Beacon 4.x to 5.0.
Read https://moberghr.github.io/beacon/getting-started/whats-new.md and apply every item under "Upgrade from 4.x"
that applies. List the ones that did not apply and why. Build and run the tests afterwards.
```

### Tools for agents

```text wrap title="Expose one endpoint as a tool"
Make <GET /api/my/orders> available to Claude as a Beacon MCP tool.
Read https://moberghr.github.io/beacon/cookbooks/endpoint-as-tool.md first. Check the endpoint against its
tool review checklist before marking it, bind the person's id from the caller, write an agent-facing description
field by field, and add a Moberg.Beacon.Testing test that proves it refuses somebody else's data.
```

```text wrap title="Review the tools we already expose"
List every endpoint in this repository marked with WithBeaconTool or [BeaconTool].
For each, check it against the tool review checklist in
https://moberghr.github.io/beacon/cookbooks/endpoint-as-tool.md and report: self-service or not, what data it
returns, whether ReadOnly is declared, whether the person's id is bound from the caller, and whether a test pins it.
Do not change anything yet.
```

```text wrap title="Publish a saved query as a tool"
Walk me through publishing our approved saved query "<name>" as an MCP tool.
Follow https://moberghr.github.io/beacon/cookbooks/saved-query-tool.md and tell me who has to do which step.
```

### Fix and harden

```text wrap title="A startup error"
Beacon refused to start with this message: <paste the message>.
Read https://moberghr.github.io/beacon/integrate/troubleshooting.md and the page the message points to, explain the
cause in two sentences, then fix it in this repository.
```

```text wrap title="Claude cannot connect to our MCP endpoint"
Claude cannot connect to https://<your-app>/beacon/mcp. The error is: <error or AADSTS code>.
Read https://moberghr.github.io/beacon/mcp/gateway.md (sections 6 to 8) and tell me what to check, in order.
Then run: beacon-doctor mcp https://<your-app>/beacon/mcp and explain every WARN and FAIL it prints.
```

```text wrap title="Production review"
Review this application's Beacon set-up against https://moberghr.github.io/beacon/integrate/production.md.
Go through every checklist item and answer pass, fail or not applicable, with the file and line that shows it.
Do not change anything; give me the list.
```

## Ask your data

For Claude, Claude Desktop, Cursor or any MCP client connected to Beacon's MCP server. The recipe
[Ask your data questions with an AI agent](/cookbooks/ask-your-data/) explains the workflow behind these.

```text wrap title="Start a session"
Use Beacon's get_context to describe my project: its data sources, the main tables and how well they are
documented. Do not run any queries yet.
```

```text wrap title="Find where something lives"
Where does Beacon keep <customer refunds>? Search for it, read the documentation of the most likely tables, and tell
me which columns hold the amount, the date and the customer.
```

```text wrap title="Write the SQL, check it, then run it"
I need <monthly refund totals for the last six months>. Get the grounding context for that question, write the SQL
yourself, validate it with dry_run and show me the SQL and the verdict. Run it with query only after I say go.
```

```text wrap title="A quick answer"
Ask Beacon: "<How many orders were placed last week, by country?>" Show me the SQL it generated with the answer.
```

```text wrap title="Explain a number"
Explain where the figure <12,430 active customers> comes from: the SQL, the tables it reads, and the filter that
decides who counts. If Beacon has no definition of the term, say so instead of guessing.
```

```text wrap title="Compare two periods"
Compare <revenue> in <September> and <October> by <product line>. Use one query, validate it with dry_run first, and
point out the three biggest changes. Say if any result was truncated or masked.
```

```text wrap title="Use the reviewed reports first"
Which saved-query tools (q_…) do I have? If one of them answers "<question>", run it instead of writing SQL, and
tell me which one you used.
```

```text wrap title="Rate the answer"
That answer was <correct | wrong because it counted cancelled orders>. Record it with Beacon's feedback tool and the
signal_id from the answer<, with this corrected SQL: …>.
```

```text wrap title="A brief for your agent's instructions"
Call get_context with format "agents_md" and turn the result into a short section I can paste into AGENTS.md:
the data sources, the main tables and what they mean, and the rules for using Beacon's tools.
```

## In an application with a Beacon gateway

For people whose own application has a Beacon MCP gateway: Claude acts as you, through the application's own
endpoints, and sees only what the application lets you see.

```text wrap title="What can you do here?"
List the <app> tools you have and, for each, one line on what it does and whether it changes anything.
```

```text wrap title="A read"
Show me my <open leave requests> in <app>, newest first, with their status.
```

```text wrap title="A change (the client asks before it runs)"
Request <leave from 3 to 7 November> in <app>. Before you call the tool, tell me exactly what you will send.
```

## Running Beacon (admins)

```text wrap title="Design project boundaries"
We have these teams and databases: <list>. Read https://moberghr.github.io/beacon/security.md and propose Beacon
projects, which data sources each links, who is a member with which role, and which projects stay restricted.
Explain each choice in one line.
```

```text wrap title="A key for one agent"
I want an AI agent to read only the <finance> project. Follow
https://moberghr.github.io/beacon/cookbooks/agent-one-project.md and give me the exact steps for our Beacon, the key
settings and the project settings I should check.
```

```text wrap title="Read the MCP audit"
Here is an export of Beacon's MCP audit: <paste or attach>. Read
https://moberghr.github.io/beacon/mcp/observability-and-audit.md for what the fields mean, then summarise: which
callers ran what, which calls were refused and why, and anything that looks unusual.
```

```text wrap title="Lock it down"
Read https://moberghr.github.io/beacon/mcp/server.md (Deployment locks and ceilings) and propose the Beacon:Mcp
configuration for a regulated production deployment, with one line per setting on why.
```

## Writing your own

- **Name the page.** "Read `https://moberghr.github.io/beacon/<page>.md`" is short, and the agent works from current docs.
- **Ask for the plan first** on anything that changes code, a tenant or a deployment: "tell me what you will do, then
  wait".
- **Name the tool when it matters.** "Validate it with `dry_run`" is more reliable than "be careful".
- **Ask for evidence.** The SQL, the row count, the `beacon-doctor` output, the test that pins it.
- **Never paste secrets.** Give environment variable names, not API keys or client secrets.
