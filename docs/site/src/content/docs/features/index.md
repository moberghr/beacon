---
title: Features
description: Reference guides for Beacon's data sources, queries, subscriptions, notifications, migration, and operations tooling.
---

Reference guides for Beacon's capabilities.

## Core Features

### [Data Sources](/features/data-sources/)
Manage connections across nine connectors — PostgreSQL, SQL Server, MySQL, Google BigQuery, Snowflake, Databricks, Azure Synapse, AWS CloudWatch, and a generic REST API. Organize them into projects for your monitoring queries.

### [Queries](/features/queries/)
Define SQL queries with parameter support. Create simple queries or multi-step workflows.

### [Subscriptions](/features/subscriptions/)
Schedule query execution with cron expressions. Control when and how often your queries run.

### [Notifications](/features/notifications/)
Deliver query results via Email, Microsoft Teams, Slack, or Jira. Configure recipients and notification formats.

### [Data Migration](/features/data-migration/)
Extract, transform, and load data between databases using multi-step ETL pipelines. Supports cross-database migrations with scheduling and retry.

### [MCP Server](/features/mcp-server/)
Connect AI assistants (Claude, Cursor, Windsurf) to your data through the Model Context Protocol. Natural language queries, schema search, and documentation — all project-scoped with API key authentication, M-Schema-grounded SQL generation, AST read-only validation, and a self-improving learning loop.

## Operations & Quality

### [Control Tower](/features/control-tower/)
A real-time operations view across every scheduled check: health buckets, success rates, anomaly sparklines, and open-task counts — auto-refreshing.

### [Tasks](/features/tasks/)
Alerting tasks created automatically when a check finds problems, tracked through to resolution and auto-resolved when the data recovers.

### [Data Quality](/features/data-quality/)
Define data contracts and evaluate them on a schedule, with scoring and evaluation history.

## Advanced Features

### [User Management](/features/user-management/)
Built-in user management with internal (password) and external (JWT/OAuth) authentication. First-run setup wizard, role assignment, and account administration.

### [Admin Settings](/features/admin-settings/)
Runtime application configuration through the Admin Settings UI. Hot-swap LLM providers, manage base URL, and view change history — all without restarting.

### [Authorization](/features/authorization/)
Flexible authorization for your Beacon installation. Supports role-based access control (RBAC), database-backed roles, and custom authorization providers, and integrates with any authentication system.

### [Anomaly Detection](/features/anomaly-detection/)
Statistical anomaly detection that learns baselines from historical execution data and alerts on unusual patterns.

### [API Keys](/features/api-keys/)
Scoped API keys (`Read`, `Execute`, `Admin`) with optional per-project restrictions and expiry — SHA256-hashed at rest, shown exactly once.

### [AI Integration (Experimental)](/features/ai-integration/)
Auto-generate data source documentation with ERD diagrams and turn natural language into SQL alerts using a runtime-swappable LLM provider.

### [AI Actors (Experimental)](/features/ai-actors/)
LLM-driven monitoring agents whose plans pass through a human approval workflow before anything executes.

## Quick Links

New to Beacon? Start with the [Quick Start Guide](/getting-started/quick-start/) to create your first alert.

For advanced use, see [Queries](/features/queries/) for multi-step query chaining and cross-database joins, and [Data Sources](/features/data-sources/) for schema-agnostic, multi-tenant configuration.
