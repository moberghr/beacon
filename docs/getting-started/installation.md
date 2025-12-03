---
layout: default
title: Installation
parent: Getting Started
nav_order: 1
---

# Installation Guide

Add Semantico to your ASP.NET Core application in under 10 minutes.

## Prerequisites

Before you begin, ensure you have:

- **.NET 9.0 SDK** or later installed
- **ASP.NET Core** web application project
- **PostgreSQL 12+** or **SQL Server 2019+** database for Semantico metadata
- **Job scheduler** implementing `ISemanticoScheduler` interface (e.g., Hangfire, Quartz.NET, or custom implementation)
- **(Optional)** Email provider (SMTP) for email notifications

## Quick Installation

### Step 1: Install NuGet Packages

Install the database provider package for your Semantico metadata database:

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

{: .note }
> **Note**: The provider you choose is for Semantico's metadata database. You can still query PostgreSQL, SQL Server, and MySQL databases for monitoring regardless of which provider you choose.

### Step 2: Configure Connection String

Add the Semantico connection string to `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "SemanticoContext": "Host=localhost;Database=semantico;Username=postgres;Password=yourpassword"
  }
}
```

**PostgreSQL example:**
```json
"SemanticoContext": "Host=localhost;Port=5432;Database=semantico;Username=semantico;Password=secretpass"
```

**SQL Server example:**
```json
"SemanticoContext": "Server=localhost;Database=semantico;User Id=semantico;Password=secretpass;TrustServerCertificate=True"
```

### Step 3: Configure a Job Scheduler

Semantico requires a job scheduler that implements `ISemanticoScheduler`. The example below uses Hangfire, but you can use any scheduler (Quartz.NET, custom implementation, etc.).

**Example using Hangfire:**

```csharp
using Hangfire;
using Hangfire.PostgreSql;

builder.Services.AddHangfire((provider, hangfireConfiguration) => hangfireConfiguration
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_170)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseFilter(new AutomaticRetryAttribute { Attempts = 0 })
    .UsePostgreSqlStorage(
        builder.Configuration.GetConnectionString("SemanticoContext"),
        new PostgreSqlStorageOptions
        {
            PrepareSchemaIfNecessary = true,
            QueuePollInterval = TimeSpan.FromSeconds(1),
        }));

builder.Services.AddHangfireServer();
```

