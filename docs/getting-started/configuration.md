---
layout: default
title: Configuration
parent: Getting Started
nav_order: 3
---

# Configuration Guide

Complete reference for all Semantico configuration options.

## Configuration in Program.cs

Semantico is configured in your ASP.NET Core application's `Program.cs` file.

### Basic Configuration

```csharp
using Semantico.Core.PostgreSql;
using Semantico.UI.AspNet;

// Add Semantico services
builder.Services.AddPostgreSqlSemantico(
    builder.Configuration.GetConnectionString("SemanticoContext")!,
    schema: "semantico");

builder.Services.AddSemanticoAdmin(builder.Configuration, options =>
{
    options.AddSemanticoScheduler<YourScheduler>();
    options.BaseUrl = "https://your-domain.com/semantico"; // For notification links
});

// Configure UI
app.UseSemanticoUI()
    .UseBasicAuthentication("admin", "admin")
    .UseAuthorization()
    .AddBlazorUI("/semantico");

// Run migrations
ServiceConfiguration.UseSemantico(app.Services);
```

## Base URL Configuration

The `BaseUrl` setting specifies where your Semantico admin UI is hosted. This URL is used to generate clickable links in notifications (especially Teams messages) that take users directly to notification details.

### Setting Base URL

```csharp
builder.Services.AddSemanticoAdmin(builder.Configuration, options =>
{
    options.AddSemanticoScheduler<YourScheduler>();
    // Set the URL where Semantico UI is accessible
    options.BaseUrl = "https://your-domain.com/semantico";
});
```

### Base URL Examples

**Development (localhost):**
```csharp
options.BaseUrl = "https://localhost:7187/semantico";
```

**Staging:**
```csharp
options.BaseUrl = "https://staging.yourdomain.com/semantico";
```

**Production:**
```csharp
options.BaseUrl = "https://yourdomain.com/semantico";
```

**From configuration:**
```csharp
options.BaseUrl = builder.Configuration["Semantico:BaseUrl"];
```

### appsettings.json Configuration

```json
{
  "Semantico": {
    "BaseUrl": "https://yourdomain.com/semantico"
  }
}
```

### How It's Used

When `BaseUrl` is configured, Teams notifications include a **"View Query Results"** button that links to:
```
{BaseUrl}/notifications/details/{notificationId}
```

For example:
```
https://yourdomain.com/semantico/notifications/details/12345
```

This allows recipients to click through from Teams to view:
- Complete query results
- Execution metrics
- Query execution history
- All notification details

{: .note }
> If `BaseUrl` is not configured, Teams notifications will still be sent but without the clickable link to view details in the Semantico UI.

## Database Provider Configuration

### PostgreSQL Provider

```csharp
using Semantico.Core.PostgreSql;

builder.Services.AddPostgreSqlSemantico(
    connectionString: builder.Configuration.GetConnectionString("SemanticoContext")!,
    schema: "semantico"); // Optional, defaults to "semantico"
```

**Connection string in appsettings.json:**
```json
{
  "ConnectionStrings": {
    "SemanticoContext": "Host=localhost;Database=semantico;Username=postgres;Password=yourpassword"
  }
}
```

### SQL Server Provider

```csharp
using Semantico.Core.SqlServer;

builder.Services.AddSqlServerSemantico(
    connectionString: builder.Configuration.GetConnectionString("SemanticoContext")!,
    schema: "semantico"); // Optional, defaults to "semantico"
```

**Connection string in appsettings.json:**
```json
{
  "ConnectionStrings": {
    "SemanticoContext": "Server=localhost;Database=semantico;User Id=sa;Password=YourPassword123!;TrustServerCertificate=True"
  }
}
```

## Connection String Reference

### PostgreSQL Connection Strings

**Basic connection:**
```
Host=hostname;Port=5432;Database=dbname;Username=user;Password=pass
```

**With SSL:**
```
Host=hostname;Database=dbname;Username=user;Password=pass;SSL Mode=Require
```

**With connection pooling:**
```
Host=hostname;Database=dbname;Username=user;Password=pass;Pooling=true;MinPoolSize=0;MaxPoolSize=100
```

