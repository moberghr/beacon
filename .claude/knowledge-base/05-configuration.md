# Semantico Configuration & Setup

## Overview

Semantico is configured through a combination of NuGet packages, service registration, and appsettings.json configuration.

---

## NuGet Packages

| Package | Purpose |
|---------|---------|
| `Semantico.Core` | Core domain logic, services, entities |
| `Semantico.Core.PostgreSql` | PostgreSQL database provider |
| `Semantico.Core.SqlServer` | SQL Server database provider |
| `Semantico.UI` | Blazor UI components |
| `Semantico.UI.AspNet` | ASP.NET Core hosting helpers |

### Installation

```bash
# PostgreSQL (recommended)
dotnet add package Semantico.Core.PostgreSql
dotnet add package Semantico.UI.AspNet

# SQL Server
dotnet add package Semantico.Core.SqlServer
dotnet add package Semantico.UI.AspNet
```

---

## Program.cs Configuration

### Complete Setup Example

```csharp
using Hangfire;
using Hangfire.PostgreSql;
using Semantico.Core.PostgreSql;
using Semantico.UI.AspNet;

var builder = WebApplication.CreateBuilder(args);

// 1. Configure Hangfire (or your preferred scheduler)
builder.Services.AddHangfire(config => config
    .UsePostgreSqlStorage(
        builder.Configuration.GetConnectionString("SemanticoContext")));
builder.Services.AddHangfireServer();

// 2. Configure database provider (choose one)
builder.Services.AddPostgreSqlSemantico(
    builder.Configuration.GetConnectionString("SemanticoContext")!,
    schema: "semantico"); // Optional schema name

// OR for SQL Server:
// builder.Services.AddSqlServerSemantico(
//     builder.Configuration.GetConnectionString("SemanticoContextSql")!);

// 3. Configure Semantico admin services
builder.Services.AddSemanticoAdmin(builder.Configuration, options =>
{
    // Required: Job scheduler implementation
    options.AddSemanticoScheduler<YourScheduler>();

    // Optional: Email adapter for email notifications
    // options.AddEmailAdapter<YourEmailAdapter>();

    // Optional: Custom authorization provider
    // options.AddAuthorizationProvider<YourAuthProvider>();

    // Optional: Base URL for notification links
    options.BaseUrl = "https://your-domain.com/semantico";
});

var app = builder.Build();

app.UseHttpsRedirection();
app.UseAuthorization();

// 4. Configure Semantico UI
app.UseSemanticoUI()
    .UseBasicAuthentication("admin", "password")  // Simple auth
    // .UseAuthorization()  // Custom auth provider
    .AddBlazorUI("/semantico");  // UI base path

app.Run();
```

---

## Database Provider Configuration

### PostgreSQL

**File:** `Semantico.Core.PostgreSql/ServiceCollectionExtensions.cs`

```csharp
builder.Services.AddPostgreSqlSemantico(
    connectionString: "Host=localhost;Database=semantico;Username=postgres;Password=password",
    schema: "semantico"  // Default schema, optional
);
```

**Features:**
- Snake_case naming convention (via EFCore.NamingConventions)
- Configurable schema
- Migrations stored in specified schema

### SQL Server

```csharp
builder.Services.AddSqlServerSemantico(
    connectionString: "Server=localhost;Database=semantico;User Id=sa;Password=password"
);
```

---

## SemanticoConfiguration Options

**File:** `Semantico.Core/ServiceConfiguration.cs`

```csharp
public class SemanticoConfiguration
{
    // Connection string name in appsettings.json
    public string ConnectionStringName { get; set; } = "SemanticoContext";

    // Base URL for notification links (View Results button)
    public string? BaseUrl { get; set; }

    // Register scheduler implementation (REQUIRED)
    public void AddSemanticoScheduler<T>() where T : class, ISemanticoScheduler;

    // Register email adapter (optional, required for email notifications)
    public void AddEmailAdapter<T>() where T : class, IEmailAdapter;

    // Register authorization provider (optional)
    public void AddAuthorizationProvider<T>() where T : class;
}
```

---

## ISemanticoScheduler Interface

**File:** `Semantico.Core/Worker/ISemanticoScheduler.cs`

```csharp
public interface ISemanticoScheduler
{
    void AddOrUpdate(int subscriptionId, string jobName, string cronExpression);
    void Remove(int subscriptionId, string jobName);
}
```

### Hangfire Implementation Example

```csharp
public class HangfireSemanticoScheduler : ISemanticoScheduler
{
    private readonly IJobService _jobService;

    public HangfireSemanticoScheduler(IJobService jobService)
    {
        _jobService = jobService;
    }

    public void AddOrUpdate(int subscriptionId, string jobName, string cronExpression)
    {
        RecurringJob.AddOrUpdate(
            $"semantico-{subscriptionId}",
            () => _jobService.ExecuteQuery(subscriptionId, CancellationToken.None),
            cronExpression);
    }

    public void Remove(int subscriptionId, string jobName)
    {
        RecurringJob.RemoveIfExists($"semantico-{subscriptionId}");
    }
}
```

