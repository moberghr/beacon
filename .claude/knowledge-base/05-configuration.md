# Beacon Configuration & Setup

## Overview

Beacon is configured through a combination of NuGet packages, service registration, and appsettings.json configuration.

---

## NuGet Packages

| Package | Purpose |
|---------|---------|
| `Beacon.Core` | Core domain logic, services, entities |
| `Beacon.Core.PostgreSql` | PostgreSQL database provider |
| `Beacon.Core.SqlServer` | SQL Server database provider |
| `Beacon.UI` | Blazor UI components |
| `Beacon.UI.AspNet` | ASP.NET Core hosting helpers |

### Installation

```bash
# PostgreSQL (recommended)
dotnet add package Beacon.Core.PostgreSql
dotnet add package Beacon.UI.AspNet

# SQL Server
dotnet add package Beacon.Core.SqlServer
dotnet add package Beacon.UI.AspNet
```

---

## Program.cs Configuration

### Complete Setup Example

```csharp
using Hangfire;
using Hangfire.PostgreSql;
using Beacon.Core.PostgreSql;
using Beacon.UI.AspNet;

var builder = WebApplication.CreateBuilder(args);

// 1. Configure Hangfire (or your preferred scheduler)
builder.Services.AddHangfire(config => config
    .UsePostgreSqlStorage(
        builder.Configuration.GetConnectionString("BeaconContext")));
builder.Services.AddHangfireServer();

// 2. Configure database provider (choose one)
builder.Services.AddPostgreSqlBeacon(
    builder.Configuration.GetConnectionString("BeaconContext")!,
    schema: "beacon"); // Optional schema name

// OR for SQL Server:
// builder.Services.AddSqlServerBeacon(
//     builder.Configuration.GetConnectionString("BeaconContextSql")!);

// 3. Configure Beacon admin services
builder.Services.AddBeaconAdmin(builder.Configuration, options =>
{
    // Required: Job scheduler implementation
    options.AddBeaconScheduler<YourScheduler>();

    // Optional: Email adapter for email notifications
    // options.AddEmailAdapter<YourEmailAdapter>();

    // Optional: Custom authorization provider
    // options.AddAuthorizationProvider<YourAuthProvider>();

    // Optional: Base URL for notification links
    options.BaseUrl = "https://your-domain.com/beacon";
});

var app = builder.Build();

app.UseHttpsRedirection();
app.UseAuthorization();

// 4. Configure Beacon UI
app.UseBeaconUI()
    .UseBasicAuthentication("admin", "password")  // Simple auth
    // .UseAuthorization()  // Custom auth provider
    .AddBlazorUI("/beacon");  // UI base path

app.Run();
```

---

## Database Provider Configuration

### PostgreSQL

**File:** `src/Beacon.Core.PostgreSql/ServiceCollectionExtensions.cs`

```csharp
builder.Services.AddPostgreSqlBeacon(
    connectionString: "Host=localhost;Database=beacon;Username=postgres;Password=password",
    schema: "beacon"  // Default schema, optional
);
```

**Features:**
- Snake_case naming convention (via EFCore.NamingConventions)
- Configurable schema
- Migrations stored in specified schema

### SQL Server

```csharp
builder.Services.AddSqlServerBeacon(
    connectionString: "Server=localhost;Database=beacon;User Id=sa;Password=password"
);
```

---

## BeaconConfiguration Options

**File:** `src/Beacon.Core/ServiceConfiguration.cs`

```csharp
public class BeaconConfiguration
{
    // Connection string name in appsettings.json
    public string ConnectionStringName { get; set; } = "BeaconContext";

    // Base URL for notification links (View Results button)
    public string? BaseUrl { get; set; }

    // Register scheduler implementation (REQUIRED)
    public void AddBeaconScheduler<T>() where T : class, IBeaconScheduler;

    // Register email adapter (optional, required for email notifications)
    public void AddEmailAdapter<T>() where T : class, IEmailAdapter;

    // Register authorization provider (optional)
    public void AddAuthorizationProvider<T>() where T : class;
}
```

---

## IBeaconScheduler Interface

**File:** `src/Beacon.Core/Worker/IBeaconScheduler.cs`

```csharp
public interface IBeaconScheduler
{
    void AddOrUpdate(int subscriptionId, string jobName, string cronExpression);
    void Remove(int subscriptionId, string jobName);
}
```