**Connection timeout:**
```
Host=hostname;Database=dbname;Username=user;Password=pass;Timeout=30;CommandTimeout=60
```

**Parameters:**
- `Host`: PostgreSQL server hostname or IP
- `Port`: Port number (default: 5432)
- `Database`: Database name
- `Username`: Database user
- `Password`: User password
- `SSL Mode`: None, Prefer, Require, VerifyCA, VerifyFull
- `Pooling`: Enable connection pooling (recommended: true)
- `MinPoolSize`: Minimum connections in pool
- `MaxPoolSize`: Maximum connections in pool
- `Timeout`: Connection timeout in seconds
- `CommandTimeout`: Command execution timeout in seconds

### SQL Server Connection Strings

**Basic connection:**
```
Server=hostname;Database=dbname;User Id=user;Password=pass;TrustServerCertificate=True
```

**Windows Authentication:**
```
Server=hostname;Database=dbname;Integrated Security=True
```

**With encryption:**
```
Server=hostname;Database=dbname;User Id=user;Password=pass;Encrypt=True;TrustServerCertificate=False
```

**Parameters:**
- `Server`: SQL Server hostname or IP
- `Database`: Database name
- `User Id`: SQL Server login
- `Password`: Login password
- `Integrated Security`: Use Windows authentication
- `TrustServerCertificate`: Trust server certificate (True for self-signed)
- `Encrypt`: Encrypt connection
- `Connection Timeout`: Connection timeout in seconds

### MySQL Connection Strings (for query projects)

**Basic connection:**
```
Server=hostname;Port=3306;Database=dbname;Uid=user;Pwd=pass
```

**With SSL:**
```
Server=hostname;Database=dbname;Uid=user;Pwd=pass;SslMode=Required
```

## Scheduler Configuration

Semantico requires an `ISemanticoScheduler` implementation for job scheduling. You can use any job scheduler library (Hangfire, Quartz.NET, etc.) or create a custom implementation.

### Example: Hangfire Scheduler Implementation

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

Register the scheduler:

```csharp
builder.Services.AddSemanticoAdmin(builder.Configuration, options =>
{
    options.AddSemanticoScheduler<SemanticoScheduler>();
});
```

## UI Configuration

### Basic Authentication

Configure username and password for UI access:

```csharp
app.UseSemanticoUI()
    .UseBasicAuthentication("admin", "secretpassword")
    .AddBlazorUI("/semantico");
```

### Custom UI Path

Change the UI path from default `/semantico`:

```csharp
app.UseSemanticoUI()
    .UseBasicAuthentication("admin", "admin")
    .AddBlazorUI("/alerts"); // Custom path
```

Access UI at: `http://localhost:5000/alerts`

### Authorization Provider

Implement custom authorization logic:

```csharp
using Semantico.UI.AspNet.Authentication;

public class CustomAuthorizationProvider : ISemanticoAuthorizationProvider
{
    private readonly IUserService _userService;

    public CustomAuthorizationProvider(IUserService userService)
    {
        _userService = userService;
    }

    public async Task<bool> HasReadPermissionAsync(string username)
    {
        var user = await _userService.GetUserAsync(username);
        return user?.HasRole("Viewer") ?? false;
    }

    public async Task<bool> HasWritePermissionAsync(string username)
    {
        var user = await _userService.GetUserAsync(username);
        return user?.HasRole("Admin") ?? false;
    }
}
```

Register the provider:

```csharp
builder.Services.AddSemanticoAdmin(builder.Configuration, options =>
{
    options.AddSemanticoScheduler<SemanticoScheduler>();
    options.AddAuthorizationProvider<CustomAuthorizationProvider>();
});

// Enable authorization checks
app.UseSemanticoUI()
    .UseBasicAuthentication("admin", "admin")
    .UseAuthorization() // Enable authorization checks
    .AddBlazorUI("/semantico");
```

## Email Adapter

### Custom Email Implementation

Implement `IEmailAdapter` for your email provider:

```csharp
using Semantico.Core.Adapters.Mail;

public class SmtpEmailAdapter : IEmailAdapter
{
    private readonly SmtpClient _smtpClient;

    public SmtpEmailAdapter(IConfiguration configuration)
    {
        _smtpClient = new SmtpClient
        {
            Host = configuration["Smtp:Host"],
            Port = int.Parse(configuration["Smtp:Port"]),
            EnableSsl = true,
            Credentials = new NetworkCredential(
                configuration["Smtp:Username"],
                configuration["Smtp:Password"])
        };
    }

    public async Task SendEmailAsync(string to, string subject, string body, Stream? attachment = null)
    {
        var message = new MailMessage
        {
            From = new MailAddress("alerts@yourdomain.com", "Semantico Alerts"),
            Subject = subject,
            Body = body,
            IsBodyHtml = true
        };

        message.To.Add(to);

        if (attachment != null)
        {
            message.Attachments.Add(new Attachment(attachment, "results.csv", "text/csv"));
        }

        await _smtpClient.SendMailAsync(message);
    }
}
```

Register the adapter:

```csharp
builder.Services.AddSemanticoAdmin(builder.Configuration, options =>
{
    options.AddSemanticoScheduler<SemanticoScheduler>();
    options.AddEmailAdapter<SmtpEmailAdapter>();
});
```

Add SMTP settings to `appsettings.json`:

```json
{
  "Smtp": {
    "Host": "smtp.gmail.com",
    "Port": "587",
    "Username": "your-email@gmail.com",
    "Password": "your-app-password"
  }
}
```

## Schema Configuration

### Custom Schema Name

Specify a custom schema for multi-tenant deployments:

```csharp
builder.Services.AddPostgreSqlSemantico(
    builder.Configuration.GetConnectionString("SemanticoContext")!,
    schema: "tenant_acme");
```

### Runtime Schema Selection

Select schema based on configuration or tenant context:

```csharp
var schema = builder.Configuration["Semantico:Schema"] ?? "semantico";

builder.Services.AddPostgreSqlSemantico(
    builder.Configuration.GetConnectionString("SemanticoContext")!,
    schema: schema);
```

**In appsettings.json:**
```json
{
  "Semantico": {
    "Schema": "production_semantico"
  }
}
```

### Multi-Tenant Deployment

Each tenant gets its own schema in the same database:

```csharp
// Detect tenant from request context
var tenantId = GetTenantFromContext();
var schema = $"tenant_{tenantId}";

builder.Services.AddPostgreSqlSemantico(
    builder.Configuration.GetConnectionString("SemanticoContext")!,
    schema: schema);
```

📚 [Learn more about multi-tenant deployments →](../advanced/multi-tenant)

## Job Scheduler Configuration

Semantico works with any job scheduler that implements `ISemanticoScheduler`. The examples below use Hangfire, but you can adapt them to Quartz.NET or other schedulers.

### Hangfire Storage Provider (Example)

**PostgreSQL (recommended):**
```csharp
using Hangfire.PostgreSql;

builder.Services.AddHangfire(config => config
    .UsePostgreSqlStorage(connectionString, new PostgreSqlStorageOptions
    {
        PrepareSchemaIfNecessary = true
    }));
```

**SQL Server:**
```csharp
using Hangfire.SqlServer;

builder.Services.AddHangfire(config => config
    .UseSqlServerStorage(connectionString, new SqlServerStorageOptions
    {
        PrepareSchemaIfNecessary = true
    }));
```

**Redis:**
```csharp
using Hangfire.Redis;

builder.Services.AddHangfire(config => config
    .UseRedisStorage(connectionString));
```

### Worker Count

Adjust worker count based on subscription load:

```csharp
builder.Services.AddHangfireServer(options =>
{
    options.WorkerCount = 10; // Default: CPU core count
});
```

**Guidelines:**
- Light load (< 100 subscriptions): 2-4 workers
- Medium load (100-500 subscriptions): 5-10 workers
- Heavy load (500+ subscriptions): 10-20 workers

## Security Configuration

### Database User Permissions

**For Semantico metadata database (PostgreSQL):**
```sql
CREATE USER semantico WITH PASSWORD 'strong-password';
GRANT ALL PRIVILEGES ON DATABASE semantico TO semantico;
GRANT ALL ON SCHEMA semantico TO semantico;
```