---

## IEmailAdapter Interface

**File:** `Semantico.Core/Adapters/Mail/IEmailAdapter.cs`

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
    "SemanticoContext": "Host=localhost;Database=semantico;Username=postgres;Password=yourpassword"
  },
  "Semantico": {
    "EncryptionKey": "YourSecure32CharacterEncryptionKey!",
    "BaseUrl": "https://your-domain.com/semantico"
  }
}
```

### Configuration Keys

| Key | Required | Description |
|-----|----------|-------------|
| `ConnectionStrings:SemanticoContext` | Yes | Database connection string |
| `Semantico:EncryptionKey` | No | 32-character key for encrypting connection strings (has default) |
| `Semantico:BaseUrl` | No | Base URL for notification "View Results" links |

---

## UI Builder Configuration

**File:** `Semantico.UI.AspNet/Helpers.cs`

### Builder Pattern

```csharp
app.UseSemanticoUI()
    .UseBasicAuthentication("admin", "password")  // Option 1: Basic auth
    .UseBasicAuthentication("admin", "pwd", "Semantico")  // With custom realm
    .UseAuthorization()  // Option 2: Custom auth provider
    .AddBlazorUI("/semantico");  // Required: Sets base path
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

### ISemanticoAuthorizationProvider

```csharp
public interface ISemanticoAuthorizationProvider
{
    Task<bool> IsAuthorizedAsync(HttpContext context);
}
```

---

## Service Registration Summary

**File:** `Semantico.Core/ServiceConfiguration.cs`

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
services.AddSingleton(configurationOptions);  // SemanticoConfiguration
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
    --project Semantico.Core.PostgreSql \
    --startup-project Semantico.SampleProject

# SQL Server
dotnet ef migrations add MigrationName \
    --project Semantico.Core.SqlServer \
    --startup-project Semantico.SampleProject
```

### Apply Migration

```bash
dotnet ef database update \
    --project Semantico.Core.PostgreSql \
    --startup-project Semantico.SampleProject
```

### Automatic Migration

Migrations are automatically applied via `ServiceConfiguration.UseSemantico()`:

```csharp
// Called internally by AddBlazorUI()
ServiceConfiguration.UseSemantico(app.Services);

// Or manually with schema creation
ServiceConfiguration.UseSemantico(app.Services, createSchema: true);
```

---

## Encryption Service

**File:** `Semantico.Core/Services/EncryptionService.cs`

Connection strings stored in DataSource entities are encrypted using AES encryption.

```csharp
// Encryption key from configuration
var encryptionKey = configuration["Semantico:EncryptionKey"]
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
Semantico/
├── Semantico.Core/                    # Core domain logic
│   ├── Data/
│   │   ├── Entities/                  # Entity classes
│   │   ├── Enums/                     # Enumerations
│   │   └── SemanticoContext.cs        # Base DbContext
│   ├── Adapters/                      # Notification adapters
│   ├── Services/                      # Business logic services
│   ├── Worker/                        # Job execution
│   └── ServiceConfiguration.cs        # DI registration
│
├── Semantico.Core.PostgreSql/         # PostgreSQL provider
│   ├── Data/
│   │   ├── PostgreSqlSemanticoContext.cs
│   │   └── Migrations/
│   └── ServiceCollectionExtensions.cs
│
├── Semantico.Core.SqlServer/          # SQL Server provider
│   ├── Data/
│   │   ├── SqlServerSemanticoContext.cs
│   │   └── Migrations/
│   └── ServiceCollectionExtensions.cs
│
├── Semantico.UI/                      # Blazor UI components
│   └── Components/
│       ├── Layout/
│       ├── Pages/
│       └── Custom/
│
├── Semantico.UI.AspNet/               # ASP.NET hosting
│   ├── Authentication/
│   └── Helpers.cs
│
└── Semantico.SampleProject/           # Example implementation
    ├── Program.cs
    └── Services/
        └── SemanticoScheduler.cs
```

---

## Environment-Specific Configuration

### Development

```json
{
  "ConnectionStrings": {
    "SemanticoContext": "Host=localhost;Database=semantico_dev;..."
  },
  "Semantico": {
    "EncryptionKey": "DevKey_32CharactersLongForAES256!",
    "BaseUrl": "https://localhost:7187/semantico"
  }
}
```

### Production

```json
{
  "ConnectionStrings": {
    "SemanticoContext": "Host=prod-server;Database=semantico;..."
  },
  "Semantico": {
    "EncryptionKey": "${SEMANTICO_ENCRYPTION_KEY}",
    "BaseUrl": "https://your-production-domain.com/semantico"
  }
}
```

Use environment variables or secrets manager for sensitive values in production.