### Hangfire Implementation Example

```csharp
public class HangfireBeaconScheduler : IBeaconScheduler
{
    private readonly IJobService _jobService;

    public HangfireBeaconScheduler(IJobService jobService)
    {
        _jobService = jobService;
    }

    public void AddOrUpdate(int subscriptionId, string jobName, string cronExpression)
    {
        RecurringJob.AddOrUpdate(
            $"beacon-{subscriptionId}",
            () => _jobService.ExecuteQuery(subscriptionId, CancellationToken.None),
            cronExpression);
    }

    public void Remove(int subscriptionId, string jobName)
    {
        RecurringJob.RemoveIfExists($"beacon-{subscriptionId}");
    }
}
```

---

## IEmailAdapter Interface

**File:** `src/Beacon.Core/Adapters/Mail/IEmailAdapter.cs`

```csharp
public interface IEmailAdapter
{
    Task SendEmailAsync(
        string to,
        string subject,
        string htmlBody,
        Stream? attachment = null,
        string? attachmentName = null);
}
```

### SMTP Implementation Example

```csharp
public class SmtpEmailAdapter : IEmailAdapter
{
    private readonly SmtpConfiguration _config;

    public async Task SendEmailAsync(
        string to,
        string subject,
        string htmlBody,
        Stream? attachment = null,
        string? attachmentName = null)
    {
        using var message = new MailMessage(_config.FromAddress, to)
        {
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true
        };

        if (attachment != null && attachmentName != null)
        {
            message.Attachments.Add(new Attachment(attachment, attachmentName));
        }

        using var client = new SmtpClient(_config.Host, _config.Port)
        {
            Credentials = new NetworkCredential(_config.Username, _config.Password),
            EnableSsl = true
        };

        await client.SendMailAsync(message);
    }
}
```

---

## appsettings.json Configuration

```json
{
  "ConnectionStrings": {
    "BeaconContext": "Host=localhost;Database=beacon;Username=postgres;Password=yourpassword"
  },
  "Beacon": {
    "EncryptionKey": "YourSecure32CharacterEncryptionKey!",
    "BaseUrl": "https://your-domain.com/beacon"
  }
}
```

### Configuration Keys

| Key | Required | Description |
|-----|----------|-------------|
| `ConnectionStrings:BeaconContext` | Yes | Database connection string |
| `Beacon:EncryptionKey` | No | 32-character key for encrypting connection strings (has default) |
| `Beacon:BaseUrl` | No | Base URL for notification "View Results" links |

---

## UI Builder Configuration

**File:** `Beacon.UI.AspNet/Helpers.cs`

### Builder Pattern

```csharp
app.UseBeaconUI()
    .UseBasicAuthentication("admin", "password")  // Option 1: Basic auth
    .UseBasicAuthentication("admin", "pwd", "Beacon")  // With custom realm
    .UseAuthorization()  // Option 2: Custom auth provider
    .AddBlazorUI("/beacon");  // Required: Sets base path
```

### Authentication Options

1. **Basic Authentication:**
   ```csharp
   .UseBasicAuthentication(username, password)
   ```

2. **Custom Authorization Provider:**
   ```csharp
   // In configuration
   options.AddAuthorizationProvider<YourAuthProvider>();

   // In pipeline
   .UseAuthorization()
   ```

### IBeaconAuthorizationProvider

```csharp
public interface IBeaconAuthorizationProvider
{
    Task<bool> IsAuthorizedAsync(HttpContext context);
}
```

---

## Service Registration Summary

**File:** `src/Beacon.Core/ServiceConfiguration.cs`

```csharp
// Registered services (via TryAddTransient)
services.TryAddTransient<IJobService, JobService>();
services.TryAddTransient<INotificationService, NotificationService>();
services.TryAddTransient<IDataSourceService, DataSourceService>();
services.TryAddTransient<IQueryService, QueryService>();
services.TryAddTransient<IQueryExecutionPreviewService, QueryExecutionPreviewService>();
services.TryAddTransient<ISubscriptionService, SubscriptionService>();
services.TryAddTransient<IRecipientService, RecipientService>();
services.TryAddTransient<ITaskService, TaskService>();
services.TryAddTransient<IStatisticsService, StatisticsService>();
services.TryAddTransient<IMigrationService, MigrationService>();
services.TryAddTransient<IDatabaseMetadataService, DatabaseMetadataService>();

// Singletons
services.AddSingleton(configurationOptions);  // BeaconConfiguration
services.AddSingleton<IEncryptionService>(new EncryptionService(encryptionKey));
services.AddSingleton<IAdapter, TeamsAdapter>();
services.AddSingleton<IAdapter, SlackAdapter>();
services.AddSingleton<IAdapter, EmailAdapter>();  // If email adapter provided
services.AddSingleton<IAdapter, JiraAdapter>();
services.AddSingleton<AdapterFactory>();
```