**For query projects (read-only recommended):**
```sql
CREATE USER semantico_readonly WITH PASSWORD 'strong-password';
GRANT CONNECT ON DATABASE your_database TO semantico_readonly;
GRANT USAGE ON SCHEMA public TO semantico_readonly;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO semantico_readonly;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO semantico_readonly;
```

### Connection String Security

**Best practices:**
- ✓ Use User Secrets for development
- ✓ Use Azure Key Vault or similar for production
- ✓ Use read-only users for query projects
- ✓ Enable SSL/TLS for production
- ✓ Rotate passwords regularly

**User Secrets (development):**
```bash
dotnet user-secrets init
dotnet user-secrets set "ConnectionStrings:SemanticoContext" "Host=localhost;Database=semantico;Username=postgres;Password=devpassword"
```

**Azure Key Vault (production):**
```csharp
builder.Configuration.AddAzureKeyVault(
    new Uri($"https://{keyVaultName}.vault.azure.net/"),
    new DefaultAzureCredential());
```

## Performance Configuration

### Connection Pooling

Enable connection pooling for better performance:

**PostgreSQL:**
```
Host=postgres;Database=semantico;Username=sa;Password=pass;Pooling=true;MinPoolSize=5;MaxPoolSize=50
```

**SQL Server:**
```
Server=sqlserver;Database=semantico;User Id=sa;Password=pass;Max Pool Size=50;Min Pool Size=5
```

### Query Timeout

Set appropriate timeouts for long-running queries:

**In connection string:**
```
Host=postgres;Database=semantico;Username=sa;Password=pass;CommandTimeout=300
```

**In subscription settings:**
- Set timeout to match expected query duration
- Default: 60 seconds
- Long-running reports: 300-600 seconds

## Notification Configuration

### Email Adapter

Semantico doesn't include a default email implementation. You must provide your own:

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

    public async Task SendEmailAsync(string to, string subject, string body, Stream? attachment = null)
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

        if (attachment != null)
        {
            message.Attachments.Add(new Attachment(attachment, "results.csv", "text/csv"));
        }

        await smtpClient.SendMailAsync(message);
    }
}
```

Register the adapter:

```csharp
builder.Services.AddSemanticoAdmin(builder.Configuration, options =>
{
    options.AddSemanticoScheduler<SemanticoScheduler>();
    options.AddEmailAdapter<SmtpEmailAdapter>();
});
```

**Email configuration in appsettings.json:**
```json
{
  "Email": {
    "SmtpHost": "smtp.gmail.com",
    "SmtpPort": "587",
    "Username": "your-email@gmail.com",
    "Password": "your-app-password",
    "FromAddress": "alerts@yourdomain.com",
    "FromName": "Semantico Alerts"
  }
}
```

### Teams Notifications

Teams notifications work out-of-the-box. No additional configuration needed beyond creating recipient with webhook URL.

### Jira Notifications

Jira notifications work out-of-the-box. Configure per recipient with Jira instance details.

## Schema Configuration

### Default Schema

By default, Semantico uses `"semantico"` schema:

```csharp
builder.Services.AddPostgreSqlSemantico(
    builder.Configuration.GetConnectionString("SemanticoContext")!);
// Uses "semantico" schema
```

### Custom Schema

Specify custom schema for multi-tenancy or environment separation:

```csharp
builder.Services.AddPostgreSqlSemantico(
    builder.Configuration.GetConnectionString("SemanticoContext")!,
    schema: "custom_schema");
```

### Runtime Schema Selection

Load schema from configuration:

```csharp
var schema = builder.Configuration["Semantico:Schema"] ?? "semantico";

builder.Services.AddPostgreSqlSemantico(
    builder.Configuration.GetConnectionString("SemanticoContext")!,
    schema: schema);
