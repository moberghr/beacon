# Semantico Feature Roadmap

## Current Phase (In Progress)

### 006 - Query Versioning & Editing
Track query changes over time with full version history, diff views, and rollback capability.
- **Status:** Planning
- **Priority:** High
- **Spec:** `specs/006-query-versioning-approval-webhook/`

### 007 - Approval Workflows
Require approval before query/subscription changes go live. Role-based approval chains.
- **Status:** Planning
- **Priority:** High
- **Spec:** `specs/006-query-versioning-approval-webhook/`

### 008 - Webhook Notification Adapter
Generic HTTP webhook adapter for integrating Semantico with any external system.
- **Status:** Planning
- **Priority:** High
- **Spec:** `specs/006-query-versioning-approval-webhook/`

---

## Next Phase (Post-Current)

### Custom Dashboards
Let users build visual dashboards from query results (charts, KPIs, gauges).
- Visual dashboard builder
- Chart types: line, bar, pie, gauge, KPI cards
- Dashboard sharing and permissions
- Auto-refresh on schedule

### Query Health Overview ("Control Tower")
A single page showing all subscriptions, their last run status, anomaly trends, and task counts.
- Subscription health grid with RAG status
- Anomaly trend sparklines
- Task count badges per subscription
- Filter by data source, folder, status

---

## Future Ideas (Backlog)

### Observability & Dashboards
- **Result count trend visualization** — Already partially exists in tasks; extend to dashboards

### Notification & Alerting Enhancements
- **Escalation chains** — If a task isn't resolved within X hours, escalate to different recipient/channel
- **Notification digest** — Aggregate multiple alerts into daily/weekly digest
- **PagerDuty / Opsgenie / Discord adapters** — Expand notification channels
- **Notification templates** — User-customizable notification formatting

### Query & Data
- **Query templates/library** — Pre-built queries for common checks (row count drift, null rate, freshness, schema changes)
- **Data quality scoring** — Automated scoring based on completeness, consistency, timeliness
- **Query dependency graph** — Visualize which queries depend on which data sources and parameters

### AI Enhancements
- **AI-powered root cause analysis** — When anomaly fires, AI auto-investigates and suggests cause
- **Natural language query builder** — Plain English to SQL via AI
- **Smart recommendations** — "You monitor table X but not Y, which has similar patterns"

### Collaboration & Workflow
- **Audit log UI** — Full audit trail for all configuration changes
- **Team/workspace support** — Group users, queries, subscriptions by team

### Integration & Extensibility
- **REST API exposure** — Expose Semantico features as REST API for external tool integration
- **Plugin/extension system** — Custom notification adapters or query transformers without modifying core
- **dbt integration** — Monitor dbt model freshness and test results
- **Grafana data source** — Let Grafana pull from Semantico query results