---

## Database Migrations

### Schema-Agnostic Migrations

Migrations are generated without hardcoded schema names. The schema is applied at runtime.

### Generate Migration

```bash
# PostgreSQL
dotnet ef migrations add MigrationName \
    --project Beacon.Core.PostgreSql \
    --startup-project Beacon.SampleProject

# SQL Server
dotnet ef migrations add MigrationName \
    --project Beacon.Core.SqlServer \
    --startup-project Beacon.SampleProject
```

### Apply Migration

```bash
dotnet ef database update \
    --project Beacon.Core.PostgreSql \
    --startup-project Beacon.SampleProject
```

### Automatic Migration

Migrations are automatically applied via `ServiceConfiguration.UseBeacon()`:

```csharp
// Called internally by AddBlazorUI()
ServiceConfiguration.UseBeacon(app.Services);

// Or manually with schema creation
ServiceConfiguration.UseBeacon(app.Services, createSchema: true);
```

---

## Encryption Service

**File:** `src/Beacon.Core/Services/EncryptionService.cs`

Connection strings stored in DataSource entities are encrypted using AES encryption.

```csharp
// Encryption key from configuration
var encryptionKey = configuration["Beacon:EncryptionKey"]
    ?? "DefaultKey_ChangeInProduction_MustBe32CharsLong!";

services.AddSingleton<IEncryptionService>(new EncryptionService(encryptionKey));
```

**Usage in services:**
```csharp
// Encrypting
dataSource.ConnectionString = encryptionService.Encrypt(rawConnectionString);

// Decrypting
var connectionString = encryptionService.Decrypt(dataSource.ConnectionString);
```

---

## Project Structure

```
Beacon/
├── src/Beacon.Core/                    # Core domain logic
│   ├── Data/
│   │   ├── Entities/                  # Entity classes
│   │   ├── Enums/                     # Enumerations
│   │   └── BeaconContext.cs        # Base DbContext
│   ├── Adapters/                      # Notification adapters
│   ├── Services/                      # Business logic services
│   ├── Worker/                        # Job execution
│   └── ServiceConfiguration.cs        # DI registration
│
├── src/Beacon.Core.PostgreSql/         # PostgreSQL provider
│   ├── Data/
│   │   ├── PostgreSqlBeaconContext.cs
│   │   └── Migrations/
│   └── ServiceCollectionExtensions.cs
│
├── src/Beacon.Core.SqlServer/          # SQL Server provider
│   ├── Data/
│   │   ├── SqlServerBeaconContext.cs
│   │   └── Migrations/
│   └── ServiceCollectionExtensions.cs
│
├── src/Beacon.UI/                      # Blazor UI components
│   └── Components/
│       ├── Layout/
│       ├── Pages/
│       └── Custom/
│
├── Beacon.UI.AspNet/               # ASP.NET hosting
│   ├── Authentication/
│   └── Helpers.cs
│
└── src/Beacon.SampleProject/           # Example implementation
    ├── Program.cs
    └── Services/
        └── BeaconScheduler.cs
```

---

## Environment-Specific Configuration

### Development

```json
{
  "ConnectionStrings": {
    "BeaconContext": "Host=localhost;Database=beacon_dev;..."
  },
  "Beacon": {
    "EncryptionKey": "DevKey_32CharactersLongForAES256!",
    "BaseUrl": "https://localhost:7187/beacon"
  }
}
```

### Production

```json
{
  "ConnectionStrings": {
    "BeaconContext": "Host=prod-server;Database=beacon;..."
  },
  "Beacon": {
    "EncryptionKey": "${BEACON_ENCRYPTION_KEY}",
    "BaseUrl": "https://your-production-domain.com/beacon"
  }
}
```

Use environment variables or secrets manager for sensitive values in production.