```

**In appsettings.json:**
```json
{
  "Semantico": {
    "Schema": "production_semantico"
  }
}
```

**Environment-specific schemas:**

**appsettings.Development.json:**
```json
{
  "Semantico": {
    "Schema": "dev_semantico"
  }
}
```

**appsettings.Production.json:**
```json
{
  "Semantico": {
    "Schema": "prod_semantico"
  }
}
```

## UI Path Configuration

### Custom Base Path

Change the Semantico UI path:

```csharp
app.UseSemanticoUI()
    .UseBasicAuthentication("admin", "admin")
    .AddBlazorUI("/custom-path");
```

Access UI at: `http://localhost:5000/custom-path`

### Multiple Paths

Host Semantico UI at multiple paths (for different auth):

```csharp
app.UseSemanticoUI()
    .UseBasicAuthentication("admin", "admin")
    .AddBlazorUI("/semantico");

app.UseSemanticoUI()
    .UseBasicAuthentication("viewer", "readonly")
    .AddBlazorUI("/semantico-viewer");
```

## Logging Configuration

Configure logging in `appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Semantico": "Debug",
      "Hangfire": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}
```

**Log levels:**
- `Trace`: Detailed diagnostic information
- `Debug`: Debugging information
- `Information`: General informational messages
- `Warning`: Warnings that might need attention
- `Error`: Errors and exceptions
- `Critical`: Critical failures

## Complete Configuration Example

```csharp
using Hangfire;
using Hangfire.PostgreSql;
using Semantico.Core;
using Semantico.Core.PostgreSql;
using Semantico.UI.AspNet;

var builder = WebApplication.CreateBuilder(args);

// Hangfire configuration
builder.Services.AddHangfire((provider, config) => config
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

builder.Services.AddHangfireServer(options =>
{
    options.WorkerCount = 10; // Adjust based on load
});

// Semantico database configuration
var schema = builder.Configuration["Semantico:Schema"] ?? "semantico";
builder.Services.AddPostgreSqlSemantico(
    builder.Configuration.GetConnectionString("SemanticoContext")!,
    schema: schema);

// Semantico admin UI configuration
builder.Services.AddSemanticoAdmin(builder.Configuration, options =>
{
    options.AddSemanticoScheduler<SemanticoScheduler>();
    options.AddAuthorizationProvider<CustomAuthorizationProvider>();
    options.AddEmailAdapter<SmtpEmailAdapter>();
    options.BaseUrl = builder.Configuration["Semantico:BaseUrl"];
});

var app = builder.Build();

app.UseHttpsRedirection();
app.UseAuthorization();

// Semantico UI
app.UseSemanticoUI()
    .UseBasicAuthentication(
        builder.Configuration["Semantico:AdminUsername"] ?? "admin",
        builder.Configuration["Semantico:AdminPassword"] ?? "admin")
    .UseAuthorization()
    .AddBlazorUI("/semantico");

// Optional: Hangfire dashboard
app.UseHangfireDashboard("/hangfire");

// Run migrations
ServiceConfiguration.UseSemantico(app.Services);

app.Run();
```

**Complete appsettings.json:**
```json
{
  "ConnectionStrings": {
    "SemanticoContext": "Host=localhost;Database=semantico;Username=semantico;Password=secretpass;Pooling=true;MaxPoolSize=50"
  },
  "Semantico": {
    "Schema": "semantico",
    "AdminUsername": "admin",
    "AdminPassword": "secretpassword",
    "BaseUrl": "https://yourdomain.com/semantico"
  },
  "Email": {
    "SmtpHost": "smtp.gmail.com",
    "SmtpPort": "587",
    "Username": "alerts@yourdomain.com",
    "Password": "app-specific-password",
    "FromAddress": "alerts@yourdomain.com",
    "FromName": "Semantico Alerts"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Semantico": "Information",
      "Hangfire": "Warning"
    }
  }
}
```

## Next Steps

<div class="code-example" markdown="1">
📊 **[Features →](../features/)**

Explore Projects, Queries, Subscriptions, and more
</div>

<div class="code-example" markdown="1">
🔧 **[Troubleshooting →](../troubleshooting/common-issues)**

Solutions for common configuration problems
</div>

<div class="code-example" markdown="1">
🏗️ **[Architecture →](../advanced/architecture)**

Understand Semantico's extensibility points
</div>
