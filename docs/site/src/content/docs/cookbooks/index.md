---
title: Cookbooks
description: "Task-shaped Beacon recipes, each one start to finish with a check that it works and a prompt you can hand to your coding agent: connect AI clients, publish tools, embed Beacon, single sign-on, alerting, data contracts and audit."
---

Each recipe takes one goal from nothing to a verified result: what you need, the steps, how to check it works, what to
do when it does not, and where to go next. Each one opens with a prompt for your AI, because every recipe is also
served as Markdown that an agent can follow.

:::tip[Hand any recipe to your AI]
```text wrap
Read https://moberghr.github.io/beacon/llms-cookbooks.txt, pick the recipe for: <your goal>,
tell me which one you picked, then follow it step by step and show me each "Check it works" result.
Ask me before every decision a recipe marks as one.
```

Or point the agent at one recipe: every page is at its URL plus `.md`, for example
`https://moberghr.github.io/beacon/cookbooks/connect-coding-agent.md`.
:::

## Start here

| If you want to | Recipe | Time |
|---|---|---|
| Let Claude act in **your own application** as your users, through your endpoints | [MCP gateway for your application](/mcp/gateway/) | about 1 hour, plus the Entra registration |
| Point **Claude Code, Cursor or VS Code** at a running Beacon | [Connect a coding agent](/cookbooks/connect-coding-agent/) | about 10 minutes |
| Run **Beacon inside your application**, with its UI, monitoring and MCP server | [Embed Beacon (PostgreSQL + Warp)](/cookbooks/embed-postgresql-warp/) | about 45 minutes |
| Get **good answers** from an agent connected to Beacon | [Ask your data with an agent](/cookbooks/ask-your-data/) | about 15 minutes |

## Connect AI to your data

| Recipe | You end up with | Time |
|---|---|---|
| [Connect Claude Code, Cursor or VS Code](/cookbooks/connect-coding-agent/) | A coding agent calling Beacon's MCP tools with a project-scoped API key kept out of your files | 10 min |
| [Add Beacon to claude.ai and Claude Desktop with Entra sign-in](/cookbooks/claude-connector-entra/) | A Claude custom connector whose users sign in with Microsoft Entra and reach their projects | 45 min |
| [Ask your data questions with an AI agent](/cookbooks/ask-your-data/) | An agent that orients, grounds, validates its SQL with `dry_run`, and rates answers | 15 min |
| [Publish an approved saved query as an MCP tool](/cookbooks/saved-query-tool/) | A reviewed, parameterised `q_<name>` tool instead of free SQL | 20 min |
| [Give an AI agent access to one project only](/cookbooks/agent-one-project/) | An agent confined to one restricted project, with masking and caps, and its calls in the audit | 20 min |

## Make your app AI-ready

| Recipe | You end up with | Time |
|---|---|---|
| [MCP gateway for your application](/mcp/gateway/) | Your application as an MCP server: Entra sign-in, your endpoints as tools, your docs as grounding | 1 h |
| [Expose an endpoint as an MCP tool, safely, and test it](/cookbooks/endpoint-as-tool/) | A self-service read tool and a confirmed write tool, with an end-to-end test | 30 min |
| [Ground AI answers in your own docs and data model](/cookbooks/ground-with-your-docs/) | Your Markdown docs and EF Core model in the answers agents give | 25 min |

## Embed Beacon

| Recipe | You end up with | Time |
|---|---|---|
| [Embed Beacon in an existing ASP.NET Core app (PostgreSQL + Warp)](/cookbooks/embed-postgresql-warp/) | Beacon's UI at `/beacon`, its API and MCP server, on your sign-in and job runner | 45 min |
| [Embed Beacon on SQL Server with Hangfire](/cookbooks/embed-sqlserver-hangfire/) | The same on SQL Server, with your own Hangfire scheduler | 40 min |
| [Sign in to Beacon with Microsoft Entra ID](/cookbooks/entra-sso/) | Single sign-on for Beacon's UI, with the first admin and a break-glass account | 30 min |

## Monitor & govern

| Recipe | You end up with | Time |
|---|---|---|
| [Alert Slack or Teams when a nightly check fails](/cookbooks/alert-on-failed-check/) | A scheduled check that posts to a channel and opens a task when it finds rows | 20 min |
| [Score a table with a data contract](/cookbooks/data-contract/) | A contract of quality rules, a score on a schedule, and an alert when it drops | 20 min |
| [Ship the MCP audit and security events to your SIEM](/cookbooks/audit-to-siem/) | Every agent call and security event in your log pipeline, with traces and metrics | 30 min |

## Prompts

The [prompt library](/cookbooks/prompts/) has ready-made prompts for coding agents in your repository, for asking your
data questions through Beacon, for people using a gateway application's tools, and for admins.

## Missing a recipe?

Open an [issue](https://github.com/moberghr/beacon/issues) with the goal you had and what you tried. For anything
not covered yet, `llms-full.txt` gives an agent the whole documentation in one file.
