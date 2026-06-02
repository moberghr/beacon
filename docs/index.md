---
layout: home
title: Beacon
nav_order: 1
description: "Semantic database monitoring, alerting, and orchestration for .NET"
permalink: /
---

# Beacon
{: .fs-9 }

Semantic database monitoring, alerting, and orchestration — with a modern React UI, a REST API, and a built-in MCP server.
{: .fs-6 .fw-300 }

[Get Started](getting-started/quick-start){: .btn .btn-primary .fs-5 .mb-4 .mb-md-0 .mr-2 }
[View on GitHub]({{ site.urls.github_repo }}){: .btn .fs-5 .mb-4 .mb-md-0 }

---

## Why Beacon?

Beacon is a .NET 9 platform that turns database monitoring into semantic queries, flexible alerting, and cross-database orchestration. You can run it **two ways**:

- **As a self-hostable app** — clone the repo and run the `Beacon.SampleProject` host, which serves the React UI at the root URL `/`.
- **As NuGet packages** — embed Beacon into your own ASP.NET Core application.

Highlights:

- **9 data sources**: PostgreSQL, SQL Server, MySQL, BigQuery, Snowflake, Databricks, Azure Synapse, AWS CloudWatch, and a generic REST API connector
- **Flexible alerting**: Email, Microsoft Teams, Slack, and Jira notifications with cron scheduling (Hangfire)
- **Query chaining**: Multi-step queries with cross-project and cross-database capabilities (in-memory SQLite joins)
- **Full results as attachments**: Email notifications include complete datasets as CSV for Excel analysis
- **Modern React UI**: React 18 + Vite + TypeScript + Tailwind CSS, served at `/`
- **REST API + MCP server**: `/beacon/api/*` minimal APIs and a Streamable HTTP MCP server at `/beacon/mcp` for AI assistants
- **AI-powered (experimental)**: auto-documentation, natural-language → SQL alerts, and statistical anomaly detection
- **Schema-agnostic**: Multi-tenant support with runtime schema configuration

---

## 🏗️ System Architecture

Beacon follows a CQRS (MediatR) core with all references converging on `Beacon.Core`:

```mermaid
graph TB
    subgraph UI["Presentation Layer"]
        React[React SPA<br/>Vite + TypeScript + Tailwind]
    end

    subgraph Edge["Host Edge — Beacon.SampleProject"]
        RestApi[REST API<br/>/beacon/api/*] ~~~ Mcp[MCP Server<br/>/beacon/mcp] ~~~ Hub[SignalR Hub<br/>/beacon/api/hub]
    end

    subgraph Core["Application Core (MediatR / CQRS)"]
        QuerySvc[Query Service] ~~~ SubSvc[Subscription Service] ~~~ MigSvc[Migration Service]
        NotifSvc[Notification Service] ~~~ DataSrcSvc[DataSource Service] ~~~ AiSvc[AI Service]
    end

    subgraph Adapters["Notification Adapters"]
        EmailAdapter[Email] ~~~ TeamsAdapter[Teams] ~~~ SlackAdapter[Slack] ~~~ JiraAdapter[Jira]
    end

    subgraph Data["Data Access Layer"]
        EFCore[EF Core 9<br/>BeaconContext] ~~~ Dapper[Dapper<br/>Hot Paths]
    end

    subgraph Infrastructure["Infrastructure Layer"]
        Meta[(Metadata DB<br/>PostgreSQL / SQL Server)] ~~~ Sources[(9 Data-Source<br/>Connectors)]
        Scheduler[Hangfire<br/>on PostgreSQL] ~~~ SQLiteVM[In-Memory SQLite<br/>Cross-DB Joins]
    end

    React --> Edge
    Edge --> Core
    Core --> Adapters
    Core --> Data
    Data --> Infrastructure
    Core --> Infrastructure
    Adapters --> Infrastructure

    style UI fill:#e3f2fd
    style Edge fill:#ede7f6
    style Core fill:#f3e5f5
    style Adapters fill:#fff3e0
    style Data fill:#e8f5e9
    style Infrastructure fill:#fce4ec
```

### Query Execution Flow

Multi-step queries with cross-database capabilities:

