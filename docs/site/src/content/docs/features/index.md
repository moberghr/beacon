---
title: Features
description: A map of every Beacon feature page — monitoring and moving data, the AI and MCP layer, and governance and security.
---

Every feature page, grouped as in the sidebar. New to Beacon? Read [Core concepts](/getting-started/concepts/) first,
then the [Quick start](/getting-started/quick-start/).

## Monitor & move data

| Page | What it covers |
|---|---|
| [Data sources](/features/data-sources/) | Nine connectors (PostgreSQL, SQL Server, MySQL, Azure Synapse, Snowflake, BigQuery, Databricks, AWS CloudWatch, REST API), encrypted connections, metadata, and per-engine read-only guarantees. |
| [Queries](/features/queries/) | Saved, versioned SQL: steps across data sources, bound `{name}` parameters, an in-memory final query, and optional change review. |
| [Subscriptions](/features/subscriptions/) | Run a query on a cron schedule as its owner, decide which runs notify, and choose what each notification carries. |
| [Notifications](/features/notifications/) | Five channels — Email, Teams, Slack, Jira and Webhook — to vetted recipients, with a delivery history. |
| [Control Tower](/features/control-tower/) | Health across every subscription: success rates, health buckets, open tasks and anomaly sparklines. |
| [Tasks](/features/tasks/) | Work items a subscription opens while its query returns rows, resolved automatically when the data recovers. |
| [Anomaly detection](/features/anomaly-detection/) | Notify only when a run's row count is unusual against its own history. |
| [Data quality](/features/data-quality/) | Data contracts with rule-based checks, a quality score and alerts, on PostgreSQL, SQL Server and MySQL. |
| [Data migration](/features/data-migration/) | Copy rows from a read source into a database an Admin marked as a write target. Off unless the host enables it. |

## AI & MCP

| Page | What it covers |
|---|---|
| [MCP server](/features/mcp-server/) | The governed MCP endpoint at `/beacon/mcp`: eight built-in tools, project scoping, read-only SQL and PII masking. |
| [Entra ID callers](/features/mcp-entra-callers/) | Calling MCP with Microsoft Entra ID tokens, on behalf of a user or as a system identity. |
| [Saved query tools](/features/saved-query-tools/) | Approved saved queries exposed as named, read-only MCP tools. |
| [Host endpoint tools](/features/host-endpoint-tools/) | Selected host endpoints exposed as MCP tools, run in-process as the caller. |
| [Host DbContext](/features/host-dbcontext/) | The host's own EF Core model as a read-only data source with an allow-list and masking. |
| [Host docs](/features/host-docs/) | The host's markdown documentation imported into a project and searchable over MCP. |
| [Knowledge base & grounding](/features/knowledge-base/) | What Beacon gives the model before it writes SQL: schema, relationships, glossary, golden examples, learned patterns. |
| [AI integration](/features/ai-integration/) | Experimental: generated data source documentation and natural-language alerts through a swappable LLM provider. |
| [AI actors](/features/ai-actors/) | Experimental: LLM-driven monitoring agents whose plans a person approves before anything runs. |
| [MCP observability & audit](/features/mcp-observability-and-audit/) | The audit row behind every MCP tool call, trace correlation and OpenTelemetry export. |

## Govern & secure

| Page | What it covers |
|---|---|
| [Security model](/features/security-model/) | The trust boundaries, deployment locks and guarantees in one place. |
| [Authorization](/features/authorization/) | Projects as the access boundary, project roles and actions, functional permissions, and host seams. |
| [Approvals](/features/approvals/) | The opt-in four-eyes review of query changes, migration jobs, Custom SQL rules and recipient destinations. |
| [User management](/features/user-management/) | Users, roles, password and SSO sign-in, and the first-run setup. |
| [API keys](/features/api-keys/) | Read and Execute keys bound to their owner, with project lists and a mandatory expiry, stored hashed. |
| [Admin settings](/features/admin-settings/) | Runtime settings: the LLM provider, the base URL, and their change history. |

For configuration keys see [Configuration](/getting-started/configuration/); to embed Beacon in your own application
see [Integrate Beacon](/integrate/).
