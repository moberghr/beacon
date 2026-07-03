# Beacon — Feature Brainstorm

> Living document for brainstorming future features. Once we pick a winner, we'll create a proper spec & plan.

---

## Already Shipped

- ~~Custom Dashboards~~ (visual dashboard builder, charts, sharing, auto-refresh)
- ~~Query Health Overview / "Control Tower"~~ (subscription health grid, anomaly sparklines, task badges)
- ~~Data Quality Scoring~~ (automated scoring — completeness, consistency, timeliness)
- Query Versioning & Editing (006)
- Approval Workflows (007)
- Webhook Notification Adapter (008)

---

## Ideas to Discuss

### Notification & Alerting Enhancements
- **Escalation chains** — If a task isn't resolved within X hours, escalate to a different recipient/channel
- **Notification digest** — Aggregate multiple alerts into a daily/weekly digest email
- **PagerDuty / Opsgenie / Discord adapters** — Expand notification channels beyond webhooks
- **Notification templates** — User-customizable notification formatting (Liquid/Handlebars?)

### Query & Data
- **Query templates / library** — Pre-built queries for common checks (row count drift, null rate, freshness, schema changes)
- **Query dependency graph** — Visualize which queries depend on which data sources and parameters

### AI Enhancements
- **AI-powered root cause analysis** — When an anomaly fires, AI auto-investigates and suggests a cause
- **Natural language query builder** — Plain English → SQL via AI
- **Smart recommendations** — "You monitor table X but not Y, which has similar patterns"

### Collaboration & Workflow
- **Audit log UI** — Full audit trail for all configuration changes
- **Team / workspace support** — Group users, queries, subscriptions by team

### Integration & Extensibility
- **REST API exposure** — Expose Beacon features as a REST API for external tool integration
- **Plugin / extension system** — Custom notification adapters or query transformers without modifying core
- **dbt integration** — Monitor dbt model freshness and test results
- **Grafana data source** — Let Grafana pull from Beacon query results

### Observability
- **Result count trend visualization** — Extend existing task-level trends to full dashboards

---

## New Ideas (Add here!)

_Drop any new thoughts below — no idea too wild at this stage._

-
