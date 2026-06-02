---
layout: default
title: Installation
parent: Getting Started
nav_order: 1
---

# Installation Guide

Complete step-by-step guide to install and configure Beacon in your ASP.NET Core application. This guide is based on the actual SampleProject implementation.

## Prerequisites

Before you begin, ensure you have:

- **.NET 9.0 SDK** or later
- **PostgreSQL 12+** or **SQL Server 2019+** for Beacon metadata database
- **Visual Studio 2022**, **Rider**, or **VS Code** with C# support
- **Job scheduler** (Hangfire recommended, but Quartz.NET or custom implementations work)

## Step 1: Install NuGet Packages

### For PostgreSQL (Recommended)

```bash
# Core Beacon packages
dotnet add package Beacon.Core.PostgreSql
dotnet add package Beacon.UI.AspNet

# Job scheduler (Hangfire example)
dotnet add package Hangfire.AspNetCore
dotnet add package Hangfire.PostgreSql
```

### For SQL Server

```bash
# Core Beacon packages
dotnet add package Beacon.Core.SqlServer
dotnet add package Beacon.UI.AspNet

# Job scheduler (Hangfire example)
dotnet add package Hangfire.AspNetCore
dotnet add package Hangfire.SqlServer
```

{: .note }
> The provider you choose is for Beacon's metadata database. You can still query PostgreSQL, SQL Server, and MySQL databases for monitoring regardless of which provider you choose.

## Step 2: Generate Encryption Key

Beacon requires an encryption key for securing sensitive data like connection strings.

```bash
openssl rand -base64 32
```

**Example output:**
```
[REMOVED-KEY]=
```

Save this key - you'll need it in the next step.

## Step 3: Configure appsettings.json

Add connection strings and Beacon configuration:

```json
{
  "ConnectionStrings": {
    "BeaconContext": "Host=localhost;Database=beacon;Username=postgres;Password=yourpassword"
  },
  "Beacon": {
    "EncryptionKey": "[REMOVED-KEY]="
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.EntityFrameworkCore.Database": "Warning"
    }
  }
}
```

### Optional: Enable AI Features (Experimental)

⚠️ **AI features are experimental.** Add LLM configuration only if you plan to use AI-powered documentation or alert generation:

```json
{
  "Beacon": {
    "EncryptionKey": "your-generated-key",
    "LLM": {
      "Provider": "OpenAI",
      "ApiKey": "sk-your-openai-key",
      "Model": "gpt-4o",
      "Limits": {
        "MaxConcurrentRequests": 5,
        "RequestsPerMinute": 60,
        "MaxTokensPerRequest": 4000
      }
    }
  }
}
```

**Supported Providers:**
- `OpenAI` - gpt-4o (recommended)
- `Anthropic` - claude-3-5-sonnet-20241022 (recommended)
- `AzureOpenAI` - Latest models only

## Step 4: Create Job Scheduler Implementation

Create a class that implements `IBeaconScheduler`. This example uses Hangfire (from SampleProject):

**Create file:** `Services/BeaconScheduler.cs`

```csharp
using Hangfire;
using Beacon.Core.Worker;

namespace YourProject.Services;

/// <summary>
/// Hangfire implementation of Beacon scheduler
/// </summary>
public class BeaconScheduler : IBeaconScheduler
{
    private readonly IRecurringJobManager _recurringJobManager;

    public BeaconScheduler(IRecurringJobManager recurringJobManager)
    {
        _recurringJobManager = recurringJobManager;
    }

    public void AddOrUpdate(int subscriptionId, string subscriptionName, string cron)
    {
        _recurringJobManager.AddOrUpdate<IJobService>(
            CompileSubscriptionJobKey(subscriptionId, subscriptionName),
            x => x.ExecuteQuery(subscriptionId),
            cron);
    }

    public void Remove(int subscriptionId, string subscriptionName)
    {
        _recurringJobManager.RemoveIfExists(
            CompileSubscriptionJobKey(subscriptionId, subscriptionName));
    }

    private static string CompileSubscriptionJobKey(int subscriptionId, string subscriptionName)
    {
        return $"{subscriptionId} - {subscriptionName}";
    }
}
```