```mermaid
sequenceDiagram
    participant User
    participant Scheduler as Hangfire Scheduler
    participant QueryOrchestrator
    participant VirtualTableManager
    participant PostgreSQL
    participant SQLServer
    participant MySQL
    participant NotificationService
    participant Adapter

    User->>Scheduler: Create Subscription (Cron)
    Scheduler->>QueryOrchestrator: Execute Query (Scheduled)

    QueryOrchestrator->>PostgreSQL: Execute Step 1
    PostgreSQL-->>QueryOrchestrator: Result Set 1
    QueryOrchestrator->>VirtualTableManager: Store @@result1

    QueryOrchestrator->>SQLServer: Execute Step 2 (with @@result1)
    SQLServer-->>QueryOrchestrator: Result Set 2
    QueryOrchestrator->>VirtualTableManager: Store @@result2

    QueryOrchestrator->>VirtualTableManager: Execute Final Query<br/>(JOIN @@result1 and @@result2)
    VirtualTableManager->>VirtualTableManager: Create in-memory SQLite tables
    VirtualTableManager-->>QueryOrchestrator: Combined Results

    QueryOrchestrator->>NotificationService: Send Results
    NotificationService->>Adapter: Dispatch (Email/Teams/Slack/Jira)
    Adapter-->>User: Notification Delivered
```

### Data Migration Flow

ETL orchestration with multiple migration modes:

```mermaid
flowchart LR
    subgraph Source["Source Extraction"]
        Q1[Query Step 1<br/>PostgreSQL]
        Q2[Query Step 2<br/>SQL Server]
        Q3[Query Step 3<br/>MySQL]
    end

    subgraph Transform["Transformation"]
        VT[In-Memory SQLite<br/>Cross-DB Join]
        Enrich[Data Enrichment<br/>Business Logic]
    end

    subgraph Load["Load Operations"]
        Insert[Insert Only<br/>New Records]
        Upsert[Upsert<br/>Insert + Update]
        Truncate[Truncate Load<br/>Full Refresh]
        Sync[Sync Delete<br/>Perfect Mirror]
    end

    subgraph Destination["Destination"]
        Target[(Target Database<br/>Any Engine)]
    end

    Q1 --> VT
    Q2 --> VT
    Q3 --> VT
    VT --> Enrich
    Enrich --> Insert
    Enrich --> Upsert
    Enrich --> Truncate
    Enrich --> Sync
    Insert --> Target
    Upsert --> Target
    Truncate --> Target
    Sync --> Target

    style Source fill:#e3f2fd
    style Transform fill:#f3e5f5
    style Load fill:#fff3e0
    style Destination fill:#e8f5e9
```

---

## Use Cases

### 🚨 Data Validation Alerts

**Problem**: Teams need to ensure data meets business rules and catch data quality issues early.

**Solution**: Create queries that trigger alerts when data is invalid, missing, or violates constraints — orphaned records, null required fields, invalid state combinations. Also used by DBAs for database-health metrics (table size, connection count, replication lag).

**Benefits**: Early detection, automated data-quality checks, prevent invalid states from reaching production.

[Learn more about alerting →](features/subscriptions)

---

### 📊 Scheduled Reports with Attachments

**Problem**: Teams need automated reports delivered regularly without manual SQL execution.

**Solution**: Schedule queries with cron expressions and receive full results as Excel/CSV attachments via email. Perfect for daily sales reports, weekly analytics, or monthly summaries.

**Benefits**: Zero-touch reporting, full-dataset delivery, Excel-ready format, automated scheduling.

[Learn more about notifications →](features/notifications)

---

### 🔄 Data Migration Orchestration

**Problem**: Teams need auditable data migration across environments and engines.

**Solution**: Data migration jobs with Insert/Upsert/Truncate/Sync modes, execution history, validation checks, and error tracking.

**Benefits**: Compliance audit trail, repeatable workflows, error visibility.

[Learn more about data migrations →](features/data-migration)

---

### 🤖 AI Assistants via MCP

**Problem**: AI assistants need safe, governed access to query your databases.

**Solution**: Beacon's built-in MCP server exposes read-only, PII-aware tools over Streamable HTTP at `/beacon/mcp`, with full audit and a self-improving learning loop.

**Benefits**: Read-only enforcement, row limits, audit trail, and usage-driven improvement.

[Learn more about the MCP server →](features/mcp-server)

---

## Quick Start

### Option A — Run the app

```bash
# 1. Start the API host (Kestrel) — http://localhost:5296 / https://localhost:7187
dotnet run --project Beacon.SampleProject --no-launch-profile

# 2. Start the React dev server (Vite) — http://localhost:5173, proxies /beacon/api
npm run dev --prefix Beacon.UI/web
```

Open `http://localhost:5173` (dev) or `http://localhost:5296` (served build). On first run, Beacon applies its EF Core migrations and walks you through first-run admin setup. Health check: `http://localhost:5296/beacon/api/health`.

### Option B — Embed via NuGet

```bash
dotnet add package Beacon.Core.PostgreSql
dotnet add package Beacon.UI
```