📚 [See Hangfire documentation](https://www.hangfire.io/) | You can also use Quartz.NET, custom implementations, or any scheduler that can call `IJobService.ExecuteQuery()`

### Step 4: Configure Semantico Services

Add Semantico to your `Program.cs`:

```csharp
using Semantico.Core;
using Semantico.Core.PostgreSql;
using Semantico.UI.AspNet;

var builder = WebApplication.CreateBuilder(args);

// Configure Semantico with PostgreSQL
builder.Services.AddPostgreSqlSemantico(
    builder.Configuration.GetConnectionString("SemanticoContext")!,
    schema: "semantico");

// Add Semantico admin UI with scheduler
builder.Services.AddSemanticoAdmin(builder.Configuration, options =>
{
    options.AddSemanticoScheduler<SemanticoScheduler>();
    options.AddAuthorizationProvider<YourAuthorizationProvider>(); // Optional
});
```

**For SQL Server:**
```csharp
using Semantico.Core.SqlServer;

builder.Services.AddSqlServerSemantico(
    builder.Configuration.GetConnectionString("SemanticoContext")!,
    schema: "semantico");
```

### Step 5: Implement ISemanticoScheduler

Create a scheduler implementation. This example uses Hangfire, but you can adapt it to any job scheduler:

```csharp
using Hangfire;
using Semantico.Core.Worker;

public class SemanticoScheduler : ISemanticoScheduler
{
    private readonly IRecurringJobManager _recurringJobManager;

    public SemanticoScheduler(IRecurringJobManager recurringJobManager)
    {
        _recurringJobManager = recurringJobManager;
    }

    public void AddOrUpdate(int subscriptionId, string subscriptionName, string cron)
    {
        var jobKey = $"{subscriptionId} - {subscriptionName}";
        _recurringJobManager.AddOrUpdate<IJobService>(
            jobKey,
            x => x.ExecuteQuery(subscriptionId),
            cron);
    }

    public void Remove(int subscriptionId, string subscriptionName)
    {
        var jobKey = $"{subscriptionId} - {subscriptionName}";
        _recurringJobManager.RemoveIfExists(jobKey);
    }
}
```

### Step 6: Configure Semantico UI

Add Semantico UI middleware to your application pipeline:

```csharp
var app = builder.Build();

// Configure Semantico UI with basic authentication
app.UseSemanticoUI()
    .UseBasicAuthentication("admin", "admin")
    .UseAuthorization()
    .AddBlazorUI("/semantico");

// Optional: Hangfire dashboard
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    IgnoreAntiforgeryToken = true,
});

// Run Semantico database migrations
ServiceConfiguration.UseSemantico(app.Services);

app.Run();
```

### Step 7: Run Your Application

```bash
dotnet run
```

Access Semantico UI at: `http://localhost:5000/semantico`

## Complete Program.cs Example

Here's a complete example from the Semantico.SampleProject:

```csharp
using Hangfire;
using Hangfire.PostgreSql;
using Semantico.Core;
using Semantico.Core.PostgreSql;
using Semantico.UI.AspNet;

var builder = WebApplication.CreateBuilder(args);

// Configure Hangfire
builder.Services.AddHangfire((provider, hangfireConfiguration) => hangfireConfiguration
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_170)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseFilter(new AutomaticRetryAttribute { Attempts = 0 })
    .UsePostgreSqlStorage(
        builder.Configuration.GetConnectionString("SemanticoContext"),
        new PostgreSqlStorageOptions
        {
            PrepareSchemaIfNecessary = true,
            QueuePollInterval = TimeSpan.FromSeconds(1),
        }));

builder.Services.AddHangfireServer();

// Configure Semantico with PostgreSQL
builder.Services.AddPostgreSqlSemantico(
    builder.Configuration.GetConnectionString("SemanticoContext")!,
    schema: "semantico");

// Add Semantico admin UI
builder.Services.AddSemanticoAdmin(builder.Configuration, options =>
{
    options.AddSemanticoScheduler<SemanticoScheduler>();
    options.AddAuthorizationProvider<SampleAuthorizationProvider>();
});

var app = builder.Build();

app.UseHttpsRedirection();
app.UseAuthorization();

// Configure Semantico UI
app.UseSemanticoUI()
    .UseBasicAuthentication("admin", "admin")
    .UseAuthorization()
    .AddBlazorUI("/semantico");

// Optional: Hangfire dashboard
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    IgnoreAntiforgeryToken = true,
});

// Run migrations
ServiceConfiguration.UseSemantico(app.Services);

app.Run();
```

## Optional: Custom Authorization

Implement `ISemanticoAuthorizationProvider` for custom permissions:

```csharp
using Semantico.UI.AspNet.Authentication;

public class SampleAuthorizationProvider : ISemanticoAuthorizationProvider
{
    public Task<bool> HasReadPermissionAsync(string username)
    {
        // Implement your logic - check database, AD, etc.
        return Task.FromResult(true);
    }

    public Task<bool> HasWritePermissionAsync(string username)
    {
        // Only admins can modify
        return Task.FromResult(username == "admin");
    }
}
```

Register in configuration:

```csharp
builder.Services.AddSemanticoAdmin(builder.Configuration, options =>
{
    options.AddSemanticoScheduler<SemanticoScheduler>();
    options.AddAuthorizationProvider<SampleAuthorizationProvider>();
});
```

## Optional: Custom Email Adapter

Implement `IEmailAdapter` for your email provider (SMTP, SendGrid, AWS SES, etc.):

```csharp
using Semantico.Core.Adapters.Mail;
using System.Net.Mail;

public class SmtpEmailAdapter : IEmailAdapter
{
    private readonly IConfiguration _configuration;

    public SmtpEmailAdapter(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task SendEmailAsync(string to, string subject, string body, QueryResultFile? queryResultAttachmentFile)
    {
        using var smtpClient = new SmtpClient
        {
            Host = _configuration["Email:SmtpHost"],
            Port = int.Parse(_configuration["Email:SmtpPort"]),
            EnableSsl = true,
            Credentials = new NetworkCredential(
                _configuration["Email:Username"],
                _configuration["Email:Password"])
        };

        var message = new MailMessage
        {
            From = new MailAddress(
                _configuration["Email:FromAddress"],
                _configuration["Email:FromName"]),
            Subject = subject,
            Body = body,
            IsBodyHtml = true
        };

        message.To.Add(to);

        // Add CSV attachment if provided
        if (queryResultAttachmentFile != null)
        {
            var attachmentStream = new MemoryStream(queryResultAttachmentFile.Data);
            message.Attachments.Add(new Attachment(
                attachmentStream,
                queryResultAttachmentFile.Name,
                queryResultAttachmentFile.ContentType));
        }

        await smtpClient.SendMailAsync(message);
    }
}
```

Register in configuration:

```csharp
builder.Services.AddSemanticoAdmin(builder.Configuration, options =>
{
    options.AddSemanticoScheduler<SemanticoScheduler>();
    options.AddEmailAdapter<SmtpEmailAdapter>();
});
```

## Database Setup

### Create Semantico Database

**PostgreSQL:**
```sql
CREATE DATABASE semantico;
CREATE USER semantico WITH PASSWORD 'secretpassword';
GRANT ALL PRIVILEGES ON DATABASE semantico TO semantico;
```

**SQL Server:**
```sql
CREATE DATABASE semantico;
CREATE LOGIN semantico WITH PASSWORD = 'SecretPassword123!';
USE semantico;
CREATE USER semantico FOR LOGIN semantico;
ALTER ROLE db_owner ADD MEMBER semantico;
```

### Run Migrations

Migrations run automatically when you call:

```csharp
ServiceConfiguration.UseSemantico(app.Services);
```

This will:
1. Create the schema if it doesn't exist (for custom schemas)
2. Apply all pending EF Core migrations
3. Initialize Semantico tables

## Troubleshooting

### NuGet Package Not Found

Ensure you're using .NET 9.0 SDK:

```bash
dotnet --version
```

Check NuGet sources:

```bash
dotnet nuget list source
```

### Database Connection Errors

Test connection string:

**PostgreSQL:**
```bash
psql "Host=localhost;Database=semantico;Username=semantico;Password=secretpass"
```

**SQL Server:**
```bash
sqlcmd -S localhost -d semantico -U semantico -P secretpass
```

### Migration Errors

Check migration status:

```bash
dotnet ef migrations list --project Semantico.Core.PostgreSql --startup-project YourProject
```

Apply migrations manually:

```bash
dotnet ef database update --project Semantico.Core.PostgreSql --startup-project YourProject
```

### Scheduler Not Scheduling

Verify your scheduler is registered:
1. Check your scheduler service is properly registered
2. Verify your `ISemanticoScheduler` implementation is registered
3. Check scheduler dashboard/logs (e.g., Hangfire dashboard at `/hangfire`)

## Package Versions

Compatible package versions:

| Package | Minimum Version |
|---------|----------------|
| Semantico.Core.PostgreSql | Latest |
| Semantico.Core.SqlServer | Latest |
| Semantico.UI.AspNet | Latest |
| Hangfire | 1.8.0+ |
| Hangfire.PostgreSql | 1.20.0+ |
| Microsoft.EntityFrameworkCore | 9.0.0+ |

## Next Steps

<div class="code-example" markdown="1">
🚀 **[Quick Start Guide →](quick-start)**

Create your first query and notification in 30 minutes
</div>

<div class="code-example" markdown="1">
⚙️ **[Configuration Guide →](configuration)**

Learn about all configuration options and customization
</div>

<div class="code-example" markdown="1">
🏗️ **[Architecture →](../advanced/architecture)**

Understand Semantico's Clean Architecture design
</div>
