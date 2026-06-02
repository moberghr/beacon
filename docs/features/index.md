---
layout: default
title: Features
nav_order: 3
has_children: true
---

# Features

Comprehensive guides for all Beacon capabilities.

## Core Features

### [Data Sources](data-sources)
Manage connections across nine connectors — PostgreSQL, SQL Server, MySQL, Google BigQuery, Snowflake, Databricks, Azure Synapse, AWS CloudWatch, and a generic REST API. Organize them into projects for your monitoring queries.

### [Queries](queries)
Define SQL queries with parameter support. Create simple queries or complex multi-step workflows.

### [Subscriptions](subscriptions)
Schedule query execution with cron expressions. Control when and how often your queries run.

### [Notifications](notifications)
Deliver query results via Email, Microsoft Teams, Slack, or Jira. Configure recipients and notification formats.

### [Data Migration](data-migration)
Extract, transform, and load data between databases using multi-step ETL pipelines. Support for cross-database migrations with scheduling and retry capabilities.

### [MCP Server](mcp-server)
Connect AI assistants (Claude, Cursor, Windsurf) to your data through the Model Context Protocol. Natural language queries, schema search, and documentation — all project-scoped with API key authentication and a self-improving learning loop.

## Advanced Features

### [User Management](user-management)
Built-in user management with internal (password) and external (JWT/OAuth) authentication. First-run setup wizard, role assignment, and account administration.

### [Admin Settings](admin-settings)
Runtime application configuration through the Admin Settings UI. Hot-swap LLM providers, manage base URL, and view change history - all without restarting.

### [Authorization](authorization)
Secure your Beacon installation with flexible authorization. Support for role-based access control (RBAC), database-backed roles, and custom authorization providers. Integrate with any authentication system.

### [Anomaly Detection](anomaly-detection)
Statistical anomaly detection that learns baselines from historical execution data and alerts on unusual patterns.

### [AI Integration (Experimental)](ai-integration)
Auto-generate data source documentation with ERD diagrams and turn natural language into SQL alerts using a runtime-swappable LLM provider.

## Quick Links

<div class="code-example" markdown="1">
🚀 **New to Beacon?**

Start with the [Quick Start Guide](../getting-started/quick-start) to create your first alert in 30 minutes.
</div>

<div class="code-example" markdown="1">
🔗 **Advanced Use Cases?**

See [Queries](queries) for multi-step query chaining and cross-database joins, and [Data Sources](data-sources) for schema-agnostic, multi-tenant configuration.
</div>