```csharp
using Beacon.Core;
using Beacon.Core.PostgreSql;
using Beacon.UI;

builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<BeaconScheduler>(); // Hangfire-backed IBeaconScheduler
        options.UseAI = true;                          // optional, experimental
    })
    .AddPostgreSqlConnector()
    .UsePostgreSql(builder.Configuration.GetConnectionString("BeaconContext")!, "beacon");

builder.Services.AddBeaconCookieAuthentication("/");

var app = builder.Build();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.MapBeaconApi();   // /beacon/api/*
app.MapBeaconUi();    // React SPA at root /
app.Run();
```

**appsettings.json:**
```json
{
  "ConnectionStrings": {
    "BeaconContext": "Host=localhost;Database=beacon;Username=postgres;Password=yourpassword"
  },
  "Beacon": {
    "EncryptionKey": "your-secure-32-character-key-here"
  }
}
```

[View the detailed installation guide →](getting-started/installation)

---

## Features

### Core Capabilities

- **[Data Sources](features/data-sources)**: Manage encrypted connections across 9 engines
- **[Queries](features/queries)**: Define SQL queries with parameters and multi-step chaining
- **[Subscriptions](features/subscriptions)**: Schedule execution with cron expressions
- **[Notifications](features/notifications)**: Deliver results via Email, Teams, Slack, or Jira
- **[Data Migrations](features/data-migration)**: Orchestrate ETL with Insert/Upsert/Truncate/Sync
- **[Anomaly Detection](features/anomaly-detection)**: Statistical baselines and deviation alerts

### Platform & Admin

- **[User Management](features/user-management)**: Internal/external users, role-based access, first-run setup
- **[Authorization](features/authorization)**: Pluggable auth providers, cookie sessions, OIDC/SSO, API keys
- **[Admin Settings](features/admin-settings)**: Runtime configuration, hot-swap LLM providers, audit trail
- **[MCP Server](features/mcp-server)**: Read-only, audited database access for AI assistants
- **[AI Integration](features/ai-integration)** (experimental): Auto-documentation and NL → SQL alerts

---

## Documentation

<div class="code-example" markdown="1">
### 🚀 Getting Started

New to Beacon? Start here.

- [Installation Guide](getting-started/installation)
- [Quick Start](getting-started/quick-start)
- [Configuration Reference](getting-started/configuration)
</div>

<div class="code-example" markdown="1">
### 📖 Features

Explore all capabilities with detailed guides and examples.

- [Data Sources](features/data-sources)
- [Queries](features/queries)
- [Subscriptions](features/subscriptions)
- [Notifications](features/notifications)
- [See all features →](features/)
</div>

<div class="code-example" markdown="1">
### 💬 Support

Get help and contribute to the project.

- [GitHub Issues]({{ site.urls.github_issues }})
- [GitHub Discussions]({{ site.urls.github_discussions }})
</div>

---

## Key Features at a Glance

| Feature | Description |
|---------|-------------|
| **9 Data Sources** | PostgreSQL, SQL Server, MySQL, BigQuery, Snowflake, Databricks, Azure Synapse, CloudWatch, REST API |
| **React UI** | React 18 + Vite + TypeScript + Tailwind, served at `/` |
| **REST API** | `/beacon/api/*` minimal APIs (one endpoint per MediatR handler), OpenAPI at `/openapi/v1.json` |
| **MCP Server** | Read-only, audited database access for AI assistants at `/beacon/mcp` |
| **Cron Scheduling** | Hangfire on PostgreSQL with flexible cron expressions |
| **Multi-Step Queries** | Chain queries with result aggregation and cross-DB joins |
| **Notifications** | Email (with CSV attachments), Teams, Slack, Jira delivery |
| **Encrypted Secrets** | Connection strings encrypted at rest (AES-256); API keys SHA256-hashed |
| **User Management** | Built-in users, roles, OIDC/SSO, API keys, first-run setup |
| **Admin Settings** | Runtime config, hot-swap LLM providers, audit trail |
| **Schema-Agnostic** | Multi-tenant deployments with runtime schema selection |
| **Execution History** | Complete audit trail of all executions |

---

## Requirements

- **.NET 9.0** SDK or later
- **Node.js 18+** (for building the React UI when developing or self-hosting)
- **PostgreSQL 12+** or **SQL Server 2019+** for Beacon's metadata database
- **Encryption key** (32-character key for AES-256) — **required** (`Beacon:EncryptionKey`)
- **(Optional)** LLM API key (OpenAI, Anthropic, Azure OpenAI, or AWS Bedrock) for AI features
- **(Optional)** SMTP/email provider for email notifications

---

## Community and Support

- **GitHub Repository**: [MiBu/semantico]({{ site.urls.github_repo }})
- **Report Issues**: [GitHub Issues]({{ site.urls.github_issues }})
- **Discussions**: [GitHub Discussions]({{ site.urls.github_discussions }})

---

**Built for .NET developers who need powerful database monitoring and alerting**
</content>
</invoke>