{: .note }
> If you prefer Quartz.NET or a custom scheduler, implement the same `IBeaconScheduler` interface with your chosen scheduler. See [Alternative Schedulers](#alternative-schedulers) section below.

## Step 5: Configure Program.cs

Update your `Program.cs` to register Beacon services. This is the complete configuration from SampleProject:

```csharp
using Hangfire;
using Hangfire.PostgreSql;
using Beacon.AI;
using Beacon.Core;
using Beacon.Core.PostgreSql;
using Beacon.UI;
using YourProject.Services;

var builder = WebApplication.CreateBuilder(args);

// 1. Configure Hangfire (job scheduler)
builder.Services.AddHangfire((provider, hangfireConfiguration) => hangfireConfiguration
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_170)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseFilter(new AutomaticRetryAttribute { Attempts = 0 })
    .UsePostgreSqlStorage(
        builder.Configuration.GetConnectionString("BeaconContext"),
        new PostgreSqlStorageOptions
        {
            PrepareSchemaIfNecessary = true,
            QueuePollInterval = TimeSpan.FromSeconds(1)
        }));

builder.Services.AddHangfireServer();

// 2. BEACON: Add core services and configure database provider
builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        // Required: Register your scheduler implementation
        options.AddBeaconScheduler<BeaconScheduler>();

        // Optional: Base URL for notification links
        options.BaseUrl = "https://localhost:7187/beacon";

        // Optional: Enable AI features (experimental)
        options.UseAI = true;
    })
    .UsePostgreSql(builder.Configuration.GetConnectionString("BeaconContext")!, "beacon");

// 3. BEACON: Add UI components
builder.Services.AddBeaconUI();

// 4. BEACON: Add AI services (optional)
builder.Services.AddBeaconAI(builder.Configuration);

var app = builder.Build();

app.UseHttpsRedirection();
app.UseStaticFiles(); // Required: Serves Beacon UI assets

// 5. BEACON: Configure admin UI with authentication
app.UseBeaconUI()
    .UseBasicAuthentication("admin", "admin")  // Change these credentials!
    .AddBlazorUI("/beacon");

// Optional: Hangfire dashboard
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    IgnoreAntiforgeryToken = true
});

app.Run();
```

### For SQL Server

If using SQL Server instead of PostgreSQL, make these changes:

```csharp
using Hangfire.SqlServer;
using Beacon.Core.SqlServer;

// Replace PostgreSQL configuration with SQL Server:
builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<BeaconScheduler>();
    })
    .UseSqlServer(builder.Configuration.GetConnectionString("BeaconContext")!, "beacon");

// Update Hangfire to use SQL Server:
builder.Services.AddHangfire(config => config
    .UseSqlServerStorage(
        builder.Configuration.GetConnectionString("BeaconContext")));
```

Update connection string in appsettings.json:
```json
{
  "ConnectionStrings": {
    "BeaconContext": "Server=localhost;Database=beacon;User Id=sa;Password=YourPassword123!;TrustServerCertificate=True"
  }
}
```

## Step 6: Optional - Extended Timeouts for AI Operations

If you plan to use AI features, configure extended timeouts for long-running operations (from SampleProject):

```csharp
// Configure Kestrel server limits for long-running AI operations
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(5);
    serverOptions.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(5);
});

// Configure Blazor Server with extended timeouts for long-running AI operations
builder.Services.AddServerSideBlazor()
    .AddCircuitOptions(options =>
    {
        options.DisconnectedCircuitMaxRetained = 100;
        options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(3);
        options.JSInteropDefaultCallTimeout = TimeSpan.FromMinutes(5);
        options.MaxBufferedUnacknowledgedRenderBatches = 10;
    });

// Increase timeout for all HTTP clients (including AI provider APIs)
builder.Services.AddHttpClient().ConfigureHttpClientDefaults(http =>
{
    http.ConfigureHttpClient(client =>
    {
        client.Timeout = TimeSpan.FromMinutes(5);
    });
});
```

## Step 7: Run Your Application

```bash
dotnet run
```

On first run, Beacon will automatically create the database schema and apply migrations.

Navigate to:
- **Beacon Admin UI**: https://localhost:7187/beacon
- **Hangfire Dashboard**: https://localhost:7187/hangfire (if enabled)

**Default credentials (basic auth):**
- Username: `admin`
- Password: `admin`

⚠️ **Important:** Change default credentials in production!

## Complete Program.cs Example (Production-Ready)

Here's the complete `Program.cs` from SampleProject with all features enabled:

```csharp
using Hangfire;
using Hangfire.PostgreSql;
using Beacon.AI;
using Beacon.Core;
using Beacon.Core.PostgreSql;
using Beacon.UI;
using YourProject.Services;

var builder = WebApplication.CreateBuilder(args);

// Configure Kestrel server limits for long-running AI operations
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(5);
    serverOptions.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(5);
});

// Increase timeout for all HTTP clients
builder.Services.AddHttpClient().ConfigureHttpClientDefaults(http =>
{
    http.ConfigureHttpClient(client =>
    {
        client.Timeout = TimeSpan.FromMinutes(5);
    });
});

// Configure Hangfire
builder.Services.AddHangfire((provider, hangfireConfiguration) => hangfireConfiguration
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_170)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseFilter(new AutomaticRetryAttribute { Attempts = 0 })
    .UsePostgreSqlStorage(
        builder.Configuration.GetConnectionString("BeaconContext"),
        new PostgreSqlStorageOptions
        {
            PrepareSchemaIfNecessary = true,
            QueuePollInterval = TimeSpan.FromSeconds(1)
        }));

builder.Services.AddHangfireServer();

// BEACON: Add core services and configure database provider
builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<BeaconScheduler>();
        options.BaseUrl = "https://localhost:7187/beacon";
        options.UseAI = true;  // Optional: Enable AI features (experimental)
    })
    .UsePostgreSql(builder.Configuration.GetConnectionString("BeaconContext")!, "beacon");

// BEACON: Add UI components
builder.Services.AddBeaconUI();

// BEACON: Add AI services (optional)
builder.Services.AddBeaconAI(builder.Configuration);

var app = builder.Build();

app.UseHttpsRedirection();
app.UseStaticFiles(); // Required: Serves Beacon UI assets

// BEACON: Configure admin UI
app.UseBeaconUI()
    .UseBasicAuthentication("admin", "admin")  // Change in production!
    .AddBlazorUI("/beacon");

// Optional: Hangfire dashboard
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    IgnoreAntiforgeryToken = true
});

app.Run();
```

## Custom Authorization Provider

Replace basic authentication with your own authorization logic:

```csharp
using Microsoft.AspNetCore.Http;
using Beacon.UI.AspNet;

public class CustomAuthorizationProvider : IBeaconAuthorizationProvider
{
    public Task<bool> IsAuthorizedAsync(HttpContext context)
    {
        // Your authorization logic here
        // Example: Check if user is authenticated and has specific role
        return Task.FromResult(
            context.User.Identity?.IsAuthenticated == true &&
            context.User.IsInRole("BeaconAdmin"));
    }
}
```

Register in Program.cs:
```csharp
builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<BeaconScheduler>();
    })
    .UsePostgreSql(builder.Configuration.GetConnectionString("BeaconContext")!, "beacon");

builder.Services.AddBeaconUI(options =>
{
    options.AddAuthorizationProvider<CustomAuthorizationProvider>();
});

// Use custom authorization instead of basic auth
app.UseBeaconUI()
    .UseAuthorization()  // Instead of UseBasicAuthentication
    .AddBlazorUI("/beacon");
```

## Alternative Schedulers

### Using Quartz.NET

```csharp
using Quartz;
using Beacon.Core.Worker;

public class QuartzBeaconScheduler : IBeaconScheduler
{
    private readonly ISchedulerFactory _schedulerFactory;

    public QuartzBeaconScheduler(ISchedulerFactory schedulerFactory)
    {
        _schedulerFactory = schedulerFactory;
    }

    public async void AddOrUpdate(int subscriptionId, string subscriptionName, string cron)
    {
        var scheduler = await _schedulerFactory.GetScheduler();

        var job = JobBuilder.Create<BeaconJob>()
            .WithIdentity($"beacon-{subscriptionId}")
            .UsingJobData("subscriptionId", subscriptionId)
            .Build();

        var trigger = TriggerBuilder.Create()
            .WithIdentity($"beacon-{subscriptionId}-trigger")
            .WithCronSchedule(cron)
            .Build();

        await scheduler.ScheduleJob(job, trigger);
    }

    public async void Remove(int subscriptionId, string subscriptionName)
    {
        var scheduler = await _schedulerFactory.GetScheduler();
        await scheduler.DeleteJob(new JobKey($"beacon-{subscriptionId}"));
    }
}

public class BeaconJob : IJob
{
    private readonly IJobService _jobService;

    public BeaconJob(IJobService jobService)
    {
        _jobService = jobService;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var subscriptionId = context.JobDetail.JobDataMap.GetInt("subscriptionId");
        await _jobService.ExecuteQuery(subscriptionId);
    }
}
```

## Troubleshooting

### Issue: "Beacon:EncryptionKey must be configured"

**Solution:** Generate and add encryption key to appsettings.json:
```bash
openssl rand -base64 32
```

### Issue: "Cannot create database schema"

**Solution:** Ensure your database user has CREATE SCHEMA permissions:

**PostgreSQL:**
```sql
GRANT CREATE ON DATABASE beacon TO your_user;
```

**SQL Server:**
```sql
GRANT CREATE SCHEMA TO your_user;
```

### Issue: Jobs not executing

**Solutions:**
1. Verify Hangfire is configured and running
2. Check Hangfire dashboard at `/hangfire` for job status
3. Ensure `AddHangfireServer()` is called
4. Verify database connection

### Issue: UI not loading at /beacon

**Solutions:**
1. Verify route matches `.AddBlazorUI("/beacon")`
2. Check browser console for errors
3. Ensure `AddServerSideBlazor()` is configured
4. Try clearing browser cache

### Issue: Authentication failing

**Solutions:**
1. Verify credentials match `.UseBasicAuthentication("admin", "admin")`
2. Check if custom authorization provider is properly configured
3. Ensure `UseAuthorization()` is called before Beacon UI

### Issue: AI features not working

**Solutions:**
1. Verify `options.UseAI = true` is set
2. Check LLM configuration in appsettings.json
3. Verify API key is valid and has quota
4. Check application logs for API errors
5. Ensure extended timeouts are configured (Step 6)

## Production Considerations

### Security

✅ **Change default credentials** - Never use admin/admin in production
✅ **Use environment variables** - Store sensitive config outside appsettings.json
✅ **Enable HTTPS** - Always use HTTPS in production
✅ **Implement proper auth** - Use custom authorization provider
✅ **Rotate encryption keys** - Regularly update encryption keys

**Example with environment variables:**
```json
{
  "Beacon": {
    "EncryptionKey": "${BEACON_ENCRYPTION_KEY}",
    "LLM": {
      "ApiKey": "${LLM_API_KEY}"
    }
  }
}
```

### Performance

✅ **Connection pooling** - Configure database connection pooling
✅ **Hangfire workers** - Adjust worker count based on load
✅ **Memory monitoring** - Monitor memory usage for large result sets
✅ **Query timeouts** - Set appropriate execution timeouts

### Monitoring

✅ **Application logging** - Enable detailed logging for troubleshooting
✅ **Hangfire dashboard** - Monitor job execution and failures
✅ **Query alerts** - Set up notifications for critical failures
✅ **AI usage tracking** - Monitor token usage and costs (if using AI)

## Next Steps

Now that Beacon is installed:

1. **[Connect your first data source](../features/data-sources)** - Add databases to monitor
2. **[Create your first query](../features/queries)** - Define SQL monitoring queries
3. **[Set up a subscription](../features/subscriptions)** - Schedule automated execution
4. **[Configure notifications](../features/notifications)** - Send results via Email, Teams, Slack, or Jira

## Additional Resources

- 📚 [Configuration Guide](configuration) - Detailed configuration options
- 🚀 [Quick Start](quick-start) - 5-minute quickstart guide
- 🎓 [Features Overview](../features/) - Complete feature documentation
- 🐛 [Report Issues](https://github.com/moberghr/beacon/issues)
