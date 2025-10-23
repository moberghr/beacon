# Semantico

[![NuGet](https://img.shields.io/badge/NuGet-available-blue)](https://www.nuget.org/)
[![Documentation](https://img.shields.io/badge/docs-github.io-blue)](https://moberghr.github.io/semantico)
[![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-9.0-purple)](https://dotnet.microsoft.com/)

Semantico is a powerful .NET library designed to provide semantic alerts and notifications for your databases.

With Semantico, you can create projects with their respective connection strings to the database (PostgreSQL, MS SQL, or MySQL), and set up personalized queries with scheduled execution and flexible notification delivery through a Blazor-based admin UI.

## 🚀 Quick Start

Add Semantico to your ASP.NET application in under 30 minutes:

1. **Install NuGet packages**
   ```bash
   dotnet add package Semantico.Core.PostgreSql
   dotnet add package Semantico.UI.AspNet
   ```

2. **Configure in Program.cs**
   ```csharp
   builder.Services.AddPostgreSqlSemantico(
       builder.Configuration.GetConnectionString("SemanticoContext")!,
       schema: "semantico");

   builder.Services.AddSemanticoAdmin(builder.Configuration, options =>
   {
       options.AddSemanticoScheduler<YourScheduler>();
   });
   ```

3. **Add UI and run migrations**
   ```csharp
   app.UseSemanticoUI()
       .UseBasicAuthentication("admin", "admin")
       .AddBlazorUI("/semantico");

   ServiceConfiguration.UseSemantico(app.Services);
   ```

4. **Access the UI** at `http://localhost:5000/semantico`

📚 [View detailed quick start guide →](https://moberghr.github.io/semantico/getting-started/quick-start)

## ✨ Key Features

- **Multi-Database Support** - Connect to PostgreSQL, SQL Server, and MySQL databases
- **Flexible Alerting** - Schedule queries with cron expressions for precise timing
- **Query Chaining** - Build multi-step queries with cross-project and cross-database capabilities
- **Notification Channels** - Deliver results via Email, Microsoft Teams, or Jira
- **Full Results as Attachments** - Send complete query results as Excel/CSV attachments for reporting
- **Query Parameters** - Use dynamic placeholders for flexible query definitions
- **Data Migration** - Orchestrate and track schema migrations with full audit history
- **Blazor Admin UI** - Modern, responsive UI built with MudBlazor
- **Schema-Agnostic** - Support multi-tenant deployments with runtime schema configuration

[Explore all features →](https://moberghr.github.io/semantico/features/)

## 📦 Installation

### NuGet Packages

Semantico is distributed as NuGet packages. Install the database provider package for your needs:

**For PostgreSQL (recommended):**
```bash
dotnet add package Semantico.Core.PostgreSql
dotnet add package Semantico.UI.AspNet
```

**For SQL Server:**
```bash
dotnet add package Semantico.Core.SqlServer
dotnet add package Semantico.UI.AspNet
```

### Basic Setup

Add to your ASP.NET Core `Program.cs`:

```csharp
using Semantico.Core.PostgreSql;
using Semantico.UI.AspNet;

var builder = WebApplication.CreateBuilder(args);

// Configure Semantico with PostgreSQL
builder.Services.AddPostgreSqlSemantico(
    builder.Configuration.GetConnectionString("SemanticoContext")!,
    schema: "semantico");

// Add Semantico admin UI
builder.Services.AddSemanticoAdmin(builder.Configuration, options =>
{
    options.AddSemanticoScheduler<YourHangfireScheduler>();
});

var app = builder.Build();

// Configure Semantico UI
app.UseSemanticoUI()
    .UseBasicAuthentication("admin", "admin")
    .UseAuthorization()
    .AddBlazorUI("/semantico");

// Run migrations
ServiceConfiguration.UseSemantico(app.Services);

app.Run();
```

Add connection string to `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "SemanticoContext": "Host=localhost;Database=semantico;Username=postgres;Password=yourpassword"
  }
}
```

📚 [View detailed installation guide →](https://moberghr.github.io/semantico/getting-started/installation)

## 📖 Documentation

- **Getting Started**
  - [Installation Guide](https://moberghr.github.io/semantico/getting-started/installation) - NuGet package setup
  - [Quick Start](https://moberghr.github.io/semantico/getting-started/quick-start) - First query in 30 minutes
  - [Configuration](https://moberghr.github.io/semantico/getting-started/configuration) - Connection strings and options

- **Features**
  - [Projects](https://moberghr.github.io/semantico/features/projects) - Database connection management
  - [Queries](https://moberghr.github.io/semantico/features/queries) - Query creation and parameters
  - [Multi-Step Queries](https://moberghr.github.io/semantico/features/multi-step-queries) - Advanced query chaining
  - [Subscriptions](https://moberghr.github.io/semantico/features/subscriptions) - Scheduled execution
  - [Notifications](https://moberghr.github.io/semantico/features/notifications) - Email, Teams, Jira delivery

- **Advanced**
  - [Query Chaining](https://moberghr.github.io/semantico/advanced/query-chaining) - Cross-project queries
  - [Multi-Tenant Deployments](https://moberghr.github.io/semantico/advanced/multi-tenant) - Schema-agnostic configuration
  - [Architecture](https://moberghr.github.io/semantico/advanced/architecture) - Clean Architecture deep-dive

- **Reference**
  - [API Services](https://moberghr.github.io/semantico/api/services) - Service interfaces
  - [Troubleshooting](https://moberghr.github.io/semantico/troubleshooting/common-issues) - Common issues and solutions

## 🎯 Use Cases

### Data Validation Alerts
Developers create queries that alert when data doesn't meet expected criteria - invalid states, missing required data, or business rule violations. DBAs can also use this for database health monitoring (table size, connection count, replication lag).

### Scheduled Reports with Attachments
Generate and deliver automated reports with full query results as Excel or CSV attachments. Perfect for daily sales reports, weekly summaries, or monthly analytics delivered directly to stakeholders' inboxes.

### Cross-Database Reporting
Aggregate data from PostgreSQL, SQL Server, and MySQL into unified reports with multi-step queries and automated delivery.

### Data Migration Orchestration
Track data migrations across environments with execution history and validation checks for compliance audit trails.

## 🔧 Requirements

- **.NET 9.0** or later
- **PostgreSQL 12+** or **SQL Server 2019+** for Semantico metadata database
- **Hangfire** (for job scheduling) - you must configure this in your application
- **(Optional)** Email provider for email notifications (built-in support for any SMTP-compatible service)

## 🤝 Support and Contributing

- **Issues** - [Report bugs or request features](https://github.com/moberghr/semantico/issues)
- **Discussions** - [Ask questions and share ideas](https://github.com/moberghr/semantico/discussions)
- **Contributing** - [Contribution guidelines](https://moberghr.github.io/semantico/contributing/guidelines)

Thank you for choosing Semantico! We hope you find it invaluable for managing your database alerts and notifications.

---

**Documentation**: [https://moberghr.github.io/semantico](https://moberghr.github.io/semantico)
**Repository**: [https://github.com/moberghr/semantico](https://github.com/moberghr/semantico)
