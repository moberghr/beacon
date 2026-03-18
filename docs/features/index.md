---
layout: default
title: Features
nav_order: 3
has_children: true
---

# Features

Comprehensive guides for all Semantico capabilities.

## Core Features

### [Projects](projects)
Manage database connections to PostgreSQL, SQL Server, and MySQL. Create projects to organize your monitoring queries.

### [Queries](queries)
Define SQL queries with parameter support. Create simple queries or complex multi-step workflows.

### [Subscriptions](subscriptions)
Schedule query execution with cron expressions. Control when and how often your queries run.

### [Notifications](notifications)
Deliver query results via Email, Microsoft Teams, or Jira. Configure recipients and notification formats.

### [Data Migration](data-migration)
Extract, transform, and load data between databases using multi-step ETL pipelines. Support for cross-database migrations with scheduling and retry capabilities.

### [MCP Server](mcp-server)
Connect AI assistants (Claude, Cursor, Windsurf) to your data through the Model Context Protocol. Natural language queries, schema search, and documentation — all project-scoped with API key authentication.

## Advanced Features

### [User Management](user-management)
Built-in user management with internal (password) and external (JWT/OAuth) authentication. First-run setup wizard, role assignment, and account administration.

### [Admin Settings](admin-settings)
Runtime application configuration through the Admin Settings UI. Hot-swap LLM providers, manage base URL, and view change history - all without restarting.

### [Authorization](authorization)
Secure your Semantico installation with flexible authorization. Support for role-based access control (RBAC), database-backed roles, and custom authorization providers. Integrate with any authentication system.

### [Multi-Step Queries](multi-step-queries)
Chain multiple queries together and aggregate results. Query across different databases in a single workflow.

### [Query Parameters](parameters)
Use dynamic placeholders in your queries for flexible monitoring and reporting.

### [Recipients](recipients)
Manage notification targets for email, Teams, and Jira delivery channels.

## Quick Links

<div class="code-example" markdown="1">
🚀 **New to Semantico?**

Start with the [Quick Start Guide](../getting-started/quick-start) to create your first alert in 30 minutes.
</div>

<div class="code-example" markdown="1">
🔗 **Advanced Use Cases?**

Explore [Advanced Topics](../advanced/) for query chaining, multi-tenant deployments, and extensibility.
</div>

<div class="code-example" markdown="1">
🔧 **Having Issues?**

Check the [Troubleshooting Guide](../troubleshooting/common-issues) for solutions.
</div>
